using Watcher.SessionService;
using Xunit;

namespace Watcher.SessionService.Tests;

// 0.13: самоудаление агента на Windows — текст задания Планировщика.
public class WindowsSelfRemoverTests
{
    [Fact]
    public void Msi_install_is_removed_by_product_code_with_data()
    {
        string s = WindowsSelfRemover.Script("{11111111-2222-3333-4444-555555555555}", @"C:\Program Files\Watcher", @"C:\ProgramData", @"C:\Windows\Temp");
        Assert.Contains(@"msiexec /x {11111111-2222-3333-4444-555555555555} /qn /norestart", s);
        Assert.Contains($"rmdir /s /q \"{Path.Combine(@"C:\ProgramData", "Watcher")}\"", s);
        Assert.Contains($"schtasks /Delete /TN {WindowsSelfRemover.TaskName} /F", s);
        Assert.DoesNotContain("install.ps1", s);
        // Хвосты обновлений по zip — вместе с каталогом.
        Assert.Contains(@"rmdir /s /q ""C:\Program Files\Watcher""", s);
    }

    [Fact]
    public void Install_without_msi_removes_the_service_without_powershell()
    {
        string s = WindowsSelfRemover.Script(null, @"C:\Program Files\Watcher", @"C:\ProgramData", @"C:\Windows\Temp");
        Assert.Contains("sc stop WatcherSessionService & sc delete WatcherSessionService", s);
        Assert.DoesNotContain("powershell", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("msiexec", s);
    }
}
