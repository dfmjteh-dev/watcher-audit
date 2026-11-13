using Xunit;

namespace Watcher.Server.Tests;

// 0.13 (PR9): загрузка сервера на «Обзоре» — разбор /proc (ServerLoad.cs).
public class ServerLoadTests
{
    [Fact]
    public void Cpu_percent_is_busy_share_between_two_samples()
    {
        var a = ProcStat.ParseCpu("cpu  100 0 100 700 100 0 0 0 0 0\ncpu0 1 2 3 4\n");
        var b = ProcStat.ParseCpu("cpu  200 0 200 1300 200 0 0 0 0 0\n");
        Assert.Equal(new CpuTimes(200, 1000), a);
        // за шаг: занято 200 из 900
        Assert.Equal(200.0 * 100 / 900, ProcStat.CpuPercent(a, b)!.Value, 6);
        Assert.Null(ProcStat.CpuPercent(null, b));
        Assert.Null(ProcStat.CpuPercent(b, a));
    }

    [Fact]
    public void Memory_total_and_available_in_bytes()
    {
        var (total, avail) = ProcStat.ParseMem("MemTotal:       16384 kB\nMemFree:  100 kB\nMemAvailable:    4096 kB\n");
        Assert.Equal(16384L * 1024, total);
        Assert.Equal(4096L * 1024, avail);
        Assert.Equal((null, null), ProcStat.ParseMem("garbage"));
    }

    private const string NetDev = """
Inter-|   Receive                                                |  Transmit
 face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
    lo: 5000 10 0 0 0 0 0 0 5000 10 0 0 0 0 0 0
  eth0: 1000 10 0 0 0 0 0 0 2000 10 0 0 0 0 0 0
docker0: 777 1 0 0 0 0 0 0 777 1 0 0 0 0 0 0
vethabc: 777 1 0 0 0 0 0 0 777 1 0 0 0 0 0 0
  ens18: 300 1 0 0 0 0 0 0 400 1 0 0 0 0 0 0
""";

    [Fact]
    public void Network_sums_external_interfaces_only()
    {
        Assert.Equal(new NetBytes(1300, 2400), ProcStat.ParseNet(NetDev));
        var (rx, tx) = ProcStat.NetRate(new NetBytes(1000, 2000), new NetBytes(4000, 2500), 15);
        Assert.Equal(200, rx);
        Assert.Equal(100.0 / 3, tx!.Value, 6);
        // Счётчик сбросился (интерфейс пересоздан) — скорость за шаг неизвестна.
        Assert.Equal((null, null), ProcStat.NetRate(new NetBytes(5000, 0), new NetBytes(10, 10), 15));
    }
}
