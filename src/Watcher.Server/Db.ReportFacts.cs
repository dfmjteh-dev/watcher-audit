using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// 0.14.4: заготовки отчётов (ReportFacts.cs, ReportFactsService) — факты по
// «хост × местные сутки сервера». Пересчитываются целыми сутками хоста: пришли
// новые сегменты или удалены старые — сутки помечаются в report_dirty, служба
// пересчитывает их из segments/segment_activity и заменяет строки.
public sealed partial class Db
{
    private const string HourFormat = "yyyy-MM-dd'T'HH";
    private const string DayFormat = "yyyy-MM-dd";

    // Миграция 18 (0.14.4).
    private static void ReportFactsTables(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE report_host_hour (host TEXT NOT NULL, hour TEXT NOT NULL, active_sec REAL NOT NULL, last_end TEXT NOT NULL,
  PRIMARY KEY (host, hour));
CREATE INDEX ix_report_host_hour_hour ON report_host_hour(hour);
CREATE TABLE report_user_hour (host TEXT NOT NULL, user TEXT NOT NULL, hour TEXT NOT NULL, active_sec REAL NOT NULL,
  segments INTEGER NOT NULL, last_end TEXT NOT NULL, PRIMARY KEY (host, user, hour));
CREATE INDEX ix_report_user_hour_hour ON report_user_hour(hour);
CREATE TABLE report_app_day (host TEXT NOT NULL, user TEXT NOT NULL, day TEXT NOT NULL, process TEXT NOT NULL,
  sec REAL NOT NULL, sec_work REAL NOT NULL, no_site REAL NOT NULL, no_site_work REAL NOT NULL);
CREATE INDEX ix_report_app_day_day ON report_app_day(day);
CREATE INDEX ix_report_app_day_host ON report_app_day(host, day);
CREATE TABLE report_subject_day (host TEXT NOT NULL, user TEXT NOT NULL, day TEXT NOT NULL, kind TEXT NOT NULL,
  key TEXT NOT NULL, variant TEXT NOT NULL, sec REAL NOT NULL, sec_work REAL NOT NULL);
CREATE INDEX ix_report_subject_day_day ON report_subject_day(day);
CREATE INDEX ix_report_subject_day_host ON report_subject_day(host, day);
CREATE TABLE report_dirty (host TEXT NOT NULL, day TEXT NOT NULL, PRIMARY KEY (host, day));";
        cmd.ExecuteNonQuery();
    }

    public static string LocalDayOf(DateTime utc) =>
        (utc + TimeSpan.FromMinutes(ServerClock.OffsetMinutes(utc))).ToString(DayFormat, CultureInfo.InvariantCulture);

    // Пометить к пересчёту местные сутки, которые задевает запись [startUtc, endUtc].
    public void MarkReportDirty(string host, DateTime startUtc, DateTime endUtc)
    {
        using var c = Open();
        MarkReportDirty(c, null, host, startUtc, endUtc);
    }

    internal static void MarkReportDirty(SqliteConnection c, SqliteTransaction? tx, string host, DateTime startUtc, DateTime endUtc)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO report_dirty (host, day) VALUES ($h, $d)";
        var pd = cmd.Parameters.Add("$d", SqliteType.Text);
        cmd.Parameters.AddWithValue("$h", host);
        if (endUtc < startUtc) endUtc = startUtc;
        string last = LocalDayOf(endUtc);
        for (var t = startUtc; ; t = t.AddDays(1))
        {
            string day = LocalDayOf(t);
            pd.Value = day;
            cmd.ExecuteNonQuery();
            if (string.CompareOrdinal(day, last) >= 0) break;
        }
    }

    // Сегменты с id > afterId — пометить их сутки; вернуть новый максимальный id.
    public long MarkReportDirtySince(long afterId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        long max = afterId;
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id, host, started_at, ended_at FROM segments WHERE id > $a ORDER BY id";
            cmd.Parameters.AddWithValue("$a", afterId);
            using var r = cmd.ExecuteReader();
            var rows = new List<(string Host, DateTime S, DateTime E)>();
            while (r.Read())
            {
                max = Math.Max(max, r.GetInt64(0));
                rows.Add((r.GetString(1), ParseUtc(r.GetString(2)), ParseUtc(r.GetString(3))));
            }
            r.Close();
            foreach (var (host, s, e) in rows.Distinct()) MarkReportDirty(c, tx, host, s, e);
        }
        tx.Commit();
        return max;
    }

    public List<(string Host, string Day)> ReportDirty(int limit)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT host, day FROM report_dirty ORDER BY day DESC, host LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<(string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    public int ReportDirtyCount()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM report_dirty";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // Полный пересчёт (сменилась зона сервера или типы программ): все факты
    // убрать, все сутки всех хостов — в очередь. Возвращает максимальный id сегмента.
    public long ResetReportFacts()
    {
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM report_host_hour; DELETE FROM report_user_hour; DELETE FROM report_app_day; DELETE FROM report_subject_day; DELETE FROM report_dirty;";
            cmd.ExecuteNonQuery();
        }
        return MarkReportDirtySince(0);
    }

    public void DeleteReportFactsOfHost(string host)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"DELETE FROM report_host_hour WHERE host = $h; DELETE FROM report_user_hour WHERE host = $h;
DELETE FROM report_app_day WHERE host = $h; DELETE FROM report_subject_day WHERE host = $h; DELETE FROM report_dirty WHERE host = $h;";
        cmd.Parameters.AddWithValue("$h", host);
        cmd.ExecuteNonQuery();
    }

    // Исходные данные одних местных суток хоста: сегменты, пересекающие сутки, и
    // смены окон в них (длительность — до следующей смены или конца сегмента).
    public (List<ReportSegment> Segments, List<ReportActivity> Activity) ReportSourceForHostDay(string host, DateTime fromUtc, DateTime toUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        // started_at ограничен снизу (сутки назад) — чтобы работал индекс (host, started_at);
        // сегмент длиннее суток — сбой агента, такие режутся MaxSegmentSeconds.
        const string where = "s.host = $h AND s.started_at >= $lo AND s.started_at < $to AND s.ended_at > $from";
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$lo", fromUtc.AddDays(-1).ToString("o"));
        cmd.Parameters.AddWithValue("$from", fromUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$to", toUtc.ToString("o"));
        cmd.CommandText = $"SELECT s.host, s.session_user, s.started_at, s.ended_at FROM segments s WHERE {where}";
        var segments = new List<ReportSegment>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                segments.Add(new ReportSegment(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), ParseUtc(r.GetString(2)), ParseUtc(r.GetString(3))));

        cmd.CommandText = $@"
SELECT host, session_user, process, ts, dur, title, site FROM (
  SELECT s.host, s.session_user, a.process, a.ts, a.title, a.site,
         COALESCE(LEAD(a.offset_sec) OVER (PARTITION BY a.segment_id ORDER BY a.offset_sec), s.duration_sec) - a.offset_sec AS dur
  FROM segment_activity a JOIN segments s ON s.id = a.segment_id
  WHERE {where}
) WHERE process IS NOT NULL AND process <> '' AND dur > 0";
        var activity = new List<ReportActivity>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                activity.Add(new ReportActivity(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), ParseUtc(r.GetString(3)), r.GetDouble(4),
                    r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return (segments, activity);
    }

    // Заменить факты одних местных суток хоста и снять отметку «к пересчёту».
    [SuppressMessage("Security", "CA2100", Justification = "Exec вызывается только со строковыми литералами; значения — через Parameters.")]
    public void ReplaceReportFacts(string host, string day, ReportFactSet f)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        void Exec(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$host", host);
            cmd.Parameters.AddWithValue("$day", day);
            cmd.ExecuteNonQuery();
        }
        // Часы суток — строки 'yyyy-MM-ddTHH', начинающиеся с day.
        Exec("DELETE FROM report_host_hour WHERE host = $host AND hour >= $day AND hour < $day || 'U'");
        Exec("DELETE FROM report_user_hour WHERE host = $host AND hour >= $day AND hour < $day || 'U'");
        Exec("DELETE FROM report_app_day WHERE host = $host AND day = $day");
        Exec("DELETE FROM report_subject_day WHERE host = $host AND day = $day");

        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO report_host_hour (host, hour, active_sec, last_end) VALUES ($host, $hour, $sec, $last)";
            cmd.Parameters.AddWithValue("$host", host);
            var ph = cmd.Parameters.Add("$hour", SqliteType.Text); var ps = cmd.Parameters.Add("$sec", SqliteType.Real); var pl = cmd.Parameters.Add("$last", SqliteType.Text);
            foreach (var h in f.HostHours)
            {
                ph.Value = h.Hour.ToString(HourFormat, CultureInfo.InvariantCulture); ps.Value = h.ActiveSec; pl.Value = h.LastEndUtc.ToString("o");
                cmd.ExecuteNonQuery();
            }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO report_user_hour (host, user, hour, active_sec, segments, last_end) VALUES ($host, $user, $hour, $sec, $n, $last)";
            cmd.Parameters.AddWithValue("$host", host);
            var pu = cmd.Parameters.Add("$user", SqliteType.Text); var ph = cmd.Parameters.Add("$hour", SqliteType.Text);
            var ps = cmd.Parameters.Add("$sec", SqliteType.Real); var pn = cmd.Parameters.Add("$n", SqliteType.Integer); var pl = cmd.Parameters.Add("$last", SqliteType.Text);
            foreach (var h in f.UserHours)
            {
                pu.Value = h.User; ph.Value = h.Hour.ToString(HourFormat, CultureInfo.InvariantCulture); ps.Value = h.ActiveSec; pn.Value = h.Segments;
                pl.Value = h.LastEndUtc.ToString("o");
                cmd.ExecuteNonQuery();
            }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO report_app_day (host, user, day, process, sec, sec_work, no_site, no_site_work) VALUES ($host, $user, $d, $p, $s, $w, $n, $nw)";
            cmd.Parameters.AddWithValue("$host", host);
            var pu = cmd.Parameters.Add("$user", SqliteType.Text); var pd = cmd.Parameters.Add("$d", SqliteType.Text); var pp = cmd.Parameters.Add("$p", SqliteType.Text);
            var ps = cmd.Parameters.Add("$s", SqliteType.Real); var pw = cmd.Parameters.Add("$w", SqliteType.Real);
            var pn = cmd.Parameters.Add("$n", SqliteType.Real); var pnw = cmd.Parameters.Add("$nw", SqliteType.Real);
            foreach (var a in f.Apps)
            {
                pu.Value = a.User; pd.Value = a.Day.ToString(DayFormat, CultureInfo.InvariantCulture); pp.Value = a.Process;
                ps.Value = a.Sec; pw.Value = a.SecWork; pn.Value = a.NoSite; pnw.Value = a.NoSiteWork;
                cmd.ExecuteNonQuery();
            }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO report_subject_day (host, user, day, kind, key, variant, sec, sec_work) VALUES ($host, $user, $d, $k, $key, $v, $s, $w)";
            cmd.Parameters.AddWithValue("$host", host);
            var pu = cmd.Parameters.Add("$user", SqliteType.Text); var pd = cmd.Parameters.Add("$d", SqliteType.Text); var pk = cmd.Parameters.Add("$k", SqliteType.Text);
            var pkey = cmd.Parameters.Add("$key", SqliteType.Text); var pv = cmd.Parameters.Add("$v", SqliteType.Text);
            var ps = cmd.Parameters.Add("$s", SqliteType.Real); var pw = cmd.Parameters.Add("$w", SqliteType.Real);
            foreach (var s in f.Subjects)
            {
                pu.Value = s.User; pd.Value = s.Day.ToString(DayFormat, CultureInfo.InvariantCulture); pk.Value = s.Kind; pkey.Value = s.Key; pv.Value = s.Variant;
                ps.Value = s.Sec; pw.Value = s.SecWork;
                cmd.ExecuteNonQuery();
            }
        }
        Exec("DELETE FROM report_dirty WHERE host = $host AND day = $day");
        tx.Commit();
    }

    // Факты за местные сутки [fromDay, toDay] (включительно) с фильтрами.
    [SuppressMessage("Security", "CA2100", Justification = "WHERE собирается из констант и имён параметров; значения — через Parameters.")]
    public ReportFactSet LoadReportFacts(string fromDay, string toDay, IReadOnlyCollection<long>? allowedGroupIds, long? groupId, string? host, string? user)
    {
        using var c = Open();
        var f = ReportFactSet.Empty();
        string toExclusive = DateTime.ParseExact(toDay, DayFormat, CultureInfo.InvariantCulture).AddDays(1).ToString(DayFormat, CultureInfo.InvariantCulture);

        SqliteCommand Cmd(string table, string timeCol, bool byUser)
        {
            var cmd = c.CreateCommand();
            var where = new List<string> { $"t.{timeCol} >= $from", $"t.{timeCol} < $to" };
            cmd.Parameters.AddWithValue("$from", fromDay);
            cmd.Parameters.AddWithValue("$to", toExclusive);
            bool join = allowedGroupIds is not null || groupId is not null;
            // Пустой список групп viewer'а — ни одного хоста.
            if (allowedGroupIds is not null) where.Add(allowedGroupIds.Count == 0 ? "0" : "h.group_id IN (" + AddInParams(cmd, "ag", allowedGroupIds) + ")");
            if (groupId is not null) { where.Add("h.group_id = $g"); cmd.Parameters.AddWithValue("$g", groupId.Value); }
            if (!string.IsNullOrEmpty(host)) { where.Add("t.host = $h"); cmd.Parameters.AddWithValue("$h", host); }
            if (byUser && !string.IsNullOrEmpty(user)) { where.Add("t.user = $u COLLATE NOCASE"); cmd.Parameters.AddWithValue("$u", user); }
            cmd.CommandText = $"SELECT t.* FROM {table} t {(join ? "JOIN hosts h ON h.name = t.host" : "")} WHERE {string.Join(" AND ", where)}";
            return cmd;
        }
        static DateTime Hour(string s) => DateTime.ParseExact(s, HourFormat, CultureInfo.InvariantCulture);
        static DateTime Day(string s) => DateTime.ParseExact(s, DayFormat, CultureInfo.InvariantCulture);

        if (string.IsNullOrEmpty(user))
            using (var cmd = Cmd("report_host_hour", "hour", false))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    f.HostHours.Add(new HostHourFact(r.GetString(0), Hour(r.GetString(1)), r.GetDouble(2), ParseUtc(r.GetString(3))));
        using (var cmd = Cmd("report_user_hour", "hour", true))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                f.UserHours.Add(new UserHourFact(r.GetString(0), r.GetString(1), Hour(r.GetString(2)), r.GetDouble(3), r.GetInt32(4), ParseUtc(r.GetString(5))));
        using (var cmd = Cmd("report_app_day", "day", true))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                f.Apps.Add(new AppDayFact(r.GetString(0), r.GetString(1), Day(r.GetString(2)), r.GetString(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7)));
        using (var cmd = Cmd("report_subject_day", "day", true))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                f.Subjects.Add(new SubjectDayFact(r.GetString(0), r.GetString(1), Day(r.GetString(2)), r.GetString(3), r.GetString(4), r.GetString(5), r.GetDouble(6), r.GetDouble(7)));
        return f;
    }

    // Подсказки поля «Пользователь» — все пользователи за период (без фильтра по
    // пользователю: иначе после первой буквы список схлопывался до одного).
    [SuppressMessage("Security", "CA2100", Justification = "WHERE собирается из констант и имён параметров; значения — через Parameters.")]
    public List<string> ReportUsers(string fromDay, string toDay, IReadOnlyCollection<long>? allowedGroupIds, long? groupId, string? host)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        string toExclusive = DateTime.ParseExact(toDay, DayFormat, CultureInfo.InvariantCulture).AddDays(1).ToString(DayFormat, CultureInfo.InvariantCulture);
        var where = new List<string> { "t.hour >= $from", "t.hour < $to", "t.user <> ''" };
        cmd.Parameters.AddWithValue("$from", fromDay);
        cmd.Parameters.AddWithValue("$to", toExclusive);
        bool join = allowedGroupIds is not null || groupId is not null;
        // Пустой список групп viewer'а — ни одного хоста.
        if (allowedGroupIds is not null) where.Add(allowedGroupIds.Count == 0 ? "0" : "h.group_id IN (" + AddInParams(cmd, "ag", allowedGroupIds) + ")");
        if (groupId is not null) { where.Add("h.group_id = $g"); cmd.Parameters.AddWithValue("$g", groupId.Value); }
        if (!string.IsNullOrEmpty(host)) { where.Add("t.host = $h"); cmd.Parameters.AddWithValue("$h", host); }
        cmd.CommandText = $"SELECT DISTINCT t.user FROM report_user_hour t {(join ? "JOIN hosts h ON h.name = t.host" : "")} WHERE {string.Join(" AND ", where)} ORDER BY t.user COLLATE NOCASE LIMIT 1000";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }
}
