using System.Diagnostics;
using System.Globalization;

namespace Watcher.Uploader;

// 0.13 (PR9б): сколько станции стоит агент — CPU и память всех процессов
// агента и размер очереди невыгруженного; уходит с heartbeat, видно в «Инфо» и
// «Хостах». Процессы агента — Watcher.* (служба, выгрузка, запись экрана в
// каждой сессии, консольный агент) и их ffmpeg: на Windows — ffmpeg.exe из
// каталога агента, на Linux — системный ffmpeg, поэтому по родителю (/proc).
// Чужой ffmpeg на станции не считается.
public sealed record AgentResourceReport(double? CpuPercent, long MemBytes, int Processes, long QueueBytes, int QueueFiles);

public sealed class AgentResources
{
    private readonly string[] _queueDirs;
    private readonly string _installDir;
    private Dictionary<int, TimeSpan> _cpu = new();
    private DateTime _at;

    public AgentResources(IEnumerable<string> queueDirs, string? installDir = null)
    {
        _queueDirs = queueDirs.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct().ToArray();
        _installDir = Path.GetFullPath(installDir ?? AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    // Очереди: видео (QueueDir выгрузки) и, на Linux, консольного агента — у
    // него свой процесс и свои каталоги: ввод (queue) и вывод (raw-output).
    // Выгруженное (подкаталоги sent) — не очередь, см. QueueSize.
    public static AgentResources For(UploaderConfig cfg)
    {
        var dirs = new List<string> { cfg.QueueDir };
        if (OperatingSystem.IsLinux())
            dirs.AddRange(new[] { "/var/lib/watcher-terminal-agent/queue", "/var/lib/watcher-terminal-agent/raw-output" }.Where(Directory.Exists));
        return new AgentResources(dirs);
    }

    public static bool IsAgentName(string name) => name.StartsWith("Watcher.", StringComparison.OrdinalIgnoreCase);

    public AgentResourceReport Collect(DateTime nowUtc)
    {
        var all = Process.GetProcesses();
        try
        {
            var agentPids = all.Where(p => IsAgentName(p.ProcessName)).Select(p => p.Id).ToHashSet();
            var mine = all.Where(p => agentPids.Contains(p.Id) || IsAgentFfmpeg(p, agentPids)).ToList();
            var cpu = new Dictionary<int, TimeSpan>();
            long mem = 0;
            foreach (var p in mine)
            {
                try
                {
                    cpu[p.Id] = p.TotalProcessorTime;
                    mem += p.WorkingSet64;
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }   // процесс уже завершился
            }
            double? pct = null;
            if (_at != default && nowUtc > _at)
            {
                // Процессы, которых не было в прошлом замере (новый Capture, ffmpeg), —
                // их время целиком за этот интервал не считаем: не знаем, когда стартовали.
                double used = cpu.Where(kv => _cpu.ContainsKey(kv.Key)).Sum(kv => Math.Max(0, (kv.Value - _cpu[kv.Key]).TotalSeconds));
                pct = Math.Clamp(used * 100 / (nowUtc - _at).TotalSeconds / Environment.ProcessorCount, 0, 100);
            }
            _cpu = cpu;
            _at = nowUtc;
            var (bytes, files) = QueueSize(_queueDirs);
            return new AgentResourceReport(pct is { } v ? Math.Round(v, 1) : null, mem, cpu.Count, bytes, files);
        }
        finally
        {
            foreach (var p in all) p.Dispose();
        }
    }

    private bool IsAgentFfmpeg(Process p, HashSet<int> agentPids)
    {
        if (!p.ProcessName.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase)) return false;
        if (OperatingSystem.IsLinux()) return ParentPid(p.Id) is { } ppid && agentPids.Contains(ppid);
        try { return p.MainModule?.FileName is { } f && f.StartsWith(_installDir, StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return false; }
    }

    // /proc/<pid>/stat: «pid (comm) state ppid …»; comm может содержать пробелы и скобки.
    public static int? ParseParentPid(string stat)
    {
        int close = stat.LastIndexOf(')');
        if (close < 0) return null;
        var f = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return f.Length > 1 && int.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out int ppid) ? ppid : null;
    }

    private static int? ParentPid(int pid)
    {
        try { return ParseParentPid(File.ReadAllText($"/proc/{pid}/stat")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static (long Bytes, int Files) QueueSize(IEnumerable<string> dirs)
    {
        long bytes = 0;
        int files = 0;
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                string sent = Path.DirectorySeparatorChar + "sent" + Path.DirectorySeparatorChar;
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    // Консольный агент держит выгруженное в queue/sent, raw-output/sent.
                    if (f.FullName[dir.TrimEnd(Path.DirectorySeparatorChar).Length..].Contains(sent, StringComparison.Ordinal)) continue;
                    bytes += f.Length;
                    files++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return (bytes, files);
    }
}
