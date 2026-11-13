using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Watcher.Server;

// Обновление сервера из веб-UI. Сервер (в Docker — непривилегированный
// контейнер) сам себя не ставит: он только кладёт архив релиза и заявку в общий
// каталог Dir, а ставит помощник на хосте (tools/server-update/server-update.sh,
// systemd-юнит watcher-server-update от root) — тем же install-server.sh из
// архива, что и при ручном обновлении. Статус и журнал помощник пишет туда же.
//
// Раскладка Dir: incoming/<id>.tar.gz — архив; requests/<id>.json — заявка
// (помощника будит непустой requests/); status.json и logs/<id>.log — от помощника;
// uploads/<id>.json — загрузка из браузера по частям (0.14.3), её часть —
// incoming/<id>.tar.gz.part.
//
// 0.14.3: загрузка по частям. Архив одним запросом обрывался перезагрузкой или
// закрытием страницы (тело шлёт браузер), за NPM прогресс браузера показывал
// 100% задолго до сервера (прокси буферизует тело), а проверка архива шла до
// ответа и упиралась в таймаут прокси. Теперь: части по ChunkBytes, сервер
// принимает только следующую по счёту и сам говорит, сколько у него есть, —
// после перезагрузки (и перезапуска сервера) загрузка продолжается с того же
// места; последняя часть — ответ сразу, проверка и заявка помощнику фоном.
//
// Подпись архива не проверяется (решение владельца): с GitHub — sha256 из
// SHA256SUMS релиза (битый/недокачанный файл), ручная загрузка — как есть.
// Поэтому только admin и каждое действие — в журнал доступа.
public sealed class ServerUpdateConfig
{
    // Пусто — обновление из веб-UI выключено (помощник не установлен).
    // Docker — /updates (docker-compose.yml), systemd — install-server.sh.
    public string Dir { get; set; } = "";
    // owner/repo публичного репозитория с релизами.
    public string GitHubRepo { get; set; } = "dfmjteh-dev/watcher-audit";
    public int MaxUploadMb { get; set; } = 4096;
}

public sealed record UploadSession(string Id, string Name, long Size, long Received, string CreatedBy, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record ServerRelease(string Version, string Tag, DateTime? PublishedAt, string? Notes,
    string ArchiveUrl, long ArchiveSize, string? SumsUrl, string HtmlUrl);

public sealed class ServerUpdateService
{
    public const string ArchivePrefix = "watcher-server-";
    private static readonly Regex VersionRx = new(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);

    private readonly ServerConfig _cfg;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ServerUpdateService> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly SemaphoreSlim _chunks = new(1, 1);
    public const int ChunkBytes = 8 * 1024 * 1024;
    public const int MaxChunkBytes = 16 * 1024 * 1024;
    // Незаконченная загрузка без новых частей дольше — брошена, удаляется.
    public static readonly TimeSpan UploadStale = TimeSpan.FromHours(24);
    private readonly object _lock = new();
    private string _phase = "idle";
    private string? _phaseVersion, _error;
    private long _bytes, _total;

    public ServerUpdateService(ServerConfig cfg, IHttpClientFactory http, ILogger<ServerUpdateService> log)
    {
        _cfg = cfg;
        _http = http;
        _log = log;
    }

    private ServerUpdateConfig C => _cfg.ServerUpdate;
    public bool Enabled => !string.IsNullOrWhiteSpace(C.Dir) && Directory.Exists(C.Dir);
    public string CurrentVersion { get; init; } = AppVersion.Core(AppVersion.Current);

    public static bool IsVersion(string? v) => v is not null && VersionRx.IsMatch(v);

    // Только вперёд: сервер старше схемы БД не стартует (DbMigrations), откат
    // через веб оставил бы его лежать. Та же версия — переустановка, можно.
    public static bool IsAllowedTarget(string target, string current) =>
        Version.TryParse(target, out var t) && Version.TryParse(current, out var c) && t >= c;

    public object PhaseSnapshot()
    {
        lock (_lock) return new { phase = _phase, version = _phaseVersion, bytes = _bytes, total = _total, error = _error };
    }

    private void SetPhase(string phase, string? version = null, long bytes = 0, long total = 0, string? error = null)
    {
        lock (_lock) { _phase = phase; _phaseVersion = version; _bytes = bytes; _total = total; _error = error; }
    }

    private void Progress(long bytes) { lock (_lock) _bytes = bytes; }

    // status.json помощника; нет файла или битый — null.
    public JsonElement? HelperStatus()
    {
        if (!Enabled) return null;
        string f = Path.Combine(C.Dir, "status.json");
        try { return File.Exists(f) ? JsonDocument.Parse(File.ReadAllText(f)).RootElement.Clone() : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    // Хвост журнала установки — по id из status.json (имя проверено: только [a-z0-9]).
    public string? HelperLogTail(string? id, int maxBytes = 16384)
    {
        if (!Enabled || id is null || !Regex.IsMatch(id, "^[a-z0-9]{1,64}$")) return null;
        string f = Path.Combine(C.Dir, "logs", id + ".log");
        try
        {
            if (!File.Exists(f)) return null;
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length > maxBytes) fs.Seek(-maxBytes, SeekOrigin.End);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public bool RequestPending() =>
        Enabled && Directory.Exists(Path.Combine(C.Dir, "requests")) && Directory.EnumerateFiles(Path.Combine(C.Dir, "requests"), "*.json").Any();

    private HttpClient Client()
    {
        var c = _http.CreateClient("server-update");
        c.DefaultRequestHeaders.UserAgent.ParseAdd("watcher-server/" + CurrentVersion);
        return c;
    }

    public async Task<ServerRelease?> LatestReleaseAsync(CancellationToken ct)
    {
        using var c = Client();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{C.GitHubRepo}/releases/latest");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await c.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"GitHub: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return ParseRelease(doc.RootElement);
    }

    public static ServerRelease? ParseRelease(JsonElement r)
    {
        string tag = r.GetProperty("tag_name").GetString() ?? "";
        string version = tag.TrimStart('v');
        if (!IsVersion(version)) return null;
        string archiveName = $"{ArchivePrefix}{version}.tar.gz";
        string? archiveUrl = null, sumsUrl = null;
        long size = 0;
        foreach (var a in r.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            if (name == archiveName) { archiveUrl = a.GetProperty("browser_download_url").GetString(); size = a.GetProperty("size").GetInt64(); }
            else if (name == "SHA256SUMS") sumsUrl = a.GetProperty("browser_download_url").GetString();
        }
        if (archiveUrl is null) return null;
        DateTime? published = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTime() : null;
        string? notes = r.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        string html = r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        return new ServerRelease(version, tag, published, notes, archiveUrl, size, sumsUrl, html);
    }

    // Строка «<sha256>  <имя>» из SHA256SUMS для файла name.
    public static string? ShaFromSums(string sums, string name)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == name && Regex.IsMatch(parts[0], "^[0-9a-fA-F]{64}$"))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    public enum Outcome { Ok, Disabled, Busy, Pending, BadArchive, Downgrade, ShaMismatch, NoSums, Failed }
    public sealed record Result(Outcome Outcome, string? Version = null, string? Error = null);

    // С GitHub: скачать архив последнего релиза, сверить с SHA256SUMS, заявка.
    // Долго (сотни МБ) — запускается фоном, ход виден в PhaseSnapshot.
    public async Task<Result> InstallFromGitHubAsync(string requestedBy, CancellationToken ct)
    {
        if (!Enabled) return new(Outcome.Disabled);
        if (RequestPending()) return new(Outcome.Pending);
        if (ActiveUpload() is not null) return new(Outcome.Busy);   // не закончена загрузка из браузера
        if (!await _busy.WaitAsync(0, ct)) return new(Outcome.Busy);
        string? part = null;
        try
        {
            SetPhase("checking");
            var rel = await LatestReleaseAsync(ct);
            if (rel is null) { SetPhase("failed", error: "в последнем релизе нет архива сервера"); return new(Outcome.BadArchive); }
            if (!IsAllowedTarget(rel.Version, CurrentVersion)) { SetPhase("idle"); return new(Outcome.Downgrade, rel.Version); }
            if (rel.SumsUrl is null) { SetPhase("failed", rel.Version, error: "в релизе нет SHA256SUMS"); return new(Outcome.NoSums, rel.Version); }

            using var c = Client();
            c.Timeout = Timeout.InfiniteTimeSpan;
            string sums = await c.GetStringAsync(rel.SumsUrl, ct);
            string? expected = ShaFromSums(sums, $"{ArchivePrefix}{rel.Version}.tar.gz");
            if (expected is null) { SetPhase("failed", rel.Version, error: "архива нет в SHA256SUMS"); return new(Outcome.NoSums, rel.Version); }

            string id = NewId();
            part = IncomingPath(id) + ".part";
            SetPhase("downloading", rel.Version, 0, rel.ArchiveSize);
            string sha;
            using (var resp = await c.GetAsync(rel.ArchiveUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                sha = await CopyHashedAsync(src, part, ct);
            }
            if (sha != expected)
            {
                SetPhase("failed", rel.Version, error: "sha256 скачанного архива не совпал с SHA256SUMS");
                return new(Outcome.ShaMismatch, rel.Version);
            }
            return await QueueAsync(id, part, sha, "github", requestedBy, rel.Version, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _log.LogWarning("server update from GitHub failed: {Err}", ex.Message);
            SetPhase("failed", error: ex.Message);
            return new(Outcome.Failed, Error: ex.Message);
        }
        finally
        {
            if (part is not null) BlobFiles.TryDelete(part);
            _busy.Release();
        }
    }

    // Ручная загрузка: поток тела запроса → incoming, версия — из VERSION в архиве.
    public async Task<Result> InstallFromUploadAsync(Stream body, string requestedBy, CancellationToken ct)
    {
        if (!Enabled) return new(Outcome.Disabled);
        if (RequestPending()) return new(Outcome.Pending);
        if (!await _busy.WaitAsync(0, ct)) return new(Outcome.Busy);
        string? part = null;
        try
        {
            string id = NewId();
            part = IncomingPath(id) + ".part";
            SetPhase("uploading");
            string sha = await CopyHashedAsync(body, part, ct, C.MaxUploadMb * 1024L * 1024L);
            SetPhase("verifying");
            string? version = ReadArchiveVersion(part);
            if (version is null) { SetPhase("failed", error: "это не архив сервера watcher (нет watcher-server-X.Y.Z/VERSION)"); return new(Outcome.BadArchive); }
            if (!IsAllowedTarget(version, CurrentVersion)) { SetPhase("idle"); return new(Outcome.Downgrade, version); }
            return await QueueAsync(id, part, sha, "upload", requestedBy, version, ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or TaskCanceledException or OperationCanceledException)
        {
            _log.LogWarning("server update upload failed: {Err}", ex.Message);
            SetPhase("failed", error: ex.Message);
            return new(Outcome.Failed, Error: ex.Message);
        }
        finally
        {
            if (part is not null) BlobFiles.TryDelete(part);
            _busy.Release();
        }
    }

    // Версия из архива: все записи — внутри одной папки watcher-server-X.Y.Z/,
    // в ней VERSION с тем же X.Y.Z и install-server.sh. Иначе null. Помощник
    // проверяет то же самое ещё раз перед распаковкой.
    public static string? ReadArchiveVersion(string path)
    {
        try { return ReadArchiveVersionCore(path); }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException) { return null; }
    }

    private static string? ReadArchiveVersionCore(string path)
    {
        string? top = null, version = null;
        bool installer = false;
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } e)
        {
            string name = e.Name.Replace('\\', '/');
            if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            if (name.Length == 0) continue;
            if (name.StartsWith('/') || name.Split('/').Contains("..")) return null;
            string first = name.Split('/')[0];
            top ??= first;
            if (first != top) return null;
            string rest = name.Length > first.Length ? name[(first.Length + 1)..] : "";
            if (rest == "install-server.sh") installer = true;
            if (rest == "VERSION" && e.DataStream is not null)
            {
                // leaveOpen: поток записи закрывает сам TarReader при переходе к следующей.
                using var sr = new StreamReader(e.DataStream, leaveOpen: true);
                version = sr.ReadToEnd().Trim();
            }
        }
        if (top is null || version is null || !installer || !IsVersion(version) || top != ArchivePrefix + version) return null;
        return version;
    }

    private async Task<Result> QueueAsync(string id, string part, string sha, string source, string requestedBy, string version, CancellationToken ct)
    {
        string archive = IncomingPath(id);
        File.Move(part, archive);
        string reqDir = Path.Combine(C.Dir, "requests");
        Directory.CreateDirectory(reqDir);
        var request = new
        {
            id,
            archive = $"incoming/{id}.tar.gz",
            sha256 = sha,
            version,
            currentVersion = CurrentVersion,
            source,
            requestedBy,
            requestedAt = DateTime.UtcNow.ToString("o"),
        };
        // Сначала .tmp, потом rename в requests/. Временный файл — вне requests/:
        // помощника будит любой файл там (DirectoryNotEmpty), и до 0.16 он, не найдя
        // *.json, выходил и будился снова — systemd останавливал юнит по лимиту
        // перезапусков (start-limit-hit), а готовая заявка так и висела в очереди.
        string tmp = Path.Combine(C.Dir, id + ".request.tmp");
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(request), ct);
        File.Move(tmp, Path.Combine(reqDir, id + ".json"));
        SetPhase("queued", version);
        _log.LogInformation("server update {Version} queued ({Source}, by {User}), id {Id}", version, source, requestedBy, id);
        return new(Outcome.Ok, version);
    }

    // ---- Загрузка по частям ----

    private string UploadsDir => Path.Combine(C.Dir, "uploads");
    private string MetaPath(string id) => Path.Combine(UploadsDir, id + ".json");
    private static bool IsId(string? id) => id is not null && Regex.IsMatch(id, "^[a-z0-9]{1,64}$");

    // Незаконченная загрузка (одна на сервер). Брошенные — удаляются. Принято —
    // по длине части на диске: она и есть правда, метаданные — имя, размер, кто.
    public UploadSession? ActiveUpload()
    {
        if (!Enabled || !Directory.Exists(UploadsDir)) return null;
        UploadSession? active = null;
        foreach (var f in Directory.EnumerateFiles(UploadsDir, "*.json"))
        {
            var s = ReadMeta(f);
            if (s is null || DateTime.UtcNow - s.UpdatedAt > UploadStale) { DropUpload(Path.GetFileNameWithoutExtension(f)); continue; }
            if (active is null || s.UpdatedAt > active.UpdatedAt) active = s;
        }
        return active;
    }

    private UploadSession? ReadMeta(string path)
    {
        try
        {
            var s = JsonSerializer.Deserialize<UploadSession>(File.ReadAllText(path));
            if (s is null || !IsId(s.Id)) return null;
            string part = IncomingPath(s.Id) + ".part";
            return s with { Received = File.Exists(part) ? new FileInfo(part).Length : 0 };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private void WriteMeta(UploadSession s)
    {
        Directory.CreateDirectory(UploadsDir);
        string tmp = MetaPath(s.Id) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s));
        File.Move(tmp, MetaPath(s.Id), overwrite: true);
    }

    private void DropUpload(string id)
    {
        if (!IsId(id)) return;
        BlobFiles.TryDelete(MetaPath(id));
        BlobFiles.TryDelete(IncomingPath(id) + ".part");
    }

    public static bool IsArchiveName(string? name) =>
        name is not null && name.Length <= 200 && Regex.IsMatch(name, @"^watcher-server-\d+\.\d+\.\d+(?: \(\d+\))?\.(?:tar\.gz|tgz)$");

    // Начать (или продолжить — тот же файл: имя и размер) загрузку.
    public (Outcome Outcome, UploadSession? Session, string? Error) StartUpload(string name, long size, string by)
    {
        if (!Enabled) return (Outcome.Disabled, null, null);
        if (RequestPending()) return (Outcome.Pending, null, null);
        if (_busy.CurrentCount == 0) return (Outcome.Busy, null, "идёт проверка архива или скачивание с GitHub");
        if (!IsArchiveName(name)) return (Outcome.BadArchive, null, "нужен архив сервера из релиза: watcher-server-X.Y.Z.tar.gz");
        long max = C.MaxUploadMb * 1024L * 1024L;
        if (size <= 0 || size > max) return (Outcome.BadArchive, null, $"размер архива — от 1 байта до {C.MaxUploadMb} МБ (Server:ServerUpdate:MaxUploadMb)");
        if (ActiveUpload() is { } active)
            return active.Name == name && active.Size == size
                ? (Outcome.Ok, active, null)
                : (Outcome.Busy, active, $"не закончена загрузка {active.Name} — продолжите её тем же файлом или отмените");
        var now = DateTime.UtcNow;
        var s = new UploadSession(NewId(), name, size, 0, by, now, now);
        File.WriteAllBytes(IncomingPath(s.Id) + ".part", []);
        WriteMeta(s);
        SetPhase("receiving", total: size);
        _log.LogInformation("server update upload {Id} started: {Name}, {Size} bytes, by {User}", s.Id, name, size, by);
        return (Outcome.Ok, s, null);
    }

    public enum ChunkOutcome { Ok, Complete, NotFound, WrongOffset, TooLarge }

    // Часть с offset == принято: дописать. Иначе — сколько принято (клиент
    // продолжит оттуда). Последняя — проверка и заявка фоном (FinishUploadAsync).
    public async Task<(ChunkOutcome Outcome, UploadSession? Session)> AppendChunkAsync(string id, long offset, Stream body, CancellationToken ct)
    {
        if (!IsId(id) || !File.Exists(MetaPath(id))) return (ChunkOutcome.NotFound, null);
        await _chunks.WaitAsync(ct);
        try
        {
            var s = ReadMeta(MetaPath(id));
            if (s is null) return (ChunkOutcome.NotFound, null);
            if (offset != s.Received) return (ChunkOutcome.WrongOffset, s);
            long room = s.Size - s.Received;
            long written = 0;
            string part = IncomingPath(id) + ".part";
            await using (var fs = new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = await body.ReadAsync(buf, ct)) > 0)
                {
                    written += n;
                    if (written > room || written > MaxChunkBytes) break;
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                }
                if (written > room || written > MaxChunkBytes)
                {
                    fs.SetLength(s.Received);   // часть целиком не принята
                    return (ChunkOutcome.TooLarge, s);
                }
            }
            s = s with { Received = s.Received + written, UpdatedAt = DateTime.UtcNow };
            WriteMeta(s);
            SetPhase("receiving", bytes: s.Received, total: s.Size);
            if (s.Received < s.Size) return (ChunkOutcome.Ok, s);
            // Принято всё — загрузки больше нет (страница не предлагает продолжить или
            // отменить); пока идёт проверка, новую не начать — занят _busy.
            BlobFiles.TryDelete(MetaPath(id));
            _ = Task.Run(() => FinishUploadAsync(s));
            return (ChunkOutcome.Complete, s);
        }
        // Обрыв посреди части (исключение наружу): на диске может остаться её
        // начало — оно и есть «принято», клиент продолжит с длины файла (ReadMeta).
        finally { _chunks.Release(); }
    }

    public bool CancelUpload(string id)
    {
        if (!IsId(id) || !File.Exists(MetaPath(id))) return false;
        DropUpload(id);
        SetPhase("idle");
        _log.LogInformation("server update upload {Id} cancelled", id);
        return true;
    }

    private async Task FinishUploadAsync(UploadSession s)
    {
        string part = IncomingPath(s.Id) + ".part";
        if (!await _busy.WaitAsync(0)) { SetPhase("failed", error: "идёт другая проверка или скачивание"); DropUpload(s.Id); return; }
        try
        {
            SetPhase("verifying", bytes: s.Size, total: s.Size);
            string sha;
            await using (var fs = File.OpenRead(part))
                sha = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
            string? version = ReadArchiveVersion(part);
            if (version is null) { SetPhase("failed", error: "это не архив сервера watcher (нет watcher-server-X.Y.Z/VERSION)"); return; }
            if (!IsAllowedTarget(version, CurrentVersion)) { SetPhase("failed", version, error: $"версия {version} старше текущей {CurrentVersion} — откат через веб не поддерживается (схема БД)"); return; }
            await QueueAsync(s.Id, part, sha, "upload", s.CreatedBy, version, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _log.LogWarning("server update upload {Id} verify failed: {Err}", s.Id, ex.Message);
            SetPhase("failed", error: ex.Message);
        }
        finally
        {
            DropUpload(s.Id);   // в очереди архив уже переименован — удаляется только недоделанное
            _busy.Release();
        }
    }

    private string IncomingPath(string id)
    {
        string dir = Path.Combine(C.Dir, "incoming");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, id + ".tar.gz");
    }

    private static string NewId() => DateTime.UtcNow.ToString("yyyyMMddHHmmss") + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    private async Task<string> CopyHashedAsync(Stream src, string dst, CancellationToken ct, long maxBytes = long.MaxValue)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var fs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buf = new byte[1 << 16];
        long total = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            total += n;
            if (total > maxBytes) throw new IOException($"архив больше {maxBytes / 1048576} МБ (Server:ServerUpdate:MaxUploadMb)");
            sha.AppendData(buf, 0, n);
            await fs.WriteAsync(buf.AsMemory(0, n), ct);
            Progress(total);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}
