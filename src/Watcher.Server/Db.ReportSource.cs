using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Watcher.Server;

public sealed record ReportSourceRow(long Id, long SegmentId, string Host, string? User, string? Process, DateTime StartUtc, DateTime EndUtc, double VideoOffset);
public sealed record ReportSourcePage(List<ReportSourceRow> Rows, string? NextCursor);
public sealed partial class Db
{
    [SuppressMessage("Security", "CA2100", Justification = "SQL fragments and identifiers are fixed constants; filter values and cursor use parameters.")]
    public ReportSourcePage ReportSource(DateTime fromUtc, DateTime toUtc, bool windows, IReadOnlyCollection<long>? allowed, long? groupId,
        string? host, string? user, string? process, int limit, string? cursor)
    {
        limit = Math.Clamp(limit, 1, 100);
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandTimeout = 3;
        var where = new List<string> { "s.started_at >= $lo", "s.started_at < $to", "s.ended_at > $from" };
        cmd.Parameters.AddWithValue("$lo", fromUtc.AddDays(-1).ToString("o")); cmd.Parameters.AddWithValue("$to", toUtc.ToString("o")); cmd.Parameters.AddWithValue("$from", fromUtc.ToString("o"));
        if (allowed is not null) where.Add(allowed.Count == 0 ? "0" : "h.group_id IN (" + AddInParams(cmd, "ag", allowed) + ")");
        if (groupId is not null) { where.Add("h.group_id=$group"); cmd.Parameters.AddWithValue("$group", groupId.Value); }
        if (!string.IsNullOrEmpty(host)) { where.Add("s.host=$host"); cmd.Parameters.AddWithValue("$host", host); }
        if (!string.IsNullOrEmpty(user)) { where.Add("s.session_user=$user COLLATE NOCASE"); cmd.Parameters.AddWithValue("$user", user); }
        string ts = windows ? "a.ts" : "s.started_at", id = windows ? "a.id" : "s.id";
        if (cursor is not null)
        {
            string[] parts = cursor.Split('.');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long rowId) || rowId <= 0) throw new ArgumentException("Invalid report cursor");
            where.Add($"({ts}>$cursorAt OR ({ts}=$cursorAt AND {id}>$cursorId))");
            cmd.Parameters.AddWithValue("$cursorAt", new DateTime(ticks, DateTimeKind.Utc).ToString("o")); cmd.Parameters.AddWithValue("$cursorId", rowId);
        }
        if (windows)
        {
            where.Add("a.ts >= $lo AND a.ts < $to AND a.process IS NOT NULL AND a.process<>''");
            if (!string.IsNullOrEmpty(process)) { where.Add("a.process=$process COLLATE NOCASE"); cmd.Parameters.AddWithValue("$process", process); }
        }
        string fields = windows ? @"a.id,s.id,s.host,s.session_user,a.process,a.ts,a.offset_sec,
COALESCE((SELECT n.offset_sec FROM segment_activity n WHERE n.segment_id=a.segment_id AND (n.offset_sec>a.offset_sec OR (n.offset_sec=a.offset_sec AND n.id>a.id)) ORDER BY n.offset_sec,n.id LIMIT 1),s.duration_sec)-a.offset_sec"
            : "s.id,s.id,s.host,s.session_user,NULL,s.started_at,0,s.ended_at";
        cmd.CommandText = $"SELECT {fields} FROM segments s {(windows ? "JOIN segment_activity a ON a.segment_id=s.id" : "")} LEFT JOIN hosts h ON h.name=s.host WHERE {string.Join(" AND ", where)} ORDER BY {ts},{id} LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100) + 1); var rows = new List<ReportSourceRow>();
        using var r = cmd.ExecuteReader(); while (r.Read())
        {
            DateTime start = ParseUtc(r.GetString(5)); double offset = r.GetDouble(6);
            DateTime end = windows ? start.AddSeconds(Math.Clamp(r.GetDouble(7), 0, 86400)) : ParseUtc(r.GetString(7));
            rows.Add(new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), start, end, offset));
        }
        bool more = rows.Count > limit; if (more) rows.RemoveAt(rows.Count - 1);
        return new(rows, more ? rows[^1].StartUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "." + rows[^1].Id.ToString(CultureInfo.InvariantCulture) : null);
    }
    [SuppressMessage("Security", "CA2100", Justification = "WHERE consists of fixed clauses and parameter names; all scope/filter values are parameters.")]
    public int ReportDirtyCount(string from, string to, IReadOnlyCollection<long>? allowed, long? groupId, string? host)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); var where = new List<string> { "d.day >= $from", "d.day <= $to" };
        cmd.Parameters.AddWithValue("$from", from); cmd.Parameters.AddWithValue("$to", to);
        if (allowed is not null) where.Add(allowed.Count == 0 ? "0" : "h.group_id IN (" + AddInParams(cmd, "ag", allowed) + ")");
        if (groupId is not null) { where.Add("h.group_id=$group"); cmd.Parameters.AddWithValue("$group", groupId.Value); }
        if (host is not null) { where.Add("d.host=$host"); cmd.Parameters.AddWithValue("$host", host); }
        cmd.CommandText = "SELECT COUNT(*) FROM report_dirty d LEFT JOIN hosts h ON h.name=d.host WHERE " + string.Join(" AND ", where);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
