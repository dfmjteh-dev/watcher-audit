using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Watcher.Uploader;

// 0.13 (п.12 обратной связи 2026-10-14): что именно потеряно — вместо голого
// счётчика droppedSegments. Каждое удаление невыгруженной записи (квота, срок
// хранения, битый файл, транскрипт без 4688) — событие с именем файла, временем
// записи и причиной. События лежат в файле (переживают перезапуск и недели без
// сервера) и уходят в heartbeat; сервер подтверждает (lossesAccepted) — тогда
// удаляются из файла. Сервер старше 0.13 не подтверждает — журнал не растёт
// бесконечно: хранится последних MaxEntries.
public sealed record LossEvent(string File, DateTime? RecordedAt, long SizeBytes, string Reason, DateTime At, string? Detail = null);

public sealed class LossJournal
{
    public const string ReasonQuota = "quota", ReasonAge = "age", ReasonBroken = "broken", ReasonTranscript = "transcript-no-4688",
        ReasonTooLarge = "too-large";
    public const int MaxEntries = 1000;

    private static readonly ConcurrentDictionary<string, LossJournal> ByPath = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Время записи — из имени: сегменты <..>_yyyyMMdd_HHmmss, транскрипты <..>.yyyyMMddHHmmss.txt.
    private static readonly Regex Stamp = new(@"(\d{8})[_-]?(\d{6})(?!.*\d{8}[_-]?\d{6})", RegexOptions.CultureInvariant);

    private readonly string _path;
    private readonly object _lock = new();
    private List<LossEvent>? _events;

    private LossJournal(string path) => _path = path;

    // Один экземпляр на файл: в службе Windows журнал пишут и выгрузка, и аудит
    // консоли (TerminalAudit) из разных потоков.
    public static LossJournal At(string path) => ByPath.GetOrAdd(Path.GetFullPath(path), p => new LossJournal(p));

    // Рядом с учёткой агента (каталог agent/), конфиг не нужен.
    public static LossJournal For(UploaderConfig cfg) =>
        At(Path.Combine(Path.GetDirectoryName(cfg.CredentialPath) ?? cfg.QueueDir, "losses.jsonl"));

    public void Add(string file, long sizeBytes, string reason, DateTime nowUtc)
    {
        lock (_lock)
        {
            var list = Load();
            list.Add(new LossEvent(Path.GetFileName(file), RecordedAtFromName(Path.GetFileName(file)), sizeBytes, reason, nowUtc));
            if (list.Count > MaxEntries) list.RemoveRange(0, list.Count - MaxEntries);
            Save(list);
        }
    }

    // Для карантина: пока подтверждённой записи на диске нет, исходник не удалять.
    public bool TryAdd(string file, long sizeBytes, string reason, DateTime nowUtc, string? detail = null)
    {
        lock (_lock)
        {
            var list = Load();
            string name = Path.GetFileName(file);
            if (!list.Any(e => e.File == name && e.Reason == reason && e.Detail == detail))
                list.Add(new LossEvent(name, RecordedAtFromName(name), sizeBytes, reason, nowUtc, detail));
            if (list.Count > MaxEntries) list.RemoveRange(0, list.Count - MaxEntries);
            return Save(list);
        }
    }

    public IReadOnlyList<LossEvent> Pending(int max)
    {
        lock (_lock) return Load().Take(max).ToList();
    }

    // Сервер обработал первые count событий из отправленных.
    public void Acknowledge(int count)
    {
        if (count <= 0) return;
        lock (_lock)
        {
            var list = Load();
            list.RemoveRange(0, Math.Min(count, list.Count));
            Save(list);
        }
    }

    // Имена файлов — во времени станции (SegmentWriter: DateTime.Now) — в UTC.
    public static DateTime? RecordedAtFromName(string name)
    {
        var m = Stamp.Match(name);
        if (!m.Success) return null;
        return DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var local) ? local.ToUniversalTime() : null;
    }

    private List<LossEvent> Load()
    {
        if (_events is not null) return _events;
        _events = new();
        try
        {
            if (File.Exists(_path))
                foreach (var line in File.ReadAllLines(_path))
                    if (line.Length > 0 && TryParse(line) is { } e) _events.Add(e);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return _events;
    }

    private static LossEvent? TryParse(string line)
    {
        try { return JsonSerializer.Deserialize<LossEvent>(line, Json); }
        catch (JsonException) { return null; }
    }

    private bool Save(List<LossEvent> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(output, new System.Text.UTF8Encoding(false), leaveOpen: true))
                {
                    foreach (var entry in list) writer.WriteLine(JsonSerializer.Serialize(entry, Json));
                }
                output.Flush(flushToDisk: true);
            }
            File.Move(tmp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
