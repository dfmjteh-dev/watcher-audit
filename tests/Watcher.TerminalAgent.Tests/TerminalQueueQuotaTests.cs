using Watcher.TerminalAgent;
using Xunit;

namespace Watcher.TerminalAgent.Tests;

public class TerminalQueueQuotaTests : IDisposable
{
    private readonly string _dir;

    public TerminalQueueQuotaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"wta-quota-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string MakeInputRecord(string name, int sizeBytes, DateTime lastWriteUtc)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    [Fact]
    public void ApplyInput_zero_maxBytes_means_unlimited_does_nothing()
    {
        string f = MakeInputRecord("a.json", 100, DateTime.UtcNow.AddMinutes(-10));

        int dropped = TerminalQueueQuota.ApplyInput(_dir, maxBytes: 0, quietSeconds: 5, Serilog.Log.Logger);

        Assert.Equal(0, dropped);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void ApplyInput_over_cap_deletes_oldest_quiet_record()
    {
        string oldest = MakeInputRecord("old.json", 100, DateTime.UtcNow.AddMinutes(-30));
        string newest = MakeInputRecord("new.json", 100, DateTime.UtcNow.AddMinutes(-20));

        int dropped = TerminalQueueQuota.ApplyInput(_dir, maxBytes: 150, quietSeconds: 5, Serilog.Log.Logger);

        Assert.Equal(1, dropped);
        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void ApplyInput_does_not_delete_a_record_still_being_written()
    {
        string notQuiet = MakeInputRecord("active.json", 200, DateTime.UtcNow);
        string quiet = MakeInputRecord("older-but-quiet.json", 50, DateTime.UtcNow.AddMinutes(-10));

        int dropped = TerminalQueueQuota.ApplyInput(_dir, maxBytes: 100, quietSeconds: 5, Serilog.Log.Logger);

        Assert.Equal(1, dropped);
        Assert.True(File.Exists(notQuiet));
        Assert.False(File.Exists(quiet));
    }

    private (string json, string outPath, string timing) MakeOutputGroup(string baseName, int outSize, DateTime lastWriteUtc)
    {
        string json = Path.Combine(_dir, baseName + ".json");
        string outPath = Path.Combine(_dir, baseName + ".out");
        string timing = Path.Combine(_dir, baseName + ".timing");
        File.WriteAllText(json, "{}");
        File.WriteAllBytes(outPath, new byte[outSize]);
        File.WriteAllText(timing, "0.1 5");
        foreach (var p in new[] { json, outPath, timing }) File.SetLastWriteTimeUtc(p, lastWriteUtc);
        return (json, outPath, timing);
    }

    [Fact]
    public void ApplyOutput_over_cap_deletes_whole_oldest_group_not_just_json()
    {
        var oldest = MakeOutputGroup("old", outSize: 100, DateTime.UtcNow.AddMinutes(-30));
        var newest = MakeOutputGroup("new", outSize: 100, DateTime.UtcNow.AddMinutes(-20));

        // Группа ~= json(2B)+out(100B)+timing(5B) =~ 107B, итого две группы
        // ~214B. Лимит 150B — меньше суммы обеих, но больше одной: должна
        // уйти ровно одна (самая старая) целиком.
        int dropped = TerminalQueueQuota.ApplyOutput(_dir, maxBytes: 150, quietSeconds: 5, Serilog.Log.Logger);

        Assert.Equal(1, dropped);
        Assert.False(File.Exists(oldest.json));
        Assert.False(File.Exists(oldest.outPath));
        Assert.False(File.Exists(oldest.timing));
        Assert.True(File.Exists(newest.json));
        Assert.True(File.Exists(newest.outPath));
        Assert.True(File.Exists(newest.timing));
    }

    [Fact]
    public void ApplyOutput_missing_sibling_files_does_not_throw()
    {
        // Только .json, без .out/.timing (например, запись оборвалась) —
        // размер группы = только json, но удаление не должно падать на
        // отсутствующих файлах.
        string json = Path.Combine(_dir, "orphan.json");
        File.WriteAllText(json, "{}");
        File.SetLastWriteTimeUtc(json, DateTime.UtcNow.AddMinutes(-30));

        int dropped = TerminalQueueQuota.ApplyOutput(_dir, maxBytes: 1, quietSeconds: 5, Serilog.Log.Logger);

        Assert.Equal(1, dropped);
        Assert.False(File.Exists(json));
    }

    [Fact]
    public void ApplyOutput_under_cap_does_nothing()
    {
        var g = MakeOutputGroup("a", outSize: 100, DateTime.UtcNow.AddMinutes(-10));

        int dropped = TerminalQueueQuota.ApplyOutput(_dir, maxBytes: 10_000, quietSeconds: 5, Serilog.Log.Logger);

        Assert.Equal(0, dropped);
        Assert.True(File.Exists(g.json));
    }

    // 0.13: запись удаляется по сроку, только если ВСЕ её файлы старше —
    // идущая сессия дописывает .out и остаётся.
    [Fact]
    public void ApplyAge_drops_whole_old_records_and_keeps_live_ones()
    {
        var old = DateTime.UtcNow.AddDays(-40);
        string a1 = MakeInputRecord("20260901-100000-5.json", 10, old);
        string b1 = MakeInputRecord("user-1756720000-42.json", 10, old);
        string b2 = MakeInputRecord("user-1756720000-42.out", 1000, old);
        string b3 = MakeInputRecord("user-1756720000-42.timing", 10, old);
        string c1 = MakeInputRecord("user-1756720500-43.start", 10, old);
        string c2 = MakeInputRecord("user-1756720500-43.out", 1000, DateTime.UtcNow.AddMinutes(-1));
        string fresh = MakeInputRecord("20261013-100000-6.json", 10, DateTime.UtcNow.AddDays(-2));

        int dropped = TerminalQueueQuota.ApplyAge(_dir, 30, DateTime.UtcNow, Serilog.Log.Logger);

        Assert.Equal(2, dropped);
        Assert.False(File.Exists(a1));
        Assert.False(File.Exists(b1) || File.Exists(b2) || File.Exists(b3));
        Assert.True(File.Exists(c1) && File.Exists(c2));
        Assert.True(File.Exists(fresh));
        Assert.Equal(0, TerminalQueueQuota.ApplyAge(_dir, 0, DateTime.UtcNow.AddYears(1), Serilog.Log.Logger));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Queue_age_from_server_survives_restart()
    {
        string path = Path.Combine(_dir, "agent", "queue-max-age-days");
        Assert.Null(QueueAgeState.Load(path));
        QueueAgeState.Save(path, 7, Serilog.Log.Logger);
        Assert.Equal(7, QueueAgeState.Load(path));
        File.WriteAllText(path, "99999");
        Assert.Null(QueueAgeState.Load(path));
    }
}
