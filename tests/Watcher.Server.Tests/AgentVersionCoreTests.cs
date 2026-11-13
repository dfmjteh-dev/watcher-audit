using Microsoft.Data.Sqlite;
using Xunit;

namespace Watcher.Server.Tests;

// Версия агента без «+<коммит>» при приёме heartbeat (сохранённые строки — миграция 32, DbMigrationsTests).
public class AgentVersionCoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"watcher-version-{Guid.NewGuid():N}");
    private readonly string _path;

    public AgentVersionCoreTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "watcher.db");
    }

    [Fact]
    public void Heartbeat_StoresVersionWithoutCommit()
    {
        var db = new Db(_path);
        db.UpsertHeartbeat("pc-a", null, "0.15.1+3d600336271884e116d431e83cddc7cdba053c24");
        db.UpsertHeartbeat("pc-b", null, "0.15.3");
        var hosts = db.Inventory().ToDictionary(h => h.Name, h => h.AgentVersion);
        Assert.Equal("0.15.1", hosts["pc-a"]);
        Assert.Equal("0.15.3", hosts["pc-b"]);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
