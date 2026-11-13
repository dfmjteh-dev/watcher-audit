using System.Diagnostics;
using System.ServiceProcess;

namespace Watcher.Guard;

public sealed partial class GuardPlatform : Watcher.Uploader.IGuardPlatform
{
    private static string Unit(string kind) => kind == "terminal" ? "watcher-terminal-agent.service" : "watcher-uploader.service";
    private static int Run(string file, IEnumerable<string> arguments, int seconds = 60)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true }; foreach (var arg in arguments) info.ArgumentList.Add(arg);
        return Run(info, seconds);
    }
    private static int Run(ProcessStartInfo info, int seconds)
    {
        using var process = Process.Start(info) ?? throw new IOException("Could not start system tool");
        if (!process.WaitForExit(seconds * 1000)) { process.Kill(entireProcessTree: true); throw new IOException("System tool timeout"); }
        return process.ExitCode;
    }
    public bool Running(string kind)
    {
        if (OperatingSystem.IsWindows())
        {
            try { using var service = new ServiceController("WatcherSessionService"); return service.Status == ServiceControllerStatus.Running; }
            catch (InvalidOperationException) { return false; }
        }
        return Run("/usr/bin/systemctl", ["is-active", "--quiet", Unit(kind)]) == 0;
    }
    public void Start(string kind, Watcher.Uploader.GuardPolicy policy)
    {
        if (OperatingSystem.IsWindows())
        {
            using var service = new ServiceController("WatcherSessionService");
            try { _ = service.Status; }
            catch (InvalidOperationException)
            {
                string exe = Path.Combine(policy.InstallDir, "Watcher.SessionService.exe");
                if (Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["create", "WatcherSessionService", "binPath=", "\"" + exe + "\"", "start=", policy.AutoStart ? "delayed-auto" : "demand", "obj=", "LocalSystem"]) != 0) throw new IOException("Service registration failed");
                service.Refresh();
            }
            ProtectWindowsService();
            if (service.Status == ServiceControllerStatus.StopPending) WaitService(service, ServiceControllerStatus.Stopped);
            service.Refresh();
            // SCM may already be restarting the service after a crash. Starting a
            // StartPending service turns successful recovery into a false failure.
            if (service.Status == ServiceControllerStatus.Stopped) service.Start();
            if (service.Status != ServiceControllerStatus.Running) WaitService(service, ServiceControllerStatus.Running);
        }
        else
        {
            if (Run("/usr/bin/systemctl", ["start", Unit(kind)]) != 0) throw new IOException("Service start failed");
        }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void ProtectWindowsService()
    {
        const string descriptor = "O:SYG:SYD:P(A;;GA;;;SY)(A;;CCLCSWLORC;;;BA)(A;;CCLCSWLORC;;;AU)";
        if (Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["sdset", "WatcherSessionService", descriptor]) != 0) throw new IOException("Service protection failed");
    }
    public void Stop(string kind, Watcher.Uploader.GuardPolicy policy)
    {
        if (OperatingSystem.IsLinux() && kind == "video") StopCaptureSessions();
        if (OperatingSystem.IsWindows())
        {
            using var service = new ServiceController("WatcherSessionService");
            if (service.Status != ServiceControllerStatus.Stopped) { service.Stop(); WaitService(service, ServiceControllerStatus.Stopped); }
        }
        else if (Run("/usr/bin/systemctl", ["stop", Unit(kind)]) != 0) throw new IOException("Service stop failed");
        // Только исполняемые файлы текущей установки, без произвольных PID из запроса.
        foreach (var process in Process.GetProcesses())
        {
            using (process)
                try
                {
                    if (!process.ProcessName.StartsWith("Watcher.Capture", StringComparison.Ordinal)) continue;
                    string? path = process.MainModule?.FileName;
                    if (path is not null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(policy.InstallDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) process.Kill(entireProcessTree: true);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
    public void EnsureRegistered(string kind, Watcher.Uploader.GuardPolicy policy)
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WatcherSessionService");
            string expected = Path.Combine(policy.InstallDir, "Watcher.SessionService.exe");
            if (key is null)
            {
                string exe = Path.Combine(policy.InstallDir, "Watcher.SessionService.exe");
                if (Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["create", "WatcherSessionService", "binPath=", "\"" + exe + "\"", "start=", policy.AutoStart ? "delayed-auto" : "demand", "obj=", "LocalSystem"]) != 0) throw new IOException("Service registration failed");
                ProtectWindowsService();
                GuardWindowsProtection.Protect(Watcher.Uploader.GuardPaths.Root, policy);
                return;
            }
            if (!string.Equals((key.GetValue("ImagePath") as string)?.Trim('"'), expected, StringComparison.OrdinalIgnoreCase))
                if (Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["config", "WatcherSessionService", "binPath=", "\"" + expected + "\""]) != 0) throw new IOException("Service path repair failed");
            if (!string.Equals(key.GetValue("ObjectName") as string, "LocalSystem", StringComparison.OrdinalIgnoreCase))
                if (Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["config", "WatcherSessionService", "obj=", "LocalSystem"]) != 0) throw new IOException("Service account repair failed");
            if (policy.AutoStart && key.GetValue("Start") is int start && start != 2)
                if (Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["config", "WatcherSessionService", "start=", "delayed-auto"]) != 0) throw new IOException("Service startup repair failed");
            ProtectWindowsService();
            GuardWindowsProtection.Protect(Watcher.Uploader.GuardPaths.Root, policy);
        }
        else if (policy.UnitHashes?.TryGetValue(Unit(kind), out string? expected) == true)
        {
            string unit = Unit(kind); string target = File.Exists("/etc/systemd/system/" + unit) ? "/etc/systemd/system/" + unit : "/usr/lib/systemd/system/" + unit;
            string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant(); }
            if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked service unit refused");
            if (!File.Exists(target) || Hash(target) != expected)
            {
                string backup = Path.Combine(Watcher.Uploader.GuardPaths.Root, "units", unit);
                if (!File.Exists(backup) || Hash(backup) != expected) throw new IOException("Verified unit backup missing");
                File.Copy(backup, "/etc/systemd/system/" + unit, true); if (Run("/usr/bin/systemctl", ["daemon-reload"]) != 0) throw new IOException("Unit reload failed");
            }
        }
    }
    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern uint MsiEnumRelatedProducts(string code, uint reserved, uint index, System.Text.StringBuilder product);
    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern int MsiQueryProductState(string product);
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static string? InstalledProductCode(bool strict = false)
    {
        for (uint index = 0; index < 64; index++)
        {
            var buffer = new System.Text.StringBuilder(39);
            uint status = MsiEnumRelatedProducts("{B57CFE32-A5D4-4ACC-8E2E-796E4F6B05C7}", 0, index, buffer);
            if (strict && status is not (0 or 259)) throw new IOException("MSI registration enumeration failed");
            if (status != 0) return null;
            // Enum возвращает также advertised/остаточные регистрации неустановленных MSI.
            if (MsiQueryProductState(buffer.ToString()) == 5) return buffer.ToString();
        }
        throw new IOException("Too many related MSI registrations");
    }
    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern uint MsiGetProductInfo(string product, string property, System.Text.StringBuilder value, ref uint length);
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string PrepareMsiSource(string package)
    {
        string? product = InstalledProductCode();
        if (product is null) return package;
        var buffer = new System.Text.StringBuilder(256);
        uint length = 256;
        if (MsiGetProductInfo(product, "PackageName", buffer, ref length) != 0) throw new IOException("MSI source name unavailable");
        string name = buffer.ToString();
        // Windows Installer требует исходное имя MSI даже при наличии нового проверенного файла.
        // Имя берём из регистрации, но разрешаем только простой basename внутри закрытого cache.
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.Contains(':') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid MSI source name");
        string directory = Path.Combine(Watcher.Uploader.GuardPaths.Root, "cache", "msi-source");
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(Path.GetDirectoryName(directory)!) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked MSI source cache refused");
        string target = Path.Combine(directory, name);
        foreach (string old in Directory.EnumerateFiles(directory))
            if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase)) File.Delete(old);
        using var input = File.OpenRead(package);
        Watcher.Uploader.GuardPaths.WriteStream(target, output => input.CopyTo(output));
        return target;
    }
    public void Install(string package, string platform, bool repair, bool downgrade = false)
    {
        Watcher.Uploader.GuardPolicy Policy() => Watcher.Uploader.GuardPaths.Load() ?? throw new IOException("Guard policy missing");
        string logDirectory = Path.Combine(Watcher.Uploader.GuardPaths.Root, "logs"); Directory.CreateDirectory(logDirectory);
        string installLog = Path.Combine(logDirectory, "msi-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + ".log");
        string source = OperatingSystem.IsWindows() && platform == "windows-msi" ? PrepareMsiSource(package) : package;
        int exit;
        try
        {
            exit = platform switch
            {
                "windows-msi" when OperatingSystem.IsWindows() => Run(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments = Watcher.Uploader.MsiCommandLine.Install(source, repair && InstalledProductCode() is not null, installLog, Policy().InstallDir, Policy().ServerUrl, Policy().UpdatePublicKey, Policy().MaintenancePublicKey, downgrade)
                }, 300),
                "linux-deb" when OperatingSystem.IsLinux() => Run("/usr/bin/dpkg", ["--force-confold", "--install", package], 300),
                "linux-rpm" when OperatingSystem.IsLinux() => Run("/usr/bin/rpm", ["--upgrade", "--replacepkgs", package], 300),
                _ => throw new IOException("Unsupported protected package platform")
            };
        }
        finally
        {
            if (!string.Equals(source, package, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(source); } catch (IOException) { }
        }
        if (exit == 3010) throw new Watcher.Uploader.GuardUpdateFailure("reboot-required", exit);
        if (exit != 0) throw new Watcher.Uploader.GuardUpdateFailure(exit == 1618 ? "installer-busy" : "install-failed", exit);
    }
    public void Uninstall(string kind, Watcher.Uploader.GuardPolicy policy)
    {
        Stop(kind, policy);
        if (OperatingSystem.IsWindows())
        {
            string? product = InstalledProductCode();
            if (product is null) throw new IOException("Installed MSI product not found");
            if (Run(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), ["/x", product, "/qn", "/norestart"], 300) is not (0 or 3010)) throw new IOException("Package removal failed");
            if (InstalledProductCode() is not null) throw new IOException("MSI product still installed");
        }
        else
        {
            int exit = File.Exists("/usr/bin/dpkg") ? Run("/usr/bin/dpkg", ["--remove", "watcher-agent"], 300) : Run("/usr/bin/rpm", ["--erase", "watcher-agent"], 300);
            if (exit != 0) throw new IOException("Package removal failed");
        }
    }
}
