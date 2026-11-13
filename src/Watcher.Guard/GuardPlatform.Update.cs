using System.Diagnostics;
using System.Runtime.Versioning;
using Watcher.Uploader;

namespace Watcher.Guard;

public sealed partial class GuardPlatform
{
    [SupportedOSPlatform("windows")]
    private static void WaitService(System.ServiceProcess.ServiceController service, System.ServiceProcess.ServiceControllerStatus status)
    {
        try { service.WaitForStatus(status, TimeSpan.FromSeconds(30)); }
        catch (System.ServiceProcess.TimeoutException ex) { throw new IOException("Service state timeout", ex); }
    }
    public Dictionary<string, string> CaptureStartup(GuardPolicy policy)
    {
        var modes = new Dictionary<string, string>();
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WatcherSessionService") ?? throw new IOException("Service registration missing");
            string expected = Path.Combine(policy.InstallDir, "Watcher.SessionService.exe");
            if (!string.Equals((key.GetValue("ImagePath") as string)?.Trim('"'), expected, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(key.GetValue("ObjectName") as string, "LocalSystem", StringComparison.OrdinalIgnoreCase)) throw new IOException("Service registration mismatch");
            string mode = key.GetValue("Start") switch
            {
                2 => key.GetValue("DelayedAutoStart") is 1 ? "delayed-auto" : "automatic",
                3 => "manual",
                4 => "disabled",
                _ => throw new IOException("Unsupported startup mode")
            };
            foreach (string kind in policy.Credentials.Keys) modes[kind] = mode;
        }
        else
        {
            foreach (string kind in policy.Credentials.Keys)
            {
                var info = new ProcessStartInfo("/usr/bin/systemctl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                info.ArgumentList.Add("is-enabled"); info.ArgumentList.Add(Unit(kind));
                using var process = Process.Start(info) ?? throw new IOException("Cannot inspect startup mode");
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(10000)) { process.Kill(entireProcessTree: true); throw new IOException("Startup inspection timeout"); }
                string mode = output.GetAwaiter().GetResult().Trim(); _ = error.GetAwaiter().GetResult();
                if (mode is not ("enabled" or "enabled-runtime" or "disabled")) throw new IOException("Unsupported startup mode");
                modes[kind] = mode;
            }
        }
        return modes;
    }
    public void RestoreStartup(GuardPolicy policy, Dictionary<string, string> modes)
    {
        if (modes.Count != policy.Credentials.Count || modes.Keys.Any(k => !policy.Credentials.ContainsKey(k))) throw new IOException("Startup snapshot mismatch");
        var current = CaptureStartup(policy);
        if (modes.All(p => current.TryGetValue(p.Key, out string? mode) && mode == p.Value)) return;
        if (OperatingSystem.IsWindows())
        {
            string mode = modes.Values.First();
            if (modes.Values.Any(v => v != mode)) throw new IOException("Startup snapshot mismatch");
            string value = mode switch { "automatic" => "auto", "delayed-auto" => "delayed-auto", "manual" => "demand", "disabled" => "disabled", _ => throw new IOException("Unsupported startup mode") };
            if (Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["config", "WatcherSessionService", "start=", value]) != 0) throw new IOException("Startup restoration failed");
        }
        else
        {
            foreach (var item in modes)
            {
                string unit = Unit(item.Key);
                if (item.Value is not ("enabled" or "enabled-runtime" or "disabled")) throw new IOException("Unsupported startup mode");
                if (Run("/usr/bin/systemctl", ["disable", "--runtime", unit]) != 0 || Run("/usr/bin/systemctl", ["disable", unit]) != 0) throw new IOException("Startup restoration failed");
                if (item.Value == "enabled" && Run("/usr/bin/systemctl", ["enable", unit]) != 0
                    || item.Value == "enabled-runtime" && Run("/usr/bin/systemctl", ["enable", "--runtime", unit]) != 0) throw new IOException("Startup restoration failed");
            }
        }
        var actual = CaptureStartup(policy);
        if (modes.Any(p => !actual.TryGetValue(p.Key, out string? mode) || mode != p.Value)) throw new IOException("Startup restoration mismatch");
    }
    public void VerifyRuntime(GuardPolicy policy, HashSet<string> expectedRunning)
    {
        if (expectedRunning.Count > 0) Thread.Sleep(TimeSpan.FromSeconds(20));
        _ = CaptureStartup(policy); // Повторно проверяет путь/account Windows-службы.
        string? expected = policy.LastVerifiedPackage?.Version ?? policy.InitialPackage?.Version;
        if (expected is not null && InstalledVersion(policy) != expected) throw new IOException("Installed agent version mismatch");
        foreach (string kind in policy.Credentials.Keys)
            if (Running(kind) != expectedRunning.Contains(kind)) throw new IOException("Service did not remain in expected state");
    }
    // X.Y.Z установленного агента: Windows — версия файла службы, Linux — версия пакета.
    public string? InstalledVersion(GuardPolicy policy)
    {
        if (OperatingSystem.IsWindows())
        {
            string exe = Path.Combine(policy.InstallDir, "Watcher.SessionService.exe");
            if (!File.Exists(exe)) return null;
            var version = FileVersionInfo.GetVersionInfo(exe);
            return $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}";
        }
        bool deb = File.Exists("/usr/bin/dpkg");
        string actual = deb ? ReadPackageVersion("/usr/bin/dpkg-query", ["--show", "--showformat=${Version}", "watcher-agent"])
            : ReadPackageVersion("/usr/bin/rpm", ["--query", "--queryformat", "%{VERSION}", "watcher-agent"]);
        // deb: 0.16.0-1 → 0.16.0.
        return deb && actual.LastIndexOf('-') is int dash and > 0 ? actual[..dash] : actual;
    }
    private static string ReadPackageVersion(string executable, string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Package version query failed");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { process.Kill(entireProcessTree: true); throw new IOException("Package version query timeout"); }
        string value = output.GetAwaiter().GetResult().Trim(); _ = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || value.Length > 128) throw new IOException("Package version query failed");
        return value;
    }
    public void Rollback(string package, string packagePlatform, GuardPolicy policy)
    {
        foreach (string kind in policy.Credentials.Keys) if (Running(kind)) Stop(kind, policy);
        if (OperatingSystem.IsWindows())
        {
            if (packagePlatform != "windows-msi") throw new IOException("Unsupported rollback platform");
            RollbackWindows(package, policy);
        }
        else
        {
            int exit = packagePlatform switch
            {
                "linux-deb" => Run("/usr/bin/dpkg", ["--force-confold", "--install", package], 300),
                "linux-rpm" => Run("/usr/bin/rpm", ["--upgrade", "--oldpackage", "--replacepkgs", package], 300),
                _ => throw new IOException("Unsupported rollback platform")
            };
            if (exit != 0) throw new GuardUpdateFailure("rollback-install-failed", exit);
        }
    }
    [SupportedOSPlatform("windows")]
    private static void RollbackWindows(string package, GuardPolicy policy)
    {
        string? product = InstalledProductCode(strict: true);
        if (product is not null)
        {
            int removal = Run(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), ["/x", product, "/qn", "/norestart"], 300);
            if (removal != 0) throw new GuardUpdateFailure(removal == 1618 ? "installer-busy" : removal == 3010 ? "reboot-required" : "rollback-install-failed", removal);
        }
        // Старый MSI нельзя поставить поверх MajorUpgrade с запретом downgrade.
        // Снятие нового продукта не удаляет закрытые identities/снимки Guard.
        if (InstalledProductCode(strict: true) is not null) throw new IOException("MSI product remains during rollback");
        GuardPaths.Write(Path.Combine(GuardPaths.Root, "internal-repair.json"), new GuardRetry(0, DateTime.UtcNow.AddMinutes(10)));
        string log = Path.Combine(GuardPaths.Root, "logs", "rollback-msi.log"); Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        int exit = Run(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = MsiCommandLine.Install(package, false, log, policy.InstallDir, policy.ServerUrl, policy.UpdatePublicKey, policy.MaintenancePublicKey)
        }, 300);
        if (exit != 0) throw new GuardUpdateFailure("rollback-install-failed", exit);
    }
}
