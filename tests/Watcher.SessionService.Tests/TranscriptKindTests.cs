using Watcher.SessionService;
using Xunit;

namespace Watcher.SessionService.Tests;

// 0.16: метки транскрипта — происхождение (цепочка 4688, учётка процесса) и режим.
public class TranscriptKindTests
{
    private static readonly DateTime T = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
    private const string Ps = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";

    [Theory]
    [InlineData("powershell", "interactive")]
    [InlineData(@"""C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe""", "interactive")]
    [InlineData("powershell -NoProfile -ExecutionPolicy Bypass", "interactive")]
    [InlineData("powershell -ep Bypass -NoLogo", "interactive")]
    [InlineData("powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand JABPAHUAdAA=", "exec")]
    [InlineData("powershell -enc JABPAHUAdAA=", "exec")]
    [InlineData("powershell -e JABPAHUAdAA=", "exec")]
    [InlineData("powershell.exe -ex bypass -File C:\\scripts\\backup.ps1", "exec")]
    [InlineData("powershell -c Get-Date", "exec")]
    [InlineData("powershell -Command \"Get-Service | Out-File x\"", "exec")]
    [InlineData("powershell Get-Date", "exec")]
    [InlineData("powershell -NoExit -Command Import-Module X", "interactive")]
    [InlineData("pwsh -NonInteractive -WindowStyle Hidden -f run.ps1", "exec")]
    public void Mode_from_host_application(string hostApplication, string expected) =>
        Assert.Equal(expected, TranscriptKind.Mode(hostApplication));

    [Fact]
    public void Mode_unknown_without_command_line() => Assert.Null(TranscriptKind.Mode(""));

    [Theory]
    [InlineData(@"NT AUTHORITY\SYSTEM", true)]
    [InlineData(@"NT AUTHORITY\СИСТЕМА", true)]
    [InlineData(@"NT AUTHORITY\NETWORK SERVICE", true)]
    [InlineData(@"CORP\SRV-1C$", true)]
    [InlineData(@"CORP\alice", false)]
    [InlineData(@"PC-1\wt-ssh", false)]
    [InlineData("", false)]
    public void Service_accounts(string runAs, bool expected) => Assert.Equal(expected, TranscriptKind.IsServiceAccount(runAs));

    // Так SYSTEM записан в заголовке на стенде (Windows 10/11/2019, русская локаль):
    // домен — рабочая группа, имя локализовано; решает SID.
    [Theory]
    [InlineData(@"WORKGROUP\СИСТЕМА", "S-1-5-18", true)]
    [InlineData(@"CORP\svc-app", "S-1-5-80-1234", true)]
    [InlineData(@"PC-1\alice", "S-1-5-21-1-2-3-1001", false)]
    [InlineData(@"WORKGROUP\СИСТЕМА", null, false)]
    public void Service_accounts_by_sid(string runAs, string? sid, bool expected)
    {
        string? SidOf(string name) => name.EndsWith(runAs.Split('\\')[^1], StringComparison.Ordinal) ? sid : null;
        Assert.Equal(expected, TranscriptKind.IsServiceAccount(runAs, SidOf));
    }

    [Theory]
    [InlineData(@"C:\Windows\explorer.exe", "local")]
    [InlineData(@"C:\Windows\System32\svchost.exe", "automation")]
    [InlineData(@"C:\Windows\System32\taskhostw.exe", "automation")]
    [InlineData(@"C:\Program Files\Microsoft SQL Server\MSSQL\Binn\SQLAGENT.EXE", "sql-agent")]
    [InlineData(@"C:\Windows\System32\OpenSSH\sshd.exe", "ssh")]
    public void Boundary_gives_origin_label(string parent, string label)
    {
        var index = SshAncestry.Index([new ProcessStart(T, 100, Ps, 50, parent)]);
        Assert.Equal(label, SshAncestry.ClassifyDetailed(100, T, index, null).Label);
    }

    [Fact]
    public void Powershell_remoting_host_is_winrm()
    {
        const string host = @"C:\Windows\System32\wsmprovhost.exe";
        var index = SshAncestry.Index([new ProcessStart(T, 300, host, 60, @"C:\Windows\System32\svchost.exe")]);
        var d = SshAncestry.ClassifyDetailed(300, T, index, host + " -Embedding");
        Assert.Equal((SessionOrigin.NotSsh, "winrm"), (d.Origin, d.Label));
    }

    [Fact]
    public void Missing_events_stay_unknown()
    {
        var d = SshAncestry.ClassifyDetailed(100, T, SshAncestry.Index([]), null);
        Assert.Equal((SessionOrigin.Unknown, "unknown"), (d.Origin, d.Label));
    }
}
