using System.Net;
using System.Text.Json;
using Watcher.TerminalAgent;
using Xunit;

namespace Watcher.TerminalAgent.Tests;

// HttpListener как лёгкий fake-сервер вместо реального Watcher.Server —
// UploadRunner/UploadClient тестируются на реальном HTTP-обмене (не на
// замоканном интерфейсе), но без зависимости от целого ASP.NET Core проекта
// в этом тест-пакете.
file sealed class FakeUploadServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _loop;
    private readonly CancellationTokenSource _cts = new();
    public int RequestCount;
    public int ResponseStatusCode = 200;
    public string? LastAuthorization;
    public string? LastAgentTime;

    public string Url { get; }

    public FakeUploadServer()
    {
        int port = GetFreePort();
        Url = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _loop = Loop();
    }

    private static int GetFreePort()
    {
        using var s = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        s.Start();
        int port = ((System.Net.IPEndPoint)s.LocalEndpoint).Port;
        s.Stop();
        return port;
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            Interlocked.Increment(ref RequestCount);
            LastAuthorization = ctx.Request.Headers["Authorization"];
            LastAgentTime = ctx.Request.Headers[Watcher.Uploader.AgentClock.Header];
            await using var _ = ctx.Request.InputStream; // drain
            using var reader = new StreamReader(ctx.Request.InputStream);
            await reader.ReadToEndAsync();
            ctx.Response.StatusCode = ResponseStatusCode;
            ctx.Response.Close();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        // Close() сам останавливает приём. Stop() перед ним на Linux (управляемый
        // HttpListener) снимает префиксы, и Close() при повторном снятии иногда
        // заново ищет/занимает endpoint — в CI это падало «Address already in
        // use» уже после всех проверок теста. Сбой очистки фейкового сервера не
        // должен ронять тест.
        try { _listener.Close(); }
        catch (HttpListenerException) { }
    }
}

public class UploadRunnerTests
{
    // Владелец файлов в тестах — текущий пользователь, sessionUser обязан с ним
    // совпадать (R5).
    private static string OutputMeta(string stem, string? sessionUser = null, string? outFile = null) =>
        JsonSerializer.Serialize(new
        {
            kind = "terminal-output",
            sessionUser = sessionUser ?? Environment.UserName,
            outFile = outFile ?? stem + ".out",
            timingFile = stem + ".timing",
        });

    private static (TerminalAgentConfig cfg, string json, string @out, string timing) NewOutputRecord(string stem, string meta)
    {
        string root = Path.Combine(Path.GetTempPath(), "wta-upload-" + Guid.NewGuid());
        var cfg = NewConfig(root);
        Directory.CreateDirectory(cfg.RawOutputDir);
        string json = Path.Combine(cfg.RawOutputDir, stem + ".json");
        string @out = Path.Combine(cfg.RawOutputDir, stem + ".out");
        string timing = Path.Combine(cfg.RawOutputDir, stem + ".timing");
        File.WriteAllText(json, meta);
        File.WriteAllText(@out, "hello");
        File.WriteAllText(timing, "0.1 5");
        return (cfg, json, @out, timing);
    }

    private static async Task<int> RunAsync(TerminalAgentConfig cfg)
    {
        using var server = new FakeUploadServer();
        var runner = new UploadRunner(cfg, new UploadClient(server.Url));
        await runner.RunOnceAsync(Serilog.Log.Logger, CancellationToken.None);
        return server.RequestCount;
    }

    // R5: outFile из пользовательского .json раньше шёл в Path.Combine как есть —
    // абсолютный путь или ../ читался root'ом и уезжал в sent/.
    [Fact]
    public async Task Output_record_with_foreign_outFile_path_is_rejected_and_target_untouched()
    {
        string secret = Path.Combine(Path.GetTempPath(), "wta-secret-" + Guid.NewGuid());
        File.WriteAllText(secret, "root-only");
        var (cfg, json, _, _) = NewOutputRecord("u-1-1", OutputMeta("u-1-1", outFile: secret));

        int requests = await RunAsync(cfg);

        Assert.Equal(0, requests);
        Assert.True(File.Exists(json));
        Assert.Equal("root-only", File.ReadAllText(secret));
    }

    [Fact]
    public async Task Output_record_whose_out_is_a_symlink_is_rejected()
    {
        string secret = Path.Combine(Path.GetTempPath(), "wta-secret-" + Guid.NewGuid());
        File.WriteAllText(secret, "root-only");
        var (cfg, json, outPath, _) = NewOutputRecord("u-1-1", OutputMeta("u-1-1"));
        File.Delete(outPath);
        File.CreateSymbolicLink(outPath, secret);

        int requests = await RunAsync(cfg);

        Assert.Equal(0, requests);
        Assert.True(File.Exists(json));
        Assert.Equal("root-only", File.ReadAllText(secret));
    }

    [Fact]
    public async Task Output_record_whose_json_is_a_symlink_is_rejected()
    {
        var (cfg, json, _, _) = NewOutputRecord("u-1-1", OutputMeta("u-1-1"));
        string realJson = json + ".real";
        File.Move(json, realJson);
        File.CreateSymbolicLink(json, realJson);

        int requests = await RunAsync(cfg);

        Assert.Equal(0, requests);
    }

    // FIFO на месте .out: обычный open() заблокировал бы агент навсегда.
    [Fact]
    public async Task Output_record_whose_out_is_a_fifo_is_rejected_without_hanging()
    {
        var (cfg, _, outPath, _) = NewOutputRecord("u-1-1", OutputMeta("u-1-1"));
        File.Delete(outPath);
        using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", outPath)) mkfifo.WaitForExit();

        var run = RunAsync(cfg);
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.Equal(0, await run);
    }

    // sessionUser в .json пишет сам пользователь — он должен быть владельцем файлов.
    [Fact]
    public async Task Output_record_claiming_another_session_user_is_rejected()
    {
        string other = Environment.UserName == "root" ? "nobody" : "root";
        var (cfg, json, _, _) = NewOutputRecord("u-1-1", OutputMeta("u-1-1", sessionUser: other));

        int requests = await RunAsync(cfg);

        Assert.Equal(0, requests);
        Assert.True(File.Exists(json));
    }

    [Fact]
    public async Task Output_upload_paused_when_sent_dir_is_a_symlink()
    {
        var (cfg, json, _, _) = NewOutputRecord("u-1-1", OutputMeta("u-1-1"));
        string elsewhere = Path.Combine(Path.GetTempPath(), "wta-elsewhere-" + Guid.NewGuid());
        Directory.CreateDirectory(elsewhere);
        Directory.CreateSymbolicLink(cfg.RawOutputSentDir, elsewhere);

        int requests = await RunAsync(cfg);

        Assert.Equal(0, requests);
        Assert.True(File.Exists(json));
        Assert.Empty(Directory.GetFiles(elsewhere));
    }

    private static TerminalAgentConfig NewConfig(string root)
    {
        return new TerminalAgentConfig
        {
            QueueDir = Path.Combine(root, "queue"),
            QueueSentDir = Path.Combine(root, "queue", "sent"),
            RawOutputDir = Path.Combine(root, "raw-output"),
            RawOutputSentDir = Path.Combine(root, "raw-output", "sent"),
            UploadQuietSeconds = 0, // не ждём в тестах
        };
    }

    [Fact]
    public async Task Input_record_uploaded_successfully_moves_to_sent()
    {
        string root = Path.Combine(Path.GetTempPath(), "wta-upload-" + Guid.NewGuid());
        var cfg = NewConfig(root);
        Directory.CreateDirectory(cfg.QueueDir);
        string jsonPath = Path.Combine(cfg.QueueDir, "20261006-100000-1.json");
        File.WriteAllText(jsonPath, """{"kind":"terminal-input","host":"h","sessionId":1}""");

        using var server = new FakeUploadServer();
        var runner = new UploadRunner(cfg, new UploadClient(server.Url));
        await runner.RunOnceAsync(Serilog.Log.Logger, CancellationToken.None);

        Assert.False(File.Exists(jsonPath));
        Assert.True(File.Exists(Path.Combine(cfg.QueueSentDir, "20261006-100000-1.json")));
        Assert.Equal(1, server.RequestCount);
    }

    // R7: время агента — на каждой выгрузке (сервер по нему видит ошибку часов).
    [Fact]
    public async Task Upload_carries_agent_time_header()
    {
        string root = Path.Combine(Path.GetTempPath(), "wta-upload-" + Guid.NewGuid());
        var cfg = NewConfig(root);
        Directory.CreateDirectory(cfg.QueueDir);
        File.WriteAllText(Path.Combine(cfg.QueueDir, "a.json"), """{"kind":"terminal-input"}""");

        using var server = new FakeUploadServer();
        await new UploadRunner(cfg, new UploadClient(server.Url)).RunOnceAsync(Serilog.Log.Logger, CancellationToken.None);

        Assert.NotNull(server.LastAgentTime);
    }

    [Fact]
    public async Task Failed_upload_leaves_file_in_place_for_retry()
    {
        string root = Path.Combine(Path.GetTempPath(), "wta-upload-" + Guid.NewGuid());
        var cfg = NewConfig(root);
        Directory.CreateDirectory(cfg.QueueDir);
        string jsonPath = Path.Combine(cfg.QueueDir, "a.json");
        File.WriteAllText(jsonPath, """{"kind":"terminal-input"}""");

        using var server = new FakeUploadServer { ResponseStatusCode = 500 };
        var runner = new UploadRunner(cfg, new UploadClient(server.Url));
        await runner.RunOnceAsync(Serilog.Log.Logger, CancellationToken.None);

        Assert.True(File.Exists(jsonPath));
        Assert.Empty(Directory.GetFiles(cfg.QueueSentDir));
    }

    [Fact]
    public async Task Output_record_without_out_timing_siblings_is_skipped_not_moved()
    {
        string root = Path.Combine(Path.GetTempPath(), "wta-upload-" + Guid.NewGuid());
        var cfg = NewConfig(root);
        Directory.CreateDirectory(cfg.RawOutputDir);
        string jsonPath = Path.Combine(cfg.RawOutputDir, "u-1-1.json");
        File.WriteAllText(jsonPath, """{"kind":"terminal-output","outFile":"u-1-1.out","timingFile":"u-1-1.timing"}""");
        // .out/.timing отсутствуют намеренно

        using var server = new FakeUploadServer();
        var runner = new UploadRunner(cfg, new UploadClient(server.Url));
        await runner.RunOnceAsync(Serilog.Log.Logger, CancellationToken.None);

        Assert.True(File.Exists(jsonPath));
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task Output_record_with_siblings_uploads_all_three_and_moves_them()
    {
        string root = Path.Combine(Path.GetTempPath(), "wta-upload-" + Guid.NewGuid());
        var cfg = NewConfig(root);
        Directory.CreateDirectory(cfg.RawOutputDir);
        string jsonPath = Path.Combine(cfg.RawOutputDir, "u-1-1.json");
        string outPath = Path.Combine(cfg.RawOutputDir, "u-1-1.out");
        string timingPath = Path.Combine(cfg.RawOutputDir, "u-1-1.timing");
        File.WriteAllText(jsonPath, OutputMeta("u-1-1"));
        File.WriteAllText(outPath, "hello");
        File.WriteAllText(timingPath, "0.1 5");

        using var server = new FakeUploadServer();
        var runner = new UploadRunner(cfg, new UploadClient(server.Url));
        await runner.RunOnceAsync(Serilog.Log.Logger, CancellationToken.None);

        Assert.False(File.Exists(jsonPath));
        Assert.False(File.Exists(outPath));
        Assert.False(File.Exists(timingPath));
        Assert.True(File.Exists(Path.Combine(cfg.RawOutputSentDir, "u-1-1.json")));
        Assert.True(File.Exists(Path.Combine(cfg.RawOutputSentDir, "u-1-1.out")));
        Assert.True(File.Exists(Path.Combine(cfg.RawOutputSentDir, "u-1-1.timing")));
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task Fresh_file_not_yet_quiet_is_skipped_this_round()
    {
        string root = Path.Combine(Path.GetTempPath(), "wta-upload-" + Guid.NewGuid());
        var cfg = NewConfig(root);
        cfg.UploadQuietSeconds = 3600; // точно не "тихий" в рамках теста
        Directory.CreateDirectory(cfg.QueueDir);
        string jsonPath = Path.Combine(cfg.QueueDir, "fresh.json");
        File.WriteAllText(jsonPath, """{"kind":"terminal-input"}""");

        using var server = new FakeUploadServer();
        var runner = new UploadRunner(cfg, new UploadClient(server.Url));
        await runner.RunOnceAsync(Serilog.Log.Logger, CancellationToken.None);

        Assert.True(File.Exists(jsonPath));
        Assert.Equal(0, server.RequestCount);
    }

    // Задача G1c: с учёткой — Bearer её секрет, не общий токен; 401 — учётка
    // отклонена (отозвана), запись остаётся в очереди.
    [Fact]
    public async Task Upload_uses_agent_credential_and_marks_it_rejected_on_401()
    {
        string root = Path.Combine(Path.GetTempPath(), "wta-upload-" + Guid.NewGuid());
        var cfg = NewConfig(root);
        Directory.CreateDirectory(cfg.QueueDir);
        string jsonPath = Path.Combine(cfg.QueueDir, "20261006-100000-1.json");
        File.WriteAllText(jsonPath, """{"kind":"terminal-input","host":"h","sessionId":1}""");
        string credPath = Path.Combine(root, "agent", "credential.json");
        Watcher.Uploader.CredentialStore.Save(credPath, new Watcher.Uploader.StoredCredential("wagt_term", "h", "terminal", "x", DateTime.UtcNow));

        using var server = new FakeUploadServer { ResponseStatusCode = 401 };
        var http = UploadClient.NewHttp();
        var identity = new Watcher.Uploader.AgentIdentity(
            new Watcher.Uploader.AgentIdentityOptions(server.Url, "", credPath), http,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "h", Watcher.Uploader.AgentIdentity.KindTerminal);
        var runner = new UploadRunner(cfg, new UploadClient(server.Url, http, identity));
        await runner.RunOnceAsync(Serilog.Log.Logger, CancellationToken.None);

        Assert.Equal("Bearer wagt_term", server.LastAuthorization);
        Assert.False(identity.HasCredential);
        Assert.True(File.Exists(jsonPath));
    }
}
