using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// Пережатие записей на сервере (TranscodeService): агент пишет быстрым
// x264 ultrafast — это дёшево для станции (RDS-сервер с сотней сессий), но
// файлы в 4–6 раз больше нужного. Сервер раз в час пережимает закрытые
// сегменты в AV1 тем же mp4 (faststart), плеер и API не меняются.
//
// transcode_status: NULL — ещё не обработан; done — пережат; kept — пережатый
// не дал выигрыша, оставлен исходный; failed — ffmpeg упал или длительность не
// сошлась, исходный не тронут. Повторы и диагностика — в transcode_jobs.
// source_sha256/source_size_bytes — что прислал агент; sha256/size_bytes после
// пережатия — у файла, который лежит сейчас (его и проверяет выгрузка).
// Исходный файл удаляется сразу после подмены; плеер, открывший запись до неё,
// получает 410 по старому ?v= и переоткрывает новую версию с той же секунды
// (RecordingEndpoints, views/video.js).
public sealed record TranscodeCandidate(long Id, string FilePath, string Sha256, long SizeBytes, double DurationSec = 0);

public sealed record TranscodedFile(string FilePath, string Sha256, long SizeBytes);

// Сводка для «Обзора»: сколько пережато и сэкономлено, сколько ждёт.
public sealed record TranscodeStats(
    long Done, long SourceBytes, long DoneBytes, long Kept, long Failed, long Pending, long PendingBytes, long Retrying = 0, long Repaired = 0, long Quarantined = 0);

public sealed partial class Db
{
    // Миграция 15.
    private static void SegmentTranscode(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
ALTER TABLE segments ADD COLUMN transcode_status TEXT;
ALTER TABLE segments ADD COLUMN transcoded_at TEXT;
ALTER TABLE segments ADD COLUMN source_sha256 TEXT;
ALTER TABLE segments ADD COLUMN source_size_bytes INTEGER;
CREATE INDEX ix_segments_transcode_pending ON segments(started_at) WHERE transcode_status IS NULL;";
        cmd.ExecuteNonQuery();
    }

    // Свежие сначала: они дольше всех проживут до retention, выигрыш по месту
    // от них больше. Под legal hold не трогаем — запись держат как есть.
    [SuppressMessage("Security", "CA2100", Justification = "В запрос вставляется только Db.HeldClause (константа кода); значения — через Parameters.")]
    public List<TranscodeCandidate> TranscodeCandidates(DateTime uploadedBeforeUtc, int limit, DateTime? nowUtc = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $@"SELECT s.id, s.file_path, s.sha256, s.size_bytes, s.duration_sec FROM segments s
WHERE s.transcode_status IS NULL AND s.size_bytes > 0 AND s.uploaded_at < $cut AND NOT {HeldClause("s")}
AND NOT EXISTS(SELECT 1 FROM transcode_jobs j WHERE j.segment_id=s.id AND ((j.next_at IS NOT NULL AND j.next_at>$now) OR (j.lease_until IS NOT NULL AND j.lease_until>$now)))
ORDER BY s.started_at DESC, s.id DESC LIMIT $lim";
        cmd.Parameters.AddWithValue("$cut", uploadedBeforeUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$lim", limit);
        cmd.Parameters.AddWithValue("$now", (nowUtc ?? DateTime.UtcNow).ToString("o"));
        var list = new List<TranscodeCandidate>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetDouble(4)));
        return list;
    }

    public TranscodeStats TranscodeStats()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT
  COALESCE(SUM(transcode_status = 'done'), 0),
  COALESCE(SUM(CASE WHEN transcode_status = 'done' THEN source_size_bytes END), 0),
  COALESCE(SUM(CASE WHEN transcode_status = 'done' THEN size_bytes END), 0),
  COALESCE(SUM(transcode_status = 'kept'), 0),
  COALESCE(SUM(transcode_status = 'failed'), 0),
  COALESCE(SUM(transcode_status IS NULL AND size_bytes > 0), 0),
  COALESCE(SUM(CASE WHEN transcode_status IS NULL THEN size_bytes END), 0)
, (SELECT COUNT(*) FROM transcode_jobs WHERE status='retry'),
  COALESCE(SUM(transcode_status='repaired'),0),
  (SELECT COUNT(*) FROM transcode_jobs WHERE status='quarantined')
FROM segments";
        using var r = cmd.ExecuteReader();
        r.Read();
        return new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9));
    }

    // Пути пережатых записей — для сверки после падения (TranscodeService.ReconcileOriginals).
    [SuppressMessage("Security", "CA2100", Justification = "Выбор между двумя строковыми константами, значения — через Parameters.")]
    public List<string> TranscodedFilePaths(DateTime? since)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = since is null
            ? $"SELECT s.file_path FROM segments s WHERE s.transcode_status = 'done' AND NOT {HeldClause("s")}"
            : $"SELECT s.file_path FROM segments s WHERE s.transcode_status = 'done' AND s.transcoded_at >= $since AND NOT {HeldClause("s")}";
        if (since is not null) cmd.Parameters.AddWithValue("$since", since.Value.ToString("o"));
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public long TranscodePendingCount()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM segments WHERE transcode_status IS NULL AND size_bytes > 0";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // Подмена файла записи. Условия в WHERE — на случай, если пока шёл ffmpeg,
    // запись удалили (retention, админ), повесили на неё hold или её уже кто-то
    // обработал: тогда 0 строк, и вызывающий удаляет свой новый файл.
    [SuppressMessage("Security", "CA2100", Justification = "В запрос вставляется только Db.HeldClause (константа кода); значения — через Parameters.")]
    public bool CommitTranscode(long id, string expectedFilePath, TranscodedFile result, DateTime nowUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $@"UPDATE segments SET
  source_sha256 = sha256, source_size_bytes = size_bytes,
  file_path = $fp, sha256 = $sha, size_bytes = $size, transcoded_at = $now, transcode_status = 'done'
WHERE id = $id AND file_path = $old AND transcode_status IS NULL AND NOT {HeldClause("segments")}";
        cmd.Parameters.AddWithValue("$fp", result.FilePath);
        cmd.Parameters.AddWithValue("$sha", result.Sha256);
        cmd.Parameters.AddWithValue("$size", result.SizeBytes);
        cmd.Parameters.AddWithValue("$now", nowUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$old", expectedFilePath);
        return cmd.ExecuteNonQuery() == 1;
    }

    // kept / failed: файл не меняется, только отметка, чтобы не браться снова.
    public void MarkTranscode(long id, string status, DateTime nowUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE segments SET transcode_status = $st, transcoded_at = $now WHERE id = $id AND transcode_status IS NULL";
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$now", nowUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}
