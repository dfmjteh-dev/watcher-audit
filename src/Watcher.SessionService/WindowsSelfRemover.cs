using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Watcher.SessionService;

// 0.13 (п.14): хост удалён в веб-UI — удалить агента со станции. Установка MSI
// (продукт ищется по UpgradeCode, installer/watcher-agent.wxs) — msiexec /x;
// установки не из MSI (до 0.16, install.ps1) — остановить и удалить службу. Как и обновление
// (UpdateTask), одноразовой задачей Планировщика от SYSTEM: процесс-
// ребёнок службы умер бы вместе с ней, а MSI её останавливает. Данные в
// ProgramData\Watcher (очередь, учётки) — тоже: хост на сервере удалён.
[SupportedOSPlatform("windows")]
public static class WindowsSelfRemover
{
    public const string TaskName = "WatcherAgentUninstall";
    public const string UpgradeCode = "{B57CFE32-A5D4-4ACC-8E2E-796E4F6B05C7}";

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumRelatedProducts(string lpUpgradeCode, uint dwReserved, uint iProductIndex, StringBuilder lpProductBuf);

    public static string? InstalledProductCode()
    {
        var buf = new StringBuilder(39);
        try { return MsiEnumRelatedProducts(UpgradeCode, 0, 0, buf) == 0 ? buf.ToString() : null; }
        catch (DllNotFoundException) { return null; }
    }

    // Текст .cmd — отдельно от запуска, чтобы проверять тестом.
    public static string Script(string? productCode, string installDir, string programData, string windowsTemp) =>
        string.Join("\r\n",
            "@echo off",
            "timeout /t 5 /nobreak >nul",
            productCode is not null
                ? $"msiexec /x {productCode} /qn /norestart /l*v \"{Path.Combine(windowsTemp, "watcher-uninstall-msi.log")}\""
                : "sc stop WatcherSessionService & sc delete WatcherSessionService",
            // То, чего нет в таблице файлов MSI (update.ps1 и .rollback от прежних обновлений
            // по zip), msiexec /x оставляет — каталог агента целиком (проверка RC 0.13).
            $"if exist \"{installDir}\" rmdir /s /q \"{installDir}\"",
            $"rmdir /s /q \"{Path.Combine(programData, "Watcher")}\"",
            $"schtasks /Delete /TN {TaskName} /F",
            "");

    public static void Remove(ILogger logger)
    {
        string temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        string cmd = Path.Combine(temp, "watcher-uninstall.cmd");
        string? product = InstalledProductCode();
        File.WriteAllText(cmd, Script(product, AppContext.BaseDirectory.TrimEnd('\\'),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), temp), Encoding.ASCII);
        if (Schtasks(["/Create", "/F", "/TN", TaskName, "/RU", "SYSTEM", "/RL", "HIGHEST", "/SC", "ONCE", "/ST", "00:00", "/TR", $"cmd.exe /c \"{cmd}\""], out string o1) != 0)
            throw new InvalidOperationException("schtasks /Create failed: " + o1);
        if (Schtasks(["/Run", "/TN", TaskName], out string o2) != 0)
            throw new InvalidOperationException("schtasks /Run failed: " + o2);
        logger.LogWarning("self-uninstall started ({How})", product is not null ? "msiexec /x " + product : "service removal");
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
