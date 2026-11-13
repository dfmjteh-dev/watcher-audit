using System.Security.Cryptography;
using Watcher;
using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

public sealed partial class MaintenanceServerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "watcher-maint-server-" + Guid.NewGuid().ToString("N"));
    private readonly Db _db;
    public MaintenanceServerTests() { Directory.CreateDirectory(_dir); _db = new Db(Path.Combine(_dir, "db")); }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }
    private AgentCredential Credential(string name)
    {
        _db.EnsureHostExists(name); long id = _db.Inventory(onlyName: name).Single().Id!.Value;
        using var c = _db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO agent_credentials(host_id,kind,secret_hash,created_at) VALUES($h,'video',$s,$now) RETURNING id";
        cmd.Parameters.AddWithValue("$h", id); cmd.Parameters.AddWithValue("$s", AgentAuth.Hash("test-" + name)); cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        return _db.AgentCredentialById(Convert.ToInt64(cmd.ExecuteScalar()))!;
    }
    [Fact]
    public void Signed_command_is_owned_by_credential_and_completion_unlocks_host_cleanup()
    {
        var a = Credential("a"); var b = Credential("b"); var now = DateTime.UtcNow;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var command = MaintenanceSigning.Sign(new(Guid.NewGuid().ToString("N"), _db.CredentialBinding(a.Id)!, "video", "uninstall", now, now.AddMinutes(15)), key);
        _db.IssueMaintenance(a, command, "admin"); Assert.Null(_db.PendingMaintenance(b.Id, now)); Assert.Equal(command, _db.PendingMaintenance(a.Id, now));
        var actor = new AgentIdentity(a.Id, a.HostId, "a", "video");
        _db.ReceiveDiagnostics(actor, new([], MaintenanceKeyId: MaintenanceSigning.KeyId(publicKey)), now);
        Assert.Equal(HostDeleteOutcome.MaintenanceRequired, _db.DeleteHost(a.HostId).Outcome);
        Assert.Empty(_db.AcceptMaintenanceResults(new(b.Id, b.HostId, "b", "video"), [new(command.Grant.Id, "completed", "completed")], now));
        _db.AcceptMaintenanceResults(actor, [new(command.Grant.Id, "completed", "completed")], now.AddSeconds(1));
        Assert.Equal(HostDeleteOutcome.Deleted, _db.DeleteHost(a.HostId).Outcome);
    }
    [Fact]
    public void Offline_export_is_not_delivered_online_and_accepts_its_own_result()
    {
        var a = Credential("offline"); var now = DateTime.UtcNow;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var grant = MaintenanceSigning.Sign(new(Guid.NewGuid().ToString("N"), _db.CredentialBinding(a.Id)!, "video", "repair", now, now.AddMinutes(15)), key);
        _db.IssueMaintenance(a, grant, "admin", offline: true);
        Assert.Null(_db.PendingMaintenance(a.Id, now)); Assert.Equal("exported", Assert.Single(_db.MaintenanceHistory(a.HostId)).Status);
        Assert.Equal([grant.Grant.Id], _db.AcceptMaintenanceResults(new(a.Id, a.HostId, "offline", "video"), [new(grant.Grant.Id, "completed", "completed")], now));
        Assert.Equal("completed", Assert.Single(_db.MaintenanceHistory(a.HostId)).Status);
    }
    [Fact]
    public void Force_forget_never_bypasses_legal_hold()
    {
        var a = Credential("held"); _db.ReceiveDiagnostics(new(a.Id, a.HostId, "held", "video"), new([], MaintenanceKeyId: new string('a', 64)), DateTime.UtcNow);
        _db.CreateLegalHold("held", null, null, "hold", "admin"); Assert.Equal(HostDeleteOutcome.LegalHold, _db.DeleteHost(a.HostId, true).Outcome);
    }
    [Fact]
    public void Signer_rejects_missing_key_and_can_load_encrypted_key_without_exposing_password()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); string path = Path.Combine(_dir, "key.pem"), passwordPath = Path.Combine(_dir, "password");
        File.WriteAllText(path, key.ExportEncryptedPkcs8PrivateKeyPem("test-key-password", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000))); File.WriteAllText(passwordPath, "test-key-password\n");
        Assert.Null(new MaintenanceSigner(new ServerConfig { MaintenanceSigningKeyPath = path }).Key());
        using var loaded = new MaintenanceSigner(new ServerConfig { MaintenanceSigningKeyPath = path, MaintenanceKeyPasswordFile = passwordPath }).Key(); Assert.NotNull(loaded);
        Assert.Equal(key.ExportSubjectPublicKeyInfo(), loaded!.ExportSubjectPublicKeyInfo());
    }
}
