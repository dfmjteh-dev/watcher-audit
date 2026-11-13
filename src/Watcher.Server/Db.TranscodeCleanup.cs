using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Watcher.Server;

public sealed record TranscodeQuarantineEntry(long Id, string Host, string RecordedAt, string Path, string ExpiresAt, bool Held);
public sealed record TranscodeCleanupResult(bool Eligible, bool Applied, string? Reason = null);

public sealed partial class Db
{
    public List<TranscodeQuarantineEntry> ExpiredTranscodeQuarantine(DateTime now)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT j.segment_id,j.host,j.recorded_at,j.quarantine_path,j.expires_at,
EXISTS(SELECT 1 FROM legal_holds h WHERE h.host=j.host AND h.released_at IS NULL
AND (h.from_ts IS NULL OR j.recorded_at>=h.from_ts) AND (h.to_ts IS NULL OR j.recorded_at<h.to_ts))
FROM transcode_jobs j WHERE j.status='quarantined' AND j.expires_at<=$now ORDER BY j.expires_at LIMIT 100";
        cmd.Parameters.AddWithValue("$now", now.ToString("o")); var result = new List<TranscodeQuarantineEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5)));
        return result;
    }

    // Отдельное контролируемое действие: по умолчанию dry-run. Блокировка SQLite
    // исключает назначение hold между проверкой и перемещением архива.
    [SuppressMessage("Security", "CA2100", Justification = "SQL — литералы; значения — параметры.")]
    public TranscodeCleanupResult CleanupTranscodeQuarantine(long id, string dataDir, DateTime now, string actor, bool apply = false)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = @"SELECT quarantine_path FROM transcode_jobs j WHERE segment_id=$id AND status IN ('quarantined','expired') AND expires_at<=$now
AND NOT EXISTS(SELECT 1 FROM legal_holds h WHERE h.host=j.host AND h.released_at IS NULL
AND (h.from_ts IS NULL OR j.recorded_at>=h.from_ts) AND (h.to_ts IS NULL OR j.recorded_at<h.to_ts))";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$now", now.ToString("o"));
        if (cmd.ExecuteScalar() is not string relative) return new(false, false, "not expired, held or absent");
        if (relative != "transcode-quarantine/" + id) return new(false, false, "unexpected quarantine path");
        string root = Path.GetFullPath(dataDir);
        string folder = Path.Combine(root, "transcode-quarantine", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string tombstone = folder + ".deleting";
        foreach (string path in new[] { root, Path.GetDirectoryName(folder)!, folder, tombstone })
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return new(false, false, "reparse point");
        // Плоский архив: никакого рекурсивного обхода ссылок/подкаталогов.
        string source = Directory.Exists(folder) ? folder : tombstone;
        if (!Directory.Exists(source)) return new(false, false, "archive missing");
        if (Directory.EnumerateDirectories(source).Any() || Directory.EnumerateFiles(source).Any(f =>
            (Path.GetFileName(f) is not ("source.mp4" or "manifest.json")) || (File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0))
            return new(false, false, "unexpected archive contents");
        if (!apply) return new(true, false);
        bool moved = false;
        try
        {
            cmd.CommandText = "INSERT INTO audit_log(ts,username,action,target,details) VALUES($now,$actor,'transcode.quarantine-cleanup',$target,$details)";
            cmd.Parameters.AddWithValue("$actor", actor); cmd.Parameters.AddWithValue("$target", "segment:" + id);
            cmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new { id, path = relative })); cmd.ExecuteNonQuery();
            if (source == folder) { Directory.Move(folder, tombstone); moved = true; }
            cmd.CommandText = "UPDATE transcode_jobs SET status='expired',updated_at=$now WHERE segment_id=$id"; cmd.ExecuteNonQuery();
            tx.Commit();
        }
        catch
        {
            if (moved && Directory.Exists(tombstone)) Directory.Move(tombstone, folder);
            throw;
        }
        // При ошибке удаления остаётся tombstone; логически операция уже завершена.
        try { Directory.Delete(tombstone, recursive: true); }
        catch (IOException) { return new(true, true, "archive removal pending"); }
        catch (UnauthorizedAccessException) { return new(true, true, "archive removal pending"); }
        return new(true, true);
    }
}
