using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Watcher.Server.Tests;

public class AndroidEventsDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"watcher-events-db-{Guid.NewGuid():N}");
    private readonly Db _db;

    public AndroidEventsDbTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new Db(Path.Combine(_dir, "watcher.db"));
    }

    private long Host(string name)
    {
        _db.UpsertHeartbeat(name, null, "1.0");
        return _db.Inventory().Single(h => h.Name == name).Id!.Value;
    }

    private string Add(long host, int daysAgo)
    {
        string id = Guid.NewGuid().ToString("D");
        Assert.Equal(1, _db.InsertAndroidEvents(host, [new(DateTime.UtcNow.AddDays(-daysAgo).ToString("o"), "device_state", null, "{}", id)]));
        return id;
    }

    private void AgeReceived(string id, int daysAgo)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE android_events SET received_at = $ts WHERE event_id = $id";
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.AddDays(-daysAgo).ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private void Retention(int days) => new RetentionService(_db,
        new ServerConfig { DataDir = _dir, RetentionDays = days, MinFreeDiskPercent = 0 },
        NullLogger<RetentionService>.Instance).RunOnce();

    [Fact]
    public void Idempotency_is_per_host_and_does_not_replace_payload_or_received_time()
    {
        long first = Host("ONE"), second = Host("TWO");
        string id = Add(first, 1);
        var original = _db.ListAndroidEvents("ONE", 10, 0).Single();
        var changed = new AndroidEventInput(DateTime.UtcNow.ToString("o"), "device_state", null, "{\"changed\":true}", id);
        Assert.Equal(0, _db.InsertAndroidEvents(first, [changed, changed]));
        Assert.Equal(original, _db.ListAndroidEvents("ONE", 10, 0).Single());
        Assert.Equal(1, _db.InsertAndroidEvents(second, [changed]));
    }

    [Fact]
    public void Pagination_breaks_timestamp_ties_with_id_and_caps_limit()
    {
        long host = Host("PHONE");
        string ts = DateTime.UtcNow.ToString("o");
        var events = Enumerable.Range(0, 1005).Select(_ => new AndroidEventInput(ts, "device_state", null, null, Guid.NewGuid().ToString("D"))).ToArray();
        Assert.Equal(1005, _db.InsertAndroidEvents(host, events));
        var first = _db.ListAndroidEvents("PHONE", 2, 0);
        var next = _db.ListAndroidEvents("PHONE", 2, 2);
        Assert.True(first[0].Id > first[1].Id && first[1].Id > next[0].Id);
        Assert.Empty(first.Select(x => x.Id).Intersect(next.Select(x => x.Id)));
        Assert.Equal(1000, _db.ListAndroidEvents("PHONE", int.MaxValue, -1).Count);
    }

    [Fact]
    public void Retention_requires_both_device_and_received_time_to_be_old_and_zero_disables_it()
    {
        long host = Host("PHONE");
        string old = Add(host, 100);
        AgeReceived(old, 100);
        Add(host, 100); // неверные часы телефона, но событие принято только сейчас
        string newEvent = Add(host, 1);
        AgeReceived(newEvent, 100);
        Retention(0);
        Assert.Equal(3, _db.CountAndroidEvents("PHONE"));
        Retention(30);
        Assert.Equal(2, _db.CountAndroidEvents("PHONE"));
        Assert.DoesNotContain(_db.ListAndroidEvents("PHONE", 10, 0), e => e.EventId == old);
    }

    [Fact]
    public void Group_retention_overrides_default_including_zero_and_global_zero()
    {
        long plain = Host("PLAIN"), keep = Host("KEEP"), shortHost = Host("SHORT");
        long keepGroup = _db.CreateGroup("keep"), shortGroup = _db.CreateGroup("short");
        _db.SetHostGroup(keep, keepGroup);
        _db.SetHostGroup(shortHost, shortGroup);
        _db.SetGroupRetention(keepGroup, 0);
        _db.SetGroupRetention(shortGroup, 7);
        AgeReceived(Add(plain, 40), 40);
        AgeReceived(Add(keep, 40), 40);
        AgeReceived(Add(shortHost, 10), 10);
        Retention(0);
        Assert.Equal(1, _db.CountAndroidEvents("PLAIN"));
        Assert.Equal(1, _db.CountAndroidEvents("KEEP"));
        Assert.Equal(0, _db.CountAndroidEvents("SHORT"));
        Retention(30);
        Assert.Equal(0, _db.CountAndroidEvents("PLAIN"));
        Assert.Equal(1, _db.CountAndroidEvents("KEEP"));
    }

    [Fact]
    public void Insert_after_host_deletion_cannot_leave_orphaned_events()
    {
        long host = Host("PHONE");
        Assert.Equal(HostDeleteOutcome.Deleted, _db.DeleteHost(host).Outcome);
        Assert.Equal(-1, _db.InsertAndroidEvents(host, [new(DateTime.UtcNow.ToString("o"), "device_state", null, null, Guid.NewGuid().ToString("D"))]));
        Assert.Equal(0, _db.CountAndroidEvents("PHONE"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }
}
