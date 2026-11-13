using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

public class ConfigTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;

    public ConfigTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"watcher-config-{Guid.NewGuid():N}.db");
        _db = new Db(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void GetGroupConfig_unset_returns_null()
    {
        long groupId = _db.CreateGroup("lobby");

        Assert.Null(_db.GetGroupConfig(groupId));
    }

    [Fact]
    public void SetGroupConfig_unknown_group_returns_null()
    {
        Assert.Null(_db.SetGroupConfig(999, "{\"a\":1}"));
    }

    [Fact]
    public void SetGroupConfig_then_get_roundtrips_and_starts_at_version_1()
    {
        long groupId = _db.CreateGroup("lobby");

        int? version = _db.SetGroupConfig(groupId, "{\"crf\":23}");

        Assert.Equal(1, version);
        var entry = _db.GetGroupConfig(groupId);
        Assert.NotNull(entry);
        Assert.Equal("{\"crf\":23}", entry!.ConfigJson);
        Assert.Equal(1, entry.ConfigVersion);
    }

    [Fact]
    public void SetGroupConfig_twice_increments_version()
    {
        long groupId = _db.CreateGroup("lobby");
        _db.SetGroupConfig(groupId, "{\"crf\":23}");

        int? version = _db.SetGroupConfig(groupId, "{\"crf\":30}");

        Assert.Equal(2, version);
        Assert.Equal("{\"crf\":30}", _db.GetGroupConfig(groupId)!.ConfigJson);
    }

    [Fact]
    public void ClearGroupConfig_removes_it()
    {
        long groupId = _db.CreateGroup("lobby");
        _db.SetGroupConfig(groupId, "{\"crf\":23}");

        bool cleared = _db.ClearGroupConfig(groupId);

        Assert.True(cleared);
        Assert.Null(_db.GetGroupConfig(groupId));
    }

    [Fact]
    public void ClearGroupConfig_unknown_group_returns_false()
    {
        Assert.False(_db.ClearGroupConfig(999));
    }

    [Fact]
    public void SetHostConfig_unknown_host_returns_null()
    {
        Assert.Null(_db.SetHostConfig(999, "{\"crf\":23}"));
    }

    [Fact]
    public void SetHostConfig_then_get_roundtrips()
    {
        _db.UpsertHeartbeat("station-1", null, null);
        long hostId = _db.ListFleetHosts(null, 10, 0).Single().Id;

        int? version = _db.SetHostConfig(hostId, "{\"crf\":18}");

        Assert.Equal(1, version);
        Assert.Equal("{\"crf\":18}", _db.GetHostConfig(hostId)!.ConfigJson);
    }

    [Fact]
    public void ClearHostConfig_removes_override()
    {
        _db.UpsertHeartbeat("station-1", null, null);
        long hostId = _db.ListFleetHosts(null, 10, 0).Single().Id;
        _db.SetHostConfig(hostId, "{\"crf\":18}");

        Assert.True(_db.ClearHostConfig(hostId));
        Assert.Null(_db.GetHostConfig(hostId));
    }

    [Fact]
    public void GetEffectiveConfig_unknown_host_returns_null()
    {
        Assert.Null(_db.GetEffectiveConfig("ghost"));
    }

    [Fact]
    public void GetEffectiveConfig_no_group_no_override_returns_empty_object()
    {
        // Пустой объект, не null: агент обновляет кэш только объектом — иначе
        // очищенный конфиг группы оставался бы на станции.
        _db.UpsertHeartbeat("station-1", null, null);

        Assert.Equal("{}", _db.GetEffectiveConfig("station-1")!.ConfigJson);
    }

    [Fact]
    public void GetEffectiveConfig_falls_back_to_group_config()
    {
        long groupId = _db.CreateGroup("lobby");
        _db.SetGroupConfig(groupId, "{\"crf\":30}");
        _db.UpsertHeartbeat("station-1", null, null);
        long hostId = _db.ListFleetHosts(null, 10, 0).Single().Id;
        _db.SetHostGroup(hostId, groupId);

        var effective = _db.GetEffectiveConfig("station-1");

        Assert.NotNull(effective);
        Assert.Equal("{\"crf\":30}", effective!.ConfigJson);
        Assert.Equal(1, effective.ConfigVersion);
    }

    [Fact]
    public void GetEffectiveConfig_host_override_wins_over_group_config()
    {
        long groupId = _db.CreateGroup("lobby");
        _db.SetGroupConfig(groupId, "{\"crf\":30}");
        _db.UpsertHeartbeat("station-1", null, null);
        long hostId = _db.ListFleetHosts(null, 10, 0).Single().Id;
        _db.SetHostGroup(hostId, groupId);
        _db.SetHostConfig(hostId, "{\"crf\":18}");

        var effective = _db.GetEffectiveConfig("station-1");

        Assert.Equal("{\"crf\":18}", effective!.ConfigJson);
    }

    [Fact]
    public void GetEffectiveConfig_merges_host_over_group_per_field()
    {
        long groupId = _db.CreateGroup("lobby");
        _db.SetGroupConfig(groupId, "{\"crf\":30,\"excludeUsers\":[\"svc\"],\"outputFps\":2}");
        _db.UpsertHeartbeat("station-1", null, null);
        long hostId = _db.ListFleetHosts(null, 10, 0).Single().Id;
        _db.SetHostGroup(hostId, groupId);
        _db.SetHostConfig(hostId, "{\"crf\":18,\"outputFps\":null}");

        var effective = _db.GetEffectiveConfig("station-1");

        // crf — с хоста, excludeUsers — из группы, null у хоста не затирает группу.
        Assert.Equal("{\"crf\":18,\"excludeUsers\":[\"svc\"],\"outputFps\":2}", effective!.ConfigJson);
        Assert.Equal(2, effective.ConfigVersion);
    }

    [Fact]
    public void GetEffectiveConfig_group_without_config_but_host_has_override()
    {
        long groupId = _db.CreateGroup("lobby");
        _db.UpsertHeartbeat("station-1", null, null);
        long hostId = _db.ListFleetHosts(null, 10, 0).Single().Id;
        _db.SetHostGroup(hostId, groupId);
        _db.SetHostConfig(hostId, "{\"crf\":18}");

        var effective = _db.GetEffectiveConfig("station-1");

        Assert.Equal("{\"crf\":18}", effective!.ConfigJson);
    }

    [Fact]
    public void Reopening_db_from_phase3_schema_migrates_config_columns()
    {
        // Симулирует апгрейд: БД создана БЕЗ config_json/config_version (как Фаза 3),
        // затем открывается текущим Db (Фаза 4) — EnsureColumn должен доехать молча,
        // без падения на "no such column" и без потери существующих данных.
        string legacyDbPath = Path.Combine(Path.GetTempPath(), $"watcher-legacy-{Guid.NewGuid():N}.db");
        try
        {
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={legacyDbPath}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"
CREATE TABLE host_groups (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE);
CREATE TABLE hosts (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE,
  ip_address TEXT, group_id INTEGER, last_heartbeat_at TEXT, agent_version TEXT);
INSERT INTO host_groups (name) VALUES ('legacy-group');
INSERT INTO hosts (name, ip_address) VALUES ('legacy-host', '10.0.0.9');
";
                cmd.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            var migrated = new Db(legacyDbPath);

            var groups = migrated.ListGroups();
            Assert.Contains(groups, g => g.Name == "legacy-group");
            long groupId = groups.Single(g => g.Name == "legacy-group").Id;
            Assert.Null(migrated.GetGroupConfig(groupId));
            Assert.Equal(1, migrated.SetGroupConfig(groupId, "{\"crf\":30}"));

            var hosts = migrated.ListFleetHosts(null, 10, 0);
            Assert.Contains(hosts, h => h.Name == "legacy-host" && h.IpAddress == "10.0.0.9");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { legacyDbPath, legacyDbPath + "-wal", legacyDbPath + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }
}
