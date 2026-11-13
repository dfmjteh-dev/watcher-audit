using Watcher;
using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

public sealed class ServerDiagnosticTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "watcher-diag-db-" + Guid.NewGuid().ToString("N"));
    private readonly Db _db;
    public ServerDiagnosticTests() { Directory.CreateDirectory(_dir); _db = new Db(Path.Combine(_dir, "db")); }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }
    private AgentIdentity Identity(string name)
    {
        _db.EnsureHostExists(name); long host = _db.Inventory(onlyName: name).Single().Id!.Value;
        using var c = _db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO agent_credentials(host_id,kind,secret_hash,created_at) VALUES($h,'video',$secret,$now) RETURNING id";
        cmd.Parameters.AddWithValue("$h", host); cmd.Parameters.AddWithValue("$secret", Guid.NewGuid().ToString("N")); cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        return new(Convert.ToInt64(cmd.ExecuteScalar()), host, name, "video");
    }
    [Fact]
    public void Delivery_is_deduplicated_scoped_and_rate_limited()
    {
        var now = DateTime.UtcNow; var a = Identity("a"); var b = Identity("b");
        var e = new AgentDiagnostic(Guid.NewGuid().ToString("N"), now, "warning", "uploader", "upload-failed"); var batch = new DiagnosticBatch([e]);
        Assert.Equal([e.Id], _db.ReceiveDiagnostics(a, batch, now)!.Accepted);
        Assert.Null(_db.ReceiveDiagnostics(a, batch, now.AddSeconds(1)));
        Assert.Equal([e.Id], _db.ReceiveDiagnostics(a, batch, now.AddSeconds(10))!.Accepted);
        Assert.Single(_db.DiagnosticEvents(a.HostId)); Assert.Empty(_db.DiagnosticEvents(b.HostId));
        _db.PruneDiagnostics(now.AddDays(15)); Assert.Empty(_db.DiagnosticEvents(a.HostId));
    }
    [Fact]
    public void Requested_package_is_bound_to_credential_and_receipt_survives_replay()
    {
        var now = DateTime.UtcNow; var a = Identity("a"); var b = Identity("b");
        _db.RequestDiagnostics(a.HostId, now, "admin");
        string request = _db.ReceiveDiagnostics(a, new([]), now)!.RequestId!; Assert.NotNull(request);
        var snapshot = new DiagnosticSnapshot(request, now, "0.15.1", "windows", 1, 1, 1, 1, 100);
        Assert.Null(_db.ReceiveDiagnostics(b, new([], snapshot), now)!.SnapshotAccepted);
        Assert.Equal(request, _db.ReceiveDiagnostics(a, new([], snapshot), now.AddSeconds(10))!.SnapshotAccepted);
        Assert.Equal(request, _db.ReceiveDiagnostics(a, new([], snapshot), now.AddSeconds(20))!.SnapshotAccepted);
        Assert.NotNull(Assert.Single(_db.DiagnosticStates(a.HostId)).SnapshotJson);
        _db.PruneDiagnostics(now.AddDays(15)); Assert.Null(Assert.Single(_db.DiagnosticStates(a.HostId)).SnapshotJson);
    }
}
