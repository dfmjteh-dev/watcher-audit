using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Watcher.Server;

public sealed partial class Db
{
    private static void AgentUpdateProgress(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "ALTER TABLE agent_maintenance ADD COLUMN progress_revision INTEGER NOT NULL DEFAULT 0; ALTER TABLE agent_maintenance ADD COLUMN progress_json TEXT; ALTER TABLE agent_maintenance ADD COLUMN progress_received_at TEXT;";
        cmd.ExecuteNonQuery();
    }
    public List<UpdateProgressAck> AcceptUpdateProgress(AgentIdentity identity, IEnumerable<UpdateProgressReport> reports, DateTime now)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); var accepted = new List<UpdateProgressAck>();
        foreach (var report in reports.Take(16))
        {
            if (report is null || !UpdateProgressContract.Valid(report)) continue;
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "SELECT grant_json,progress_revision,progress_json,status FROM agent_maintenance WHERE id=$id AND credential_id=$cid AND host_id=$host AND action='update'";
            cmd.Parameters.AddWithValue("$id", report.GrantId); cmd.Parameters.AddWithValue("$cid", identity.CredentialId); cmd.Parameters.AddWithValue("$host", identity.HostId);
            string document, status; string? previousJson; long revision;
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) continue;
                document = r.GetString(0); revision = r.GetInt64(1); previousJson = r.IsDBNull(2) ? null : r.GetString(2); status = r.GetString(3);
            }
            var grant = JsonSerializer.Deserialize<SignedMaintenanceGrant>(document, DiagnosticJson)?.Grant;
            if (grant?.Version != report.ToVersion || grant.Kind != identity.Kind) continue;
            accepted.Add(new(report.GrantId, report.Revision));
            if (report.Revision <= revision) continue;
            var previous = previousJson is null ? null : JsonSerializer.Deserialize<UpdateProgressReport>(previousJson, DiagnosticJson);
            // Запоздавший download не заменяет итог и не возобновляет завершённую операцию.
            if (previous is not null && UpdateProgressContract.Terminal(previous.Stage)) continue;
            if (status is "completed" or "failed" or "rejected" && !UpdateProgressContract.Terminal(report.Stage)) continue;
            if (status == "completed" && report.Stage != "completed" || status == "failed" && report.Stage == "completed" || status == "rejected") continue;
            cmd.CommandText = "UPDATE agent_maintenance SET progress_revision=$revision,progress_json=$json,progress_received_at=$now WHERE id=$id AND credential_id=$cid";
            cmd.Parameters.AddWithValue("$revision", report.Revision);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(report with { At = report.At > now.AddMinutes(5) ? now : report.At }, DiagnosticJson));
            cmd.Parameters.AddWithValue("$now", now.ToString("o")); cmd.ExecuteNonQuery();
        }
        tx.Commit(); return accepted;
    }
}
