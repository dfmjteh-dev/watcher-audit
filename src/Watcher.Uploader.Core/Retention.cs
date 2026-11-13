namespace Watcher.Uploader;

public static class Retention
{
    public static void Apply(string dir, int retainDays, long maxBytes, Action<string> log)
    {
        if (!Directory.Exists(dir)) return;

        var files = new DirectoryInfo(dir)
            .GetFiles("*", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();

        DateTime cutoff = DateTime.UtcNow.AddDays(-retainDays);
        int deletedAge = 0;
        foreach (var f in files.ToArray())
        {
            if (f.LastWriteTimeUtc < cutoff)
            {
                TryDelete(f, log);
                files.Remove(f);
                deletedAge++;
            }
        }
        if (deletedAge > 0) log($"retention: deleted {deletedAge} files older than {retainDays}d");

        long total = files.Sum(f => f.Length);
        int deletedSize = 0;
        while (total > maxBytes && files.Count > 0)
        {
            var oldest = files[0];
            total -= oldest.Length;
            TryDelete(oldest, log);
            files.RemoveAt(0);
            deletedSize++;
        }
        if (deletedSize > 0) log($"retention: deleted {deletedSize} files to fit {maxBytes:N0} B cap");
    }

    private static void TryDelete(FileInfo f, Action<string> log)
    {
        try { f.Delete(); }
        catch (Exception ex) { log($"retention: delete failed {f.Name}: {ex.Message}"); }
    }
}
