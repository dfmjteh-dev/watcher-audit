using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// H6 (roadmap-next.md): срок хранения на уровне группы и legal hold.
public sealed record LegalHold(
    long Id, string Host, DateTime? From, DateTime? To, string Reason,
    string? CreatedBy, DateTime CreatedAt, string? ReleasedBy, DateTime? ReleasedAt, string? ReleaseReason)
{
    public bool Active => ReleasedAt is null;
}

public sealed record GroupRetention(long GroupId, string Name, int? RetentionDays);

public sealed partial class Db
{
    // Миграция 5 (H6). retention_days у группы: NULL — общий RetentionDays,
    // 0 — без удаления по возрасту. Legal hold не удаляется, а снимается
    // (released_at) — история отметок остаётся рядом с журналом доступа.
    private static void RetentionAndHolds(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
ALTER TABLE host_groups ADD COLUMN retention_days INTEGER;
CREATE TABLE legal_holds (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  host TEXT NOT NULL,
  from_ts TEXT,
  to_ts TEXT,
  reason TEXT NOT NULL,
  created_by TEXT,
  created_at TEXT NOT NULL,
  released_by TEXT,
  released_at TEXT,
  release_reason TEXT
);
CREATE INDEX ix_legal_holds_host_active ON legal_holds(host) WHERE released_at IS NULL;";
        cmd.ExecuteNonQuery();
    }

    // Условие «запись под действующим hold» для retention. alias — псевдоним
    // таблицы записи (segments / terminal_sessions) с колонками host и
    // started_at. Константа кода, не ввод пользователя.
    public static string HeldClause(string alias) => $@"EXISTS (
  SELECT 1 FROM legal_holds lh WHERE lh.released_at IS NULL AND lh.host = {alias}.host
    AND (lh.from_ts IS NULL OR {alias}.started_at >= lh.from_ts)
    AND (lh.to_ts IS NULL OR {alias}.started_at < lh.to_ts))";

    public List<GroupRetention> GroupRetentions()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, retention_days FROM host_groups ORDER BY name";
        var list = new List<GroupRetention>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new GroupRetention(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2)));
        return list;
    }

    public bool SetGroupRetention(long groupId, int? days)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE host_groups SET retention_days = $d WHERE id = $id";
        cmd.Parameters.AddWithValue("$d", (object?)days ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", groupId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public long CreateLegalHold(string host, DateTime? from, DateTime? to, string reason, string? createdBy)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO legal_holds (host, from_ts, to_ts, reason, created_by, created_at)
VALUES ($h, $f, $t, $r, $u, $now) RETURNING id";
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$f", (object?)from?.ToUniversalTime().ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", (object?)to?.ToUniversalTime().ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$r", reason);
        cmd.Parameters.AddWithValue("$u", (object?)createdBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        return (long)cmd.ExecuteScalar()!;
    }

    public bool ReleaseLegalHold(long id, string? releasedBy, string? reason)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE legal_holds SET released_at = $now, released_by = $u, release_reason = $r WHERE id = $id AND released_at IS NULL";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$u", (object?)releasedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$r", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public LegalHold? LegalHoldById(long id) => QueryHolds("WHERE id = $id", ("$id", id)).FirstOrDefault();

    // host = null — все хосты. Сначала действующие, потом снятые, новые сверху.
    public List<LegalHold> ListLegalHolds(string? host, bool activeOnly) =>
        (host, activeOnly) switch
        {
            (null, false) => QueryHolds(""),
            (null, true) => QueryHolds("WHERE released_at IS NULL"),
            (_, false) => QueryHolds("WHERE host = $h", ("$h", host)),
            (_, true) => QueryHolds("WHERE host = $h AND released_at IS NULL", ("$h", host)),
        };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "where — только константы из ListLegalHolds/LegalHoldById, значения — через Parameters.")]
    private List<LegalHold> QueryHolds(string where, params (string Name, object Value)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $@"SELECT id, host, from_ts, to_ts, reason, created_by, created_at, released_by, released_at, release_reason
FROM legal_holds {where} ORDER BY released_at IS NOT NULL, created_at DESC, id DESC LIMIT 1000";
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        var list = new List<LegalHold>();
        using var r = cmd.ExecuteReader();
        static DateTime? Ts(SqliteDataReader r, int i) =>
            r.IsDBNull(i) ? null : DateTime.Parse(r.GetString(i), null, System.Globalization.DateTimeStyles.RoundtripKind);
        while (r.Read())
            list.Add(new LegalHold(r.GetInt64(0), r.GetString(1), Ts(r, 2), Ts(r, 3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), Ts(r, 6)!.Value, r.IsDBNull(7) ? null : r.GetString(7), Ts(r, 8),
                r.IsDBNull(9) ? null : r.GetString(9)));
        return list;
    }
}
