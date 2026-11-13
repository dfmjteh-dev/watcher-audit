using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Watcher.Server;

public sealed record TranscodeMemorySnapshot(long TotalBytes, long AvailableBytes, long ChildRssBytes = 0, long LargestChildBytes = 0);

public interface ITranscodeMemorySource
{
    TranscodeMemorySnapshot? Read();
}

// Все запущенные сервером ffmpeg/ffprobe, включая проверку и ремукс.
// WorkingSet — физическая память; виртуальный размер и swap не используются.
public sealed class LinuxTranscodeMemorySource : ITranscodeMemorySource
{
    private static readonly ConcurrentDictionary<int, (Process Process, bool Worker)> Children = new();
    private static readonly AsyncLocal<bool> Worker = new();
    private static long _peak;
    internal static IDisposable EnterWorker()
    {
        bool previous = Worker.Value;
        Worker.Value = true;
        return new WorkerScope(previous);
    }
    private sealed class WorkerScope(bool previous) : IDisposable
    {
        public void Dispose() => Worker.Value = previous;
    }
    private readonly Func<string, string> _read;
    public LinuxTranscodeMemorySource(Func<string, string>? read = null) => _read = read ?? File.ReadAllText;
    internal static void Track(Process process) => Children[process.Id] = (process, Worker.Value);
    internal static void Untrack(Process process)
    {
        if (!Children.TryRemove(process.Id, out var entry) || !entry.Worker) return;
        try { ObservePeak(process.PeakWorkingSet64); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    private static void ObservePeak(long value)
    {
        long previous;
        do { previous = Interlocked.Read(ref _peak); if (previous >= value) return; }
        while (Interlocked.CompareExchange(ref _peak, value, previous) != previous);
    }

    public TranscodeMemorySnapshot? Read()
    {
        try
        {
            var (rss, largest) = ReadChildren();
            var info = _read("/proc/meminfo").Split('\n').Where(l => l.Contains(':'))
                .ToDictionary(l => l.Split(':')[0], l => l.Split(':')[1].Trim().Split(' ')[0]);
            long total = long.Parse(info["MemTotal"], CultureInfo.InvariantCulture) * 1024;
            long available = long.Parse(info["MemAvailable"], CultureInfo.InvariantCulture) * 1024;
            foreach (string line in _read("/proc/self/cgroup").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split(':', 3);
                bool v2 = fields[0] == "0" && fields[1] == "";
                if (v2 || fields[1].Split(',').Contains("memory"))
                    ReadHierarchy(fields[2], v2, ref total, ref available);
            }
            return total > 0 && available >= 0 && available <= total ? new(total, available, rss, largest) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or OverflowException or KeyNotFoundException or IndexOutOfRangeException or ArgumentException)
        { return null; }
    }

    private static (long Rss, long Largest) ReadChildren()
    {
        long rss = 0, largest = 0;
        foreach (var entry in Children.Values)
        {
            if (!entry.Worker) continue;
            var child = entry.Process;
            try
            {
                child.Refresh();
                rss += child.WorkingSet64;
                largest = Math.Max(largest, Math.Max(child.WorkingSet64, child.PeakWorkingSet64));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        ObservePeak(largest);
        return (rss, Interlocked.Read(ref _peak));
    }

    private void ReadHierarchy(string group, bool v2, ref long total, ref long available)
    {
        bool found = false;
        foreach (string mount in _read("/proc/self/mountinfo").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var halves = mount.Split(" - ", 2);
            var left = halves[0].Split(' ');
            var right = halves[1].Split(' ');
            if (right[0] != (v2 ? "cgroup2" : "cgroup") || (!v2 && !right[2].Split(',').Contains("memory"))) continue;
            string root = Unescape(left[3]), mountPoint = Unescape(left[4]);
            // В cgroup namespace путь может быть уже относительно namespace.
            string relative = root != "/" && (group == root || group.StartsWith(root + "/", StringComparison.Ordinal))
                ? group[root.Length..].TrimStart('/') : group.TrimStart('/');
            string dir = Path.GetFullPath(Path.Combine(mountPoint, relative));
            if (dir != mountPoint && !dir.StartsWith(mountPoint + "/", StringComparison.Ordinal)) throw new IOException("cgroup path outside mount");
            ReadLimits(dir, mountPoint, root, v2, ref total, ref available);
            found = true;
        }
        if (!found) throw new IOException("memory cgroup mount unavailable");
    }

    private void ReadLimits(string dir, string mountPoint, string root, bool v2, ref long total, ref long available)
    {
        while (true)
        {
            string limitText;
            try { limitText = _read(Path.Combine(dir, v2 ? "memory.max" : "memory.limit_in_bytes")).Trim(); }
            // У корня unified hierarchy нет собственного лимита.
            catch (FileNotFoundException) when (v2 && dir == mountPoint && root == "/") { break; }
            long limit = limitText == "max" ? long.MaxValue : long.Parse(limitText, CultureInfo.InvariantCulture);
            long used = long.Parse(_read(Path.Combine(dir, v2 ? "memory.current" : "memory.usage_in_bytes")).Trim(), CultureInfo.InvariantCulture);
            if (limit <= 0 || used < 0) throw new IOException("invalid cgroup memory");
            // В v1 unlimited — большое число около LONG_MAX.
            if (limit < long.MaxValue / 2)
            {
                total = Math.Min(total, limit);
                available = Math.Min(available, Math.Max(0, limit - used));
            }
            if (dir == mountPoint) break;
            dir = Path.GetDirectoryName(dir) ?? throw new IOException("invalid cgroup hierarchy");
        }
    }

    private static string Unescape(string value) => value.Replace("\\040", " ", StringComparison.Ordinal)
        .Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);
}

public sealed record TranscodeMemoryStatus(string Reason, long TotalBytes, long AvailableBytes, long ReserveBytes,
    long WorkerEstimateBytes, long ChildRssBytes, int ActiveWorkers, int EffectiveWorkers);

// Один контроллер на службу: чтение метрик и резервирование слота атомарны.
// Неиспользованный резерв уже выданных задач вычитается из текущего запаса,
// иначе стартующий ffmpeg с маленьким RSS допускает одновременный запуск всего пула.
public sealed class TranscodeMemoryGuard
{
    private const long Mb = 1048576;
    private readonly object _sync = new();
    private readonly ITranscodeMemorySource _source;
    private readonly List<Lease> _active = new();
    private long _estimate;
    private bool _paused;
    private TranscodeMemoryStatus _status = new("initializing", 0, 0, 0, 0, 0, 0, 0);
    public TranscodeMemoryGuard(ITranscodeMemorySource source, int memoryPerWorkerMb) =>
        (_source, _estimate) = (source, Math.Max(1536L, memoryPerWorkerMb) * Mb);
    public TranscodeMemoryStatus Status { get { lock (_sync) return _status; } }

    public Lease? TryAcquire(int maximum, CancellationToken ct)
    {
        lock (_sync)
        {
            ct.ThrowIfCancellationRequested();
            Update(maximum);
            if (_status.EffectiveWorkers <= _active.Count) return null;
            var lease = new Lease(this, ct);
            _active.Add(lease);
            _status = _status with { ActiveWorkers = _active.Count };
            return lease;
        }
    }

    public void CheckPressure(int maximum)
    {
        lock (_sync)
        {
            Update(maximum);
            if (_status.Reason != "critical-memory") return;
            // Отменяем по одной задаче; следующая оценка после освобождения памяти.
            var victim = _active.LastOrDefault(l => !l.Token.IsCancellationRequested);
            victim?.Cancel();
        }
    }

    private void Update(int maximum)
    {
        var sample = _source.Read();
        if (sample is null)
        {
            _paused = true;
            _status = new("metrics-unavailable", 0, 0, 0, _estimate, 0, _active.Count, _active.Count);
            return;
        }
        _estimate = Math.Max(_estimate, sample.LargestChildBytes + sample.LargestChildBytes / 4);
        long reserve = Math.Max(1024 * Mb, sample.TotalBytes / 4);
        long critical = Math.Max(256 * Mb, sample.TotalBytes / 20);
        long unspent = Math.Max(0, _estimate * _active.Count - sample.ChildRssBytes);
        long budget = sample.AvailableBytes - reserve - unspent;
        long needed = _estimate + (_paused ? 256 * Mb : 0);
        bool canStart = budget >= needed;
        _paused = !canStart;
        int extra = canStart ? (int)Math.Min(maximum, budget / _estimate) : 0;
        string reason = sample.AvailableBytes < critical ? "critical-memory" : !canStart ? "low-memory" : "ready";
        if (reason == "critical-memory") extra = 0;
        _status = new(reason, sample.TotalBytes, sample.AvailableBytes, reserve, _estimate,
            sample.ChildRssBytes, _active.Count, Math.Min(maximum, _active.Count + extra));
    }

    public sealed class Lease : IDisposable
    {
        private readonly TranscodeMemoryGuard _owner;
        private readonly CancellationTokenSource _cancel;
        internal Lease(TranscodeMemoryGuard owner, CancellationToken ct) =>
            (_owner, _cancel) = (owner, CancellationTokenSource.CreateLinkedTokenSource(ct));
        public CancellationToken Token => _cancel.Token;
        internal void Cancel() => _cancel.Cancel();
        public void Dispose()
        {
            lock (_owner._sync)
            {
                _owner._active.Remove(this);
                _cancel.Dispose();
            }
        }
    }
}
