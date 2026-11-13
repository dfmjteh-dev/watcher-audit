using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

public class FleetTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;

    public FleetTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"watcher-fleet-{Guid.NewGuid():N}.db");
        _db = new Db(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void UpsertHeartbeat_new_host_creates_row()
    {
        _db.UpsertHeartbeat("station-1", "10.0.0.5", "0.1.0+abc");

        var rows = _db.ListFleetHosts(groupId: null, limit: 10, offset: 0);

        Assert.Single(rows);
        Assert.Equal("station-1", rows[0].Name);
        Assert.Equal("10.0.0.5", rows[0].IpAddress);
        Assert.Equal("0.1.0", rows[0].AgentVersion); // хвост коммита отрезается при приёме
        Assert.NotNull(rows[0].LastHeartbeatAt);
        Assert.Null(rows[0].GroupId);
        Assert.Equal(0, rows[0].DroppedSegments);
    }

    [Fact]
    public void UpsertHeartbeat_stores_dropped_segments_and_overwrites_on_next_heartbeat()
    {
        _db.UpsertHeartbeat("station-1", "10.0.0.5", "0.1.0", droppedSegments: 3);
        Assert.Equal(3, _db.ListFleetHosts(null, 10, 0).Single().DroppedSegments);

        // Счётчик агента монотонный с момента ЕГО старта, не накопительный на сервере —
        // сервер просто хранит последнее сообщённое значение (см. комментарий в Db.cs).
        _db.UpsertHeartbeat("station-1", "10.0.0.5", "0.1.0", droppedSegments: 7);
        Assert.Equal(7, _db.ListFleetHosts(null, 10, 0).Single().DroppedSegments);
    }

    [Fact]
    public void UpsertHeartbeat_same_host_twice_updates_not_duplicates()
    {
        _db.UpsertHeartbeat("station-1", "10.0.0.5", "0.1.0");
        _db.UpsertHeartbeat("station-1", "10.0.0.6", "0.1.1");

        var rows = _db.ListFleetHosts(groupId: null, limit: 10, offset: 0);

        Assert.Single(rows);
        Assert.Equal("10.0.0.6", rows[0].IpAddress);
        Assert.Equal("0.1.1", rows[0].AgentVersion);
    }

    [Fact]
    public void UpsertHeartbeat_preserves_group_assignment()
    {
        _db.UpsertHeartbeat("station-1", "10.0.0.5", "0.1.0");
        long hostId = _db.ListFleetHosts(null, 10, 0).Single().Id;
        long groupId = _db.CreateGroup("lobby");
        _db.SetHostGroup(hostId, groupId);

        _db.UpsertHeartbeat("station-1", "10.0.0.5", "0.1.1");

        var row = _db.ListFleetHosts(null, 10, 0).Single();
        Assert.Equal(groupId, row.GroupId);
    }

    [Fact]
    public void CreateGroup_duplicate_name_throws()
    {
        _db.CreateGroup("lobby");

        Assert.ThrowsAny<Exception>(() => _db.CreateGroup("lobby"));
    }

    [Fact]
    public void ListGroups_includes_host_count()
    {
        long groupId = _db.CreateGroup("lobby");
        _db.UpsertHeartbeat("station-1", null, null);
        _db.UpsertHeartbeat("station-2", null, null);
        var ids = _db.ListFleetHosts(null, 10, 0);
        _db.SetHostGroup(ids.First(h => h.Name == "station-1").Id, groupId);

        var groups = _db.ListGroups();

        var lobby = groups.Single(g => g.Name == "lobby");
        Assert.Equal(1, lobby.HostCount);
    }

    [Fact]
    public void DeleteGroup_clears_group_id_on_hosts_instead_of_failing()
    {
        long groupId = _db.CreateGroup("lobby");
        _db.UpsertHeartbeat("station-1", null, null);
        long hostId = _db.ListFleetHosts(null, 10, 0).Single().Id;
        _db.SetHostGroup(hostId, groupId);

        bool deleted = _db.DeleteGroup(groupId);

        Assert.True(deleted);
        var row = _db.ListFleetHosts(null, 10, 0).Single();
        Assert.Null(row.GroupId);
    }

    [Fact]
    public void DeleteGroup_unknown_id_returns_false()
    {
        Assert.False(_db.DeleteGroup(999));
    }

    [Fact]
    public void RenameGroup_duplicate_name_throws()
    {
        _db.CreateGroup("lobby");
        long otherId = _db.CreateGroup("entrance");

        Assert.ThrowsAny<Exception>(() => _db.RenameGroup(otherId, "lobby"));
    }

    [Fact]
    public void ListFleetHosts_filters_by_group()
    {
        long groupId = _db.CreateGroup("lobby");
        _db.UpsertHeartbeat("station-1", null, null);
        _db.UpsertHeartbeat("station-2", null, null);
        _db.SetHostGroup(_db.ListFleetHosts(null, 10, 0).First(h => h.Name == "station-1").Id, groupId);

        var filtered = _db.ListFleetHosts(groupId, 10, 0);

        Assert.Single(filtered);
        Assert.Equal("station-1", filtered[0].Name);
    }

    [Fact]
    public void ListFleetHosts_respects_limit_and_offset()
    {
        for (int i = 0; i < 5; i++) _db.UpsertHeartbeat($"station-{i}", null, null);

        var page1 = _db.ListFleetHosts(null, limit: 2, offset: 0);
        var page2 = _db.ListFleetHosts(null, limit: 2, offset: 2);

        Assert.Equal(2, page1.Count);
        Assert.Equal(2, page2.Count);
        Assert.NotEqual(page1[0].Name, page2[0].Name);
        Assert.Equal(5, _db.CountFleetHosts(null));
    }

    [Fact]
    public void SetHostGroup_unknown_host_returns_false()
    {
        Assert.False(_db.SetHostGroup(999, null));
    }
}
