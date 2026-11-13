using Serilog;
using Serilog.Extensions.Logging;
using Watcher.Uploader;

namespace Watcher.TerminalAgent;

// Этот агент — сам себе Uploader для Фазы 8 (не Watcher.Uploader, см.
// roadmap.md): он уже системный root-процесс с доступом и к queue/ (свои
// же файлы), и к raw-output/ (чужие 600-файлы вывода пользователей) —
// per-user Watcher.Uploader не смог бы прочитать второе. Здесь — только сборка
// потокового multipart; отправка, 401/409 — общий TerminalUploadClient из
// Watcher.Uploader.Core (тот же контракт у Windows-службы).
//
// Задача G1c: Bearer — из AgentIdentity (учётка вида terminal, общая с
// Watcher.Uploader логика регистрации и отзыва), ставится на каждый запрос.
public sealed class UploadClient
{
    private readonly TerminalUploadClient _core;

    public UploadClient(string serverUrl, HttpClient http, AgentIdentity identity)
    {
        _core = new TerminalUploadClient(serverUrl, http, identity,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    // Без регистрации — только общий токен (тесты, установки до G1).
    public UploadClient(string serverUrl, string apiToken)
    {
        var http = NewHttp();
        _core = new TerminalUploadClient(serverUrl, http,
            new AgentIdentity(new AgentIdentityOptions(serverUrl, "", apiToken, ""), http,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, Environment.MachineName, AgentIdentity.KindTerminal),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    public static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromMinutes(2) };

    public AgentIdentity Identity => _core.Identity;

    // 0.12: интервал отправки идущих сессий от сервера; null — сервер куски не
    // принимает (старый) или ещё не отвечал.
    public int? LiveIntervalSec => _core.LiveIntervalSec;
    public int? QueueMaxAgeDays => _core.QueueMaxAgeDays;

    // Кусок ввода (файл очереди с inputOffset/live) — ответ сервера важен.
    public Task<ChunkResult> UploadInputChunkAsync(string jsonPath, ILogger logger, CancellationToken ct)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StreamContent(File.OpenRead(jsonPath)), "meta", Path.GetFileName(jsonPath));
        return PostChunk(content, Path.GetFileName(jsonPath), logger, ct);
    }

    public Task<ChunkResult> UploadOutputChunkAsync(byte[] meta, string label, byte[] output, string outName,
        byte[] timing, string timingName, ILogger logger, CancellationToken ct)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(meta), "meta", label);
        content.Add(new ByteArrayContent(output), "out", outName);
        content.Add(new ByteArrayContent(timing), "timing", timingName);
        return PostChunk(content, label, logger, ct);
    }

    // Узнать, принимает ли сервер куски: сервер 0.12+ отвечает на пробу с
    // заголовком интервала, старый — 400 без него (и ничего не сохраняет).
    public Task ProbeAsync(string host, ILogger logger, CancellationToken ct)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent($"{{\"probe\":true,\"host\":{System.Text.Json.JsonSerializer.Serialize(host)}}}"), "meta", "probe.json");
        return PostChunk(content, "probe", logger, ct);
    }

    private async Task<ChunkResult> PostChunk(MultipartFormDataContent content, string label, ILogger logger, CancellationToken ct)
    {
        using var factory = new SerilogLoggerFactory(logger);
        return await _core.PostChunkAsync(content, label, factory.CreateLogger("Watcher.TerminalAgent.Upload"), ct);
    }

    public Task<bool> UploadInputAsync(string jsonPath, ILogger logger, CancellationToken ct)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StreamContent(File.OpenRead(jsonPath)), "meta", Path.GetFileName(jsonPath));
        return Post(content, Path.GetFileName(jsonPath), logger, ct);
    }

    // R5: потоки уже открыты вызывающим (UploadRunner) безопасным способом —
    // клиент сам по путям из raw-output/ ничего не открывает.
    public Task<bool> UploadOutputAsync(byte[] meta, string metaName, Stream output, string outName,
        Stream timing, string timingName, ILogger logger, CancellationToken ct)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(meta), "meta", metaName);
        content.Add(new StreamContent(output), "out", outName);
        content.Add(new StreamContent(timing), "timing", timingName);
        return Post(content, metaName, logger, ct);
    }

    private async Task<bool> Post(MultipartFormDataContent content, string label, ILogger logger, CancellationToken ct)
    {
        using var factory = new SerilogLoggerFactory(logger);
        return await _core.PostAsync(content, label, factory.CreateLogger("Watcher.TerminalAgent.Upload"), ct);
    }
}
