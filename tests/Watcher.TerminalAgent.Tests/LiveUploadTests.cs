using System.Net;
using System.Text;
using System.Text.Json;
using Watcher.TerminalAgent;
using Xunit;

namespace Watcher.TerminalAgent.Tests;

// Фейк сервера 0.12: отвечает с заголовком интервала и заданным телом,
// запоминает тела запросов (multipart целиком — проверяем по подстрокам meta).
file sealed class LiveFakeServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    public readonly List<string> Requests = new();
    public Func<string, string> Respond = _ => "{}";
    public string Url { get; }

    public LiveFakeServer()
    {
        using var s = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        s.Start();
        int port = ((IPEndPoint)s.LocalEndpoint).Port;
        s.Stop();
        Url = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _ = Loop();
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream)) body = await reader.ReadToEndAsync();
            lock (Requests) Requests.Add(body);
            ctx.Response.Headers["X-Watcher-Live-Interval"] = "60";
            byte[] resp = Encoding.UTF8.GetBytes(Respond(body));
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(resp);
            ctx.Response.Close();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Close(); } catch (HttpListenerException) { }
    }
}

public class LiveUploadTests
{
    private static TerminalAgentConfig NewConfig()
    {
        string root = Path.Combine(Path.GetTempPath(), "wta-live-" + Guid.NewGuid());
        var cfg = new TerminalAgentConfig
        {
            QueueDir = Path.Combine(root, "queue"),
            QueueSentDir = Path.Combine(root, "queue", "sent"),
            RawOutputDir = Path.Combine(root, "raw-output"),
            RawOutputSentDir = Path.Combine(root, "raw-output", "sent"),
            LiveStateDir = Path.Combine(root, "live-state"),
            UploadQuietSeconds = 0,
        };
        Directory.CreateDirectory(cfg.RawOutputDir);
        return cfg;
    }

    private static string StartMeta(string stem, int pid) => JsonSerializer.Serialize(new
    {
        kind = "terminal-output",
        sessionUser = Environment.UserName,
        startedAt = "2026-10-12T09:00:00Z",
        outFile = stem + ".out",
        timingFile = stem + ".timing",
        source = "linux-script",
        auditSession = 7,
        pid,
    });

    private static string Sizes(long outSize, long timingSize) => $"{{\"outSize\":{outSize},\"timingSize\":{timingSize}}}";

    [Fact]
    public async Task Running_session_goes_in_chunks_and_closes_by_json()
    {
        var cfg = NewConfig();
        string dir = cfg.RawOutputDir;
        File.WriteAllText(Path.Combine(dir, "u-1.start"), StartMeta("u-1", Environment.ProcessId));
        File.WriteAllText(Path.Combine(dir, "u-1.out"), "hello");
        File.WriteAllText(Path.Combine(dir, "u-1.timing"), "0.1 5\n0.2 1");   // последняя строка не дописана
        using var server = new LiveFakeServer();
        server.Respond = body => body.Contains("\"probe\":true") ? "{\"probe\":true}" : Sizes(5, 6);
        var runner = new UploadRunner(cfg, new UploadClient(server.Url, ""));

        await runner.RunOnceAsync(Serilog.Log.Logger, CancellationToken.None);
        Assert.Equal(2, server.Requests.Count);   // проба + первый кусок
        Assert.Contains("\"live\":true", server.Requests[1]);
        Assert.Contains("\"outOffset\":0", server.Requests[1]);
        Assert.Contains("0.1 5\n", server.Requests[1]);
        Assert.DoesNotContain("0.2 1", server.Requests[1]);   // .timing — только целые строки
        Assert.DoesNotContain("\"pid\"", server.Requests[1]);

        await runner.RunOnceAsync(Serilog.Log.Logger, CancellationToken.None);
        Assert.Equal(2, server.Requests.Count);   // интервал не прошёл

        File.AppendAllText(Path.Combine(dir, "u-1.out"), " world");
        File.WriteAllText(Path.Combine(dir, "u-1.timing"), "0.1 5\n0.2 6\n");
        File.WriteAllText(Path.Combine(dir, "u-1.json"), JsonSerializer.Serialize(new
        {
            kind = "terminal-output",
            sessionUser = Environment.UserName,
            startedAt = "2026-10-12T09:00:00Z",
            endedAt = "2026-10-12T09:10:00Z",
            exitCode = 0,
            outFile = "u-1.out",
            timingFile = "u-1.timing",
            auditSession = 7,
        }));
        server.Respond = _ => Sizes(11, 12);
        await runner.RunOnceAsync(Serilog.Log.Logger, CancellationToken.None);

        Assert.Equal(3, server.Requests.Count);
        Assert.Contains("\"live\":false", server.Requests[2]);
        Assert.Contains("\"outOffset\":5", server.Requests[2]);
        Assert.Contains(" world", server.Requests[2]);
        Assert.DoesNotContain("hello", server.Requests[2]);
        Assert.False(File.Exists(Path.Combine(dir, "u-1.start")));
        Assert.True(File.Exists(Path.Combine(cfg.RawOutputSentDir, "u-1.out")));
        Assert.Empty(Directory.GetFiles(cfg.LiveStateDir));
    }

    [Fact]
    public async Task Session_whose_shell_is_gone_without_json_is_closed_by_the_agent()
    {
        var cfg = NewConfig();
        string dir = cfg.RawOutputDir;
        // PID, которого нет: shell убит SIGHUP при обрыве SSH, .json не записан.
        File.WriteAllText(Path.Combine(dir, "u-2.start"), StartMeta("u-2", 4_000_000));
        File.WriteAllText(Path.Combine(dir, "u-2.out"), "partial");
        File.WriteAllText(Path.Combine(dir, "u-2.timing"), "0.1 7\n");
        using var server = new LiveFakeServer();
        server.Respond = body => body.Contains("\"probe\":true") ? "{}" : Sizes(7, 6);

        await new UploadRunner(cfg, new UploadClient(server.Url, "")).RunOnceAsync(Serilog.Log.Logger, CancellationToken.None);

        Assert.Contains(server.Requests, r => r.Contains("\"live\":false") && r.Contains("partial") && r.Contains("\"endedAt\""));
        Assert.False(File.Exists(Path.Combine(dir, "u-2.start")));
        Assert.True(File.Exists(Path.Combine(cfg.RawOutputSentDir, "u-2.out")));
    }

    [Fact]
    public async Task Old_server_without_live_support_gets_the_session_whole_at_the_end()
    {
        var cfg = NewConfig();
        string dir = cfg.RawOutputDir;
        File.WriteAllText(Path.Combine(dir, "u-3.start"), StartMeta("u-3", Environment.ProcessId));
        File.WriteAllText(Path.Combine(dir, "u-3.out"), "x");
        File.WriteAllText(Path.Combine(dir, "u-3.timing"), "0.1 1\n");
        using var listener = new HttpListener();
        using var s = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        s.Start(); int port = ((IPEndPoint)s.LocalEndpoint).Port; s.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var bodies = new List<string>();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch (Exception) { return; }
                using (var r = new StreamReader(ctx.Request.InputStream)) lock (bodies) bodies.Add(r.ReadToEnd());
                ctx.Response.StatusCode = 400;   // старый сервер: пробу не понимает, заголовка нет
                ctx.Response.Close();
            }
        });

        await new UploadRunner(cfg, new UploadClient($"http://127.0.0.1:{port}/", "")).RunOnceAsync(Serilog.Log.Logger, CancellationToken.None);

        Assert.Single(bodies);   // только проба, кусков нет
        Assert.Contains("\"probe\":true", bodies[0]);
        Assert.True(File.Exists(Path.Combine(dir, "u-3.start")));
    }

    [Fact]
    public void Collector_writes_input_chunks_while_the_session_runs()
    {
        string queue = Path.Combine(Path.GetTempPath(), "wta-liveq-" + Guid.NewGuid());
        int? interval = 60;
        var collector = new SessionCollector(queue, "srv-1", () => interval);
        collector.Feed(new AuditLine(AuditLineType.UserStart, 9, 1000, "alice", null, null));
        collector.Feed(new AuditLine(AuditLineType.Tty, 9, 1001, null, Convert.ToHexString(Encoding.UTF8.GetBytes("ls\r")), null));
        collector.FlushLive(1002);
        collector.Feed(new AuditLine(AuditLineType.Tty, 9, 1010, null, Convert.ToHexString(Encoding.UTF8.GetBytes("pwd\r")), null));
        collector.FlushLive(1030);   // интервал не прошёл
        collector.FlushLive(1070);
        collector.Feed(new AuditLine(AuditLineType.Tty, 9, 1080, null, Convert.ToHexString(Encoding.UTF8.GetBytes("exit\r")), null));
        collector.Feed(new AuditLine(AuditLineType.UserEnd, 9, 1090, null, null, null));

        var files = Directory.GetFiles(queue).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(3, files.Length);
        var chunks = files.Select(f => JsonDocument.Parse(File.ReadAllText(f)).RootElement).ToArray();
        Assert.Equal(new[] { "ls\r", "pwd\r", "exit\r" }, chunks.Select(c => c.GetProperty("input").GetString()));
        Assert.Equal(new[] { 0, 3, 7 }, chunks.Select(c => c.GetProperty("inputOffset").GetInt32()));
        Assert.Equal(new[] { true, true, false }, chunks.Select(c => c.GetProperty("live").GetBoolean()));
    }

    [Fact]
    public void Collector_without_live_support_writes_one_record_as_before()
    {
        string queue = Path.Combine(Path.GetTempPath(), "wta-liveq-" + Guid.NewGuid());
        var collector = new SessionCollector(queue, "srv-1", () => null);
        collector.Feed(new AuditLine(AuditLineType.UserStart, 9, 1000, "alice", null, null));
        collector.Feed(new AuditLine(AuditLineType.Tty, 9, 1001, null, Convert.ToHexString(Encoding.UTF8.GetBytes("ls\r")), null));
        collector.FlushLive(5000);
        collector.Feed(new AuditLine(AuditLineType.UserEnd, 9, 1090, null, null, null));

        var root = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(queue)))).RootElement;
        Assert.False(root.TryGetProperty("live", out _));
        Assert.Equal("ls\r", root.GetProperty("input").GetString());
    }

    // Обрыв SSH: ядро сбрасывает ввод уже после USER_END — он должен попасть в
    // ту же сессию (с пользователем и терминалом входа), а не в новую «unknown».
    [Fact]
    public void Input_flushed_after_user_end_joins_the_same_session()
    {
        string queue = Path.Combine(Path.GetTempPath(), "wta-liveq-" + Guid.NewGuid());
        var collector = new SessionCollector(queue, "srv-1", () => null, endGraceSeconds: 10);
        collector.Feed(new AuditLine(AuditLineType.UserStart, 5, 1000, "carol", null, null));
        collector.Feed(new AuditLine(AuditLineType.UserEnd, 5, 1015, null, null, null));
        collector.Feed(new AuditLine(AuditLineType.Tty, 5, 1016, null, Convert.ToHexString(Encoding.UTF8.GetBytes("echo before-drop\r")), null));
        collector.SweepAbandoned(1020, 86400);
        Assert.False(Directory.Exists(queue) && Directory.GetFiles(queue).Length > 0);   // ждём
        collector.SweepAbandoned(1026, 86400);

        var root = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(queue)))).RootElement;
        Assert.Equal("carol", root.GetProperty("sessionUser").GetString());
        Assert.Equal("echo before-drop\r", root.GetProperty("input").GetString());
        Assert.Equal(15, root.GetProperty("durationSec").GetDouble());
    }
}
