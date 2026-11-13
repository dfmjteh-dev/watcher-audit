using System.Text.Json;

namespace Watcher.Capture;

// 0.13 (п.5): процесс записи экрана после каждой проверки конфига
// (ConfigCheckIntervalSeconds) оставляет в очереди отметку: кто, какой конфиг
// применил (configHash из кэша, его кладёт туда Uploader), пишет ли сейчас.
// Uploader собирает свежие отметки в heartbeat — так в веб-UI видно, дошёл ли
// конфиг до самой записи, а не только до станции, и кто на ней пишется.
// Очередь — потому что туда процесс записи и так пишет (в каталог конфига — нет,
// на Linux он root). Расширение не .mp4/.json — выгрузка и квоты их не трогают.
// Формат читает Watcher.Uploader.Core/CaptureStatus.cs.
public static class CaptureStatusFile
{
    public const string Prefix = "capture-status_", Suffix = ".status";

    public static void Write(string queueDir, string user, string? configHash, bool recording)
    {
        try
        {
            string safe = string.Concat(user.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_'));
            string path = Path.Combine(queueDir, Prefix + safe + Suffix);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new { user, configHash, recording, at = DateTime.UtcNow.ToString("o") }));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
