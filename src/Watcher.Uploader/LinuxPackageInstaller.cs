using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Watcher.Uploader;

// H2 (roadmap-next.md): установка обновления на Linux — только для агента,
// поставленного пакетом (.deb/.rpm): у tar.gz-установки нет менеджера пакетов,
// через который откатиться. Ставит /usr/share/watcher-agent/apply-update.sh из
// УЖЕ установленного пакета, запущенный через systemd-run отдельным юнитом: при
// установке пакет перезапускает watcher-uploader (то есть этот процесс), и
// установка, запущенная его ребёнком, оборвалась бы на середине.
public sealed class LinuxPackageInstaller : IUpdateInstaller
{
    public const string ApplyScript = "/usr/share/watcher-agent/apply-update.sh";
    public const string UnitName = "watcher-agent-update";

    private readonly string _kind;
    private readonly ILogger _logger;

    private LinuxPackageInstaller(string kind, ILogger logger)
    {
        _kind = kind;
        _logger = logger;
    }

    public string Platform => "linux-" + _kind;

    // null — эта установка автоматически не обновляется (не Linux, не root,
    // не пакет).
    public static LinuxPackageInstaller? Detect(ILogger logger)
    {
        if (!OperatingSystem.IsLinux() || !Environment.IsPrivilegedProcess || !File.Exists(ApplyScript)) return null;
        if (Run("dpkg-query", ["-W", "-f=${Status}", "watcher-agent"], out string st) == 0 && st.Contains("install ok installed", StringComparison.Ordinal))
            return new LinuxPackageInstaller("deb", logger);
        if (Run("rpm", ["-q", "watcher-agent"], out _) == 0)
            return new LinuxPackageInstaller("rpm", logger);
        return null;
    }

    public bool InProgress() => Run("systemctl", ["is-active", "--quiet", UnitName + ".service"], out _) == 0;

    public void Start(string packagePath, string version, string workDir, string resultFile)
    {
        if (Run("systemd-run", ["--unit=" + UnitName, "--collect", "--quiet", "--", ApplyScript, _kind, packagePath, version, resultFile], out string output) != 0)
            throw new InvalidOperationException("systemd-run failed: " + output);
        _logger.LogInformation("update {Version}: {Unit} started", version, UnitName);
    }

    private static int Run(string file, string[] args, out string output)
    {
        var psi = new ProcessStartInfo { FileName = file, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit(30_000);
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            output = e.Message;   // нет такой программы (dpkg-query на RHEL и наоборот)
            return -1;
        }
    }
}
