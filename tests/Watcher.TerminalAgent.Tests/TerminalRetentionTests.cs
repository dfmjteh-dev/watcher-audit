using Watcher.TerminalAgent;
using Xunit;

namespace Watcher.TerminalAgent.Tests;

public class TerminalRetentionTests : IDisposable
{
    private readonly string _dir;

    public TerminalRetentionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"wta-retention-{Guid.NewGuid():N}");
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
        TerminalRetention.Apply(Path.Combine(_dir, "missing"), retainDays: 7, maxBytes: 100, Serilog.Log.Logger);
        // не бросило — этого достаточно
    }

    [Fact]
    public void Apply_deletes_files_older_than_retainDays_keeps_recent()
    {
        string old = MakeFile("old.json", 10, DateTime.UtcNow.AddDays(-10));
        string recent = MakeFile("recent.json", 10, DateTime.UtcNow);

        TerminalRetention.Apply(_dir, retainDays: 5, maxBytes: 0, Serilog.Log.Logger);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Apply_retainDays_zero_disables_age_pruning()
    {
        string old = MakeFile("old.json", 10, DateTime.UtcNow.AddYears(-5));

        TerminalRetention.Apply(_dir, retainDays: 0, maxBytes: 0, Serilog.Log.Logger);

        Assert.True(File.Exists(old));
    }

    [Fact]
    public void Apply_maxBytes_deletes_oldest_first_until_under_cap()
    {
        string s1 = MakeFile("s1.out", 100, DateTime.UtcNow.AddMinutes(-30));
        string s2 = MakeFile("s2.out", 100, DateTime.UtcNow.AddMinutes(-20));
        string s3 = MakeFile("s3.out", 100, DateTime.UtcNow.AddMinutes(-10));

        TerminalRetention.Apply(_dir, retainDays: 0, maxBytes: 250, Serilog.Log.Logger);

        Assert.False(File.Exists(s1));
        Assert.True(File.Exists(s2));
        Assert.True(File.Exists(s3));
    }

    [Fact]
    public void Apply_maxBytes_zero_disables_size_pruning()
    {
        string big = MakeFile("big.out", 10_000, DateTime.UtcNow);

        TerminalRetention.Apply(_dir, retainDays: 0, maxBytes: 0, Serilog.Log.Logger);

        Assert.True(File.Exists(big));
    }

    [Fact]
    public void Apply_age_then_size_both_active_prunes_by_both_rules()
    {
        string veryOld = MakeFile("very-old.json", 50, DateTime.UtcNow.AddDays(-30));
        string old = MakeFile("old.out", 100, DateTime.UtcNow.AddDays(-2));
        string recent = MakeFile("recent.out", 100, DateTime.UtcNow);

        // age (retainDays=10) убирает только very-old; из оставшихся (old+recent=200)
        // size-кап 150 должен убрать ещё и old (самый старый из оставшихся).
        TerminalRetention.Apply(_dir, retainDays: 10, maxBytes: 150, Serilog.Log.Logger);

        Assert.False(File.Exists(veryOld));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }
}
