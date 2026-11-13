using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

public class QueueQuotaTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _logLines = new();

    public QueueQuotaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"watcher-queue-quota-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string MakeSegment(string name, int sizeBytes, DateTime lastWriteUtc, bool withMeta = true)
    {
        string mp4 = Path.Combine(_dir, name);
        File.WriteAllBytes(mp4, new byte[sizeBytes]);
        File.SetLastWriteTimeUtc(mp4, lastWriteUtc);
        if (withMeta) File.WriteAllText(mp4 + ".json", "{}");
        return mp4;
    }

    [Fact]
    public void Apply_zero_maxBytes_means_unlimited_does_nothing()
    {
        string f = MakeSegment("a.mp4", 100, DateTime.UtcNow.AddMinutes(-10));

        int dropped = QueueQuota.Apply(_dir, maxBytes: 0, quietSeconds: 5, _logLines.Add);

        Assert.Equal(0, dropped);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void Apply_on_missing_directory_does_nothing()
    {
        int dropped = QueueQuota.Apply(Path.Combine(_dir, "missing"), maxBytes: 10, quietSeconds: 5, _logLines.Add);

        Assert.Equal(0, dropped);
    }

    [Fact]
    public void Apply_under_cap_does_nothing()
    {
        string f = MakeSegment("a.mp4", 100, DateTime.UtcNow.AddMinutes(-10));

        int dropped = QueueQuota.Apply(_dir, maxBytes: 1000, quietSeconds: 5, _logLines.Add);

        Assert.Equal(0, dropped);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void Apply_over_cap_deletes_oldest_quiet_segment_and_its_meta()
    {
        string oldest = MakeSegment("old.mp4", 100, DateTime.UtcNow.AddMinutes(-30));
        string newest = MakeSegment("new.mp4", 100, DateTime.UtcNow.AddMinutes(-20));

        int dropped = QueueQuota.Apply(_dir, maxBytes: 150, quietSeconds: 5, _logLines.Add);

        Assert.Equal(1, dropped);
        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(oldest + ".json"));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void Apply_does_not_delete_a_file_still_being_written()
    {
        // "Не тихий" файл (LastWriteTimeUtc внутри quietSeconds) мог ещё дозаписываться
        // Capture'ом — удалять его нельзя, даже если он самый старый по имени/порядку.
        string notQuiet = MakeSegment("active.mp4", 200, DateTime.UtcNow);
        string quiet = MakeSegment("older-but-quiet.mp4", 50, DateTime.UtcNow.AddMinutes(-10));

        int dropped = QueueQuota.Apply(_dir, maxBytes: 100, quietSeconds: 5, _logLines.Add);

        Assert.Equal(1, dropped);
        Assert.True(File.Exists(notQuiet));
        Assert.False(File.Exists(quiet));
    }

    [Fact]
    public void Apply_stops_once_under_cap_leaves_newest_segments()
    {
        MakeSegment("s1.mp4", 100, DateTime.UtcNow.AddMinutes(-40));
        MakeSegment("s2.mp4", 100, DateTime.UtcNow.AddMinutes(-30));
        string s3 = MakeSegment("s3.mp4", 100, DateTime.UtcNow.AddMinutes(-20));
        string s4 = MakeSegment("s4.mp4", 100, DateTime.UtcNow.AddMinutes(-10));

        int dropped = QueueQuota.Apply(_dir, maxBytes: 250, quietSeconds: 5, _logLines.Add);

        Assert.Equal(2, dropped);
        Assert.True(File.Exists(s3));
        Assert.True(File.Exists(s4));
    }

    [Fact]
    public void Apply_logs_a_summary_line_when_segments_dropped()
    {
        MakeSegment("old.mp4", 100, DateTime.UtcNow.AddMinutes(-30));

        QueueQuota.Apply(_dir, maxBytes: 10, quietSeconds: 5, _logLines.Add);

        Assert.Contains(_logLines, l => l.Contains("dropped 1 unsent segment"));
    }

    [Fact]
    public void Apply_missing_meta_file_does_not_throw()
    {
        string f = MakeSegment("no-meta.mp4", 100, DateTime.UtcNow.AddMinutes(-30), withMeta: false);

        int dropped = QueueQuota.Apply(_dir, maxBytes: 10, quietSeconds: 5, _logLines.Add);

        Assert.Equal(1, dropped);
        Assert.False(File.Exists(f));
    }
}
