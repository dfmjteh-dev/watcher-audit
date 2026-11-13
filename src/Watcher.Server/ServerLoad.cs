using System.Diagnostics;
using System.Globalization;

namespace Watcher.Server;

// 0.13 (PR9): загрузка сервера на «Обзоре» — CPU, память, сеть. Источник —
// /proc. В Docker без lxcfs /proc/stat и /proc/meminfo показывают весь хост
// (что и нужно: «хватает ли серверу ресурсов»), а /proc/net/dev — сеть
// контейнера, то есть трафик самого Watcher (агенты и веб-UI через Caddy). Под
// systemd сеть — все интерфейсы хоста, кроме lo и мостов Docker/виртуальных.
// Плюс процесс сервера (CPU и память). Скорость и проценты — по разнице двух
// замеров, поэтому замеряет фоновая служба раз в Interval, а /api/overview
// отдаёт последнее и историю за последний час.
public sealed record CpuTimes(ulong Busy, ulong Total);
public sealed record NetBytes(ulong Rx, ulong Tx);

public sealed record LoadSample(
    DateTime At,
    double? CpuPercent,
    long? MemTotalBytes,
    long? MemAvailableBytes,
    double? NetRxBytesPerSec,
    double? NetTxBytesPerSec,
    double ProcessCpuPercent,
    long ProcessMemBytes);

public static class ProcStat
{
    // Первая строка /proc/stat: cpu user nice system idle iowait irq softirq steal …
    // Простой — idle + iowait.
    public static CpuTimes? ParseCpu(string stat)
    {
        string? line = stat.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null) return null;
        var v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
            .Select(x => ulong.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();
        if (v.Length < 4) return null;
        ulong total = 0;
        foreach (var x in v) total += x;
        ulong idle = v[3] + (v.Length > 4 ? v[4] : 0);
        return new CpuTimes(total - idle, total);
    }

    public static double? CpuPercent(CpuTimes? prev, CpuTimes? cur)
    {
        if (prev is null || cur is null || cur.Total <= prev.Total) return null;
        return Math.Clamp((cur.Busy - (double)prev.Busy) * 100 / (cur.Total - prev.Total), 0, 100);
    }

    // MemTotal / MemAvailable в kB.
    public static (long? Total, long? Available) ParseMem(string meminfo)
    {
        long? Get(string key)
        {
            string? line = meminfo.Split('\n').FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.Ordinal));
            if (line is null) return null;
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return p.Length >= 2 && long.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out long kb) ? kb * 1024 : null;
        }
        return (Get("MemTotal"), Get("MemAvailable"));
    }

    // Интерфейсы, трафик которых не уходит наружу: петля и мосты/пары контейнеров.
    public static bool IsInternal(string iface) =>
        iface == "lo" || iface.StartsWith("docker", StringComparison.Ordinal) || iface.StartsWith("br-", StringComparison.Ordinal)
        || iface.StartsWith("veth", StringComparison.Ordinal) || iface.StartsWith("virbr", StringComparison.Ordinal);

    // /proc/net/dev: «iface: rxBytes … (8 полей) txBytes …».
    public static NetBytes? ParseNet(string netdev)
    {
        ulong rx = 0, tx = 0;
        bool any = false;
        foreach (var line in netdev.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string iface = line[..colon].Trim();
            if (iface.Length == 0 || iface.Contains('|') || IsInternal(iface)) continue;
            var f = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 9) continue;
            if (!ulong.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var r)
                || !ulong.TryParse(f[8], NumberStyles.None, CultureInfo.InvariantCulture, out var t)) continue;
            rx += r;
            tx += t;
            any = true;
        }
        return any ? new NetBytes(rx, tx) : null;
    }

    public static (double? Rx, double? Tx) NetRate(NetBytes? prev, NetBytes? cur, double seconds)
    {
        // Счётчик уменьшился — интерфейс пересоздан; скорость за этот шаг неизвестна.
        if (prev is null || cur is null || seconds <= 0 || cur.Rx < prev.Rx || cur.Tx < prev.Tx) return (null, null);
        return ((cur.Rx - prev.Rx) / seconds, (cur.Tx - prev.Tx) / seconds);
    }
}

public sealed class ServerLoad : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan History = TimeSpan.FromHours(1);

    private readonly object _lock = new();
    private readonly LinkedList<LoadSample> _samples = new();
    private CpuTimes? _cpu;
    private NetBytes? _net;
    private TimeSpan _procCpu;
    private DateTime _at;

    public bool InContainer { get; } = File.Exists("/.dockerenv");

    public List<LoadSample> Samples()
    {
        lock (_lock) return [.. _samples];
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Sample(DateTime.UtcNow);
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { Sample(DateTime.UtcNow); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private void Sample(DateTime now)
    {
        var cpu = Read("/proc/stat") is { } s ? ProcStat.ParseCpu(s) : null;
        var (memTotal, memAvail) = Read("/proc/meminfo") is { } m ? ProcStat.ParseMem(m) : (null, null);
        var net = Read("/proc/net/dev") is { } n ? ProcStat.ParseNet(n) : null;
        using var self = Process.GetCurrentProcess();
        var procCpu = self.TotalProcessorTime;
        double secs = _at == default ? 0 : (now - _at).TotalSeconds;
        var (rx, tx) = ProcStat.NetRate(_net, net, secs);
        double procPct = secs > 0 ? Math.Clamp((procCpu - _procCpu).TotalSeconds * 100 / secs / Environment.ProcessorCount, 0, 100) : 0;
        var sample = new LoadSample(now, ProcStat.CpuPercent(_cpu, cpu), memTotal, memAvail, rx, tx, procPct, self.WorkingSet64);
        _cpu = cpu;
        _net = net;
        _procCpu = procCpu;
        _at = now;
        if (secs == 0) return;   // первый замер — только база для разницы
        lock (_lock)
        {
            _samples.AddLast(sample);
            while (_samples.First is { } f && now - f.Value.At > History) _samples.RemoveFirst();
        }
    }
}
