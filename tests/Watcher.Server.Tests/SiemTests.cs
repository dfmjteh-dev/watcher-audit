using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Watcher.Server.Tests;

// H4 (roadmap-next.md): экспорт журнала доступа в SIEM.
public class SiemTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"watcher-siem-{Guid.NewGuid():N}");
    private readonly Db _db;

    public SiemTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new Db(Path.Combine(_dir, "watcher.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly DateTime Ts = new(2026, 10, 9, 7, 15, 5, 238, DateTimeKind.Utc);

    private static AuditEntry Entry(string action = "segment.video", string? user = "admin", string? ip = "10.1.2.3", string? target = "segment:5", string? details = null) =>
        new(42, Ts, user, ip, action, target, details);

    // --- форматтер ---

    [Fact]
    public void Cef_escapes_header_and_extension_and_maps_fields()
    {
        var e = Entry("login.failed", user: "bo=b\\x", details: "{\"reason\":\"bad|password\nx\"}");

        string cef = SiemFormatter.Cef(e, "0.9.0");

        Assert.StartsWith("CEF:0|Watcher|Watcher Server|0.9.0|login.failed|login.failed|5|", cef);
        Assert.Contains("rt=1791530105238", cef);
        Assert.Contains("suser=bo\\=b\\\\x", cef);
        Assert.Contains("src=10.1.2.3", cef);
        Assert.Contains("externalId=42", cef);
        Assert.Contains("cs1Label=target cs1=segment:5", cef);
        Assert.Contains("cs2=", cef);
        Assert.DoesNotContain("\n", cef);
        Assert.Contains("\\n", cef);
    }

    [Fact]
    public void Cef_omits_src_that_is_not_an_ip_and_escapes_header_pipes()
    {
        string cef = SiemFormatter.Cef(Entry(ip: "unknown"), "1|2");
        Assert.DoesNotContain("src=", cef);
        Assert.StartsWith("CEF:0|Watcher|Watcher Server|1\\|2|", cef);
    }

    [Fact]
    public void Header_pipe_escaping() => Assert.Equal("a\\|b\\\\c", SiemFormatter.Header("a|b\\c"));

    [Theory]
    [InlineData("segment.video", null, 2)]
    [InlineData("segment.video", "{\"denied\":true}", 6)]
    [InlineData("login.failed", null, 5)]
    [InlineData("user.create", null, 4)]
    [InlineData("agent.revoke", null, 4)]
    [InlineData("login.ok", null, 2)]
    public void Severity_mapping(string action, string? details, int expected) =>
        Assert.Equal(expected, SiemFormatter.Severity(Entry(action, details: details)));

    [Fact]
    public void Syslog_5424_header_with_facility_and_severity()
    {
        var cfg = new SiemConfig { Facility = 10, AppName = "watcher" };

        string line = SiemFormatter.Syslog(Entry("login.failed"), cfg, "watcher srv", "0.9.0");

        // authpriv(10)*8 + notice(5) = 85; пробел в имени хоста убран.
        Assert.StartsWith("<85>1 2026-10-09T07:15:05.238Z watchersrv watcher - audit - CEF:0|", line);
    }

    [Fact]
    public void Json_format_keeps_cyrillic_and_details_as_object()
    {
        var cfg = new SiemConfig { Format = "json" };

        string line = SiemFormatter.Syslog(Entry(details: "{\"group\":\"Бухгалтерия\"}"), cfg, "h", "0.9.0");
        var json = JsonDocument.Parse(line[(line.IndexOf("- audit - ", StringComparison.Ordinal) + 10)..]).RootElement;

        Assert.Contains("Бухгалтерия", line);
        Assert.Equal("Бухгалтерия", json.GetProperty("details").GetProperty("group").GetString());
        Assert.Equal(42, json.GetProperty("id").GetInt64());
    }

    // --- пересыльщик ---

    private sealed class FakeTransport : ISiemTransport
    {
        public readonly List<string> Sent = new();
        public bool Fail;
        public bool Closed;
        public Task SendAsync(IReadOnlyList<string> messages, CancellationToken ct)
        {
            if (Fail) throw new IOException("siem down");
            Sent.AddRange(messages);
            return Task.CompletedTask;
        }
        public bool ResetIfClosed()
        {
            bool c = Closed;
            Closed = false;
            return c;
        }
        public void Dispose() { }
    }

    private void Audit(string action) => _db.InsertAudit(new AuditEntry(0, DateTime.UtcNow, "admin", "10.0.0.1", action, null, null));

    private DateTime _now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private (SiemForwarder, ServerHealth) Forwarder(bool history = false, int batch = 200)
    {
        var cfg = new ServerConfig { DataDir = _dir, Siem = new SiemConfig { Host = "siem", SendHistory = history, BatchSize = batch } };
        var health = new ServerHealth();
        return (new SiemForwarder(_db, cfg, health, NullLogger<SiemForwarder>.Instance, () => new FakeTransport(), () => _now), health);
    }

    private static List<string> Actions(FakeTransport t) =>
        t.Sent.Select(s => s.Split('|')[4]).ToList();

    // Сервер умер сразу после записи (RST ещё не пришёл, запись «прошла»):
    // при следующей проверке — откат к подтверждённой позиции и повтор.
    [Fact]
    public async Task Connection_lost_right_after_write_resends_unconfirmed_entries()
    {
        Audit("a"); Audit("b");
        var (f, _) = Forwarder(history: true);
        var t = new FakeTransport();

        await f.ForwardOnceAsync(t, default);                       // a, b; подтверждено — 0
        Audit("c");
        _now += TimeSpan.FromMilliseconds(100);
        await f.ForwardOnceAsync(t, default);                       // c; слишком рано, подтверждение не двигается
        t.Closed = true;
        _now += TimeSpan.FromMilliseconds(100);
        await f.ForwardOnceAsync(t, default);                       // обрыв → a, b, c заново

        Assert.Equal(new[] { "a", "b", "c", "a", "b", "c" }, Actions(t));
    }

    [Fact]
    public async Task Connection_lost_after_confirmation_does_not_resend()
    {
        Audit("a");
        var (f, _) = Forwarder(history: true);
        var t = new FakeTransport();

        await f.ForwardOnceAsync(t, default);
        _now += TimeSpan.FromSeconds(2);
        await f.ForwardOnceAsync(t, default);                       // соединение живо, прошло > 0,5 с — «a» подтверждена
        Audit("b");
        t.Closed = true;
        _now += TimeSpan.FromSeconds(2);
        await f.ForwardOnceAsync(t, default);

        Assert.Equal(new[] { "a", "b" }, Actions(t));
    }

    [Fact]
    public async Task Send_failure_rewinds_to_confirmed_position()
    {
        Audit("a");
        var (f, _) = Forwarder(history: true);
        var t = new FakeTransport();

        await f.ForwardOnceAsync(t, default);                       // a ушла, не подтверждена
        Audit("b");
        _now += TimeSpan.FromMilliseconds(100);
        t.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => f.ForwardOnceAsync(t, default));
        t.Fail = false;
        await f.ForwardOnceAsync(t, default);

        Assert.Equal(new[] { "a", "a", "b" }, Actions(t));
    }

    [Fact]
    public async Task First_start_skips_history_then_forwards_new_entries_and_resumes_after_restart()
    {
        Audit("old.1"); Audit("old.2");
        var t = new FakeTransport();
        var (f1, h1) = Forwarder();

        Assert.Equal(0, await f1.ForwardOnceAsync(t, default));
        Audit("new.1");
        Assert.Equal(1, await f1.ForwardOnceAsync(t, default));

        Audit("new.2");
        var (f2, _) = Forwarder();   // «перезапуск»: новый экземпляр, тот же курсор на диске
        Assert.Equal(1, await f2.ForwardOnceAsync(t, default));

        Assert.Equal(2, t.Sent.Count);
        Assert.Contains("|new.1|", t.Sent[0]);
        Assert.Contains("|new.2|", t.Sent[1]);
        Assert.Equal(1, h1.SiemForwarded);
    }

    [Fact]
    public async Task Send_history_starts_from_the_beginning_in_batches()
    {
        for (int i = 0; i < 5; i++) Audit($"a.{i}");
        var t = new FakeTransport();
        var (f, h) = Forwarder(history: true, batch: 2);

        Assert.Equal(2, await f.ForwardOnceAsync(t, default));
        Assert.Equal(3, h.SiemLag);
        Assert.Equal(2, await f.ForwardOnceAsync(t, default));
        Assert.Equal(1, await f.ForwardOnceAsync(t, default));
        Assert.Equal(0, await f.ForwardOnceAsync(t, default));

        Assert.Equal(5, t.Sent.Count);
        Assert.Equal(0, h.SiemLag);
    }

    [Fact]
    public async Task Failed_send_keeps_cursor_and_retries_same_entries()
    {
        var (f, _) = Forwarder();
        var t = new FakeTransport();
        await f.ForwardOnceAsync(t, default);
        Audit("x.1");

        t.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => f.ForwardOnceAsync(t, default));
        t.Fail = false;
        Assert.Equal(1, await f.ForwardOnceAsync(t, default));

        Assert.Contains("|x.1|", Assert.Single(t.Sent));
    }

    [Fact]
    public async Task Cursor_ahead_of_db_restarts_from_the_end()
    {
        Audit("a");
        File.WriteAllText(Path.Combine(_dir, "siem.cursor"), "999");   // БД восстановлена из старой копии
        var (f, _) = Forwarder();
        var t = new FakeTransport();

        Assert.Equal(0, await f.ForwardOnceAsync(t, default));
        Audit("b");
        Assert.Equal(1, await f.ForwardOnceAsync(t, default));
        Assert.Contains("|b|", Assert.Single(t.Sent));
    }

    [Theory]
    [InlineData("udp", "cef", "octet", null)]
    [InlineData("tls", "json", "lf", null)]
    [InlineData("http", "cef", "octet", "Protocol")]
    [InlineData("tcp", "leef", "octet", "Format")]
    [InlineData("tcp", "cef", "crlf", "Framing")]
    public void Config_validation(string protocol, string format, string framing, string? error)
    {
        var v = new SiemConfig { Host = "h", Protocol = protocol, Format = format, Framing = framing }.Validate();
        if (error is null) Assert.Null(v); else Assert.Contains(error, v);
    }

    // --- транспорт, настоящие сокеты ---

    private static async Task<byte[]> ReadAll(Stream s, int min)
    {
        var buf = new MemoryStream();
        var b = new byte[4096];
        while (buf.Length < min)
        {
            int n = await s.ReadAsync(b);
            if (n == 0) break;
            buf.Write(b, 0, n);
        }
        return buf.ToArray();
    }

    [Theory]
    [InlineData("octet", "5 hello6 мир")]   // длина — в байтах UTF-8 (RFC 6587)
    [InlineData("lf", "hello\nмир\n")]
    public async Task Tcp_framing(string framing, string expected)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var transport = new StreamSiemTransport(new SiemConfig { Host = "127.0.0.1", Port = port, Protocol = "tcp", Framing = framing });

        var accept = listener.AcceptTcpClientAsync();
        await transport.SendAsync(new[] { "hello", "мир" }, default);
        using var client = await accept;
        byte[] got = await ReadAll(client.GetStream(), Encoding.UTF8.GetByteCount(expected));
        listener.Stop();

        Assert.Equal(expected, Encoding.UTF8.GetString(got));
    }

    // Сервер закрыл соединение между пачками (перезапуск SIEM): следующая пачка
    // должна уйти по новому соединению, а не в мёртвый сокет.
    [Fact]
    public async Task Tcp_reconnects_after_server_closed_connection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var transport = new StreamSiemTransport(new SiemConfig { Host = "127.0.0.1", Port = port, Protocol = "tcp" });

        var accept1 = listener.AcceptTcpClientAsync();
        await transport.SendAsync(new[] { "first" }, default);
        using (var c1 = await accept1)
            Assert.Equal("5 first", Encoding.UTF8.GetString(await ReadAll(c1.GetStream(), 7)));
        await Task.Delay(200);   // FIN дошёл до клиента

        var accept2 = listener.AcceptTcpClientAsync();
        await transport.SendAsync(new[] { "second" }, default);
        using var c2 = await accept2.WaitAsync(TimeSpan.FromSeconds(5));
        string got = Encoding.UTF8.GetString(await ReadAll(c2.GetStream(), 8));
        listener.Stop();

        Assert.Equal("6 second", got);
    }

    [Fact]
    public async Task Udp_one_message_per_datagram()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var transport = new UdpSiemTransport("127.0.0.1", port);

        await transport.SendAsync(new[] { "one", "two" }, default);
        var a = await server.ReceiveAsync();
        var b = await server.ReceiveAsync();

        Assert.Equal("one", Encoding.UTF8.GetString(a.Buffer));
        Assert.Equal("two", Encoding.UTF8.GetString(b.Buffer));
    }

    // CA + серверный сертификат на localhost, подписанный этим CA.
    private static (X509Certificate2 Ca, X509Certificate2 Server) MakeCerts()
    {
        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest("CN=Watcher Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var ca = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var signed = req.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), new byte[] { 1, 2, 3, 4 });
        var server = new X509Certificate2(signed.CopyWithPrivateKey(key).Export(X509ContentType.Pfx));
        return (ca, server);
    }

    private async Task<(bool Ok, string Received)> TlsRoundTrip(X509Certificate2 trustedCa, X509Certificate2 serverCert)
    {
        string caPath = Path.Combine(_dir, $"ca-{Guid.NewGuid():N}.crt");
        File.WriteAllText(caPath, trustedCa.ExportCertificatePem());
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var ssl = new SslStream(client.GetStream());
            try
            {
                await ssl.AuthenticateAsServerAsync(serverCert);
                return Encoding.UTF8.GetString(await ReadAll(ssl, 7));
            }
            catch (Exception) { return ""; }
        });
        using var transport = new StreamSiemTransport(new SiemConfig { Host = "localhost", Port = port, Protocol = "tls", CaCertPath = caPath });
        bool ok = true;
        try { await transport.SendAsync(new[] { "hello" }, default); }
        catch (Exception) { ok = false; }
        string received = await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();
        return (ok, received);
    }

    [Fact]
    public async Task Tls_with_custom_ca_delivers()
    {
        var (ca, server) = MakeCerts();
        var (ok, received) = await TlsRoundTrip(ca, server);
        Assert.True(ok);
        Assert.Equal("5 hello", received);
    }

    // TLS-сервер закрывает соединение с close_notify (это байты, а не «0 к
    // чтению») — так syslog-ng закрывал соединения при перезапуске, и пачка
    // уходила в мёртвый сокет.
    [Fact]
    public async Task Tls_reconnects_after_server_close_notify()
    {
        var (ca, serverCert) = MakeCerts();
        string caPath = Path.Combine(_dir, "ca-close.crt");
        File.WriteAllText(caPath, ca.ExportCertificatePem());
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        async Task<string> ServeOnce(int bytes)
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var ssl = new SslStream(client.GetStream());
            await ssl.AuthenticateAsServerAsync(serverCert);
            string got = Encoding.UTF8.GetString(await ReadAll(ssl, bytes));
            await ssl.ShutdownAsync();   // close_notify
            return got;
        }
        using var transport = new StreamSiemTransport(new SiemConfig { Host = "localhost", Port = port, Protocol = "tls", CaCertPath = caPath });

        var first = ServeOnce(7);
        await transport.SendAsync(new[] { "first" }, default);
        Assert.Equal("5 first", await first.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.Delay(300);

        var second = ServeOnce(8);
        await transport.SendAsync(new[] { "second" }, default);
        string got = await second.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();

        Assert.Equal("6 second", got);
    }

    [Fact]
    public async Task Tls_rejects_server_signed_by_other_ca()
    {
        var (_, server) = MakeCerts();
        var (otherCa, _) = MakeCerts();
        var (ok, received) = await TlsRoundTrip(otherCa, server);
        Assert.False(ok);
        Assert.Equal("", received);
    }
}
