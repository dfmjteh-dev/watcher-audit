using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

// Задача G1: регистрация агента и выбор токена для запросов.
public sealed class AgentIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"watcher-identity-{Guid.NewGuid():N}");
    private readonly FakeServer _server = new();
    private DateTime _now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private UploaderConfig Cfg(string enrollToken = "") => new()
    {
        ServerUrl = "http://srv",
        EnrollToken = enrollToken,
        CredentialPath = Path.Combine(_dir, "agent", "credential.json"),
    };

    private AgentIdentity Identity(UploaderConfig cfg) =>
        new(cfg, new HttpClient(_server), NullLogger.Instance, "PC-1", now: () => _now);

    [Fact]
    public async Task Enrolls_once_saves_secret_and_uses_it()
    {
        var cfg = Cfg(enrollToken: "wenr_a");
        var id = Identity(cfg);
        Assert.Null(id.Bearer);

        await id.EnsureEnrolledAsync(CancellationToken.None);
        await id.EnsureEnrolledAsync(CancellationToken.None);

        Assert.Equal(1, _server.EnrollCalls);
        Assert.Equal("wagt_1", id.Bearer);
        Assert.Contains("\"host\":\"PC-1\"", _server.LastEnrollBody);
        Assert.Contains("\"kind\":\"video\"", _server.LastEnrollBody);
        // Перезапуск агента — учётка с диска, без повторной регистрации.
        var restarted = Identity(cfg);
        await restarted.EnsureEnrolledAsync(CancellationToken.None);
        Assert.Equal("wagt_1", restarted.Bearer);
        Assert.Equal(1, _server.EnrollCalls);
    }

    [Fact]
    public void Credential_file_is_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;
        var cfg = Cfg();
        CredentialStore.Save(cfg.CredentialPath, new StoredCredential("wagt_x", "PC-1", "video", "h", _now));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(cfg.CredentialPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(cfg.CredentialPath)!));
    }

    [Fact]
    public async Task Revoked_credential_does_not_reenroll_with_same_token()
    {
        var cfg = Cfg(enrollToken: "wenr_a");
        var id = Identity(cfg);
        await id.EnsureEnrolledAsync(CancellationToken.None);

        id.OnUnauthorized();
        _now = _now.AddHours(1);
        await id.EnsureEnrolledAsync(CancellationToken.None);

        Assert.Equal(1, _server.EnrollCalls);
        Assert.Equal("wagt_1", id.Bearer);
    }

    [Fact]
    public async Task New_enroll_token_after_revocation_registers_again()
    {
        var first = Identity(Cfg(enrollToken: "wenr_a"));
        await first.EnsureEnrolledAsync(CancellationToken.None);

        // Переустановка с новым токеном: агент поднимается с той же (отозванной) учёткой на диске.
        var reinstalled = Identity(Cfg(enrollToken: "wenr_b"));
        reinstalled.OnUnauthorized();
        await reinstalled.EnsureEnrolledAsync(CancellationToken.None);

        Assert.Equal(2, _server.EnrollCalls);
        Assert.Equal("wagt_2", reinstalled.Bearer);
    }

    [Fact]
    public async Task Refused_enroll_token_backs_off()
    {
        _server.RefuseEnroll = true;
        var id = Identity(Cfg(enrollToken: "wenr_expired"));

        await id.EnsureEnrolledAsync(CancellationToken.None);
        _now = _now.AddMinutes(5);
        await id.EnsureEnrolledAsync(CancellationToken.None);
        Assert.Equal(1, _server.EnrollCalls);
        Assert.Null(id.Bearer);

        _now = _now.AddMinutes(11);
        await id.EnsureEnrolledAsync(CancellationToken.None);
        Assert.Equal(2, _server.EnrollCalls);
    }

    [Fact]
    public async Task Unreachable_server_retries_next_minute()
    {
        _server.Throw = true;
        var id = Identity(Cfg(enrollToken: "wenr_a"));
        await id.EnsureEnrolledAsync(CancellationToken.None);
        _server.Throw = false;
        await id.EnsureEnrolledAsync(CancellationToken.None);
        Assert.Null(id.Bearer);

        _now = _now.AddMinutes(2);
        await id.EnsureEnrolledAsync(CancellationToken.None);
        Assert.Equal("wagt_1", id.Bearer);
    }

    [Fact]
    public async Task Uploader_marks_credential_rejected_on_401()
    {
        var cfg = Cfg(enrollToken: "wenr_a");
        var http = new HttpClient(_server);
        var id = new AgentIdentity(cfg, http, NullLogger.Instance, "PC-1", now: () => _now);
        await id.EnsureEnrolledAsync(CancellationToken.None);
        var uploader = new Uploader(cfg, NullLogger.Instance, http, id);
        _server.HeartbeatStatus = HttpStatusCode.Unauthorized;

        await uploader.SendHeartbeatAsync("PC-1", "1.0", 0, CancellationToken.None);

        Assert.Equal("Bearer wagt_1", _server.LastAuthorization);
        Assert.False(id.HasCredential);
    }

    // R7: сервер по этому заголовку видит ошибку часов станции.
    [Fact]
    public async Task Heartbeat_carries_agent_time_header()
    {
        var cfg = Cfg(enrollToken: "");
        var uploader = new Uploader(cfg, NullLogger.Instance, new HttpClient(_server));

        await uploader.SendHeartbeatAsync("PC-1", "1.0", 0, CancellationToken.None);

        Assert.NotNull(_server.LastAgentTime);
        var sent = DateTime.Parse(_server.LastAgentTime!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
        Assert.InRange(sent, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public int EnrollCalls;
        public string LastEnrollBody = "";
        public string? LastAuthorization;
        public string? LastAgentTime;
        public bool RefuseEnroll;
        public bool Throw;
        public HttpStatusCode HeartbeatStatus = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Throw) throw new HttpRequestException("connection refused");
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastAgentTime = request.Headers.TryGetValues(AgentClock.Header, out var t) ? t.Single() : null;
            if (request.RequestUri!.AbsolutePath == "/api/enroll")
            {
                EnrollCalls++;
                LastEnrollBody = await request.Content!.ReadAsStringAsync(ct);
                if (RefuseEnroll) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                string json = JsonSerializer.Serialize(new { secret = $"wagt_{EnrollCalls}", host = "PC-1", kind = "video" });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(HeartbeatStatus) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }
}
