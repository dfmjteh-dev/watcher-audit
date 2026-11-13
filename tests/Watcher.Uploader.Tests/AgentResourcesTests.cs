using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

// 0.13 (PR9б): ресурсы агента для heartbeat (AgentResources.cs).
public class AgentResourcesTests
{
    [Theory]
    [InlineData("1234 (ffmpeg) S 77 1234 1234 0", 77)]
    [InlineData("42 (Watcher.Capture) R 1 42 42", 1)]
    [InlineData("9 (a ) b (c)) S 555 9 9", 555)]   // comm со скобками и пробелами
    [InlineData("garbage", null)]
    public void Parent_pid_from_proc_stat(string stat, int? expected) =>
        Assert.Equal(expected, AgentResources.ParseParentPid(stat));

    [Theory]
    [InlineData("Watcher.Capture", true)]
    [InlineData("Watcher.Uploade", true)]   // comm на Linux — 15 символов
    [InlineData("watcher.sessionservice", true)]
    [InlineData("ffmpeg", false)]
    [InlineData("WatcherX", false)]
    public void Agent_processes_by_name(string name, bool expected) =>
        Assert.Equal(expected, AgentResources.IsAgentName(name));

    [Fact]
    public void Queue_size_counts_files_in_all_dirs_recursively()
    {
        string a = Path.Combine(Path.GetTempPath(), "wq-" + Guid.NewGuid().ToString("N"));
        string b = Path.Combine(Path.GetTempPath(), "wq-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(a, "sub"));
            Directory.CreateDirectory(b);
            File.WriteAllBytes(Path.Combine(a, "1.mp4"), new byte[100]);
            File.WriteAllBytes(Path.Combine(a, "sub", "2.json"), new byte[20]);
            File.WriteAllBytes(Path.Combine(b, "3.in"), new byte[5]);
            // Выгруженное консольным агентом — не очередь.
            Directory.CreateDirectory(Path.Combine(b, "sent"));
            File.WriteAllBytes(Path.Combine(b, "sent", "old.json"), new byte[999]);
            Assert.Equal((125L, 3), AgentResources.QueueSize([a, b, Path.Combine(a, "missing")]));
        }
        finally
        {
            Directory.Delete(a, true);
            Directory.Delete(b, true);
        }
    }

    [Fact]
    public void First_sample_has_no_cpu_second_has()
    {
        var r = new AgentResources([]);
        var t = DateTime.UtcNow;
        Assert.Null(r.Collect(t).CpuPercent);
        Assert.NotNull(r.Collect(t.AddSeconds(60)).CpuPercent);
    }
}
