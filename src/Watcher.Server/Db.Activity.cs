using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// H9 (roadmap-next.md): активное окно рядом с видео. Агент пишет в sidecar
// сегмента смены окна (смещение во «времени видео», процесс, заголовок), сервер
// раскладывает их строками — поиск «когда открывали X» и подписи в плеере.
// Site — домен активной вкладки браузера (агенты с 0.14.3), см. AppKinds.
public sealed record ActivityEntry(double OffsetSec, string? Process, string? Title, string? Site = null);

public sealed record ActivityRow(long SegmentId, string Host, string? SessionUser, DateTime Ts, double OffsetSec, string? Process, string? Title, string? Site = null);

public sealed partial class Db
{
    // Миграция 4 (H9). search — процесс и заголовок в нижнем регистре
    // (ToLowerInvariant в C#): LIKE в SQLite без учёта регистра только для
    // ASCII, кириллица без этого искалась бы с точностью до регистра. Строки
    // уходят вместе с сегментом — триггер, а не правка каждого DELETE (retention,
    // откат загрузки R9).
    private static void SegmentActivity(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE segment_activity (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  segment_id INTEGER NOT NULL,
  host TEXT NOT NULL,
  ts TEXT NOT NULL,
  offset_sec REAL NOT NULL,
  process TEXT,
  title TEXT,
  search TEXT NOT NULL
);
CREATE INDEX ix_segment_activity_segment ON segment_activity(segment_id);
CREATE INDEX ix_segment_activity_host_ts ON segment_activity(host, ts);
CREATE INDEX ix_segment_activity_ts ON segment_activity(ts);
CREATE TRIGGER trg_segments_delete_activity AFTER DELETE ON segments
BEGIN
  DELETE FROM segment_activity WHERE segment_id = OLD.id;
END;";
        cmd.ExecuteNonQuery();
    }

    // Миграция 16 (0.14.3): домен вкладки браузера от агента — отчёт «Сайты»
    // группирует по нему, а не по заголовку страницы.
    private static void SegmentActivitySite(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "ALTER TABLE segment_activity ADD COLUMN site TEXT;";
        cmd.ExecuteNonQuery();
    }

    public void InsertActivity(long segmentId, string host, DateTime startedAt, IReadOnlyList<ActivityEntry> entries)
    {
        if (entries.Count == 0) return;
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO segment_activity (segment_id, host, ts, offset_sec, process, title, site, search)
VALUES ($seg, $host, $ts, $off, $proc, $title, $site, $search)";
        var pSeg = cmd.Parameters.Add("$seg", SqliteType.Integer);
        var pHost = cmd.Parameters.Add("$host", SqliteType.Text);
        var pTs = cmd.Parameters.Add("$ts", SqliteType.Text);
        var pOff = cmd.Parameters.Add("$off", SqliteType.Real);
        var pProc = cmd.Parameters.Add("$proc", SqliteType.Text);
        var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
        var pSite = cmd.Parameters.Add("$site", SqliteType.Text);
        var pSearch = cmd.Parameters.Add("$search", SqliteType.Text);
        foreach (var e in entries)
        {
            pSeg.Value = segmentId;
            pHost.Value = host;
            pTs.Value = startedAt.AddSeconds(e.OffsetSec).ToUniversalTime().ToString("o");
            pOff.Value = e.OffsetSec;
            pProc.Value = (object?)e.Process ?? DBNull.Value;
            pTitle.Value = (object?)e.Title ?? DBNull.Value;
            pSite.Value = (object?)e.Site ?? DBNull.Value;
            pSearch.Value = ((e.Process ?? "") + "\n" + (e.Title ?? "") + (e.Site is null ? "" : "\n" + e.Site)).ToLowerInvariant();
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<ActivityRow> ActivityForSegment(long segmentId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT a.segment_id, a.host, s.session_user, a.ts, a.offset_sec, a.process, a.title, a.site
FROM segment_activity a JOIN segments s ON s.id = a.segment_id
WHERE a.segment_id = $id ORDER BY a.offset_sec, a.id";
        cmd.Parameters.AddWithValue("$id", segmentId);
        return ReadActivity(cmd);
    }

    // Поиск по процессу и заголовку. host = null — по всем хостам (с учётом
    // групп viewer'а). Новые сверху, как остальные списки.
    [SuppressMessage("Security", "CA2100", Justification = "WHERE/JOIN собираются только из констант кода и имён параметров ($x, AddInParams); значения пользователя — только через Parameters.")]
    public List<ActivityRow> SearchActivity(string? host, DateTime? from, DateTime? to, string? query, string? sessionUser,
        IReadOnlyCollection<long>? allowedGroupIds, int limit, int offset)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        if (host is not null) { where.Add("a.host = $h"); cmd.Parameters.AddWithValue("$h", host); }
        if (from is not null) { where.Add("a.ts >= $from"); cmd.Parameters.AddWithValue("$from", from.Value.ToString("o")); }
        if (to is not null) { where.Add("a.ts < $to"); cmd.Parameters.AddWithValue("$to", to.Value.ToString("o")); }
        if (sessionUser is not null) { where.Add("s.session_user = $su"); cmd.Parameters.AddWithValue("$su", sessionUser); }
        if (!string.IsNullOrWhiteSpace(query))
        {
            where.Add("a.search LIKE $q ESCAPE '\\'");
            string q = query.Trim().ToLowerInvariant();
            cmd.Parameters.AddWithValue("$q", "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        }
        string joinSql = "";
        if (allowedGroupIds is not null)
        {
            joinSql = "JOIN hosts h ON h.name = a.host";
            where.Add(GroupIn(cmd, "g", allowedGroupIds));
        }
        string whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $@"
SELECT a.segment_id, a.host, s.session_user, a.ts, a.offset_sec, a.process, a.title, a.site
FROM segment_activity a JOIN segments s ON s.id = a.segment_id {joinSql} {whereSql}
ORDER BY a.ts DESC, a.id DESC LIMIT $lim OFFSET $off";
        cmd.Parameters.AddWithValue("$lim", limit);
        cmd.Parameters.AddWithValue("$off", offset);
        return ReadActivity(cmd);
    }

    // 0.14.3: процессы активных окон по всему парку — для «Настройки → Программы».
    public List<(string Process, long Count, bool HasSite)> TopProcesses(int days, int limit)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT MIN(process), COUNT(*), MAX(site IS NOT NULL) FROM segment_activity WHERE ts >= $since AND process IS NOT NULL AND process <> ''
GROUP BY process COLLATE NOCASE ORDER BY COUNT(*) DESC, MIN(process) COLLATE NOCASE LIMIT $lim";
        cmd.Parameters.AddWithValue("$since", DateTime.UtcNow.AddDays(-days).ToString("o"));
        cmd.Parameters.AddWithValue("$lim", limit);
        var list = new List<(string, long, bool)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2) == 1));
        return list;
    }

    private static List<ActivityRow> ReadActivity(SqliteCommand cmd)
    {
        var list = new List<ActivityRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ActivityRow(
                r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                DateTime.Parse(r.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
                r.GetDouble(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7)));
        return list;
    }
}
