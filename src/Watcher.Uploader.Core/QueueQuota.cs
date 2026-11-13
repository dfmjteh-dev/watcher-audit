namespace Watcher.Uploader;

// Жёсткий порог на queue/ (неотправленные сегменты, см. roadmap Фаза 5) — отдельная
// функция от Retention.cs (sent/), не переиспользует её retainDays: там retainDays=0
// по факту означает "удалить почти всё" (известный баг, см. development.md), здесь
// такого параметра просто нет — только размер, без возрастного компонента.
public static class QueueQuota
{
    // quietSeconds — то же правило, что Uploader использует перед загрузкой (FileQuietSeconds):
    // не трогать файл, который Capture мог ещё дозаписывать. Общий размер считается по
    // ВСЕМ *.mp4 в queue (включая "не тихие"), иначе активная запись могла бы расти без
    // ограничения, пока числится недостаточно "тихой" для удаления — удалять же можно
    // только из "тихих" кандидатов.
    public static int Apply(string queueDir, long maxBytes, int quietSeconds, Action<string> log, Action<string, long>? onLoss = null)
    {
        if (maxBytes <= 0 || !Directory.Exists(queueDir)) return 0;

        var all = new DirectoryInfo(queueDir).GetFiles("*.mp4", SearchOption.TopDirectoryOnly);
        long total = all.Sum(f => f.Length);
        if (total <= maxBytes) return 0;

        var candidates = all
            .Where(f => (DateTime.UtcNow - f.LastWriteTimeUtc).TotalSeconds >= quietSeconds)
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();

        int dropped = 0;
        foreach (var oldest in candidates)
        {
            if (total <= maxBytes) break;
            total -= oldest.Length;
            onLoss?.Invoke(oldest.Name, oldest.Length);
            TryDelete(oldest, log);
            string json = oldest.FullName + ".json";
            if (File.Exists(json))
            {
                try { File.Delete(json); }
                catch (Exception ex) { log($"queue quota: delete meta failed {Path.GetFileName(json)}: {ex.Message}"); }
            }
            dropped++;
        }
        if (dropped > 0)
            log($"queue quota: dropped {dropped} unsent segment(s) to fit {maxBytes:N0} B cap");
        return dropped;
    }

    private static void TryDelete(FileInfo f, Action<string> log)
    {
        try { f.Delete(); }
        catch (Exception ex) { log($"queue quota: delete failed {f.Name}: {ex.Message}"); }
    }
}
