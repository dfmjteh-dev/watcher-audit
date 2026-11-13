using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

public class RetentionTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _logLines = new();

    public RetentionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"watcher-uploader-retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string MakeFile(string name, int sizeBytes, DateTime lastWriteUtc)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    [Fact]
    public void Apply_on_missing_directory_does_nothing()
    {
        string missing = Path.Combine(_dir, "does-not-exist");

        Retention.Apply(missing, retainDays: 7, maxBytes: 1000, _logLines.Add);

        Assert.Empty(_logLines);
    }

    [Fact]
    public void Apply_deletes_files_older_than_retainDays_keeps_recent()
    {
        string oldFile = MakeFile("old.mp4", 100, DateTime.UtcNow.AddDays(-10));
        string newFile = MakeFile("new.mp4", 100, DateTime.UtcNow);

        Retention.Apply(_dir, retainDays: 5, maxBytes: long.MaxValue, _logLines.Add);

        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
    }

    [Fact]
    public void Apply_deletes_oldest_first_to_fit_maxBytes_cap()
    {
        string f1 = MakeFile("f1.mp4", 100, DateTime.UtcNow.AddMinutes(-30));
        string f2 = MakeFile("f2.mp4", 100, DateTime.UtcNow.AddMinutes(-20));
        string f3 = MakeFile("f3.mp4", 100, DateTime.UtcNow);

        // retainDays huge so the age-based pass doesn't touch anything; the cap alone forces
        // size-based pruning.
        Retention.Apply(_dir, retainDays: 3650, maxBytes: 250, _logLines.Add);

        Assert.False(File.Exists(f1));
        Assert.True(File.Exists(f2));
        Assert.True(File.Exists(f3));
    }

    [Fact]
    public void Apply_retainDays_zero_deletes_almost_everything_unlike_server_retention()
    {
        // Watcher.Server.RetentionService treats RetentionDays == 0 as "no age limit"
        // (guarded by `if (_cfg.RetentionDays > 0)`). This Uploader-side Retention.Apply has
        // no such guard: cutoff = UtcNow.AddDays(-0) == UtcNow, so every file not written in
        // the future counts as "older than cutoff" and gets deleted. Documents a real
        // asymmetry between the two retention implementations — see roadmap.md.
        string file = MakeFile("anything.mp4", 100, DateTime.UtcNow.AddSeconds(-1));

        Retention.Apply(_dir, retainDays: 0, maxBytes: long.MaxValue, _logLines.Add);

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Apply_logs_a_summary_line_when_files_deleted_by_age()
    {
        MakeFile("old.mp4", 100, DateTime.UtcNow.AddDays(-10));

        Retention.Apply(_dir, retainDays: 5, maxBytes: long.MaxValue, _logLines.Add);

        Assert.Contains(_logLines, l => l.Contains("deleted 1 files older than"));
    }

    [Fact]
    public void Apply_does_not_delete_anything_when_under_both_limits()
    {
        string file = MakeFile("fresh.mp4", 100, DateTime.UtcNow);

        Retention.Apply(_dir, retainDays: 30, maxBytes: 1_000_000, _logLines.Add);

        Assert.True(File.Exists(file));
        Assert.Empty(_logLines);
    }
}
