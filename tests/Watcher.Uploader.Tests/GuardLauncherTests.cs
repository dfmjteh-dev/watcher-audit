using System.Runtime.Versioning;
using Watcher.Guard;
using Xunit;

namespace Watcher.Uploader.Tests;

// 0.16: лаунчер GuardRepair (бывшие recover.sh / recover.ps1) — сценарии прежнего
// installer/guard/test-launcher.sh, только во временном каталоге.
public class GuardLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "watcher-launcher-" + Guid.NewGuid().ToString("N"));
    public GuardLauncherTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private string Put(string name, string content)
    {
        string payload = Path.Combine(_root, "payload");
        File.WriteAllText(payload, content);
        string hash = GuardLauncher.Sha256(payload);
        Directory.CreateDirectory(Path.Combine(_root, "bin", hash));
        File.Copy(payload, Path.Combine(_root, "bin", hash, name), true);
        File.WriteAllText(Path.Combine(_root, "launcher"), hash + "\n");
        return hash;
    }

    private static string Primary => OperatingSystem.IsWindows() ? "GuardRepair.exe" : "GuardRepair";
    private static string Legacy => OperatingSystem.IsWindows() ? "Watcher.Guard.exe" : "Watcher.Guard";

    [Fact]
    public void Legacy_name_is_still_launched()
    {
        string hash = Put(Legacy, "old");
        Assert.Equal(Path.Combine(_root, "bin", hash, Legacy), GuardLauncher.Current(_root));
    }

    [Fact]
    public void New_name_has_priority()
    {
        string hash = Put(Legacy, "same");
        File.Copy(Path.Combine(_root, "bin", hash, Legacy), Path.Combine(_root, "bin", hash, Primary));
        Assert.Equal(Path.Combine(_root, "bin", hash, Primary), GuardLauncher.Current(_root));
    }

    [Fact]
    public void Corrupt_primary_is_refused_without_silent_fallback()
    {
        string hash = Put(Legacy, "good");
        File.WriteAllText(Path.Combine(_root, "bin", hash, Primary), "corrupt");
        Assert.Contains("checksum mismatch", Assert.Throws<IOException>(() => GuardLauncher.Current(_root)).Message);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public void Invalid_or_unknown_launcher_is_refused(string launcher)
    {
        File.WriteAllText(Path.Combine(_root, "launcher"), launcher);
        Assert.Throws<IOException>(() => GuardLauncher.Current(_root));
    }

    [Fact]
    public void Launcher_is_recognised_only_at_its_stable_path()
    {
        Assert.True(GuardLauncher.IsLauncher(_root, GuardLauncher.Path(_root)));
        Assert.False(GuardLauncher.IsLauncher(_root, Path.Combine(_root, "bin", "x", Primary)));
        Assert.False(GuardLauncher.IsLauncher(_root, "/opt/watcher-guard/GuardRepair"));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void Linux_timer_runs_launcher_with_private_bundle_directory()
    {
        if (!OperatingSystem.IsLinux()) return;
        string unit = GuardLinuxSetup.ServiceText("/var/lib/watcher/guard");
        Assert.Contains("\nExecStart=/var/lib/watcher/guard/GuardRepair tick\n", unit);
        Assert.Contains("\nEnvironment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/watcher/guard/bundle\n", unit);
        Assert.DoesNotContain("recover.sh", unit);
        Assert.StartsWith("[Unit]\n", unit);
        Assert.Contains("OnUnitActiveSec=2min", GuardLinuxSetup.TimerText);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void Linux_setup_arguments_match_former_script()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.True(GuardLinuxSetup.Parse(["--refresh"]).Refresh);
        var full = GuardLinuxSetup.Parse(["m", "u", "https://s", "", ""]);
        Assert.False(full.Refresh);
        Assert.Equal(("m", "u", "https://s", ""), (full.MaintenanceKey, full.UpdateKey, full.ServerUrl, full.Package));
        Assert.Equal("p", GuardLinuxSetup.Parse(["m", "u", "s", "p", "f"]).Package);
        Assert.Throws<ArgumentException>(() => GuardLinuxSetup.Parse(["m", "u"]));
    }
}
