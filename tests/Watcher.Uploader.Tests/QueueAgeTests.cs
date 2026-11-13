using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

// 0.13: срок хранения невыгруженного видео на станции.
public sealed class QueueAgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"watcher-qage-{Guid.NewGuid():N}");
    private readonly List<string> _log = new();

    public QueueAgeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Make(string name, DateTime lastWriteUtc)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, new byte[10]);
        File.SetLastWriteTimeUtc(p, lastWriteUtc);
        return p;
    }

    [Fact]
    public void Old_segments_are_dropped_and_counted_fresh_kept()
    {
        var old = DateTime.UtcNow.AddDays(-31);
        string a = Make("PC_20260901_100000.mp4", old);
        string aj = Make("PC_20260901_100000.mp4.json", old);
        string rec = Make("PC_20260902_100000.rec.mp4", old);
        string fresh = Make("PC_20261013_100000.mp4", DateTime.UtcNow.AddDays(-1));
        string freshJ = Make("PC_20261013_100000.mp4.json", DateTime.UtcNow.AddDays(-1));
        string strayJson = Make("PC_20260801_100000.mp4.json", old);

        int dropped = QueueAge.Apply(_dir, 30, DateTime.UtcNow, _log.Add);

        Assert.Equal(2, dropped);
        Assert.False(File.Exists(a) || File.Exists(aj) || File.Exists(rec));
        Assert.True(File.Exists(fresh) && File.Exists(freshJ));
        Assert.False(File.Exists(strayJson));
        Assert.Contains(_log, l => l.StartsWith("queue max age: dropped PC_20260901_100000.mp4", StringComparison.Ordinal));
    }

    [Fact]
    public void Zero_days_means_no_limit()
    {
        string a = Make("PC_20200101_100000.mp4", DateTime.UtcNow.AddYears(-5));
        Assert.Equal(0, QueueAge.Apply(_dir, 0, DateTime.UtcNow, _log.Add));
        Assert.True(File.Exists(a));
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData("{}", 30)]
    [InlineData("{\"queueMaxAgeDays\":7}", 7)]
    [InlineData("{\"queueMaxAgeDays\":0}", 0)]
    [InlineData("{\"queueMaxAgeDays\":-3}", 30)]
    [InlineData("{\"queueMaxAgeDays\":\"7\"}", 30)]
    [InlineData("not json", 30)]
    public void Effective_days_from_server_config_cache(string? cache, int expected)
    {
        string path = Path.Combine(_dir, "capture-config.json");
        if (cache is not null) File.WriteAllText(path, cache);
        Assert.Equal(expected, QueueAge.Effective(path, 30));
    }
}
