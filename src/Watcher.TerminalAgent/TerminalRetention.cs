using Serilog;

namespace Watcher.TerminalAgent;

// Чистит sent/ (уже выгруженное на сервер, см. UploadRunner.cs) по возрасту,
// потом по размеру — та же логика и тот же порядок, что
// Watcher.Uploader/Retention.cs. Не различает .json/.out/.timing — "все
// файлы в директории", как и у Watcher.Uploader (там тоже не группирует
// .mp4+.json явно, просто сортирует по mtime, а они почти всегда совпадают
// у пары, записанной/перенесённой одним действием) — для уже успешно
// отправленного архива это достаточно, не нужна точная групповая атомарность.
public static class TerminalRetention
{
    public static void Apply(string dir, int retainDays, long maxBytes, ILogger logger)
    {
        if (!Directory.Exists(dir)) return;

        var files = new DirectoryInfo(dir)
            .GetFiles("*", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();

        if (retainDays > 0)
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-retainDays);
            int deletedAge = 0;
            foreach (var f in files.ToArray())
            {
                if (f.LastWriteTimeUtc < cutoff)
                {
                    TryDelete(f, logger);
                    files.Remove(f);
                    deletedAge++;
                }
            }
            if (deletedAge > 0) logger.Information("sent retention {Dir}: deleted {Count} files older than {Days}d", dir, deletedAge, retainDays);
        }

        if (maxBytes > 0)
        {
            long total = files.Sum(f => f.Length);
            int deletedSize = 0;
            while (total > maxBytes && files.Count > 0)
            {
                var oldest = files[0];
                total -= oldest.Length;
                TryDelete(oldest, logger);
                files.RemoveAt(0);
                deletedSize++;
            }
            if (deletedSize > 0) logger.Information("sent retention {Dir}: deleted {Count} files to fit {Max:N0} B cap", dir, deletedSize, maxBytes);
        }
    }

    private static void TryDelete(FileInfo f, ILogger logger)
    {
        try { f.Delete(); }
        catch (Exception ex) { logger.Warning(ex, "sent retention: delete failed {File}", f.Name); }
    }
}
