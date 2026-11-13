using System.Text.Json;
using Watcher.Uploader;

namespace Watcher.SessionService;

// Ограниченный журнал метаданных: ни командных строк, ни содержимого сессий.
public sealed class ProcessStartHistory
{
    public const int MaxEntries = 20000;
    public static readonly TimeSpan Retention = TimeSpan.FromHours(48);
    private const long MaxFileBytes = 64 * 1024 * 1024;
    public sealed record Snapshot(long RecordId, DateTime? RecordTimeUtc, List<ProcessStart> Events);
    private readonly string _path;
    private readonly HashSet<ProcessStart> _events = new();
    public long RecordId { get; private set; }
    public DateTime? RecordTimeUtc { get; private set; }
    public IReadOnlyList<ProcessStart> Events => _events.OrderBy(e => e.TimeUtc).ToList();
    public bool Dirty { get; private set; }
    public string? LoadError { get; private set; }

    public ProcessStartHistory(string path, DateTime nowUtc)
    {
        _path = path;
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > MaxFileBytes) { LoadError = "cache-too-large"; return; }
            var saved = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path));
            if (saved?.Events is null || saved.Events.Count > MaxEntries || saved.RecordId < 0)
            { LoadError = "cache-invalid"; return; }
            RecordId = saved.RecordId;
            RecordTimeUtc = saved.RecordTimeUtc;
            foreach (var e in saved.Events)
                if (Valid(e)) _events.Add(e);
            Trim(nowUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { LoadError = "cache-read-failed"; }
    }

    private static bool Valid(ProcessStart? e) => e is not null && e.NewPid > 0 && e.CreatorPid >= 0
        && e.TimeUtc.Kind == DateTimeKind.Utc && !string.IsNullOrEmpty(e.NewName) && e.NewName.Length <= 1024
        && e.ParentName is not null && e.ParentName.Length <= 1024;

    public void Observe(long recordId, DateTime timeUtc, ProcessStart? process)
    {
        if (recordId <= RecordId) return;
        RecordId = recordId;
        RecordTimeUtc = timeUtc;
        if (Valid(process)) _events.Add(process!);
        Dirty = true;
    }

    // После очистки/разрыва Security перечитываем доступную историю. Старые
    // метаданные не теряем: их время всё ещё позволяет разбирать старые файлы.
    public void ResetCursor()
    {
        RecordId = 0;
        RecordTimeUtc = null;
        Dirty = true;
    }

    public void Trim(DateTime nowUtc)
    {
        var remove = _events.Where(e => e.TimeUtc < nowUtc - Retention || e.TimeUtc > nowUtc.AddMinutes(5)).ToList();
        foreach (var e in remove) Dirty |= _events.Remove(e);
        if (_events.Count <= MaxEntries) return;
        foreach (var e in _events.OrderBy(e => e.TimeUtc).ThenBy(e => e.NewPid).Take(_events.Count - MaxEntries).ToList())
            _events.Remove(e);
        Dirty = true;
    }

    public bool Save()
    {
        if (!Dirty) return true;
        try
        {
            CredentialStore.CreateProtectedDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Snapshot(RecordId, RecordTimeUtc, Events.ToList())));
            File.Move(tmp, _path, overwrite: true);
            Dirty = false;
            LoadError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

// Не выдаём предположение о перезаписи журнала за доказанную причину потери.
public sealed record ProcessHistoryStatus(bool ReadFailed, bool SaveFailed, bool CatchingUp, bool? AuditEnabled,
    DateTime? OldestAvailableUtc, string? CacheLoadError)
{
    public string MissingReason(DateTime startedUtc, DateTime nowUtc) => ReadFailed ? Watcher.Shared.TranscriptIssue.EventReadFailed
        : AuditEnabled == false ? Watcher.Shared.TranscriptIssue.AuditDisabled
        : CatchingUp || startedUtc > nowUtc - SshAncestry.After ? Watcher.Shared.TranscriptIssue.EventPending
        : OldestAvailableUtc is { } oldest && startedUtc < oldest ? Watcher.Shared.TranscriptIssue.HistoryUnavailable
        : Watcher.Shared.TranscriptIssue.EventMissing;
}
