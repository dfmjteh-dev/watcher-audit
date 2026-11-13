using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;
using Watcher.Uploader;

namespace Watcher.SessionService;

public sealed record MsiUpdateJob(string Msi, string Version, string ResultFile, string Log, string InstallDir,
    string ServerUrl, string UpdateKey, bool TerminalAudit);

// 0.16: установка обновления MSI одноразовой SYSTEM-задачей (бывший update-msi.ps1).
// Запускается копией Watcher.SessionService.exe из каталога обновления.
[SupportedOSPlatform("windows")]
public static class MsiUpdateHelper
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string ServiceName = "WatcherSessionService";

    public static int Run(string jobFile)
    {
        var job = JsonSerializer.Deserialize<MsiUpdateJob>(File.ReadAllText(jobFile), Json) ?? throw new IOException("Invalid update job");
        try { Apply(job); }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.ServiceProcess.TimeoutException)
        { Write(job, false, e.Message); }
        finally
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe")) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "/Delete", "/TN", UpdateTask.TaskName, "/F" }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            process?.WaitForExit(30000);
        }
        return 0;
    }

    private static void Apply(MsiUpdateJob job)
    {
        int start = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName)?.GetValue("Start") as int? ?? throw new IOException("Service registration missing");
        bool wasRunning = Status() == ServiceControllerStatus.Running;
        bool startAfter = start == 2 || (start == 3 && wasRunning);
        // Служба, Capture в сессиях и их ffmpeg держат exe — msiexec иначе отложил бы
        // замену до перезагрузки (3010).
        StopService();
        string prefix = job.InstallDir.TrimEnd('\\') + "\\";
        foreach (var process in Process.GetProcesses())
            using (process)
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (path is not null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) process.Kill(entireProcessTree: true);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        Thread.Sleep(2000);

        var msiexec = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), CommandLine(job, IsDowngrade(job.Version, Path.Combine(job.InstallDir, "Watcher.SessionService.exe")))) { UseShellExecute = false, CreateNoWindow = true };
        int code;
        using (var process = Process.Start(msiexec) ?? throw new IOException("msiexec did not start"))
        {
            process.WaitForExit();
            code = process.ExitCode;
        }
        if (code is not (0 or 3010))
        {
            if (wasRunning && start != 4) TryStart();
            Write(job, false, $"msiexec exit code {code}; previous installation recovery not verified; log {job.Log}");
            return;
        }
        if (startAfter) Thread.Sleep(20000);
        if (startAfter && Status() != ServiceControllerStatus.Running) { TryStart(); Thread.Sleep(10000); }
        var expected = startAfter ? ServiceControllerStatus.Running : ServiceControllerStatus.Stopped;
        var version = FileVersionInfo.GetVersionInfo(Path.Combine(job.InstallDir, "Watcher.SessionService.exe"));
        string actual = $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}";
        if (actual != job.Version) { Write(job, false, $"MSI returned {code}, but installed version is {actual}, expected {job.Version}; log {job.Log}"); return; }
        if (code == 3010) { Write(job, false, $"installed version {actual}; reboot required before update verification; log {job.Log}"); return; }
        if (Status() == expected) Write(job, true, "installed (msi)");
        else Write(job, false, $"installed (msi), but service is not in expected state {expected}; log {job.Log}");
    }

    // msiexec разбирает СВОЙСТВО="значение" сам, не по правилам CRT — строка целиком.
    // downgrade — цель группы старше установленной версии: REINSTALLMODE=amus, иначе
    // Windows Installer не ставит «младшие» файлы (2753 на GuardRepair.exe), см. MsiCommandLine.
    public static string CommandLine(MsiUpdateJob job, bool downgrade = false) =>
        $"/i {Quote(job.Msi)} /qn /norestart /l*v {Quote(job.Log)}{(downgrade ? " REINSTALLMODE=amus" : "")} SERVERURL={Quote(job.ServerUrl)} UPDATEKEY={Quote(job.UpdateKey)} TERMINALAUDIT={(job.TerminalAudit ? "1" : "0")}";

    public static bool IsDowngrade(string target, string installedExe)
    {
        if (!File.Exists(installedExe) || !Version.TryParse(target, out var wanted)) return false;
        var info = FileVersionInfo.GetVersionInfo(installedExe);
        return wanted < new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
    }

    // Значение свойства msiexec в кавычках; кавычки внутри значения не допускаются.
    public static string Quote(string value) =>
        value.Contains('"') ? throw new ArgumentException("Quote in msiexec argument") : "\"" + value + "\"";

    private static ServiceControllerStatus? Status()
    {
        try { using var service = new ServiceController(ServiceName); return service.Status; }
        catch (InvalidOperationException) { return null; }
    }

    private static void StopService()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            if (service.Status != ServiceControllerStatus.Stopped) { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ServiceProcess.TimeoutException) { }
    }

    private static void TryStart()
    {
        try { using var service = new ServiceController(ServiceName); if (service.Status == ServiceControllerStatus.Stopped) service.Start(); }
        catch (InvalidOperationException) { }
    }

    private static void Write(MsiUpdateJob job, bool ok, string message)
    {
        string temporary = job.ResultFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new UpdateResult(job.Version, ok, message, DateTime.UtcNow), Json));
        File.Move(temporary, job.ResultFile, overwrite: true);
    }
}
