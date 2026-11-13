using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Watcher.Server;

// H7 (roadmap-next.md): выгрузка записей хоста за период — zip с файлами,
// manifest.json и SHA256SUMS. Хеш в SHA256SUMS — тот, что посчитан при загрузке
// и лежит в БД (у файлов, для которых его нет: ввод консоли, .timing — посчитан
// при выгрузке): `sha256sum -c` у получателя доказывает, что файл тот же, что
// прислал агент, а не только что zip не побился по дороге. Видео, пережатое
// сервером (Transcode.cs), — хеш пережатого файла, посчитанный при пережатии,
// хеш от агента — в manifest (sourceSha256, hashSource = transcode). Если файл на диске
// сервера с тех пор изменился, проверка у получателя честно падает, а манифест и
// журнал доступа показывают расхождение.
public static class RecordingExport
{
    public const string Format = "watcher-export/1";

    public sealed record Request(string Host, DateTime From, DateTime To, bool Video, bool Console, string? SessionUser);

    // Activity (H9) — смены активного окна по id сегмента, уходят в манифест.
    public sealed record Plan(List<Segment> Segments, List<TerminalSession> Terminal, long Bytes, bool TooMany,
        Dictionary<long, List<ActivityRow>>? Activity = null);

    // 0.12 (п. 10 обратной связи): выгрузка отмеченных в плеере записей, а не
    // всего за период. Чужой хост в списке молча отбрасывается; к записи ввода
    // консоли добавляется её вывод (одна строка сессии в UI). Период в манифесте
    // — от первой до последней записи.
    public static (Request Req, Plan Plan)? BuildFromIds(Db db, string host, IReadOnlyCollection<long> segmentIds, IReadOnlyCollection<long> terminalIds, int maxItems, bool withActivity = false)
    {
        var segments = segmentIds.Distinct().Take(maxItems + 1).Select(db.ById)
            .Where(s => s is not null && string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase)).Select(s => s!).ToList();
        var picked = terminalIds.Distinct().Take(maxItems + 1).Select(db.TerminalById)
            .Where(t => t is not null && string.Equals(t.Host, host, StringComparison.OrdinalIgnoreCase)).Select(t => t!).ToList();
        var terminal = picked.Concat(db.TerminalChildren(picked.Where(t => t.Kind == "terminal-input").Select(t => t.Id).ToList()))
            .GroupBy(t => t.Id).Select(g => g.First()).OrderBy(t => t.StartedAt).ToList();
        segments = segments.OrderBy(s => s.StartedAt).ToList();
        if (segments.Count + terminal.Count == 0) return null;
        var starts = segments.Select(s => s.StartedAt).Concat(terminal.Select(t => t.StartedAt));
        var ends = segments.Select(s => s.EndedAt).Concat(terminal.Select(t => t.EndedAt));
        var req = new Request(host, starts.Min(), ends.Max().AddSeconds(1), segments.Count > 0, terminal.Count > 0, null);
        bool tooMany = segments.Count + terminal.Count > maxItems;
        long bytes = segments.Sum(s => s.SizeBytes) + terminal.Sum(t => t.SizeBytes + Encoding.UTF8.GetByteCount(t.Input ?? ""));
        var activity = tooMany || !withActivity ? null : segments.ToDictionary(s => s.Id, s => db.ActivityForSegment(s.Id));
        return (req, new Plan(segments, terminal, bytes, tooMany, activity));
    }

    // Что попадёт в выгрузку, по возрастанию времени. Лимит +1 — чтобы отличить
    // «ровно предел» от «больше предела».
    public static Plan Build(Db db, Request req, int maxItems, bool withActivity = false)
    {
        var segments = req.Video
            ? db.Query(req.Host, req.From, req.To, false, maxItems + 1, req.SessionUser)
            : new List<Segment>();
        var terminal = req.Console
            ? db.QueryTerminal(req.Host, req.SessionUser, null, req.From, req.To, null, null, maxItems + 1)
            : new List<TerminalSession>();
        bool tooMany = segments.Count + terminal.Count > maxItems;
        segments.Reverse();
        terminal.Reverse();
        long bytes = segments.Sum(s => s.SizeBytes) + terminal.Sum(t => t.SizeBytes + Encoding.UTF8.GetByteCount(t.Input ?? ""));
        var activity = tooMany || !withActivity ? null : segments.ToDictionary(s => s.Id, s => db.ActivityForSegment(s.Id));
        return new(segments, terminal, bytes, tooMany, activity);
    }

    public sealed record Meta(long ExportId, string ExportedBy, DateTime ExportedAt);

    public sealed record Result(string ManifestSha256, int Files, long Bytes, List<string> Mismatched, List<string> Missing);

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    // Пишет zip в output. ZipArchive пишет синхронно — вызывающий разрешает
    // синхронный ввод-вывод для этого запроса (см. ExportEndpoints). ct —
    // обрыв скачивания: Kestrel после него молча выбрасывает запись в закрытое
    // соединение, ошибки записи не будет — без проверки оборванная выгрузка
    // попала бы в журнал как завершённая (найдено живой проверкой).
    public static Result Write(Stream output, Plan plan, Request req, Meta meta, string dataDir, CancellationToken ct = default)
    {
        var files = new List<object>();
        var sums = new StringBuilder();
        var mismatched = new List<string>();
        var missing = new List<string>();
        long total = 0;

        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void AddFile(string path, string? fullPath, string? text, string? storedSha, DateTime ts, object item, string? hashSource = null)
            {
                Stream? src = null;
                if (text is not null) src = new MemoryStream(Encoding.UTF8.GetBytes(text));
                else if (fullPath is not null && File.Exists(fullPath)) src = File.OpenRead(fullPath);
                if (src is null)
                {
                    missing.Add(path);
                    files.Add(new { path, missing = true, storedSha256 = storedSha, item });
                    return;
                }
                string sha;
                long size;
                using (src)
                {
                    // mp4 и так сжат — не тратим CPU; текст консоли сжимается хорошо.
                    var entry = zip.CreateEntry(path, text is null && path.StartsWith("video/", StringComparison.Ordinal) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(DateTime.SpecifyKind(ts, DateTimeKind.Utc));
                    using var dst = entry.Open();
                    (sha, size) = CopyHashed(src, dst, ct);
                }
                bool? matches = storedSha is null ? null : string.Equals(sha, storedSha, StringComparison.OrdinalIgnoreCase);
                if (matches == false) mismatched.Add(path);
                total += size;
                sums.Append(storedSha?.ToLowerInvariant() ?? sha).Append("  ").Append(path).Append('\n');
                files.Add(new
                {
                    path,
                    size,
                    sha256 = sha,
                    storedSha256 = storedSha?.ToLowerInvariant(),
                    // upload — хеш посчитан сервером при приёме файла от агента;
                    // export — хранимого хеша нет, посчитан при выгрузке.
                    // transcode — файл пережат сервером (Transcode.cs), хеш
                    // посчитан при пережатии; хеш от агента — в item.sourceSha256.
                    hashSource = hashSource ?? (storedSha is null ? "export" : "upload"),
                    matchesUpload = matches,
                    item,
                });
            }

            foreach (var s in plan.Segments)
            {
                AddFile($"video/{s.Filename}", Path.GetFullPath(Path.Combine(dataDir, s.FilePath)), null, s.Sha256, s.StartedAt, new
                {
                    type = "segment",
                    s.Id,
                    s.Host,
                    s.SessionUser,
                    startedAt = s.StartedAt.ToString("o"),
                    endedAt = s.EndedAt.ToString("o"),
                    s.DurationSec,
                    s.Frames,
                    s.MotionEvents,
                    s.StartReason,
                    uploadedAt = s.UploadedAt.ToString("o"),
                    stationStartedAt = s.StationStartedAt?.ToString("o"),
                    activity = plan.Activity?.GetValueOrDefault(s.Id)?.Select(a => new { offsetSec = a.OffsetSec, ts = a.Ts.ToString("o"), a.Process, a.Title }),
                    sourceSha256 = s.SourceSha256,
                    transcodedAt = s.TranscodeStatus == "done" ? s.TranscodedAt?.ToString("o") : null,
                }, s.TranscodeStatus == "done" ? "transcode" : null);
            }

            foreach (var t in plan.Terminal)
            {
                var item = new
                {
                    type = "terminal",
                    t.Id,
                    t.Kind,
                    t.Host,
                    t.SessionUser,
                    t.RemoteAddr,
                    startedAt = t.StartedAt.ToString("o"),
                    endedAt = t.EndedAt.ToString("o"),
                    t.DurationSec,
                    t.ExitCode,
                    t.Source,
                    t.Shell,
                    uploadedAt = t.UploadedAt.ToString("o"),
                    stationStartedAt = t.StationStartedAt?.ToString("o"),
                };
                string prefix = $"terminal/{t.StartedAt:yyyyMMdd'T'HHmmss'Z'}_{t.Id}";
                if (t.Kind == "terminal-input")
                    AddFile($"{prefix}_input.txt", null, t.Input ?? "", null, t.StartedAt, item);
                if (t.OutFilePath is not null)
                    AddFile($"{prefix}_{Path.GetFileName(t.OutFilePath)}", Path.GetFullPath(Path.Combine(dataDir, t.OutFilePath)), null, t.Sha256, t.StartedAt, item);
                if (t.TimingFilePath is not null)
                    AddFile($"{prefix}_{Path.GetFileName(t.TimingFilePath)}", Path.GetFullPath(Path.Combine(dataDir, t.TimingFilePath)), null, null, t.StartedAt, item);
            }

            var manifest = new
            {
                format = Format,
                exportId = meta.ExportId,
                exportedBy = meta.ExportedBy,
                exportedAt = meta.ExportedAt.ToString("o"),
                serverVersion = AppVersion.Current,
                host = req.Host,
                from = req.From.ToString("o"),
                to = req.To.ToString("o"),
                sessionUser = req.SessionUser,
                video = req.Video,
                console = req.Console,
                files,
                missing,
                mismatched,
            };
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJson);
            string manifestSha = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
            ct.ThrowIfCancellationRequested();
            WriteText(zip, "manifest.json", manifestBytes, meta.ExportedAt);
            WriteText(zip, "SHA256SUMS", Encoding.UTF8.GetBytes(sums.ToString()), meta.ExportedAt);
            WriteText(zip, "README.txt", Encoding.UTF8.GetBytes(Readme(meta, manifestSha)), meta.ExportedAt);
            return new(manifestSha, files.Count, total, mismatched, missing);
        }
    }

    private static string Readme(Meta meta, string manifestSha) => $"""
        Выгрузка записей Watcher № {meta.ExportId}
        Выгрузил: {meta.ExportedBy}, {meta.ExportedAt:yyyy-MM-dd HH:mm:ss} UTC

        Проверка целостности:

        1. Файлы записей:  sha256sum -c SHA256SUMS
           (Windows PowerShell: Get-FileHash -Algorithm SHA256 <файл>)
           Хеш в SHA256SUMS посчитан сервером при приёме файла от агента
           (manifest.json: hashSource = "upload"); для ввода консоли и .timing
           хранимого хеша нет — он посчитан при выгрузке (hashSource = "export").

        2. Сам манифест: SHA-256 файла manifest.json —
           {manifestSha}
           Это же значение записано в журнал доступа Watcher (действие
           export.done, выгрузка № {meta.ExportId}). Совпадение подтверждает, что
           состав и хеши в манифесте не менялись после выгрузки.

        manifest.json — список файлов с метаданными записей (хост, пользователь,
        время начала/конца по часам сервера). Поле mismatched — файлы, которые
        на сервере на момент выгрузки уже не совпадали с хешем при загрузке;
        missing — записи, файлов которых на сервере не оказалось.

        """;

    private static void WriteText(ZipArchive zip, string name, byte[] content, DateTime ts)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(DateTime.SpecifyKind(ts, DateTimeKind.Utc));
        using var s = entry.Open();
        s.Write(content);
    }

    private static (string Sha, long Size) CopyHashed(Stream src, Stream dst, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buf = new byte[81920];
        long size = 0;
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buf, 0, n);
            dst.Write(buf, 0, n);
            size += n;
        }
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), size);
    }
}
