using Serilog;

namespace Watcher.TerminalAgent;

// Жёсткий размерный порог на НЕотправленное (queue/, raw-output/) — тот же
// приём, что Watcher.Uploader/QueueQuota.cs (Windows/видео-агент), не
// переиспользован как общий код между проектами (см. комментарии по всему
// репозиторию про "не шарим логику между агентами"), только сам приём:
// quiet-период перед тем, как файл можно трогать (не дёргать то, что
// коллектор/скрипт ещё дописывает), удаление от самого старого.
//
// Два метода, не один обобщённый — terminal-input (queue/) это один файл
// на запись (.json, без сайдкаров), terminal-output (raw-output/) — тройка
// .json+.out+.timing, которую нужно удалять ЦЕЛИКОМ: оставить .json без
// .out означало бы, что UploadRunner будет бесконечно пытаться и падать на
// "missing .out/.timing sibling" вместо того, чтобы просто не видеть эту
// запись вообще.
public static class TerminalQueueQuota
{
    public static int ApplyInput(string queueDir, long maxBytes, int quietSeconds, ILogger logger)
    {
        if (maxBytes <= 0 || !Directory.Exists(queueDir)) return 0;

        var all = new DirectoryInfo(queueDir).GetFiles("*.json", SearchOption.TopDirectoryOnly);
        long total = all.Sum(f => f.Length);
        if (total <= maxBytes) return 0;

        var candidates = all
            .Where(f => (DateTime.UtcNow - f.LastWriteTimeUtc).TotalSeconds >= quietSeconds)
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();

        int dropped = 0;
        foreach (var f in candidates)
        {
            if (total <= maxBytes) break;
            total -= f.Length;
            TryDelete(f, logger);
            dropped++;
        }
        if (dropped > 0)
            logger.Warning("queue quota (input): dropped {Count} unsent record(s) to fit {Max:N0} B cap", dropped, maxBytes);
        return dropped;
    }

    public static int ApplyOutput(string rawOutputDir, long maxBytes, int quietSeconds, ILogger logger)
    {
        if (maxBytes <= 0 || !Directory.Exists(rawOutputDir)) return 0;

        var jsonFiles = new DirectoryInfo(rawOutputDir).GetFiles("*.json", SearchOption.TopDirectoryOnly);
        var groups = jsonFiles.Select(j => BuildGroup(rawOutputDir, j)).ToList();
        long total = groups.Sum(g => g.TotalSize);
        if (total <= maxBytes) return 0;

        var candidates = groups
            .Where(g => (DateTime.UtcNow - g.Json.LastWriteTimeUtc).TotalSeconds >= quietSeconds)
            .OrderBy(g => g.Json.LastWriteTimeUtc)
            .ToList();

        int dropped = 0;
        foreach (var g in candidates)
        {
            if (total <= maxBytes) break;
            total -= g.TotalSize;
            TryDelete(g.Json, logger);
            if (g.Out is not null) TryDelete(g.Out, logger);
            if (g.Timing is not null) TryDelete(g.Timing, logger);
            dropped++;
        }
        if (dropped > 0)
            logger.Warning("queue quota (output): dropped {Count} unsent record(s) to fit {Max:N0} B cap", dropped, maxBytes);
        return dropped;
    }

    // 0.13: срок хранения невыгруженного. Файлы одной записи (.json, .out, .timing,
    // .start, состояние живой выгрузки) — по общему имени; запись удаляется, только
    // если ВСЕ её файлы старше срока — идущая сессия дописывает .out и не тронется.
    public static int ApplyAge(string dir, int maxAgeDays, DateTime nowUtc, ILogger logger)
    {
        if (maxAgeDays <= 0 || !Directory.Exists(dir)) return 0;
        DateTime cutoff = nowUtc.AddDays(-maxAgeDays);
        int dropped = 0;
        var groups = new DirectoryInfo(dir).GetFiles("*", SearchOption.TopDirectoryOnly)
            .GroupBy(f => f.Name.Split('.')[0], StringComparer.Ordinal);
        foreach (var g in groups)
        {
            if (g.Any(f => f.LastWriteTimeUtc >= cutoff)) continue;
            foreach (var f in g) TryDelete(f, logger);
            dropped++;
        }
        if (dropped > 0)
            logger.Warning("queue max age {Dir}: dropped {Count} unsent record(s) older than {Days} d", dir, dropped, maxAgeDays);
        return dropped;
    }

    private sealed record Group(FileInfo Json, FileInfo? Out, FileInfo? Timing)
    {
        public long TotalSize => Json.Length + (Out?.Length ?? 0) + (Timing?.Length ?? 0);
    }

    private static Group BuildGroup(string dir, FileInfo json)
    {
        string baseName = Path.GetFileNameWithoutExtension(json.Name);
        var outFile = new FileInfo(Path.Combine(dir, baseName + ".out"));
        var timingFile = new FileInfo(Path.Combine(dir, baseName + ".timing"));
        return new Group(json, outFile.Exists ? outFile : null, timingFile.Exists ? timingFile : null);
    }

    private static void TryDelete(FileInfo f, ILogger logger)
    {
        try { f.Delete(); }
        catch (Exception ex) { logger.Warning(ex, "queue quota: delete failed {File}", f.Name); }
    }
}
