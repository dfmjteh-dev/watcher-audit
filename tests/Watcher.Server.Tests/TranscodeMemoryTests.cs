using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

public class TranscodeMemoryTests
{
    private const long Mb = 1048576;
    private sealed class Memory : ITranscodeMemorySource
    {
        public TranscodeMemorySnapshot? Sample = new(16L << 30, 12L << 30);
        public TranscodeMemorySnapshot? Read() => Sample;
    }

    [Fact]
    public void Simultaneous_admission_reserves_memory_even_before_children_grow()
    {
        var source = new Memory();
        var guard = new TranscodeMemoryGuard(source, 600);
        var leases = new System.Collections.Concurrent.ConcurrentBag<TranscodeMemoryGuard.Lease>();
        Parallel.For(0, 32, _ => { if (guard.TryAcquire(32, default) is { } lease) leases.Add(lease); });
        Assert.Equal(5, leases.Count); // (12 GiB available - 4 GiB reserve) / 1.5 GiB
        foreach (var lease in leases) lease.Dispose();
    }

    [Fact]
    public void Resume_requires_hysteresis_and_observed_peak_increases_reservation()
    {
        var source = new Memory { Sample = new(8L << 30, 3500 * Mb) };
        var guard = new TranscodeMemoryGuard(source, 600);
        Assert.Null(guard.TryAcquire(20, default));
        source.Sample = new(8L << 30, 3600 * Mb);
        Assert.Null(guard.TryAcquire(20, default));
        source.Sample = new(8L << 30, 3900 * Mb);
        using (var lease = guard.TryAcquire(20, default)) Assert.NotNull(lease);
        source.Sample = new(8L << 30, 3900 * Mb, 0, 2L << 30);
        Assert.Null(guard.TryAcquire(20, default));
        Assert.Equal(2560 * Mb, guard.Status.WorkerEstimateBytes);
    }

    [Fact]
    public void Critical_pressure_cancels_one_task_and_missing_metrics_prevent_new_starts()
    {
        var source = new Memory();
        var guard = new TranscodeMemoryGuard(source, 600);
        using var first = guard.TryAcquire(3, default)!;
        using var second = guard.TryAcquire(3, default)!;
        source.Sample = new(16L << 30, 100 * Mb);
        guard.CheckPressure(3);
        Assert.False(first.Token.IsCancellationRequested);
        Assert.True(second.Token.IsCancellationRequested);
        Assert.Null(guard.TryAcquire(3, default));
        source.Sample = null;
        Assert.Null(guard.TryAcquire(3, default));
        Assert.Equal("metrics-unavailable", guard.Status.Reason);
    }

    [Fact]
    public void Explicit_worker_limit_remains_an_upper_bound()
    {
        var guard = new TranscodeMemoryGuard(new Memory(), 600);
        using var first = guard.TryAcquire(1, default);
        Assert.NotNull(first);
        Assert.Null(guard.TryAcquire(1, default));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void Host_and_all_cgroup_ancestors_constrain_budget(bool v2, bool unlimited)
    {
        var files = new Dictionary<string, string>
        {
            ["/proc/meminfo"] = "MemTotal: 16777216 kB\nMemAvailable: 12582912 kB\nSwapFree: 999999999 kB\n",
            ["/proc/self/cgroup"] = v2 ? "0::/parent/child\n" : "5:cpu,memory:/parent/child\n",
            ["/proc/self/mountinfo"] = v2 ? "1 0 0:1 / /cg rw - cgroup2 cgroup rw\n" : "1 0 0:1 / /cg rw - cgroup cgroup rw,memory\n",
        };
        foreach (string dir in new[] { "/cg", "/cg/parent", "/cg/parent/child" })
        {
            files[dir + (v2 ? "/memory.max" : "/memory.limit_in_bytes")] = unlimited ? (v2 ? "max" : "9223372036854771712") : (dir == "/cg/parent" ? (4L << 30).ToString() : (8L << 30).ToString());
            files[dir + (v2 ? "/memory.current" : "/memory.usage_in_bytes")] = (3L << 30).ToString();
        }
        var sample = new LinuxTranscodeMemorySource(path => files[path]).Read();
        Assert.NotNull(sample);
        Assert.Equal((unlimited ? 16L : 4L) << 30, sample.TotalBytes);
        Assert.Equal((unlimited ? 12L : 1L) << 30, sample.AvailableBytes);
    }

    [Fact]
    public void Outside_container_uses_host_and_invalid_metrics_fail_closed()
    {
        var files = new Dictionary<string, string>
        {
            ["/proc/meminfo"] = "MemTotal: 8388608 kB\nMemAvailable: 4194304 kB\n",
            ["/proc/self/cgroup"] = "2:cpu:/\n"
        };
        var source = new LinuxTranscodeMemorySource(path => files[path]);
        Assert.Equal(4L << 30, source.Read()!.AvailableBytes);
        files["/proc/meminfo"] = "MemFree: 1 kB\n";
        Assert.Null(source.Read());
    }
    [Theory]
    [InlineData("/container", "/", "/cg/memory.max")]
    [InlineData("/container", "/container", "/cg/memory.max")]
    [InlineData("/container", "/child", "/cg/child/memory.max")]
    public void Cgroup_namespace_and_subtree_mounts_are_resolved(string root, string group, string limitPath)
    {
        var files = new Dictionary<string, string>
        {
            ["/proc/meminfo"] = "MemTotal: 16777216 kB\nMemAvailable: 12582912 kB\n",
            ["/proc/self/cgroup"] = "0::" + group,
            ["/proc/self/mountinfo"] = "1 0 0:1 " + root + " /cg rw - cgroup2 cgroup rw",
            ["/cg/memory.max"] = "max",
            ["/cg/memory.current"] = "0",
            [limitPath] = (4L << 30).ToString(),
            [limitPath.Replace("memory.max", "memory.current")] = (3L << 30).ToString()
        };
        var source = new LinuxTranscodeMemorySource(path => files[path]);
        Assert.Equal(1L << 30, source.Read()!.AvailableBytes);
        files.Remove(limitPath);
        Assert.Null(source.Read());
    }

    [Fact]
    public void Unified_hierarchy_root_has_no_memory_max()
    {
        var source = new LinuxTranscodeMemorySource(path => path switch
        {
            "/proc/meminfo" => "MemTotal: 8388608 kB\nMemAvailable: 4194304 kB\n",
            "/proc/self/cgroup" => "0::/",
            "/proc/self/mountinfo" => "1 0 0:1 / /cg rw - cgroup2 cgroup rw",
            _ => throw new FileNotFoundException()
        });
        Assert.Equal(4L << 30, source.Read()!.AvailableBytes);
    }

}
