using Watcher.SessionService;
using Xunit;

namespace Watcher.SessionService.Tests;

// Задача F1: цепочки процессов — как в событиях 4688 со стенда спайка F0.
public class SshAncestryTests
{
    private static readonly DateTime T = new(2026, 10, 8, 12, 33, 28, DateTimeKind.Utc);
    private const string Ps = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
    private const string Conhost = @"C:\Windows\System32\conhost.exe";
    private const string Sshd = @"C:\Windows\System32\OpenSSH\sshd.exe";

    [Fact]
    public void Command_mode_powershell_child_of_sshd_is_ssh()
    {
        var ev = new[] { new ProcessStart(T, 4588, Ps, 900, Sshd) };
        Assert.Equal((SessionOrigin.Ssh, 900), SshAncestry.Classify(4588, T, ev));
    }

    [Fact]
    public void Interactive_powershell_under_conhost_under_sshd_is_ssh()
    {
        var ev = new[]
        {
            new ProcessStart(T.AddMilliseconds(-200), 3956, Conhost, 901, Sshd),
            new ProcessStart(T, 5900, Ps, 3956, Conhost),
        };
        Assert.Equal((SessionOrigin.Ssh, 901), SshAncestry.Classify(5900, T.AddSeconds(1), ev));
    }

    [Fact]
    public void Rdp_or_task_powershell_is_not_ssh()
    {
        var ev = new[]
        {
            new ProcessStart(T, 100, Ps, 50, @"C:\Windows\explorer.exe"),
            new ProcessStart(T, 101, Ps, 51, @"C:\Windows\System32\svchost.exe"),
            new ProcessStart(T.AddMilliseconds(-100), 60, Conhost, 52, @"C:\Windows\explorer.exe"),
            new ProcessStart(T, 102, Ps, 60, Conhost),
        };
        Assert.Equal(SessionOrigin.NotSsh, SshAncestry.Classify(100, T, ev).Origin);
        Assert.Equal(SessionOrigin.NotSsh, SshAncestry.Classify(101, T, ev).Origin);
        Assert.Equal(SessionOrigin.NotSsh, SshAncestry.Classify(102, T, ev).Origin);
    }

    [Fact]
    public void Newer_openssh_sshd_session_exe_counts_as_sshd()
    {
        var ev = new[] { new ProcessStart(T, 7, Ps, 8, @"C:\Program Files\OpenSSH\sshd-session.exe") };
        Assert.Equal(SessionOrigin.Ssh, SshAncestry.Classify(7, T, ev).Origin);
    }

    // Без событий 4688 (аудит выключен, журнал перезаписан) — не знаем, а не «не SSH».
    [Fact]
    public void Missing_or_reused_pid_is_unknown()
    {
        Assert.Equal(SessionOrigin.Unknown, SshAncestry.Classify(4588, T, Array.Empty<ProcessStart>()).Origin);
        // Тот же PID, но процесс стартовал за час до транскрипта — это другой процесс.
        var old = new[] { new ProcessStart(T.AddHours(-1), 4588, Ps, 900, Sshd) };
        Assert.Equal(SessionOrigin.Unknown, SshAncestry.Classify(4588, T, old).Origin);
    }

    [Theory]
    [InlineData("SQLPS.exe")]
    [InlineData(@"C:\Program Files\Microsoft SQL Server\150\Tools\Binn\sqlps.EXE")]
    public void Sqlps_under_sql_agent_is_not_ssh(string name)
    {
        var ev = new[] { new ProcessStart(T, 4448, name, 300, @"C:\SQL\SQLAGENT.EXE") };
        Assert.Equal((SessionOrigin.NotSsh, 0), SshAncestry.Classify(4448, T, ev));
    }

    [Theory]
    [InlineData(-601)]
    [InlineData(31)]
    public void Sqlps_outside_time_window_is_unknown(int seconds)
    {
        var ev = new[] { new ProcessStart(T.AddSeconds(seconds), 4448, "SQLPS.exe", 300, "SQLAGENT.EXE") };
        Assert.Equal((SessionOrigin.Unknown, 0), SshAncestry.Classify(4448, T, ev));
    }

    [Fact]
    public void Sqlps_still_checks_ssh_ancestry()
    {
        var direct = new[] { new ProcessStart(T, 4448, "SQLPS.exe", 900, Sshd) };
        Assert.Equal((SessionOrigin.Ssh, 900), SshAncestry.Classify(4448, T, direct));
        var interactive = new[]
        {
            new ProcessStart(T.AddMilliseconds(-200), 300, Conhost, 901, Sshd),
            new ProcessStart(T, 4448, "SQLPS.exe", 300, Conhost),
        };
        Assert.Equal((SessionOrigin.Ssh, 901), SshAncestry.Classify(4448, T, interactive));
        Assert.Equal((SessionOrigin.Unknown, 0), SshAncestry.Classify(4448, T, interactive.Skip(1).ToArray()));
    }

    [Fact]
    public void Unconfirmed_host_or_missing_sqlps_event_is_unknown()
    {
        Assert.Equal((SessionOrigin.Unknown, 0), SshAncestry.Classify(4448, T, Array.Empty<ProcessStart>()));
        var ev = new[] { new ProcessStart(T, 4448, "other.exe", 300, "SQLAGENT.EXE") };
        Assert.Equal((SessionOrigin.Unknown, 0), SshAncestry.Classify(4448, T, ev));
    }

    [Fact]
    public void Latest_matching_process_wins_when_sqlps_pid_is_reused()
    {
        var ev = new[]
        {
            new ProcessStart(T.AddMinutes(-1), 4448, "SQLPS.exe", 900, Sshd),
            new ProcessStart(T, 4448, "SQLPS.exe", 300, "SQLAGENT.EXE"),
        };
        Assert.Equal((SessionOrigin.NotSsh, 0), SshAncestry.Classify(4448, T, ev));
    }

    [Fact]
    public void Remote_address_prefers_same_sshd_then_latest_login_of_user()
    {
        var accepts = new[]
        {
            SshAncestry.ParseAccept(T.AddSeconds(-30), 777, "sshd: Accepted publickey for alice from 10.0.0.1 port 5000 ssh2: ED25519 SHA256:x")!,
            SshAncestry.ParseAccept(T.AddSeconds(-5), 778, "sshd: Accepted publickey for alice from 10.0.0.2 port 5001 ssh2: ED25519 SHA256:x")!,
            SshAncestry.ParseAccept(T.AddSeconds(-1), 779, "sshd: Accepted password for corp\\bob from 10.0.0.3 port 5002 ssh2")!,
        };
        Assert.Equal("10.0.0.1", SshAncestry.RemoteAddress("alice", 777, T, accepts));
        Assert.Equal("10.0.0.2", SshAncestry.RemoteAddress("alice", 0, T, accepts));
        Assert.Equal("10.0.0.3", SshAncestry.RemoteAddress("bob", 1, T, accepts));
        Assert.Null(SshAncestry.RemoteAddress("carol", 1, T, accepts));
        Assert.Null(SshAncestry.ParseAccept(T, 1, "sshd: Disconnected from user alice"));
    }
}
