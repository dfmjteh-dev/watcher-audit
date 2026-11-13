using Microsoft.Data.Sqlite;

namespace Watcher.Server;

public sealed class RetentionService : BackgroundService
{
    private readonly Db _db;
    private readonly ServerConfig _cfg;
    private readonly ILogger<RetentionService> _log;
    // H4: «retention жив» для /healthz и счётчики удалений для /metrics.
    // Необязателен — тесты создают сервис без него.
    private readonly ServerHealth? _health;

    public RetentionService(Db db, ServerConfig cfg, ILogger<RetentionService> log, ServerHealth? health = null)
    {
        _db = db;
        _cfg = cfg;
        _log = log;
        _health = health;
        DiskSpace = () => DriveSpace(_cfg.DataDir);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                int stale = _db.CloseStaleTerminalSessions(TimeSpan.FromHours(48));
                if (stale > 0) _log.LogInformation("terminal: {Count} live session(s) without chunks for 48 h marked as no longer running", stale);
                RunOnce();
                _health?.RetentionSucceeded(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _health?.RetentionFailed();
                _log.LogError(ex, "retention failed");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(5, _cfg.RetentionScanIntervalMinutes)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "H6: в запрос вставляются только Db.HeldClause (константа) и AgeRule.Filter (константы и имена параметров $gN); значения — через Parameters.")]
    public void RunOnce()
    {
        _db.PruneDiagnostics(DateTime.UtcNow);
        _db.PruneDiagnosticArchives(DateTime.UtcNow);
        int byAge = 0;
        long freed = 0;

        using (var c = _db.Open())
        {
            foreach (var rule in AgeRules())
            {
                using var cmd = c.CreateCommand();
                // R7: и uploaded_at тоже — started_at это часы станции, у станции со
                // сброшенной датой запись иначе удалялась при первом же проходе.
                // H6: срок — по группе хоста, записи под legal hold не трогаем.
                cmd.CommandText = $@"SELECT s.id, s.file_path, s.size_bytes FROM segments s LEFT JOIN hosts h ON h.name = s.host
WHERE s.started_at < $cut AND s.uploaded_at < $cut AND {rule.Filter} AND NOT {Db.HeldClause("s")} ORDER BY s.started_at ASC";
                rule.Bind(cmd);
                var toDelete = new List<(long Id, string Path, long Size)>();
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) toDelete.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2)));
                foreach (var t in toDelete)
                {
                    if (DeleteOne(c, t.Id, t.Path)) { byAge++; freed += t.Size; }
                }
            }
        }

        if (byAge > 0)
            _log.LogInformation("retention: deleted {ByAge} by age, freed {Mb:F1} MB",
                byAge, freed / 1024.0 / 1024.0);
        _health?.RetentionDeletedRecords("age", "video", byAge);

        RunOnceTerminal();
        RunOnceAndroidEvents();
        RunOnceBySpace();
        RunOnceAudit();
        // Этап E2: отметки «был на связи в этот час» нужны только вкладке «Инфо»
        // (показывает последние 7 дней) — держим месяц, не настраивается.
        _db.DeletePresenceOlderThan(DateTime.UtcNow.AddDays(-PresenceRetentionDays));
        // 0.13: принятые потери — 90 дней после «Принято»; непринятые не трогаем.
        _db.DeleteAcknowledgedLossesOlderThan(DateTime.UtcNow.AddDays(-AcknowledgedLossRetentionDays));
        SweepStaleParts();
    }

    // H6: правило удаления по возрасту. Общий RetentionDays — для хостов без
    // группы и групп без своего срока; у группы со своим retention_days — свой
    // проход (0 — без удаления по возрасту, даже если общий срок задан).
    private sealed record AgeRule(DateTime Cutoff, string Filter, IReadOnlyList<long> GroupIds)
    {
        public void Bind(SqliteCommand cmd)
        {
            cmd.Parameters.AddWithValue("$cut", Cutoff.ToString("o"));
            for (int i = 0; i < GroupIds.Count; i++) cmd.Parameters.AddWithValue($"$g{i}", GroupIds[i]);
        }
    }

    private List<AgeRule> AgeRules()
    {
        var rules = new List<AgeRule>();
        var overrides = _db.GroupRetentions().Where(g => g.RetentionDays is not null).ToList();
        DateTime now = DateTime.UtcNow;
        if (_cfg.RetentionDays > 0)
        {
            var ids = overrides.Select(g => g.GroupId).ToList();
            string filter = ids.Count == 0
                ? "1 = 1"
                : "(h.group_id IS NULL OR h.group_id NOT IN (" + string.Join(", ", ids.Select((_, i) => $"$g{i}")) + "))";
            rules.Add(new AgeRule(now.AddDays(-_cfg.RetentionDays), filter, ids));
        }
        foreach (var g in overrides.Where(g => g.RetentionDays > 0))
            rules.Add(new AgeRule(now.AddDays(-g.RetentionDays!.Value), "h.group_id = $g0", [g.GroupId]));
        return rules;
    }

    internal const int PresenceRetentionDays = 30;
    private const int AcknowledgedLossRetentionDays = 90;
    public static readonly TimeSpan StalePartAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan PartSweepInterval = TimeSpan.FromDays(1);
    private DateTime _lastPartSweep = DateTime.MinValue;

    // R9: .part от загрузок, прерванных падением сервера. Обход всего blobs/ —
    // поэтому раз в сутки (и при старте), а не на каждом проходе retention.
    private void SweepStaleParts()
    {
        DateTime now = DateTime.UtcNow;
        if (now - _lastPartSweep < PartSweepInterval) return;
        _lastPartSweep = now;
        int deleted = BlobFiles.SweepStaleParts(Path.Combine(_cfg.DataDir, "blobs"), StalePartAge, now, _log);
        if (deleted > 0)
            _log.LogInformation("retention: deleted {Count} stale {Suffix} files", deleted, BlobFiles.PartSuffix);
    }

    // Этап C3 (roadmap-next.md): только по возрасту, отдельным AuditRetentionDays —
    // строки журнала крошечные, лимит по байтам к ним не имеет смысла, а удаление
    // журнала ради места под видео было бы ровно тем, от чего журнал защищает.
    private void RunOnceAudit()
    {
        if (_cfg.AuditRetentionDays <= 0) return;
        int deleted = _db.DeleteAuditOlderThan(DateTime.UtcNow.AddDays(-_cfg.AuditRetentionDays));
        if (deleted > 0)
            _log.LogInformation("audit retention: deleted {Count} entries older than {Days} days", deleted, _cfg.AuditRetentionDays);
    }

    // Фаза 8 (roadmap.md) — тот же RetentionDays/RetentionMaxBytes, что у
    // segments, не отдельный конфиг под terminal_sessions: это один и тот же
    // "сколько держим" выбор администратора, не повод плодить вторую пару
    // настроек под другую таблицу. terminal-input строки не имеют файлов на
    // диске (size_bytes=0, out_file_path/timing_file_path NULL) — удаляется
    // только строка; terminal-output удаляет оба блоба (.out и .timing).
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "H6: в запрос вставляются только Db.HeldClause (константа) и AgeRule.Filter (константы и имена параметров $gN); значения — через Parameters.")]
    private void RunOnceTerminal()
    {
        int byAge = 0;
        long freed = 0;

        using (var c = _db.Open())
        {
            foreach (var rule in AgeRules())
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = $@"SELECT s.id, s.out_file_path, s.timing_file_path, s.size_bytes FROM terminal_sessions s LEFT JOIN hosts h ON h.name = s.host
WHERE s.started_at < $cut AND s.uploaded_at < $cut AND s.live <> 1 AND {rule.Filter} AND NOT {Db.HeldClause("s")} ORDER BY s.started_at ASC";
                rule.Bind(cmd);
                var toDelete = new List<(long Id, string? Out, string? Timing, long Size)>();
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        toDelete.Add((r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3)));
                foreach (var t in toDelete)
                {
                    if (DeleteOneTerminal(c, t.Id, t.Out, t.Timing)) { byAge++; freed += t.Size; }
                }
            }
        }

        if (byAge > 0)
            _log.LogInformation("terminal retention: deleted {ByAge} by age, freed {Mb:F1} MB",
                byAge, freed / 1024.0 / 1024.0);
        _health?.RetentionDeletedRecords("age", "terminal", byAge);
    }

    // Этап 3 Android (docs/android-plan.md): события телефона чистятся тем же
    // сроком хранения, что видео и консоль (RetentionDays общий + retention_days
    // группы, 0 — не удалять по возрасту) — один выбор администратора «сколько
    // держим», не отдельная настройка. По месту (RetentionMaxBytes/диск) события
    // не считаем: это крошечные строки без блобов, как журнал доступа/потерь.
    // Legal hold к ним не применяется (он про записи экрана/консоли).
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "H6: в запрос вставляется только AgeRule.Filter (константы и имена параметров $gN); значения — через Parameters.")]
    private void RunOnceAndroidEvents()
    {
        int byAge = 0;
        using var c = _db.Open();
        foreach (var rule in AgeRules())
        {
            using var cmd = c.CreateCommand();
            // И ts, и received_at старше среза (R7: ts — часы устройства, у телефона
            // со сбитой датой события иначе удалялись бы при первом же проходе).
            cmd.CommandText = $@"DELETE FROM android_events WHERE id IN (
  SELECT e.id FROM android_events e LEFT JOIN hosts h ON h.name = e.host
  WHERE e.ts < $cut AND e.received_at < $cut AND {rule.Filter})";
            rule.Bind(cmd);
            byAge += cmd.ExecuteNonQuery();
        }
        if (byAge > 0)
            _log.LogInformation("android events retention: deleted {ByAge} by age", byAge);
    }

    // R8 (docs/code-review-2026-10.md): удаление по месту — одно на видео и
    // консоль. Раньше RetentionMaxBytes проверялся для каждой таблицы отдельно,
    // и вместе они занимали до 2× лимита; свободное место на томе не
    // проверялось вовсе, и при RetentionMaxBytes = 0 (дефолт) диск заполнялся
    // до отказа. Теперь освобождается max(превышение общего лимита, нехватка
    // до MinFreeDiskPercent), начиная с самых старых записей обеих таблиц,
    // независимо от возраста. terminal-input (size_bytes = 0, файлов нет) не
    // трогаем: место они не занимают, а удалять ввод ради места незачем.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "H6: в запрос вставляются только Db.HeldClause (константа) и AgeRule.Filter (константы и имена параметров $gN); значения — через Parameters.")]
    private void RunOnceBySpace()
    {
        using var c = _db.Open();
        long overBudget = 0;
        if (_cfg.RetentionMaxBytes > 0)
        {
            using var sumCmd = c.CreateCommand();
            sumCmd.CommandText = "SELECT (SELECT COALESCE(SUM(size_bytes),0) FROM segments) + (SELECT COALESCE(SUM(size_bytes),0) FROM terminal_sessions)";
            overBudget = Convert.ToInt64(sumCmd.ExecuteScalar()) - _cfg.RetentionMaxBytes;
        }
        long diskDeficit = DiskDeficitBytes();
        long toFree = Math.Max(overBudget, diskDeficit);
        if (toFree <= 0) return;
        if (diskDeficit > 0)
            _log.LogWarning("retention: free disk space below {Pct}% — deleting oldest records regardless of age, {Mb:F1} MB to free",
                _cfg.MinFreeDiskPercent, diskDeficit / 1024.0 / 1024.0);

        int deleted = 0, deletedTerminal = 0;
        long freed = 0;
        while (freed < toFree)
        {
            var batch = new List<(bool Terminal, long Id, string? Path, string? Timing, long Size)>();
            using (var cmd = c.CreateCommand())
            {
                // H6: записи под legal hold место не освобождают — если остались
                // только они, цикл ниже остановится, и в лог уйдёт предупреждение.
                cmd.CommandText = $@"
SELECT 0, s.id, s.file_path, NULL, s.size_bytes, s.started_at FROM segments s WHERE s.size_bytes > 0 AND NOT {Db.HeldClause("s")}
UNION ALL
SELECT 1, s.id, s.out_file_path, s.timing_file_path, s.size_bytes, s.started_at FROM terminal_sessions s WHERE s.size_bytes > 0 AND s.live <> 1 AND NOT {Db.HeldClause("s")}
ORDER BY 6 ASC LIMIT 200";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    batch.Add((r.GetInt64(0) == 1, r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4)));
            }
            if (batch.Count == 0) break;
            bool progress = false;
            foreach (var t in batch)
            {
                if (freed >= toFree) break;
                bool ok = t.Terminal ? DeleteOneTerminal(c, t.Id, t.Path, t.Timing) : DeleteOne(c, t.Id, t.Path!);
                // Не удалось — стоп, иначе следующий проход выберет ту же запись.
                if (!ok) { progress = false; break; }
                deleted++;
                if (t.Terminal) deletedTerminal++;
                freed += t.Size;
                progress = true;
            }
            if (!progress) break;
        }

        _log.LogInformation("retention: deleted {Count} oldest records by size/free space, freed {Mb:F1} MB",
            deleted, freed / 1024.0 / 1024.0);
        if (freed < toFree)
            _log.LogWarning("retention: could not free {Mb:F1} MB more — remaining records are under legal hold or failed to delete",
                (toFree - freed) / 1024.0 / 1024.0);
        _health?.RetentionDeletedRecords("space", "video", deleted - deletedTerminal);
        _health?.RetentionDeletedRecords("space", "terminal", deletedTerminal);
    }

    // Сколько байт не хватает до MinFreeDiskPercent свободного на томе с
    // DataDir; 0 — хватает, порог выключен или статистики тома нет.
    private long DiskDeficitBytes()
    {
        if (_cfg.MinFreeDiskPercent <= 0) return 0;
        if (DiskSpace() is not { } d || d.Total <= 0) return 0;
        long need = (long)(d.Total * (_cfg.MinFreeDiskPercent / 100.0));
        return Math.Max(0, need - d.Free);
    }

    // Подменяется в тестах. null — статистика тома недоступна.
    public Func<(long Free, long Total)?> DiskSpace { get; set; }

    public static (long Free, long Total)? DriveSpace(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetFullPath(path));
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception) { return null; }
    }

    private bool DeleteOneTerminal(SqliteConnection c, long id, string? outRelPath, string? timingRelPath)
    {
        try
        {
            foreach (string? rel in new[] { outRelPath, timingRelPath })
            {
                if (rel is null) continue;
                string full = Path.GetFullPath(Path.Combine(_cfg.DataDir, rel));
                if (File.Exists(full)) File.Delete(full);
                TryRemoveEmptyDirs(Path.GetDirectoryName(full));
            }
            Db.UnlinkTerminalChildren(c, id);
            using var del = c.CreateCommand();
            del.CommandText = "DELETE FROM terminal_sessions WHERE id = $id";
            del.Parameters.AddWithValue("$id", id);
            del.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("terminal delete failed id={Id}: {Err}", id, ex.Message);
            return false;
        }
    }

    private bool DeleteOne(SqliteConnection c, long id, string relPath)
    {
        string full = Path.GetFullPath(Path.Combine(_cfg.DataDir, relPath));
        try
        {
            // Оба варианта файла — исходный и пережатый (TranscodeService.FileVariants).
            foreach (string v in TranscodeService.FileVariants(relPath))
            {
                string p = Path.GetFullPath(Path.Combine(_cfg.DataDir, v));
                if (File.Exists(p)) File.Delete(p);
            }
            // Заготовки отчётов этих суток — пересчитать без удалённой записи.
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT host, started_at, ended_at FROM segments WHERE id = $id";
                q.Parameters.AddWithValue("$id", id);
                using var r = q.ExecuteReader();
                if (r.Read())
                {
                    var (h, s, e) = (r.GetString(0), Db.ParseUtc(r.GetString(1)), Db.ParseUtc(r.GetString(2)));
                    r.Close();
                    Db.MarkReportDirty(c, null, h, s, e);
                }
            }
            using var del = c.CreateCommand();
            del.CommandText = "DELETE FROM segments WHERE id = $id";
            del.Parameters.AddWithValue("$id", id);
            del.ExecuteNonQuery();
            _db.InvalidateHostTotals();
            TryRemoveEmptyDirs(Path.GetDirectoryName(full));
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("delete failed {File}: {Err}", relPath, ex.Message);
            return false;
        }
    }

    private void TryRemoveEmptyDirs(string? dir)
    {
        try
        {
            while (!string.IsNullOrEmpty(dir)
                && Directory.Exists(dir)
                && Path.GetFullPath(dir).StartsWith(Path.GetFullPath(Path.Combine(_cfg.DataDir, "blobs"))))
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any()) break;
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("failed to remove empty dir {Dir}: {Err}", dir, ex.Message);
        }
    }
}
