using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Watcher.Server;

public sealed record DiagnosticRow(long Id, string Kind, string ReceivedAt, AgentDiagnostic Event);
public sealed record DiagnosticState(long CredentialId, string Kind, string? LastAt, string? RequestId, string? RequestedAt, string? SnapshotJson, string? MaintenanceKeyId = null);
public sealed partial class Db
{
    private static void AgentDiagnostics(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"CREATE TABLE agent_diagnostic_events(id INTEGER PRIMARY KEY,credential_id INTEGER NOT NULL,host_id INTEGER NOT NULL,
event_id TEXT NOT NULL,received_at TEXT NOT NULL,at TEXT NOT NULL,level TEXT NOT NULL,component TEXT NOT NULL,code TEXT NOT NULL,
UNIQUE(credential_id,event_id));
CREATE INDEX ix_agent_diagnostics_host ON agent_diagnostic_events(host_id,id DESC);
CREATE INDEX ix_agent_diagnostics_received ON agent_diagnostic_events(received_at);
CREATE TABLE agent_diagnostic_state(credential_id INTEGER PRIMARY KEY,host_id INTEGER NOT NULL,last_at TEXT,
request_id TEXT,requested_at TEXT,snapshot_json TEXT);
CREATE INDEX ix_agent_diagnostic_state_host ON agent_diagnostic_state(host_id);";
        cmd.ExecuteNonQuery();
    }
    private static readonly JsonSerializerOptions DiagnosticJson = new(JsonSerializerDefaults.Web);
    public DiagnosticReceipt? ReceiveDiagnostics(AgentIdentity identity, DiagnosticBatch batch, DateTime now)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$cid", identity.CredentialId); cmd.Parameters.AddWithValue("$host", identity.HostId);
        cmd.Parameters.AddWithValue("$now", now.ToString("o"));
        cmd.CommandText = "SELECT last_at FROM agent_diagnostic_state WHERE credential_id=$cid";
        if (cmd.ExecuteScalar() is string last && ParseUtc(last) > now.AddSeconds(-5)) return null;
        cmd.CommandText = "INSERT INTO agent_diagnostic_state(credential_id,host_id,last_at,maintenance_key_id) VALUES($cid,$host,$now,$key) ON CONFLICT(credential_id) DO UPDATE SET last_at=$now,maintenance_key_id=COALESCE($key,maintenance_key_id)";
        cmd.Parameters.AddWithValue("$key", (object?)batch.MaintenanceKeyId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        var accepted = new List<string>();
        foreach (var e in batch.Events ?? [])
        {
            accepted.Add(e.Id);
            if (e.At < now.AddDays(-14)) continue; // Подтвердить старое, но не обходить retention повтором.
            using var insert = c.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = @"INSERT OR IGNORE INTO agent_diagnostic_events(credential_id,host_id,event_id,received_at,at,level,component,code)
VALUES($cid,$host,$event,$now,$at,$level,$component,$code)";
            insert.Parameters.AddWithValue("$cid", identity.CredentialId); insert.Parameters.AddWithValue("$host", identity.HostId);
            insert.Parameters.AddWithValue("$event", e.Id); insert.Parameters.AddWithValue("$now", now.ToString("o"));
            insert.Parameters.AddWithValue("$at", (e.At > now.AddMinutes(5) ? now : e.At).ToString("o"));
            insert.Parameters.AddWithValue("$level", e.Level); insert.Parameters.AddWithValue("$component", e.Component); insert.Parameters.AddWithValue("$code", e.Code);
            insert.ExecuteNonQuery();
        }
        cmd.CommandText = "SELECT request_id FROM agent_diagnostic_state WHERE credential_id=$cid AND requested_at >= $cut";
        cmd.Parameters.AddWithValue("$cut", now.AddHours(-1).ToString("o"));
        string? request = cmd.ExecuteScalar() as string, snapshotAccepted = null;
        cmd.CommandText = "SELECT json_extract(snapshot_json,'$.requestId') FROM agent_diagnostic_state WHERE credential_id=$cid";
        if (batch.Snapshot is { } duplicate && cmd.ExecuteScalar() as string == duplicate.RequestId) snapshotAccepted = duplicate.RequestId;
        if (batch.Snapshot is { } snapshot && snapshot.RequestId == request && snapshotAccepted is null)
        {
            cmd.CommandText = "UPDATE agent_diagnostic_state SET request_id=NULL,snapshot_json=$snapshot WHERE credential_id=$cid";
            cmd.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(snapshot with { At = now }, DiagnosticJson)); cmd.ExecuteNonQuery();
            snapshotAccepted = request; request = null;
        }
        cmd.CommandText = @"DELETE FROM agent_diagnostic_events WHERE host_id=$host AND (received_at < $old OR id IN
(SELECT id FROM agent_diagnostic_events WHERE host_id=$host ORDER BY id DESC LIMIT -1 OFFSET 2000));
UPDATE agent_diagnostic_state SET snapshot_json=NULL WHERE host_id=$host AND json_extract(snapshot_json,'$.at') < $old";
        cmd.Parameters.AddWithValue("$old", now.AddDays(-14).ToString("o")); cmd.ExecuteNonQuery(); tx.Commit();
        var maintenanceAccepted = AcceptMaintenanceResults(identity, batch.MaintenanceResults ?? [], now);
        var progressAccepted = AcceptUpdateProgress(identity, batch.UpdateProgress ?? [], now);
        return new(accepted, request, snapshotAccepted, PendingMaintenance(identity.CredentialId, now), maintenanceAccepted, progressAccepted, PendingDiagnosticArchive(identity, now));
    }
    public void PruneDiagnostics(DateTime now)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = @"DELETE FROM agent_diagnostic_events WHERE received_at < $cut;
UPDATE agent_diagnostic_state SET snapshot_json=NULL WHERE json_extract(snapshot_json,'$.at') < $cut;
UPDATE agent_diagnostic_state SET request_id=NULL,requested_at=NULL WHERE requested_at < $expired;
UPDATE agent_maintenance SET status='expired',code=CASE WHEN code LIKE 'group-update:%' THEN 'group-update:expired' ELSE 'expired' END WHERE status IN ('queued','exported','accepted') AND expires_at < $now;
DELETE FROM agent_maintenance WHERE expires_at < $old AND id NOT IN
(SELECT keep.id FROM agent_maintenance keep WHERE keep.host_id=agent_maintenance.host_id
AND keep.status='completed' AND keep.action IN ('uninstall','resume','repair')
ORDER BY COALESCE(keep.updated_at,keep.issued_at) DESC,keep.issued_at DESC,keep.rowid DESC LIMIT 1)";
        cmd.Parameters.AddWithValue("$now", now.ToString("o")); cmd.Parameters.AddWithValue("$old", now.AddDays(-30).ToString("o"));
        cmd.Parameters.AddWithValue("$cut", now.AddDays(-14).ToString("o")); cmd.Parameters.AddWithValue("$expired", now.AddHours(-1).ToString("o")); cmd.ExecuteNonQuery();
    }
    public List<DiagnosticRow> DiagnosticEvents(long hostId, long? before = null)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT e.id,a.kind,e.received_at,e.event_id,e.at,e.level,e.component,e.code FROM agent_diagnostic_events e
JOIN agent_credentials a ON a.id=e.credential_id WHERE e.host_id=$h AND e.received_at >= $cut AND ($before IS NULL OR e.id<$before)
ORDER BY e.id DESC LIMIT 51";
        cmd.Parameters.AddWithValue("$h", hostId); cmd.Parameters.AddWithValue("$cut", DateTime.UtcNow.AddDays(-14).ToString("o")); cmd.Parameters.AddWithValue("$before", (object?)before ?? DBNull.Value);
        using var reader = cmd.ExecuteReader(); var rows = new List<DiagnosticRow>();
        while (reader.Read()) rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), new(reader.GetString(3), ParseUtc(reader.GetString(4)), reader.GetString(5), reader.GetString(6), reader.GetString(7))));
        return rows;
    }
    public List<DiagnosticState> DiagnosticStates(long hostId)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT a.id,a.kind,s.last_at,s.request_id,s.requested_at,CASE WHEN json_extract(s.snapshot_json,'$.at') >= $cut THEN s.snapshot_json END,s.maintenance_key_id FROM agent_credentials a
LEFT JOIN agent_diagnostic_state s ON s.credential_id=a.id WHERE a.host_id=$h AND a.revoked_at IS NULL ORDER BY a.id DESC LIMIT 20";
        cmd.Parameters.AddWithValue("$cut", DateTime.UtcNow.AddDays(-14).ToString("o"));
        cmd.Parameters.AddWithValue("$h", hostId); using var r = cmd.ExecuteReader(); var result = new List<DiagnosticState>();
        while (r.Read()) result.Add(new(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return result;
    }
    public int RequestDiagnostics(long hostId, DateTime now, string actor)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO agent_diagnostic_state(credential_id,host_id,request_id,requested_at)
SELECT id,host_id,lower(hex(randomblob(16))),$now FROM agent_credentials WHERE host_id=$h AND revoked_at IS NULL
ON CONFLICT(credential_id) DO UPDATE SET request_id=CASE WHEN requested_at>$recent THEN request_id ELSE excluded.request_id END,
requested_at=CASE WHEN requested_at>$recent THEN requested_at ELSE excluded.requested_at END";
        cmd.Parameters.AddWithValue("$h", hostId); cmd.Parameters.AddWithValue("$now", now.ToString("o")); cmd.Parameters.AddWithValue("$recent", now.AddMinutes(-2).ToString("o"));
        int count = cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO audit_log(ts,username,action,target,details) VALUES($now,$actor,'agent.diagnostics-request',$target,'{}')";
        cmd.Parameters.AddWithValue("$actor", actor); cmd.Parameters.AddWithValue("$target", "host:" + hostId); cmd.ExecuteNonQuery(); tx.Commit(); return count;
    }
}
