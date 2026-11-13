namespace Watcher.Server;

// R9 (docs/code-review-2026-10.md): блоб пишется во временный файл рядом с
// итоговым, строка в БД вставляется, и только потом файл переезжает на место.
// Раньше файл создавался сразу по итоговому пути, и упавший Insert (UNIQUE при
// гонке двух одинаковых загрузок, диск, исключение) оставлял файл без строки в
// БД — retention его не видит, он лежит вечно. Две одновременные загрузки к
// тому же писали в один и тот же файл.
//
// Порядок именно Insert → Move, а не наоборот: при гонке проигравший получает
// UNIQUE и удаляет СВОЙ .part, не трогая файл победителя.
public static class BlobFiles
{
    public const string PartSuffix = ".part";

    // Уникальное имя на каждый запрос — две параллельные загрузки одного и того
    // же файла не пишут в один .part.
    public static string PartPath(string finalPath) => $"{finalPath}.{Guid.NewGuid():N}{PartSuffix}";

    // overwrite: файл по итоговому пути без строки в БД мог остаться от сбоя до
    // R9; строка теперь наша (Insert прошёл), значит и путь наш.
    public static void Commit(string partPath, string finalPath) => File.Move(partPath, finalPath, overwrite: true);

    public static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // .part, которые никто не довёл до конца: сервер упал или был убит посреди
    // загрузки (обработчик ошибок за собой удаляет). Час — с запасом больше
    // любой загрузки в пределах MaxUploadSizeMb.
    public static int SweepStaleParts(string blobsRoot, TimeSpan maxAge, DateTime nowUtc, ILogger log)
    {
        if (!Directory.Exists(blobsRoot)) return 0;
        int deleted = 0;
        foreach (string f in Directory.EnumerateFiles(blobsRoot, "*" + PartSuffix, SearchOption.AllDirectories))
        {
            try
            {
                if (nowUtc - File.GetLastWriteTimeUtc(f) < maxAge) continue;
                File.Delete(f);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning("failed to delete stale {File}: {Err}", f, ex.Message);
            }
        }
        return deleted;
    }
}
