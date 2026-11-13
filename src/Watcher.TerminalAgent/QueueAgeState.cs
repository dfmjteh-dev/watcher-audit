using System.Globalization;
using Serilog;

namespace Watcher.TerminalAgent;

// Срок хранения невыгруженного, присланный сервером (X-Watcher-Queue-Max-Age), —
// одно число в файле. Сервер недоступен месяц — агент после перезапуска помнит
// срок, заданный в веб-UI, а не возвращается к appsettings.
public static class QueueAgeState
{
    public static int? Load(string path)
    {
        try
        {
            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int d)
                && d >= Watcher.Uploader.QueueAge.MinDays && d <= Watcher.Uploader.QueueAge.MaxDays ? d : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void Save(string path, int days, ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, days.ToString(CultureInfo.InvariantCulture));
            logger.Information("queue max age from server: {Days} d", days);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "queue max age: save failed");
        }
    }
}
