using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Watcher.Uploader;

// Задача F1 (roadmap-next.md): выгрузка записи консоли на /api/upload-terminal —
// для Windows-службы (транскрипты PowerShell). Учётка вида terminal
// (AgentIdentity), 409 = уже на сервере = успех — тот же контракт, что у
// Linux TerminalAgent (его UploadClient).
public sealed class TerminalUploadClient
{
    private readonly string _serverUrl;
    private readonly HttpClient _http;
    private readonly AgentIdentity _identity;
    private readonly ILogger _logger;

    public TerminalUploadClient(string serverUrl, HttpClient http, AgentIdentity identity, ILogger logger)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _http = http;
        _identity = identity;
        _logger = logger;
    }

    public AgentIdentity Identity => _identity;

    // files: (поле формы, имя файла, содержимое) — "out" у terminal-output.
    public Task<bool> UploadAsync(string metaJson, string label, IEnumerable<(string Field, string FileName, byte[] Content)> files, CancellationToken ct)
    {
        return PostAsync(Multipart(metaJson, files), label, _logger, ct);
    }

    // 0.12: кусок идущей сессии — ответ сервера (принятые размеры) нужен.
    public Task<ChunkResult> UploadChunkAsync(string metaJson, string label, IEnumerable<(string Field, string FileName, byte[] Content)> files, CancellationToken ct) =>
        PostChunkAsync(Multipart(metaJson, files), label, _logger, ct);

    // Спросить, принимает ли сервер куски (ответ — заголовок интервала, LiveIntervalSec).
    public Task ProbeAsync(string host, CancellationToken ct) =>
        PostChunkAsync(Multipart($"{{\"probe\":true,\"host\":{System.Text.Json.JsonSerializer.Serialize(host)}}}", []), "probe", _logger, ct);

    private static MultipartFormDataContent Multipart(string metaJson, IEnumerable<(string Field, string FileName, byte[] Content)> files)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(metaJson), "meta", "meta.json");
        foreach (var (field, fileName, bytes) in files)
            content.Add(new ByteArrayContent(bytes), field, fileName);
        return content;
    }

    // 0.12: сервер принимает незакрытые сессии кусками и сообщает интервал
    // (заголовок X-Watcher-Live-Interval в ответе на любую загрузку). null —
    // ответа с ним ещё не было: сервер старый или ещё не спрашивали, сессии
    // шлются только целиком, как до 0.12.
    public int? LiveIntervalSec { get; private set; }

    // 0.13: срок хранения невыгруженного (заголовок X-Watcher-Queue-Max-Age, дни;
    // 0 — без срока). null — сервер его не присылал (старый или ещё не отвечал).
    public int? QueueMaxAgeDays { get; private set; }

    // Общая часть с Linux TerminalAgent (его UploadClient собирает потоковый
    // multipart сам): Bearer, метка времени станции, 401 → перерегистрация,
    // 409 = уже на сервере = успех. Забирает content и освобождает его.
    public async Task<bool> PostAsync(HttpContent content, string label, ILogger logger, CancellationToken ct)
    {
        var r = await PostChunkAsync(content, label, logger, ct);
        return r.Outcome is ChunkOutcome.Accepted or ChunkOutcome.Closed;
    }

    // Кусок идущей сессии: Accepted — принят; Closed (409) — запись на сервере
    // уже закрыта, слать больше нечего; Gap (416) — сервер принял меньше, чем
    // думает агент, в Body принятые размеры; Failed — повторить позже.
    public async Task<ChunkResult> PostChunkAsync(HttpContent content, string label, ILogger logger, CancellationToken ct)
    {
        using (content)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{_serverUrl}/api/upload-terminal") { Content = content };
                AgentClock.Stamp(req);
                if (_identity.Bearer is { } bearer) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                using var resp = await _http.SendAsync(req, ct);
                if (resp.Headers.TryGetValues(LiveIntervalHeader, out var values)
                    && int.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int sec)
                    && sec >= 10 && sec <= 3600)   // AgentConfigLimits.ConsoleLiveIntervalSec
                    LiveIntervalSec = sec;
                if (resp.Headers.TryGetValues(QueueMaxAgeHeader, out var ageValues)
                    && int.TryParse(ageValues.FirstOrDefault(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int days)
                    && days >= QueueAge.MinDays && days <= QueueAge.MaxDays)
                    QueueMaxAgeDays = days;
                if (resp.StatusCode == HttpStatusCode.Unauthorized) _identity.OnUnauthorized();
                AgentRemoval.Check(resp, logger);
                string body = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    // skipped — пользователь исключён в конфиге группы/хоста, сервер запись не сохранил.
                    if (body.Contains("\"skipped\":true", StringComparison.Ordinal))
                        logger.LogInformation("not stored by server (user excluded by config): {Label}", label);
                    else
                        logger.LogInformation("uploaded {Label}", label);
                    return new ChunkResult(ChunkOutcome.Accepted, Parse(body));
                }
                if (resp.StatusCode == HttpStatusCode.Conflict)
                {
                    logger.LogInformation("already on server: {Label}", label);
                    return new ChunkResult(ChunkOutcome.Closed, null);
                }
                if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    logger.LogWarning("server has less of {Label} than sent before: {Body}", label, body.Length > 200 ? body[..200] : body);
                    return new ChunkResult(ChunkOutcome.Gap, Parse(body));
                }
                logger.LogWarning("upload failed {Status} for {Label}: {Body}", (int)resp.StatusCode, label, body.Length > 200 ? body[..200] : body);
                return new ChunkResult(ChunkOutcome.Failed, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "upload error for {Label}", label);
                return new ChunkResult(ChunkOutcome.Failed, null);
            }
        }
    }

    public const string LiveIntervalHeader = "X-Watcher-Live-Interval";
    public const string QueueMaxAgeHeader = "X-Watcher-Queue-Max-Age";

    private static System.Text.Json.JsonElement? Parse(string body)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}

public enum ChunkOutcome { Accepted, Closed, Gap, Failed }

public sealed record ChunkResult(ChunkOutcome Outcome, System.Text.Json.JsonElement? Body)
{
    public long Long(string name) =>
        Body is { ValueKind: System.Text.Json.JsonValueKind.Object } b && b.TryGetProperty(name, out var v) && v.TryGetInt64(out long n) ? n : 0;
}
