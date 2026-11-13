using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Watcher.Server;

public sealed record TranscodeLease(string Token, int Attempts, int Failures);
public sealed record TranscodeIssue(long Id, string Host, string RecordedAt, string Status, int Attempts, string? Reason,
    string? Error, string? NextAt, string? UpdatedAt, string? QuarantinePath);

public sealed partial class Db
{
    private static void TranscodeRecovery(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"CREATE TABLE transcode_jobs (
segment_id INTEGER PRIMARY KEY, host TEXT NOT NULL, recorded_at TEXT NOT NULL, source_path TEXT NOT NULL,
status TEXT NOT NULL DEFAULT 'pending', attempts INTEGER NOT NULL DEFAULT 0, failures INTEGER NOT NULL DEFAULT 0,
reason TEXT, error TEXT, next_at TEXT, updated_at TEXT, token TEXT, lease_until TEXT,
quarantine_path TEXT, expires_at TEXT, snapshot_json TEXT);
CREATE INDEX ix_transcode_jobs_due ON transcode_jobs(next_at);
CREATE TABLE transcode_attempts (id INTEGER PRIMARY KEY AUTOINCREMENT, segment_id INTEGER NOT NULL,
attempt INTEGER NOT NULL, at TEXT NOT NULL, outcome TEXT NOT NULL, reason TEXT, error TEXT);
CREATE INDEX ix_transcode_attempts_segment ON transcode_attempts(segment_id,id);
UPDATE segments SET transcode_status=NULL WHERE transcode_status='failed';";
        cmd.ExecuteNonQuery();
    }

    private static void ListPageIndexes(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"CREATE INDEX ix_terminal_roots_page ON terminal_sessions(started_at DESC,id DESC) WHERE parent_id IS NULL;
CREATE INDEX ix_terminal_host_page ON terminal_sessions(host,started_at DESC,id DESC);
CREATE INDEX ix_activity_app_catalog ON segment_activity(ts,process,site);";
        cmd.ExecuteNonQuery();
    }

    [SuppressMessage("Security", "CA2100", Justification = "HeldClause — константа, все значения привязаны параметрами.")]
    public TranscodeLease? ClaimTranscode(TranscodeCandidate candidate, DateTime now)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO transcode_jobs(segment_id,host,recorded_at,source_path) SELECT id,host,started_at,file_path FROM segments WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", candidate.Id);
        cmd.ExecuteNonQuery();
        string token = Guid.NewGuid().ToString("N");
        cmd.CommandText = $@"UPDATE transcode_jobs SET token=$token, lease_until=$lease, attempts=attempts+1, status='working', updated_at=$now
WHERE segment_id=$id AND (lease_until IS NULL OR lease_until <= $now) AND (next_at IS NULL OR next_at <= $now)
AND EXISTS(SELECT 1 FROM segments s WHERE s.id=$id AND s.file_path=$path AND s.transcode_status IS NULL AND NOT {HeldClause("s")})
RETURNING attempts, failures";
        cmd.Parameters.AddWithValue("$token", token);
        cmd.Parameters.AddWithValue("$lease", now.AddHours(6).ToString("o"));
        cmd.Parameters.AddWithValue("$now", now.ToString("o"));
        cmd.Parameters.AddWithValue("$path", candidate.FilePath);
        TranscodeLease? result = null;
        using (var reader = cmd.ExecuteReader()) if (reader.Read()) result = new(token, reader.GetInt32(0), reader.GetInt32(1));
        tx.Commit();
        return result;
    }

    [SuppressMessage("Security", "CA2100", Justification = "Выбор только между литералами SQL, значения — параметры.")]
    public void ReleaseTranscode(long id, string token, bool done = false)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = done
            ? "DELETE FROM transcode_jobs WHERE segment_id=$id AND token=$t AND failures=0"
            : "UPDATE transcode_jobs SET token=NULL,lease_until=NULL,status=CASE WHEN status='working' THEN 'pending' ELSE status END WHERE segment_id=$id AND token=$t";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$t", token);
        cmd.ExecuteNonQuery();
        if (done)
        {
            cmd.CommandText = "UPDATE transcode_jobs SET token=NULL,lease_until=NULL,status='done',next_at=NULL WHERE segment_id=$id AND token=$t";
            cmd.ExecuteNonQuery();
        }
    }

    [SuppressMessage("Security", "CA2100", Justification = "HeldClause — константа; остальные SQL строки фиксированы, ввод — параметры.")]
    public bool FinishTranscodeIssue(TranscodeCandidate cand, TranscodeLease lease, string status, string reason, string? error,
        DateTime now, DateTime? nextAt = null, TranscodedFile? replacement = null, string? quarantine = null,
        int quarantineDays = 7, bool failure = true)
    {
        using var totals = TotalsChange();
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = $@"SELECT COUNT(*) FROM segments s JOIN transcode_jobs j ON j.segment_id=s.id
WHERE s.id=$id AND s.file_path=$old AND s.transcode_status IS NULL AND j.token=$token AND NOT {HeldClause("s")}";
        cmd.Parameters.AddWithValue("$id", cand.Id); cmd.Parameters.AddWithValue("$old", cand.FilePath); cmd.Parameters.AddWithValue("$token", lease.Token);
        if (Convert.ToInt64(cmd.ExecuteScalar()) != 1) return false;
        // Аудит и состояние фиксируются одной транзакцией до удаления исходника.
        if (status != "retry")
        {
            cmd.CommandText = "INSERT INTO audit_log(ts,username,action,target,details) VALUES($now,'transcode',$action,$target,$details)";
            cmd.Parameters.AddWithValue("$now", now.ToString("o"));
            cmd.Parameters.AddWithValue("$action", "transcode." + status);
            cmd.Parameters.AddWithValue("$target", "segment:" + cand.Id);
            cmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new { id = cand.Id, segment = ById(cand.Id), reason, attempts = lease.Attempts, quarantine, path = cand.FilePath }));
            cmd.ExecuteNonQuery();
        }
        else cmd.Parameters.AddWithValue("$now", now.ToString("o"));
        if (status == "quarantined")
        {
            cmd.CommandText = "DELETE FROM segment_activity WHERE segment_id=$id"; cmd.ExecuteNonQuery();
            cmd.CommandText = "DELETE FROM segments WHERE id=$id"; cmd.ExecuteNonQuery();
        }
        else if (replacement is not null)
        {
            cmd.CommandText = "UPDATE segments SET source_sha256=sha256,source_size_bytes=size_bytes,file_path=$new,sha256=$sha,size_bytes=$size,transcode_status='repaired',transcoded_at=$now WHERE id=$id";
            cmd.Parameters.AddWithValue("$new", replacement.FilePath); cmd.Parameters.AddWithValue("$sha", replacement.Sha256); cmd.Parameters.AddWithValue("$size", replacement.SizeBytes);
            cmd.ExecuteNonQuery();
        }
        else
        {
            cmd.CommandText = "UPDATE segments SET transcode_status=$status,transcoded_at=$now WHERE id=$id";
            cmd.Parameters.AddWithValue("$status", status == "retry" ? DBNull.Value : status);
            cmd.ExecuteNonQuery();
        }
        cmd.CommandText = @"UPDATE transcode_jobs SET status=$result,reason=$reason,error=$error,next_at=$next,updated_at=$now,
failures=failures+$failure,token=NULL,lease_until=NULL,quarantine_path=$q,expires_at=$expiry,snapshot_json=$snapshot WHERE segment_id=$id;
INSERT INTO transcode_attempts(segment_id,attempt,at,outcome,reason,error) VALUES($id,$attempt,$now,$result,$reason,$error)";
        cmd.Parameters.AddWithValue("$result", status); cmd.Parameters.AddWithValue("$reason", reason);
        cmd.Parameters.AddWithValue("$error", (object?)ClipTranscodeError(error) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$next", (object?)nextAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$failure", failure ? 1 : 0);
        cmd.Parameters.AddWithValue("$q", (object?)quarantine ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$expiry", quarantine is null ? DBNull.Value : now.AddDays(Math.Clamp(quarantineDays, 1, 30)).ToString("o"));
        cmd.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(cand)); cmd.Parameters.AddWithValue("$attempt", lease.Attempts);
        cmd.ExecuteNonQuery(); tx.Commit(); return true;
    }

    public static string? ClipTranscodeError(string? error) => error is null ? null : error[..Math.Min(error.Length, 2000)];

    public List<TranscodeIssue> TranscodeIssues(int limit = 50, long? beforeId = null, string? status = null)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT segment_id,host,recorded_at,status,attempts,reason,error,next_at,updated_at,quarantine_path FROM transcode_jobs
WHERE reason IS NOT NULL AND (reason <> 'no-saving' OR $status IS NOT NULL) AND ($before IS NULL OR segment_id<$before) AND ($status IS NULL OR status=$status)
ORDER BY segment_id DESC LIMIT $lim";
        cmd.Parameters.AddWithValue("$before", (object?)beforeId ?? DBNull.Value); cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lim", Math.Clamp(limit, 1, 100)); using var reader = cmd.ExecuteReader(); var result = new List<TranscodeIssue>();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
        return result;
    }

    public void PauseHeldTranscode(long id, string token, DateTime now)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = @"UPDATE transcode_jobs SET status='held',reason='legal-hold',updated_at=$now,next_at=NULL
WHERE segment_id=$id AND token=$token AND EXISTS(SELECT 1 FROM segments s JOIN legal_holds h ON h.host=s.host
WHERE s.id=$id AND h.released_at IS NULL AND (h.from_ts IS NULL OR s.started_at>=h.from_ts) AND (h.to_ts IS NULL OR s.started_at<h.to_ts))";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$token", token); cmd.Parameters.AddWithValue("$now", now.ToString("o")); cmd.ExecuteNonQuery();
    }

    public DateTime? NextTranscodeRetry()
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT MIN(next_at) FROM transcode_jobs WHERE status='retry'";
        return cmd.ExecuteScalar() is string value ? ParseUtc(value) : null;
    }
}
