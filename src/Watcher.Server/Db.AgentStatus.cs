using Watcher.Shared;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// 0.13 (п.5 и п.12 обратной связи 2026-10-14): что на самом деле происходит на
// агенте. Конфиг — какой хэш итогового конфига агент получил и какой применил
// каждый процесс записи экрана. Потери — журнал событий (какой файл, за какое
// время, почему) вместо голого счётчика droppedSegments, с отметкой «принято».

// Процесс записи экрана на станции (сессия пользователя): какой конфиг применил,
// пишет ли сейчас, когда последний раз отметился.
public sealed record CaptureReport(string? User, string? ConfigHash, bool? Recording, DateTime? At);

// Потерянная запись, как её сообщил агент. RecordedAt — когда запись была
// сделана (из имени файла), Reason — quota | age | broken | transcript-no-4688.
public sealed record LossReport(string? File, DateTime? RecordedAt, long? SizeBytes, string? Reason, DateTime? At, string? Detail = null);

public sealed record HostLoss(long Id, string Host, string File, DateTime? RecordedAt, long SizeBytes, string Reason,
    DateTime ReportedAt, DateTime? AcknowledgedAt, string? AcknowledgedBy, string? Detail = null);

public sealed record HostAgentStatus(string? DeliveredHash, DateTime? DeliveredAt, string? CapturesJson, bool LossJournal);

public sealed record HostLossGroup(string Reason, string? Detail, long Count, DateTime FirstReportedAt, DateTime LastReportedAt, long UnacknowledgedCount);

public sealed partial class Db
{
    public static readonly string[] LossReasons = ["quota", "age", "broken", "transcript-no-4688", "too-large"];

    // Хэш итогового конфига хоста — однозначная метка «какой конфиг»: сумма версий
    // группы и хоста (ConfigEntry.ConfigVersion) совпадает и у разных конфигов
    // (хост перенесли в другую группу).
    public static string ConfigHash(string configJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configJson)))[..12].ToLowerInvariant();

    // Миграция 10.
    private static void AgentStatus(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
ALTER TABLE hosts ADD COLUMN config_hash TEXT;
ALTER TABLE hosts ADD COLUMN config_hash_at TEXT;
ALTER TABLE hosts ADD COLUMN captures_json TEXT;
ALTER TABLE hosts ADD COLUMN loss_journal INTEGER NOT NULL DEFAULT 0;
CREATE TABLE host_losses (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  host TEXT NOT NULL,
  file TEXT NOT NULL,
  recorded_at TEXT,
  size_bytes INTEGER NOT NULL DEFAULT 0,
  reason TEXT NOT NULL,
  reported_at TEXT NOT NULL,
  acknowledged_at TEXT,
  acknowledged_by TEXT
);
CREATE UNIQUE INDEX ix_host_losses_unique ON host_losses(host, file, reason);
CREATE INDEX ix_host_losses_host_unack ON host_losses(host) WHERE acknowledged_at IS NULL;";
        cmd.ExecuteNonQuery();
    }

    // Из heartbeat: что агент получил (configHash — null у агентов до 0.13) и
    // кто на станции пишет экран. lossJournal — агент шлёт журнал потерь (поле
    // losses есть, хоть пустое): тогда UI показывает журнал, иначе старый счётчик.
    public void UpdateAgentStatus(string host, string? configHash, string? capturesJson, bool lossJournal)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
UPDATE hosts SET
  config_hash_at = CASE WHEN $hash IS NOT NULL AND config_hash IS NOT $hash THEN $now ELSE config_hash_at END,
  config_hash = COALESCE($hash, config_hash),
  captures_json = $caps,
  loss_journal = CASE WHEN $lj = 1 THEN 1 ELSE loss_journal END
WHERE name = $name";
        cmd.Parameters.AddWithValue("$hash", (object?)configHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$caps", (object?)capturesJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lj", lossJournal ? 1 : 0);
        cmd.Parameters.AddWithValue("$name", host);
        cmd.ExecuteNonQuery();
    }

    public HostAgentStatus? AgentStatusOf(string host)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT config_hash, config_hash_at, captures_json, loss_journal FROM hosts WHERE name = $n";
        cmd.Parameters.AddWithValue("$n", host);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new HostAgentStatus(r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : ParseUtc(r.GetString(1)),
            r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3) != 0);
    }

    // Принимает события потерь; повтор того же (host, file, reason) — не дубль.
    // Возвращает, сколько событий из присланных обработано (принято или уже было) —
    // агент удаляет их из своего журнала.
    public int InsertLosses(string host, IReadOnlyList<LossReport> losses)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        int handled = 0;
        foreach (var l in losses)
        {
            handled++;
            if (string.IsNullOrWhiteSpace(l.File) || l.File.Length > 260 || !UploadValidation.IsSafeMetaField(l.File)) continue;
            if (l.Reason is null || !LossReasons.Contains(l.Reason)) continue;
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO host_losses (host, file, recorded_at, size_bytes, reason, reported_at, detail)
VALUES ($h, $f, $rec, $size, $reason, $now, $detail)
ON CONFLICT(host, file, reason) DO UPDATE SET detail = COALESCE(host_losses.detail, excluded.detail)";
            cmd.Parameters.AddWithValue("$h", host);
            cmd.Parameters.AddWithValue("$f", l.File);
            cmd.Parameters.AddWithValue("$rec", l.RecordedAt is { } t ? t.ToUniversalTime().ToString("o") : DBNull.Value);
            cmd.Parameters.AddWithValue("$size", Math.Max(0, l.SizeBytes ?? 0));
            cmd.Parameters.AddWithValue("$reason", l.Reason);
            cmd.Parameters.AddWithValue("$detail", l.Reason == "transcript-no-4688" && TranscriptIssue.IsKnown(l.Detail) ? l.Detail : DBNull.Value);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return handled;
    }

    public List<HostLoss> ListLosses(string host, bool unacknowledgedOnly, int limit = 500)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $@"
SELECT id, host, file, recorded_at, size_bytes, reason, reported_at, acknowledged_at, acknowledged_by, detail
FROM host_losses WHERE host = $h {(unacknowledgedOnly ? "AND acknowledged_at IS NULL" : "")}
ORDER BY COALESCE(recorded_at, reported_at) DESC, id DESC LIMIT $lim";
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$lim", limit);
        var list = new List<HostLoss>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new HostLoss(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : ParseUtc(r.GetString(3)),
                r.GetInt64(4), r.GetString(5), ParseUtc(r.GetString(6)), r.IsDBNull(7) ? null : ParseUtc(r.GetString(7)),
                r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9)));
        return list;
    }

    private static void TranscriptLossDetail(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "ALTER TABLE host_losses ADD COLUMN detail TEXT";
        cmd.ExecuteNonQuery();
    }

    // Сводка считается по всему журналу, а не по первым 500 строкам UI.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Выбор между двумя константными запросами; имя хоста передаётся параметром.")]
    public List<HostLossGroup> LossGroups(string host, bool unacknowledgedOnly)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = unacknowledgedOnly
            ? "SELECT reason, detail, COUNT(*), MIN(reported_at), MAX(reported_at), SUM(CASE WHEN acknowledged_at IS NULL THEN 1 ELSE 0 END) FROM host_losses WHERE host=$h AND acknowledged_at IS NULL GROUP BY reason, detail ORDER BY COUNT(*) DESC, reason, detail LIMIT 64"
            : "SELECT reason, detail, COUNT(*), MIN(reported_at), MAX(reported_at), SUM(CASE WHEN acknowledged_at IS NULL THEN 1 ELSE 0 END) FROM host_losses WHERE host=$h GROUP BY reason, detail ORDER BY COUNT(*) DESC, reason, detail LIMIT 64";
        cmd.Parameters.AddWithValue("$h", host);
        using var reader = cmd.ExecuteReader();
        var result = new List<HostLossGroup>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2), ParseUtc(reader.GetString(3)), ParseUtc(reader.GetString(4)), reader.GetInt64(5)));
        return result;
    }

    public int AcknowledgeLosses(string host, string? username)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE host_losses SET acknowledged_at = $now, acknowledged_by = $u WHERE host = $h AND acknowledged_at IS NULL";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$u", (object?)username ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$h", host);
        return cmd.ExecuteNonQuery();
    }

    // Последний id в журнале — для алертов (новые события после него).
    public long MaxLossId()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(id), 0) FROM host_losses";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public List<(string Host, int Count)> LossesSince(long afterId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT host, COUNT(*) FROM host_losses WHERE id > $id GROUP BY host";
        cmd.Parameters.AddWithValue("$id", afterId);
        var list = new List<(string, int)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1)));
        return list;
    }

    // Принятые потери старше срока — не нужны (непринятые не удаляются: их ещё не видели).
    public int DeleteAcknowledgedLossesOlderThan(DateTime cutoffUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM host_losses WHERE acknowledged_at IS NOT NULL AND acknowledged_at < $cut";
        cmd.Parameters.AddWithValue("$cut", cutoffUtc.ToString("o"));
        return cmd.ExecuteNonQuery();
    }
}
