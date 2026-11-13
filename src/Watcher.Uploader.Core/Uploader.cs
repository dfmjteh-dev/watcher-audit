using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Watcher.Uploader;

public sealed partial class Uploader
{
    private readonly UploaderConfig _cfg;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly AgentIdentity _identity;

    public Uploader(UploaderConfig cfg, ILogger logger, HttpClient? http = null, AgentIdentity? identity = null)
    {
        _cfg = cfg;
        _logger = logger;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _identity = identity ?? new AgentIdentity(cfg, _http, logger, Environment.MachineName);
    }

    public AgentIdentity Identity => _identity;

    // 0.14.2: предел тела запроса /api/upload, который сообщил сервер в heartbeat
    // (null — ещё не было heartbeat или сервер старше 0.14.2).
    public long? ServerMaxUploadBytes { get; private set; }

    // Multipart-обёртка, sha256/size и .json поверх самого mp4 — с запасом.
    private const long MultipartOverheadBytes = 64 * 1024;

    // Файл, который сервер не примет никогда: не отправляем его, а то он
    // гоняется по сети заново на каждом проходе и держит за собой всю очередь.
    public static bool ExceedsLimit(long mp4Bytes, long jsonBytes, long? maxUploadBytes) =>
        maxUploadBytes is > 0 and var max && mp4Bytes + jsonBytes + MultipartOverheadBytes > max;

    // Задача G1: заголовок — на каждый запрос, не DefaultRequestHeaders: секрет
    // появляется после регистрации, уже на работающем клиенте.
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent content, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url) { Content = content };
        AgentClock.Stamp(req);
        if (url.EndsWith("/api/upload", StringComparison.Ordinal)) req.Headers.Add("X-Watcher-Video-Validation", "1");
        if (_identity.Bearer is { } bearer)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) _identity.OnUnauthorized();
        AgentRemoval.Check(resp, _logger);
        return resp;
    }

    public async Task<UploadOutcome> UploadAsync(string mp4Path, string jsonPath, CancellationToken ct)
    {
        var fi = new FileInfo(mp4Path);
        if (!fi.Exists || fi.Length == 0)
        {
            _logger.LogWarning("skip empty/missing: {File}", Path.GetFileName(mp4Path));
            return UploadOutcome.Failed;
        }
        long jsonBytes = File.Exists(jsonPath) ? new FileInfo(jsonPath).Length : 0;
        if (ExceedsLimit(fi.Length, jsonBytes, ServerMaxUploadBytes))
        {
            _logger.LogWarning("too large for server: {File} ({Size:N0} B, server limit {Max:N0} B)",
                Path.GetFileName(mp4Path), fi.Length, ServerMaxUploadBytes);
            return UploadOutcome.TooLarge;
        }

        string sha256 = await ComputeSha256Async(mp4Path, ct);
        string url = $"{_cfg.ServerUrl.TrimEnd('/')}/api/upload";

        await using var videoStream = File.OpenRead(mp4Path);
        await using Stream metaStream = File.Exists(jsonPath)
            ? File.OpenRead(jsonPath)
            : new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{}"));

        using var content = new MultipartFormDataContent();
        var videoPart = new StreamContent(videoStream);
        videoPart.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        content.Add(videoPart, "video", Path.GetFileName(mp4Path));

        var metaPart = new StreamContent(metaStream);
        metaPart.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(metaPart, "meta", Path.GetFileName(jsonPath));

        content.Add(new StringContent(sha256), "sha256");
        content.Add(new StringContent(fi.Length.ToString()), "size");

        using var resp = await SendAsync(HttpMethod.Post, url, content, ct);
        if (resp.IsSuccessStatusCode)
        {
            _logger.LogInformation("uploaded {File} ({Size:N0} B)", Path.GetFileName(mp4Path), fi.Length);
            return UploadOutcome.Uploaded;
        }
        if ((int)resp.StatusCode == 409)
        {
            _logger.LogInformation("already on server: {File}", Path.GetFileName(mp4Path));
            return UploadOutcome.Uploaded;
        }
        if ((int)resp.StatusCode == 422)
        {
            // Имя занято ДРУГОЙ записью (sha256 не совпал, см. сервер /api/upload) —
            // это не "уже выгружено", удалять нельзя. Вызывающий код переименует
            // пару файлов и отправит заново (docs/code-review-2026-10.md, R2).
            _logger.LogWarning("filename collision on server: {File}, will rename and retry", Path.GetFileName(mp4Path));
            return UploadOutcome.NameCollision;
        }
        if (resp.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge)
        {
            // Сервер старше 0.14.2 (предел в heartbeat не сообщает) — тот же исход.
            _logger.LogWarning("server rejected {File} as too large (413, {Size:N0} B)", Path.GetFileName(mp4Path), fi.Length);
            return UploadOutcome.TooLarge;
        }
        string body = await resp.Content.ReadAsStringAsync(ct);
        if ((int)resp.StatusCode == 415)
        {
            try
            {
                using var detail = JsonDocument.Parse(body);
                if (detail.RootElement.ValueKind == JsonValueKind.Object && detail.RootElement.TryGetProperty("code", out var code)
                    && code.ValueKind == JsonValueKind.String && code.GetString() == "invalid-video")
                    return UploadOutcome.Rejected;
            }
            catch (JsonException) { }
        }
        _logger.LogWarning("upload failed {StatusCode}: {Body}", (int)resp.StatusCode, body.Truncate(200));
        return UploadOutcome.Failed;
    }

    // mp4 и его .json переименовываются парой (Program.cs ищет sidecar как mp4 + ".json").
    // Случайный суффикс, не счётчик: коллизия может повториться с тем же счётчиком у
    // другой машины с тем же именем.
    public static string RenameForCollision(string mp4Path)
    {
        string dir = Path.GetDirectoryName(mp4Path)!;
        string stem = Path.GetFileNameWithoutExtension(mp4Path);
        string suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
        string newMp4 = Path.Combine(dir, $"{stem}_c{suffix}.mp4");
        File.Move(mp4Path, newMp4);
        string json = mp4Path + ".json";
        if (File.Exists(json)) File.Move(json, newMp4 + ".json");
        return newMp4;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // H2: скачать пакет обновления (url — относительный от сервера) по своей
    // учётке; файл пишется через .part, возвращается SHA-256 скачанного.
    public async Task<string> DownloadAsync(string url, string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        ct = timeout.Token;
        string full = $"{_cfg.ServerUrl.TrimEnd('/')}/{url.TrimStart('/')}";
        using var req = new HttpRequestMessage(HttpMethod.Get, full);
        if (_identity.Bearer is { } bearer)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        string part = path + ".part";
        try
        {
            if (resp.Content.Headers.ContentLength is > AgentUpdater.MaxPackageSize) throw new IOException("Update package exceeds size limit");
            long total = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(part))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    total += n;
                    if (total > AgentUpdater.MaxPackageSize) throw new IOException("Update package exceeds size limit");
                    hash.AppendData(buf, 0, n);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            if (resp.Content.Headers.ContentLength is long length && total != length) throw new IOException("Incomplete update download");
            File.Move(part, path, overwrite: true);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }

    // /api/heartbeat встроен в Uploader (не отдельный процесс/служба, см. roadmap Фаза 3) —
    // он уже держит HTTP-канал и ApiToken к серверу, заводить второй канал ради heartbeat
    // избыточно. Отдельный плюс: Linux-агент (Фаза 7) получает heartbeat бесплатно, просто
    // переиспользуя тот же Uploader, без своего кода.
    //
    // Фаза 4: ответ heartbeat несёт {configVersion, config} — последний валидный конфиг
    // (с хоста или его группы) кэшируется в ConfigCachePath. Кэш обновляется только при
    // успешном heartbeat с непустым config — недоступность сервера не откатывает станцию
    // на дефолт (см. roadmap), просто оставляет последний закэшированный файл как есть.
    // Применение самого конфига Capture'ом — не в этой фазе, см. architecture.md.
    // H2: updater — платформа и итог обновления уходят в heartbeat, предложенный
    // сервером пакет передаётся ему (null — станция не обновляется).
    // 0.13: configHash — хэш конфига в кэше (что у станции сейчас), captures —
    // отметки процессов записи экрана (CaptureStatus), losses — журнал потерь;
    // сервер отвечает configHash текущего конфига (кладём в кэш — его видят
    // процессы записи) и lossesAccepted (столько событий журнала можно удалить).
    public async Task SendHeartbeatAsync(string host, string version, int droppedSegments, CancellationToken ct, AgentUpdater? updater = null,
        LossJournal? losses = null, AgentResources? resources = null)
    {
        try
        {
            string url = $"{_cfg.ServerUrl.TrimEnd('/')}/api/heartbeat";
            string? ip = await LocalAddress.TowardsServerAsync(_cfg.ServerUrl, ct);
            var pending = losses?.Pending(MaxLossesPerHeartbeat);
            var body = new
            {
                host,
                ip,
                version,
                droppedSegments,
                platform = updater?.Platform,
                updateStatus = updater?.StatusForHeartbeat(),
                configHash = CachedConfigHash(_cfg.ConfigCachePath),
                captures = CaptureStatus.Collect(_cfg.QueueDir, DateTime.UtcNow),
                losses = pending,
                // 0.13: CPU/память процессов агента и очередь (AgentResources).
                resources = resources?.Collect(DateTime.UtcNow),
            };
            using var resp = await SendAsync(HttpMethod.Post, url, JsonContent.Create(body), ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("heartbeat failed: {StatusCode}", (int)resp.StatusCode);
                return;
            }

            var payload = await resp.Content.ReadFromJsonAsync<HeartbeatResponse>(cancellationToken: ct);
            if (payload?.Update is { } offer && updater is not null)
                updater.OnOffer(offer, DownloadAsync, ct);
            if (payload?.MaintenanceKey is { } maintenanceKey && !(updater?.Busy ?? false))
                ProtectionActivation.Offer(maintenanceKey, _cfg, _logger, DateTime.UtcNow);
            // Сервер старше 0.13 поля не знает — журнал остаётся до нового сервера.
            if (payload?.MaxUploadBytes is > 0 and var maxUpload) ServerMaxUploadBytes = maxUpload;
            if (payload?.LossesAccepted is { } accepted && losses is not null && pending is not null)
                losses.Acknowledge(Math.Min(accepted, pending.Count));

            if (string.IsNullOrWhiteSpace(_cfg.ConfigCachePath)) return;
            if (payload is null || payload.Config.ValueKind != JsonValueKind.Object) return;

            string cached = WithConfigHash(payload.Config, payload.ConfigHash);
            // Не трогаем файл, если ничего не изменилось: процессы записи
            // перечитывают его по времени изменения.
            if (File.Exists(_cfg.ConfigCachePath) && await File.ReadAllTextAsync(_cfg.ConfigCachePath, ct) == cached) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_cfg.ConfigCachePath)!);
            string tmp = _cfg.ConfigCachePath + ".tmp";
            await File.WriteAllTextAsync(tmp, cached, ct);
            File.Move(tmp, _cfg.ConfigCachePath, overwrite: true);
            _logger.LogInformation("config cache updated to version {Version} ({Hash})", payload.ConfigVersion, payload.ConfigHash ?? "-");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Пропущенный heartbeat не должен останавливать основной цикл выгрузки видео —
            // сервер и так считает агента offline по истечении интервала, это не фатально.
            _logger.LogWarning(ex, "heartbeat error");
        }
    }
}

public sealed partial class Uploader
{
    private const int MaxLossesPerHeartbeat = 200;   // как AgentEndpoints.MaxLossesPerHeartbeat на сервере
    public const string ConfigHashKey = "configHash";

    // Конфиг с сервера + его хэш одним файлом: процесс записи читает их вместе,
    // без гонки двух файлов. Лишний ключ процессы записи игнорируют.
    public static string WithConfigHash(JsonElement config, string? hash)
    {
        if (hash is null) return config.GetRawText();
        var obj = System.Text.Json.Nodes.JsonObject.Create(config)!;
        obj[ConfigHashKey] = hash;
        return obj.ToJsonString();
    }

    public static string? CachedConfigHash(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(ConfigHashKey, out var h)
                && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}

internal static class StrExt
{
    public static string Truncate(this string s, int max)
        => s.Length <= max ? s : s.Substring(0, max);
}

public enum UploadOutcome
{
    Uploaded,
    Failed,
    NameCollision,
    // Больше предела сервера — не примется никогда, вызывающий код удаляет файл.
    TooLarge,
    Rejected,
}

internal sealed record HeartbeatResponse(int? ConfigVersion, JsonElement Config, UpdateOffer? Update = null,
    string? ConfigHash = null, int? LossesAccepted = null, long? MaxUploadBytes = null, MaintenanceKeyOffer? MaintenanceKey = null);
