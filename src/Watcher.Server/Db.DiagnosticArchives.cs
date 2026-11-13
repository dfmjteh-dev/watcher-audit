using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Watcher.Server;

public sealed record DiagnosticArchiveState(long CredentialId, string Kind, string Id, string RequestedAt, string ExpiresAt, string? ReceivedAt);
public sealed partial class Db
{
    private static void AgentDiagnosticArchives(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"CREATE TABLE agent_diagnostic_archives(credential_id INTEGER PRIMARY KEY REFERENCES agent_credentials(id) ON DELETE CASCADE,
request_id TEXT NOT NULL,requested_at TEXT NOT NULL,expires_at TEXT NOT NULL,received_at TEXT,report_json TEXT);
CREATE INDEX ix_agent_archives_expires ON agent_diagnostic_archives(expires_at);";
        cmd.ExecuteNonQuery();
    }
    public int RequestDiagnosticArchives(long hostId, DateTime now, string actor)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO agent_diagnostic_archives(credential_id,request_id,requested_at,expires_at)
SELECT id,lower(hex(randomblob(16))),$now,$expires FROM agent_credentials WHERE host_id=$host AND revoked_at IS NULL
ON CONFLICT(credential_id) DO UPDATE SET request_id=excluded.request_id,requested_at=excluded.requested_at,expires_at=excluded.expires_at,received_at=NULL,report_json=NULL
WHERE agent_diagnostic_archives.requested_at<$recent AND (agent_diagnostic_archives.received_at IS NOT NULL OR agent_diagnostic_archives.expires_at<$now)";
        cmd.Parameters.AddWithValue("$host", hostId); cmd.Parameters.AddWithValue("$now", now.ToString("o")); cmd.Parameters.AddWithValue("$expires", now.AddHours(1).ToString("o")); cmd.Parameters.AddWithValue("$recent", now.AddMinutes(-5).ToString("o"));
        int count = cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO audit_log(ts,username,action,target,details) VALUES($now,$actor,'agent.archive-request',$target,'{}')";
        cmd.Parameters.AddWithValue("$actor", actor); cmd.Parameters.AddWithValue("$target", "host:" + hostId); cmd.ExecuteNonQuery(); tx.Commit(); return count;
    }
    public DiagnosticArchiveRequest? PendingDiagnosticArchive(AgentIdentity identity, DateTime now)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT r.request_id,r.expires_at FROM agent_diagnostic_archives r JOIN agent_credentials a ON a.id=r.credential_id
WHERE a.id=$cid AND a.host_id=$host AND a.kind=$kind AND a.revoked_at IS NULL AND r.received_at IS NULL AND r.expires_at>=$now";
        cmd.Parameters.AddWithValue("$cid", identity.CredentialId); cmd.Parameters.AddWithValue("$host", identity.HostId); cmd.Parameters.AddWithValue("$kind", identity.Kind); cmd.Parameters.AddWithValue("$now", now.ToString("o"));
        using var r = cmd.ExecuteReader(); return r.Read() ? new(r.GetString(0), ParseUtc(r.GetString(1))) : null;
    }
    public bool ReceiveDiagnosticArchive(AgentIdentity identity, DiagnosticArchiveReport report, DateTime now)
    {
        if (!DiagnosticArchiveContract.Valid(report)) return false;
        // События уже могли быть подтверждены до повторной отправки архива.
        var events = new List<AgentDiagnostic>(report.Events);
        using (var connection = Open())
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT event_id,at,level,component,code FROM agent_diagnostic_events WHERE credential_id=$cid AND received_at >= $cut ORDER BY id DESC LIMIT 128";
            query.Parameters.AddWithValue("$cid", identity.CredentialId); query.Parameters.AddWithValue("$cut", now.AddDays(-14).ToString("o"));
            using var reader = query.ExecuteReader(); while (reader.Read()) events.Add(new(reader.GetString(0), ParseUtc(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }
        string json = JsonSerializer.Serialize(report with { CollectedAt = now, Events = events.DistinctBy(e => e.Id).Take(128).ToList() }, DiagnosticJson);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > DiagnosticArchiveContract.MaxBytes) return false;
        using var c = Open(); using var cmd = c.CreateCommand();
        // Повтор потерянного HTTP-ответа подтверждается, но не меняет байты/TTL.
        cmd.CommandText = @"UPDATE agent_diagnostic_archives SET report_json=COALESCE(report_json,$json),received_at=COALESCE(received_at,$now),
expires_at=CASE WHEN received_at IS NULL THEN $expires ELSE expires_at END
WHERE credential_id=$cid AND request_id=$id AND expires_at>=$now
AND EXISTS(SELECT 1 FROM agent_credentials WHERE id=$cid AND host_id=$host AND kind=$kind AND revoked_at IS NULL)";
        cmd.Parameters.AddWithValue("$json", json); cmd.Parameters.AddWithValue("$now", now.ToString("o")); cmd.Parameters.AddWithValue("$expires", now.AddDays(1).ToString("o"));
        cmd.Parameters.AddWithValue("$cid", identity.CredentialId); cmd.Parameters.AddWithValue("$host", identity.HostId); cmd.Parameters.AddWithValue("$kind", identity.Kind); cmd.Parameters.AddWithValue("$id", report.RequestId);
        return cmd.ExecuteNonQuery() == 1;
    }
    public List<DiagnosticArchiveState> DiagnosticArchives(long hostId, DateTime now)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = @"SELECT a.id,a.kind,r.request_id,r.requested_at,r.expires_at,r.received_at
FROM agent_diagnostic_archives r JOIN agent_credentials a ON a.id=r.credential_id WHERE a.host_id=$host AND a.revoked_at IS NULL AND r.expires_at>=$now ORDER BY a.id LIMIT 20";
        cmd.Parameters.AddWithValue("$host", hostId); cmd.Parameters.AddWithValue("$now", now.ToString("o")); using var r = cmd.ExecuteReader(); var rows = new List<DiagnosticArchiveState>();
        while (r.Read()) rows.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5))); return rows;
    }
    public string? DiagnosticArchiveJson(long hostId, string requestId, DateTime now)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = @"SELECT r.report_json FROM agent_diagnostic_archives r JOIN agent_credentials a ON a.id=r.credential_id
WHERE a.host_id=$host AND a.revoked_at IS NULL AND r.request_id=$id AND r.expires_at>=$now";
        cmd.Parameters.AddWithValue("$host", hostId); cmd.Parameters.AddWithValue("$id", requestId); cmd.Parameters.AddWithValue("$now", now.ToString("o")); return cmd.ExecuteScalar() as string;
    }
    public void PruneDiagnosticArchives(DateTime now)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = @"DELETE FROM agent_diagnostic_archives WHERE expires_at<$now OR credential_id IN
(SELECT id FROM agent_credentials WHERE revoked_at IS NOT NULL OR deleted_host IS NOT NULL)";
        cmd.Parameters.AddWithValue("$now", now.ToString("o")); cmd.ExecuteNonQuery();
    }
}
