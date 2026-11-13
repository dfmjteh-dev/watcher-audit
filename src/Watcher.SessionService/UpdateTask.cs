using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Watcher.SessionService;

// Одноразовая задача Планировщика от SYSTEM для установки обновления агента
// (WindowsMsiInstaller): она переживает остановку этой службы — процесс-ребёнок
// службы умер бы вместе с ней. До 0.16 тем же путём ставился и zip-пакет
// (WindowsZipInstaller + update.ps1); Windows-агент теперь обновляется только MSI.
[SupportedOSPlatform("windows")]
public static class UpdateTask
{
    public const string TaskName = "WatcherAgentUpdate";

    public static bool TaskExists() => Schtasks(["/Query", "/TN", TaskName], out _) == 0;

    // 0.15.0: задача обновления, которая так и не убрала себя (скрипт не стартовал,
    // убит AV, ребут посреди установки, msiexec завис), раньше навсегда означала
    // «обновление уже идёт» — агент молча больше не обновлялся и не слал статус
    // (на проде так застрял LT-AVZ-HURES007 на 0.13.0). Обновление занимает минуты:
    // задача старше StaleAfter — мусор, снимаем её. Без отметки старта (задачу
    // ставила версия до 0.15.0) возраст считаем от запуска этой службы: установка
    // её останавливает, так что служба, живущая дольше StaleAfter, — значит, зависла.
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);
    public static string StartedMarker => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Watcher", "update-task.started");

    public static bool TaskRunning(ILogger logger)
    {
        if (!TaskExists()) return false;
        if (!IsStale(MarkerTime() ?? ServiceStartedUtc, DateTime.UtcNow)) return true;
        logger.LogWarning("update task {Task} is stale (started {Started}) — removing it", TaskName, MarkerTime()?.ToString("o") ?? "unknown");
        Schtasks(["/End", "/TN", TaskName], out _);
        Schtasks(["/Delete", "/TN", TaskName, "/F"], out _);
        try { File.Delete(StartedMarker); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return TaskExists();
    }

    private static DateTime ServiceStartedUtc => Process.GetCurrentProcess().StartTime.ToUniversalTime();

    // Часы перевели назад на больше StaleAfter — тоже не верим отметке.
    public static bool IsStale(DateTime startedUtc, DateTime nowUtc) =>
        nowUtc - startedUtc > StaleAfter || startedUtc - nowUtc > StaleAfter;

    private static DateTime? MarkerTime()
    {
        try
        {
            return File.Exists(StartedMarker) && DateTime.TryParse(File.ReadAllText(StartedMarker).Trim(), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at.ToUniversalTime() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // Одноразовая задача от SYSTEM — общая и для обновления MSI (WindowsMsiInstaller).
    public static void RunTask(string command)
    {
        if (command.Length > 261) throw new InvalidOperationException("update command line too long for Task Scheduler — shorten UpdateDir");
        if (Schtasks(["/Create", "/F", "/TN", TaskName, "/RU", "SYSTEM", "/RL", "HIGHEST", "/SC", "ONCE", "/ST", "00:00", "/TR", command], out string createOut) != 0)
            throw new InvalidOperationException("schtasks /Create failed: " + createOut);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartedMarker)!);
            File.WriteAllText(StartedMarker, DateTime.UtcNow.ToString("o"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* без отметки возраст — от старта службы */ }
        if (Schtasks(["/Run", "/TN", TaskName], out string runOut) != 0)
        {
            Schtasks(["/Delete", "/TN", TaskName, "/F"], out _);
            throw new InvalidOperationException("schtasks /Run failed: " + runOut);
        }
    }

    private static int Schtasks(string[] args, out string output)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
        p.WaitForExit(30_000);
        return p.ExitCode;
    }
}
