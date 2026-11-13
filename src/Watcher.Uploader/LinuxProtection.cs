using System.Diagnostics;

namespace Watcher.Uploader;

// 0.16: включение защиты Linux-агентом (ProtectionActivation). GuardRepair setup
// останавливает и перезапускает watcher-uploader — поэтому отдельным юнитом через
// systemd-run, как установка обновления (LinuxPackageInstaller).
public static class LinuxProtection
{
    public const string GuardRepair = "/opt/watcher-guard/GuardRepair";
    public const string GuardRoot = "/var/lib/watcher/guard";
    public const string UnitName = "watcher-agent-protect";

    public static void Start(string maintenanceKey, string updateKey, string serverUrl)
    {
        if (!OperatingSystem.IsLinux()) return;
        if (new DirectoryInfo(GuardRoot).LinkTarget is not null) throw new IOException("Guard directory is a symlink");
        // Распаковка single-file — в закрытый каталог Guard, не в общий /tmp.
        Directory.CreateDirectory(GuardRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (Run("systemctl", ["is-active", "--quiet", UnitName + ".service"]) == 0) return;
        int exit = Run("systemd-run", ["--unit=" + UnitName, "--collect", "--quiet",
            "--setenv=DOTNET_BUNDLE_EXTRACT_BASE_DIR=" + GuardRoot + "/bundle", "--", GuardRepair, "setup", maintenanceKey, updateKey, serverUrl]);
        if (exit != 0) throw new InvalidOperationException("systemd-run failed with exit code " + exit);
    }

    private static int Run(string file, string[] args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info) ?? throw new InvalidOperationException(file + " did not start");
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return p.ExitCode;
    }
}
