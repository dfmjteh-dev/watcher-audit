using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// Журнал доступа (C3): запись, поиск, выдача в SIEM (H4), срок хранения.

// Этап C3 (roadmap-next.md) — журнал доступа: кто, когда, с какого IP что
// открыл/поменял. Username у CLI-команд — "cli" (веб-сессии там нет), у
// неудачного логина — введённое (не обязательно существующее) имя.
// Details — JSON-строка (сериализует AuditLog.cs), не отдельные колонки под
// каждое действие: набор полей у "поиск по консоли" и "смена роли" разный,
// а SIEM-экспорту (H4) JSON удобнее, чем разбирать произвольный текст.
public sealed record AuditEntry(
    long Id,
    DateTime Ts,
    string? Username,
    string? Ip,
    string Action,
    string? Target,
    string? Details);

public sealed partial class Db
{
    public long InsertAudit(AuditEntry e)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO audit_log (ts, username, ip, action, target, details)
VALUES ($ts, $u, $ip, $a, $t, $d)
RETURNING id;";
        cmd.Parameters.AddWithValue("$ts", e.Ts.ToUniversalTime().ToString("o"));
        cmd.Parameters.AddWithValue("$u", (object?)e.Username ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ip", (object?)e.Ip ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", e.Action);
        cmd.Parameters.AddWithValue("$t", (object?)e.Target ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", (object?)e.Details ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    // action — точное совпадение или префикс до точки: "login" находит и
    // login.ok, и login.failed; "user" — все user.*, не "username...".
    private static string AuditWhere(SqliteCommand cmd, string? username, string? action, DateTime? from, DateTime? to)
    {
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(username)) { where.Add("username = $u"); cmd.Parameters.AddWithValue("$u", username); }
        if (!string.IsNullOrWhiteSpace(action))
        {
            where.Add("(action = $a OR substr(action, 1, length($a) + 1) = $a || '.')");
            cmd.Parameters.AddWithValue("$a", action);
        }
        if (from is not null) { where.Add("ts >= $from"); cmd.Parameters.AddWithValue("$from", from.Value.ToUniversalTime().ToString("o")); }
        if (to is not null) { where.Add("ts < $to"); cmd.Parameters.AddWithValue("$to", to.Value.ToUniversalTime().ToString("o")); }
        return where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
    }

    [SuppressMessage("Security", "CA2100", Justification = "WHERE/JOIN собираются только из констант кода и имён параметров ($x, AddInParams); значения пользователя — только через Parameters.")]
    public int CountAudit(string? username, string? action, DateTime? from, DateTime? to)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM audit_log {AuditWhere(cmd, username, action, from, to)}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [SuppressMessage("Security", "CA2100", Justification = "WHERE/JOIN собираются только из констант кода и имён параметров ($x, AddInParams); значения пользователя — только через Parameters.")]
    public List<AuditEntry> QueryAudit(string? username, string? action, DateTime? from, DateTime? to, int limit, int offset)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id, ts, username, ip, action, target, details FROM audit_log {AuditWhere(cmd, username, action, from, to)} ORDER BY ts DESC, id DESC LIMIT $lim OFFSET $off";
        cmd.Parameters.AddWithValue("$lim", limit);
        cmd.Parameters.AddWithValue("$off", offset);
        var list = new List<AuditEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new AuditEntry(
                r.GetInt64(0),
                DateTime.Parse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6)));
        return list;
    }

    // H4, SIEM-экспорт: записи журнала после курсора, по возрастанию id.
    public List<AuditEntry> AuditAfter(long afterId, int limit)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, ts, username, ip, action, target, details FROM audit_log WHERE id > $after ORDER BY id LIMIT $lim";
        cmd.Parameters.AddWithValue("$after", afterId);
        cmd.Parameters.AddWithValue("$lim", limit);
        var list = new List<AuditEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new AuditEntry(
                r.GetInt64(0),
                DateTime.Parse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6)));
        return list;
    }

    // AUTOINCREMENT: id не переиспользуются и после удаления старых строк.
    public long MaxAuditId()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(id), 0) FROM audit_log";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public int DeleteAuditOlderThan(DateTime cutoff)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM audit_log WHERE ts < $cut";
        cmd.Parameters.AddWithValue("$cut", cutoff.ToUniversalTime().ToString("o"));
        return cmd.ExecuteNonQuery();
    }
}
