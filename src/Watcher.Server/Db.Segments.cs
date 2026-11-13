using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// Видео-сегменты: запись после приёма, поиск для плеера и выгрузки.

public sealed record Segment(
    long Id,
    string Host,
    string Filename,
    DateTime StartedAt,
    DateTime EndedAt,
    double DurationSec,
    int Frames,
    int MotionEvents,
    string StartReason,
    long SizeBytes,
    string Sha256,
    string FilePath,
    DateTime UploadedAt,
    string? SessionUser = null,
    // R7: исходное время начала по часам станции, если StartedAt исправлен
    // (часы станции были неверны, см. StationClock); иначе null.
    DateTime? StationStartedAt = null,
    // 0.13: мониторы в координатах кадра [{x,y,w,h,primary}] — плеер показывает
    // один монитор; null — агент старше 0.13 или монитор один без раскладки.
    string? MonitorsJson = null,
    // Пережатие на сервере (Db.Transcode.cs): хеш файла от агента и когда
    // пережат; null — файл тот, что прислал агент.
    string? SourceSha256 = null,
    DateTime? TranscodedAt = null,
    string? TranscodeStatus = null);

public sealed partial class Db
{
    // R9: откат строки, если после Insert блоб не удалось перенести на место.
    public void DeleteSegmentRow(long id) => DeleteRow("segments", id);

    // Средний суточный прирост для прогноза хранилища на «Обзоре» (E2): по
    // started_at за последние N дней, видео + консоль.
    public long BytesSince(DateTime sinceUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT (SELECT COALESCE(SUM(size_bytes),0) FROM segments WHERE started_at >= $s)
     + (SELECT COALESCE(SUM(size_bytes),0) FROM terminal_sessions WHERE started_at >= $s)";
        cmd.Parameters.AddWithValue("$s", sinceUtc.ToString("o"));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public long Insert(Segment s)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO segments (host, filename, started_at, ended_at, duration_sec, frames, motion_events, start_reason, size_bytes, sha256, file_path, uploaded_at, session_user, station_started_at, monitors_json)
VALUES ($host, $filename, $sa, $ea, $dur, $fr, $me, $sr, $size, $sha, $fp, $up, $su, $ssa, $mon)
RETURNING id;";
        cmd.Parameters.AddWithValue("$host", s.Host);
        cmd.Parameters.AddWithValue("$filename", s.Filename);
        cmd.Parameters.AddWithValue("$sa", s.StartedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$ea", s.EndedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$dur", s.DurationSec);
        cmd.Parameters.AddWithValue("$fr", s.Frames);
        cmd.Parameters.AddWithValue("$me", s.MotionEvents);
        cmd.Parameters.AddWithValue("$sr", s.StartReason);
        cmd.Parameters.AddWithValue("$size", s.SizeBytes);
        cmd.Parameters.AddWithValue("$sha", s.Sha256);
        cmd.Parameters.AddWithValue("$fp", s.FilePath);
        cmd.Parameters.AddWithValue("$up", s.UploadedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$su", (object?)s.SessionUser ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ssa", (object?)s.StationStartedAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mon", (object?)s.MonitorsJson ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    public Segment? ByFilename(string filename)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM segments WHERE filename = $f LIMIT 1";
        cmd.Parameters.AddWithValue("$f", filename);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public Segment? ById(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM segments WHERE id = $i LIMIT 1";
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    // offset (E3, плеер грузит сутки страницами): порядок обязан быть полным —
    // id вторым ключом, иначе сегменты с одинаковым started_at (RDS: сессии
    // одного хоста стартуют одновременно) могли бы повториться или пропасть на
    // границе страниц.
    [SuppressMessage("Security", "CA2100", Justification = "WHERE/JOIN собираются только из констант кода и имён параметров ($x, AddInParams); значения пользователя — только через Parameters.")]
    public List<Segment> Query(string? host, DateTime? from, DateTime? to, bool motionOnly, int limit, string? sessionUser = null, IReadOnlyCollection<long>? allowedGroupIds = null, int offset = 0)
    {
        var list = new List<Segment>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        if (host is not null) { where.Add("host = $h"); cmd.Parameters.AddWithValue("$h", host); }
        if (from is not null) { where.Add("started_at >= $from"); cmd.Parameters.AddWithValue("$from", from.Value.ToString("o")); }
        if (to is not null) { where.Add("started_at < $to"); cmd.Parameters.AddWithValue("$to", to.Value.ToString("o")); }
        if (motionOnly) { where.Add("motion_events > 0"); }
        if (sessionUser is not null) { where.Add("session_user = $su"); cmd.Parameters.AddWithValue("$su", sessionUser); }
        // Этап C1 (roadmap-next.md): ограничение viewer'а группами — см. Inventory.
        string joinSql = "";
        if (allowedGroupIds is not null)
        {
            joinSql = "JOIN hosts h ON h.name = segments.host";
            where.Add(GroupIn(cmd, "g", allowedGroupIds));
        }
        string whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $"SELECT segments.* FROM segments {joinSql} {whereSql} ORDER BY started_at DESC, segments.id DESC LIMIT $lim OFFSET $off";
        cmd.Parameters.AddWithValue("$off", offset);
        cmd.Parameters.AddWithValue("$lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    private static Segment Map(SqliteDataReader r) => new(
        r.GetInt64(r.GetOrdinal("id")),
        r.GetString(r.GetOrdinal("host")),
        r.GetString(r.GetOrdinal("filename")),
        DateTime.Parse(r.GetString(r.GetOrdinal("started_at")), null, System.Globalization.DateTimeStyles.RoundtripKind),
        DateTime.Parse(r.GetString(r.GetOrdinal("ended_at")), null, System.Globalization.DateTimeStyles.RoundtripKind),
        r.GetDouble(r.GetOrdinal("duration_sec")),
        r.GetInt32(r.GetOrdinal("frames")),
        r.GetInt32(r.GetOrdinal("motion_events")),
        r.GetString(r.GetOrdinal("start_reason")),
        r.GetInt64(r.GetOrdinal("size_bytes")),
        r.GetString(r.GetOrdinal("sha256")),
        r.GetString(r.GetOrdinal("file_path")),
        DateTime.Parse(r.GetString(r.GetOrdinal("uploaded_at")), null, System.Globalization.DateTimeStyles.RoundtripKind),
        r.IsDBNull(r.GetOrdinal("session_user")) ? null : r.GetString(r.GetOrdinal("session_user")),
        ReadStationStartedAt(r),
        r.IsDBNull(r.GetOrdinal("monitors_json")) ? null : r.GetString(r.GetOrdinal("monitors_json")),
        r.IsDBNull(r.GetOrdinal("source_sha256")) ? null : r.GetString(r.GetOrdinal("source_sha256")),
        r.IsDBNull(r.GetOrdinal("transcoded_at")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("transcoded_at")), null, System.Globalization.DateTimeStyles.RoundtripKind),
        r.IsDBNull(r.GetOrdinal("transcode_status")) ? null : r.GetString(r.GetOrdinal("transcode_status"))
    );

    // Миграция 12.
    private static void SegmentMonitors(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "ALTER TABLE segments ADD COLUMN monitors_json TEXT";
        cmd.ExecuteNonQuery();
    }

    private static DateTime? ReadStationStartedAt(SqliteDataReader r)
    {
        int i = r.GetOrdinal("station_started_at");
        return r.IsDBNull(i) ? null : DateTime.Parse(r.GetString(i), null, System.Globalization.DateTimeStyles.RoundtripKind);
    }
}
