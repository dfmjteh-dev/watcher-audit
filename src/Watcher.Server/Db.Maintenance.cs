using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Watcher.Server;

public sealed record MaintenanceRow(string Id, long CredentialId, string Action, string Status, string Code, string IssuedAt, string ExpiresAt, UpdateProgressReport? Progress = null, string? ProgressReceivedAt = null, string? UpdatedAt = null);
public sealed partial class Db
{
    private static void AgentMaintenance(SqliteConnection c)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = @"ALTER TABLE agent_diagnostic_state ADD COLUMN maintenance_key_id TEXT;
CREATE TABLE agent_maintenance(id TEXT PRIMARY KEY,credential_id INTEGER NOT NULL,host_id INTEGER NOT NULL,action TEXT NOT NULL,
status TEXT NOT NULL,code TEXT NOT NULL,updated_at TEXT,issued_at TEXT NOT NULL,expires_at TEXT NOT NULL,grant_json TEXT NOT NULL);
CREATE INDEX ix_agent_maintenance_credential ON agent_maintenance(credential_id,issued_at DESC);
CREATE INDEX ix_agent_maintenance_host ON agent_maintenance(host_id,issued_at DESC);"; cmd.ExecuteNonQuery();
    }
    public string? CredentialBinding(long id)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT secret_hash FROM agent_credentials WHERE id=$id AND revoked_at IS NULL";
        cmd.Parameters.AddWithValue("$id", id); return cmd.ExecuteScalar() is string hash ? MaintenanceSigning.BindingFromHash(hash) : null;
    }
    public void IssueMaintenance(AgentCredential credential, SignedMaintenanceGrant grant, string actor, bool offline = false)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO agent_maintenance(id,credential_id,host_id,action,status,code,issued_at,expires_at,grant_json)
VALUES($id,$cid,$h,$action,$state,$state,$at,$expires,$grant)";
        cmd.Parameters.AddWithValue("$id", grant.Grant.Id); cmd.Parameters.AddWithValue("$cid", credential.Id); cmd.Parameters.AddWithValue("$h", credential.HostId);
        cmd.Parameters.AddWithValue("$state", offline ? "exported" : "queued");
        cmd.Parameters.AddWithValue("$action", grant.Grant.Action); cmd.Parameters.AddWithValue("$at", grant.Grant.IssuedAt.ToString("o")); cmd.Parameters.AddWithValue("$expires", grant.Grant.ExpiresAt.ToString("o"));
        cmd.Parameters.AddWithValue("$grant", JsonSerializer.Serialize(grant, DiagnosticJson)); cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO audit_log(ts,username,action,target,details) VALUES($at,$actor,'agent.maintenance-issued',$target,$details)";
        cmd.Parameters.AddWithValue("$actor", actor); cmd.Parameters.AddWithValue("$target", "host:" + credential.HostId);
        cmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new { id = grant.Grant.Id, credentialId = credential.Id, action = grant.Grant.Action, expiresAt = grant.Grant.ExpiresAt })); cmd.ExecuteNonQuery(); tx.Commit();
    }
    public SignedMaintenanceGrant? PendingMaintenance(long credentialId, DateTime now)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.Parameters.AddWithValue("$cid", credentialId); cmd.Parameters.AddWithValue("$now", now.ToString("o"));
        for (int attempt = 0; attempt < 16; attempt++)
        {
            cmd.CommandText = @"SELECT m.id,m.grant_json,m.code,g.agent_target_version,d.maintenance_key_id,h.agent_version
FROM agent_maintenance m JOIN hosts h ON h.id=m.host_id LEFT JOIN host_groups g ON g.id=h.group_id
LEFT JOIN agent_diagnostic_state d ON d.credential_id=m.credential_id
WHERE m.credential_id=$cid AND m.status='queued' AND m.expires_at>$now ORDER BY m.issued_at,m.id LIMIT 1";
            string id, value, code; string? target, pin, version;
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return null;
                id = r.GetString(0); value = r.GetString(1); code = r.GetString(2);
                target = r.IsDBNull(3) ? null : r.GetString(3); pin = r.IsDBNull(4) ? null : r.GetString(4);
                version = r.IsDBNull(5) ? null : r.GetString(5);
            }
            var grant = JsonSerializer.Deserialize<SignedMaintenanceGrant>(value, DiagnosticJson);
            if (!code.StartsWith("group-update:", StringComparison.Ordinal)) return grant;
            // Заявка на базу (текущая версия хоста ради цели группы) — своя проверка:
            // актуальна, пока хост на этой версии и у группы есть другая цель.
            string? reason = code == "group-update:base-seed"
                ? pin != grant?.KeyId ? "pin-changed" : target is null || target == grant?.Grant.Version || version is not null && AppVersion.Core(version) != grant?.Grant.Version ? "target-changed" : null
                : target != grant?.Grant.Version ? "target-changed" : pin != grant?.KeyId ? "pin-changed"
                : AppVersion.Core(version ?? "") == target ? "target-met" : null;
            if (reason is null) return grant;
            cmd.CommandText = "UPDATE agent_maintenance SET status='rejected',code=$code,updated_at=$now WHERE id=$id AND status='queued'";
            cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$code", "group-update:" + reason);
            int changed = cmd.ExecuteNonQuery();
            if (changed > 0 && reason == "target-met")
            {
                cmd.CommandText = "UPDATE hosts SET update_status=$status,update_status_at=$now WHERE id=(SELECT host_id FROM agent_maintenance WHERE id=$id)";
                cmd.Parameters.AddWithValue("$status", "ok " + target); cmd.ExecuteNonQuery(); cmd.Parameters.RemoveAt("$status");
            }
            cmd.Parameters.RemoveAt("$id"); cmd.Parameters.RemoveAt("$code");
        }
        return null;
    }

    public List<string> AcceptMaintenanceResults(AgentIdentity identity, IEnumerable<MaintenanceResult> results, DateTime now)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); var accepted = new List<string>();
        foreach (var result in results.Take(16))
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$id", result.GrantId); cmd.Parameters.AddWithValue("$cid", identity.CredentialId);
            cmd.CommandText = "SELECT status,code,grant_json FROM agent_maintenance WHERE id=$id AND credential_id=$cid";
            string old, previousCode, document;
            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read()) continue;
                old = reader.GetString(0); previousCode = reader.GetString(1); document = reader.GetString(2);
            }
            bool groupUpdate = previousCode.StartsWith("group-update:", StringComparison.Ordinal);
            accepted.Add(result.GrantId);
            if (old == "completed" && result.Status == "completed")
            {
                cmd.CommandText = "UPDATE agent_maintenance SET updated_at=$now WHERE id=$id AND credential_id=$cid AND action='uninstall'";
                cmd.Parameters.AddWithValue("$now", now.ToString("o")); cmd.ExecuteNonQuery(); continue;
            }
            if (old == result.Status || old is "completed" or "failed" or "rejected") continue;
            cmd.Parameters.AddWithValue("$now", now.ToString("o"));
            cmd.CommandText = "UPDATE agent_maintenance SET status=$state,code=$code,updated_at=$now WHERE id=$id AND credential_id=$cid";
            cmd.Parameters.AddWithValue("$state", result.Status); cmd.Parameters.AddWithValue("$code", groupUpdate ? "group-update:" + result.Code : result.Code); cmd.ExecuteNonQuery();
            if (groupUpdate && JsonSerializer.Deserialize<SignedMaintenanceGrant>(document, DiagnosticJson)?.Grant.Version is { } version)
            {
                cmd.CommandText = "UPDATE hosts SET update_status=$status,update_status_at=$now WHERE id=$host";
                string status = result.Status == "completed" ? "ok " + version : result.Status == "accepted" ? "installing " + version : "failed " + version + ": " + result.Code;
                cmd.Parameters.AddWithValue("$status", status); cmd.Parameters.AddWithValue("$host", identity.HostId);
                cmd.ExecuteNonQuery();
            }
            cmd.CommandText = "INSERT INTO audit_log(ts,username,action,target,details) VALUES($now,'agent','agent.maintenance-result',$target,$details)";
            cmd.Parameters.AddWithValue("$target", "host:" + identity.HostId);
            cmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(result)); cmd.ExecuteNonQuery();
        }
        tx.Commit(); return accepted;
    }
    public bool ProtectedUninstallRequired(long host)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT EXISTS(SELECT 1 FROM agent_diagnostic_state WHERE host_id=$h AND maintenance_key_id IS NOT NULL)
AND NOT EXISTS(SELECT 1 FROM agent_maintenance m WHERE m.host_id=$h AND m.action='uninstall' AND m.status='completed'
AND m.updated_at >= COALESCE((SELECT MAX(last_at) FROM agent_diagnostic_state WHERE host_id=$h),''))";
        cmd.Parameters.AddWithValue("$h", host); return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
    }
    public List<MaintenanceRow> MaintenanceHistory(long host)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT id,credential_id,action,status,code,issued_at,expires_at,progress_json,progress_received_at,updated_at FROM agent_maintenance WHERE host_id=$h ORDER BY issued_at DESC LIMIT 50";
        cmd.Parameters.AddWithValue("$h", host); using var r = cmd.ExecuteReader(); var rows = new List<MaintenanceRow>();
        while (r.Read()) rows.Add(new(r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : JsonSerializer.Deserialize<UpdateProgressReport>(r.GetString(7), DiagnosticJson), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9))); return rows;
    }
}

public sealed class MaintenanceSigner(ServerConfig cfg)
{
    public ECDsa? Key(string? expectedId = null)
    {
        foreach (var path in new[] { cfg.MaintenanceSigningKeyPath, cfg.MaintenancePreviousSigningKeyPath })
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            ECDsa? key = null;
            try
            {
                if (new FileInfo(path).Length > 16384) continue;
                key = ECDsa.Create(); string pem = File.ReadAllText(path);
                if (pem.Contains("ENCRYPTED", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(cfg.MaintenanceKeyPasswordFile) || new FileInfo(cfg.MaintenanceKeyPasswordFile).Length > 4096) { key.Dispose(); continue; }
                    key.ImportFromEncryptedPem(pem, File.ReadAllText(cfg.MaintenanceKeyPasswordFile).TrimEnd('\r', '\n'));
                }
                else key.ImportFromPem(pem);
                if (key.KeySize == 256 && key.ExportParameters(false).Curve.Oid.Value == "1.2.840.10045.3.1.7" && (expectedId is null || MaintenanceSigning.KeyId(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())) == expectedId)) return key;
                key.Dispose();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException) { key?.Dispose(); }
        }
        return null;
    }
}
