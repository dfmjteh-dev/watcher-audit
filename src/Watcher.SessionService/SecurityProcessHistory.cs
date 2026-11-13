using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml;
using Watcher.Shared;

namespace Watcher.SessionService;

[SupportedOSPlatform("windows")]
public sealed class SecurityProcessHistory
{
    public const int MaxPerPoll = 2000;
    private readonly ProcessStartHistory _history;
    private DateTime _savedAt = DateTime.MinValue;
    private volatile IReadOnlyDictionary<int, IReadOnlyList<ProcessStart>> _index = SshAncestry.Index([]);
    private volatile ProcessHistoryStatus _status = new(false, false, false, null, null, null);
    public IReadOnlyDictionary<int, IReadOnlyList<ProcessStart>> Index => _index;
    public ProcessHistoryStatus Status => _status;
    public bool CatchingUp => _status.CatchingUp;
    public bool ReadFailed => _status.ReadFailed;
    public bool SaveFailed => _status.SaveFailed;

    public SecurityProcessHistory(string path) => _history = new(path, DateTime.UtcNow);

    public void Refresh()
    {
        DateTime now = DateTime.UtcNow;
        bool readFailed = false, catchingUp = false;
        bool saveFailed = _status.SaveFailed;
        DateTime? oldest = _status.OldestAvailableUtc;
        bool? auditEnabled;
        try
        {
            // Проверяем сам checkpoint: RecordID может повториться после очистки.
            if (_history.RecordId > 0)
            {
                using var check = new EventLogReader(new EventLogQuery("Security", PathType.LogName,
                    $"*[System[EventRecordID={_history.RecordId}]]"));
                using var last = check.ReadEvent();
                if (last?.TimeCreated?.ToUniversalTime() != _history.RecordTimeUtc) _history.ResetCursor();
            }
            using (var first = new EventLogReader(new EventLogQuery("Security", PathType.LogName, "*")))
            using (var entry = first.ReadEvent()) oldest = entry?.TimeCreated?.ToUniversalTime();
            using var reader = new EventLogReader(new EventLogQuery("Security", PathType.LogName,
                $"*[System[EventID=4688 and EventRecordID>{_history.RecordId}]]"));
            int count = 0;
            for (; count < MaxPerPoll; count++)
            {
                using var entry = reader.ReadEvent();
                if (entry is null) break;
                if (entry.RecordId is not { } id || entry.TimeCreated is not { } time) continue;
                ProcessStart? process = null;
                try { process = ProcessStartEvent.Parse(entry.ToXml()); }
                catch (XmlException) { /* повреждённое событие не блокирует очередь */ }
                _history.Observe(id, time.ToUniversalTime(), process);
            }
            catchingUp = count == MaxPerPoll;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        { readFailed = true; }
        try { auditEnabled = WindowsAuditPolicy.ProcessCreationEnabled(); }
        catch (Win32Exception) { auditEnabled = null; }
        _history.Trim(now);
        _index = SshAncestry.Index(_history.Events);
        if (now - _savedAt >= TimeSpan.FromMinutes(1))
        {
            saveFailed = !_history.Save();
            _savedAt = now;
        }
        _status = new(readFailed, saveFailed, catchingUp, auditEnabled, oldest, _history.LoadError);
    }

    public string MissingReason(DateTime startedUtc, DateTime nowUtc) => _status.MissingReason(startedUtc, nowUtc);
}

[SupportedOSPlatform("windows")]
internal static class WindowsAuditPolicy
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Policy
    {
        public Guid Subcategory;
        public uint Flags;
        public Guid Category;
    }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool AuditQuerySystemPolicy([In] Guid[] ids, uint count, out IntPtr buffer);
    [DllImport("advapi32.dll")]
    private static extern void AuditFree(IntPtr buffer);

    public static bool ProcessCreationEnabled()
    {
        if (!AuditQuerySystemPolicy([new Guid("0CCE922B-69AE-11D9-BED3-505054503030")], 1, out var buffer))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return (Marshal.PtrToStructure<Policy>(buffer).Flags & 1) != 0; }
        finally { AuditFree(buffer); }
    }
}
