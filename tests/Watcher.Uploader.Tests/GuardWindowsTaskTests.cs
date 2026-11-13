using System.Runtime.Versioning;
using Watcher.Guard;
using Xunit;

namespace Watcher.Uploader.Tests;

// 0.16: задача GuardRepair без PowerShell — XML и проверка, что задача наша
// (прежняя PowerShell-задача или новый лаунчер), а не чужая с тем же именем.
// Сами методы чистые (разбор строк), поэтому тест идёт и вне Windows.
[SupportedOSPlatform("windows")]
public class GuardWindowsTaskTests
{
    private const string Root = @"C:\Program Files\WatcherGuard";

    [Fact]
    public void Task_xml_runs_stable_launcher_as_system_with_protected_descriptor()
    {
        string xml = GuardWindowsTask.TaskXml(GuardWindowsTask.Launcher(Root), "tick", repeating: true, "d");
        Assert.Contains("<SecurityDescriptor>O:SYG:SYD:P(A;;GA;;;SY)(A;;GRGX;;;BA)</SecurityDescriptor>", xml);
        Assert.Contains("<BootTrigger>", xml);
        Assert.Contains("<Interval>PT2M</Interval>", xml);
        Assert.Contains("<UserId>S-1-5-18</UserId>", xml);
        Assert.Contains("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>", xml);
        var action = GuardWindowsTask.ParseAction(xml)!;
        Assert.Equal(GuardWindowsTask.Kind.Native, GuardWindowsTask.Classify(action, Root));
    }

    [Fact]
    public void Arguments_with_quotes_and_ampersands_are_escaped()
    {
        string xml = GuardWindowsTask.TaskXml(@"C:\a & b\GuardRepair.exe", "install \"C:\\Program Files\\Watcher\" key <x>", repeating: false, "d");
        var action = GuardWindowsTask.ParseAction(xml)!;
        Assert.Equal(@"C:\a & b\GuardRepair.exe", action.Command);
        Assert.Equal("install \"C:\\Program Files\\Watcher\" key <x>", action.Arguments);
        Assert.Contains("<Triggers />", xml);
    }

    [Fact]
    public void Legacy_powershell_task_is_recognised_as_ours()
    {
        var action = new GuardWindowsTask.Action(GuardWindowsTask.LegacyCommand, GuardWindowsTask.LegacyArguments(Root), "S-1-5-18");
        Assert.Equal(GuardWindowsTask.Kind.Legacy, GuardWindowsTask.Classify(action, Root));
        // Регистр пути, как Windows его вернёт, не важен.
        var upper = action with { Command = action.Command.ToUpperInvariant(), UserId = "SYSTEM" };
        Assert.Equal(GuardWindowsTask.Kind.Legacy, GuardWindowsTask.Classify(upper, Root));
    }

    [Theory]
    [InlineData(@"C:\Program Files\WatcherGuard\GuardRepair.exe", "tick", "S-1-5-21-1-2-3-500")]   // не от SYSTEM
    [InlineData(@"C:\Temp\GuardRepair.exe", "tick", "S-1-5-18")]                                      // чужой exe
    [InlineData(@"C:\Program Files\WatcherGuard\GuardRepair.exe", "tick --evil", "S-1-5-18")]          // другие аргументы
    public void Foreign_task_is_refused(string command, string arguments, string user) =>
        Assert.Null(GuardWindowsTask.Classify(new GuardWindowsTask.Action(command, arguments, user), Root));

    [Fact]
    public void Task_with_two_actions_is_not_parsed_as_ours()
    {
        string xml = GuardWindowsTask.TaskXml(GuardWindowsTask.Launcher(Root), "tick", true, "d")
            .Replace("</Exec></Actions>", "</Exec><Exec><Command>calc.exe</Command></Exec></Actions>");
        Assert.Null(GuardWindowsTask.ParseAction(xml));
    }
}
