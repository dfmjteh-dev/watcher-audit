using System.Text;
using System.Text.Json;
using Watcher.TerminalAgent;
using Xunit;

namespace Watcher.TerminalAgent.Tests;

// 0.12.2: команды без терминала — запуски процессов по правилу auditd watcher-exec.
public class ExecAuditTests
{
    private const string Ts = "1791831259.672";

    private static string Syscall(long serial, int ses, string tty, string comm = "ls", string key = "watcher-exec") =>
        $"type=SYSCALL msg=audit({Ts}:{serial}): arch=c000003e syscall=59 success=yes exit=0 items=2 ppid=10 pid=11 auid=1002 uid=1002 " +
        $"gid=1002 euid=1002 tty={tty} ses={ses} comm=\"{comm}\" exe=\"/usr/bin/{comm}\" subj=unconfined key=\"{key}\"ARCH=x86_64 SYSCALL=execve AUID=\"claude-rw\"";

    private static string Execve(long serial, string args) => $"type=EXECVE msg=audit({Ts}:{serial}): argc=3 {args}";
    private static string Cwd(long serial, string cwd) => $"type=CWD msg=audit({Ts}:{serial}): cwd=\"{cwd}\"";
    private static string UserStart(int ses, string exe) =>
        $"type=USER_START msg=audit(1791831250.000:1): pid=9 uid=0 auid=1002 ses={ses} subj=unconfined msg='op=PAM:session_open grantors=pam_permit acct=\"claude-rw\" exe=\"{exe}\" hostname=10.0.0.5 addr=10.0.0.5 terminal=ssh res=success'";
    private static string UserEnd(int ses) =>
        $"type=USER_END msg=audit(1791831270.000:99): pid=9 uid=0 auid=1002 ses={ses} subj=unconfined msg='op=PAM:session_close acct=\"claude-rw\" exe=\"/usr/sbin/sshd\" terminal=ssh res=success'";

    [Fact]
    public void Parser_reads_syscall_execve_and_cwd()
    {
        var sc = AuditLineParser.TryParse(Syscall(5, 7, "(none)"))!;
        Assert.Equal(AuditLineType.Syscall, sc.Type);
        Assert.Equal(7, sc.Ses);
        Assert.Equal(5, sc.Serial);
        Assert.Equal("(none)", sc.Terminal);

        // a2 — hex («/tmp/a b»), a3 — длинный аргумент частями.
        string hex = Convert.ToHexString(Encoding.UTF8.GetBytes("/tmp/a b"));
        var ex = AuditLineParser.TryParse(Execve(5, $"a0=\"rm\" a1=\"-rf\" a2={hex} a3_len=6 a3[0]=\"abc\" a3[1]=\"def\""))!;
        Assert.Equal(new[] { "rm", "-rf", "/tmp/a b", "abcdef" }, ex.Argv);
        Assert.Equal(5, ex.Serial);

        Assert.Equal("/home/claude-rw", AuditLineParser.TryParse(Cwd(5, "/home/claude-rw"))!.Cwd);
        // Чужие правила аудита хоста — не наши.
        Assert.Null(AuditLineParser.TryParse(Syscall(6, 7, "(none)", key: "other")));
    }

    private static List<JsonElement> Run(params string[] lines)
    {
        string queue = Path.Combine(Path.GetTempPath(), "wta-exec-" + Guid.NewGuid());
        var c = new SessionCollector(queue, "srv-1");
        foreach (var l in lines)
            if (AuditLineParser.TryParse(l) is { } p) c.Feed(p);
        c.SweepAbandoned(1791831300, 86400);
        return Directory.Exists(queue)
            ? Directory.GetFiles(queue).Select(f => JsonDocument.Parse(File.ReadAllText(f)).RootElement).ToList()
            : new List<JsonElement>();
    }

    [Fact]
    public void Ssh_session_without_terminal_becomes_a_list_of_commands()
    {
        string hex = Convert.ToHexString(Encoding.UTF8.GetBytes("/tmp/a b"));
        var records = Run(
            UserStart(7, "/usr/sbin/sshd"),
            Syscall(5, 7, "(none)", "bash"), Execve(5, "a0=\"bash\" a1=\"-c\" a2=\"rm -rf /tmp/x\""), Cwd(5, "/home/claude-rw"),
            Syscall(6, 7, "(none)", "rm"), Execve(6, $"a0=\"rm\" a1=\"-rf\" a2={hex}"), Cwd(6, "/home/claude-rw"),
            UserEnd(7));

        var r = Assert.Single(records);
        Assert.Equal("linux-exec", r.GetProperty("source").GetString());
        Assert.Equal("claude-rw", r.GetProperty("sessionUser").GetString());
        string input = r.GetProperty("input").GetString()!;
        Assert.Contains("/home/claude-rw$ bash -c 'rm -rf /tmp/x'", input);
        Assert.Contains("$ rm -rf '/tmp/a b'", input);
    }

    // MOTD и прочее, что sshd/PAM запускает до shell пользователя (от root), — не его действия.
    [Fact]
    public void Session_setup_before_the_user_shell_is_skipped()
    {
        string motd = Syscall(20, 12, "(none)", "run-parts").Replace("uid=1002 gid", "uid=0 gid");
        var r = Assert.Single(Run(UserStart(12, "/usr/sbin/sshd"),
            motd, Execve(20, "a0=\"run-parts\" a1=\"/etc/update-motd.d\""), Cwd(20, "/"),
            Syscall(21, 12, "(none)", "bash"), Execve(21, "a0=\"bash\" a1=\"-c\" a2=\"id\""), Cwd(21, "/home/claude-rw"),
            UserEnd(12)));
        string input = r.GetProperty("input").GetString()!;
        Assert.DoesNotContain("update-motd", input);
        Assert.Contains("bash -c id", input);
    }

    // sudo внутри SSH-сессии: его USER_START/USER_END с тем же ses не должны
    // подменять пользователя и закрывать сессию — команды до и после sudo в одной записи.
    [Fact]
    public void Sudo_inside_a_session_does_not_split_or_rename_it()
    {
        string sudoStart = UserStart(13, "/usr/bin/sudo").Replace("acct=\"claude-rw\"", "acct=\"root\"");
        string sudoEnd = UserEnd(13).Replace("/usr/sbin/sshd", "/usr/bin/sudo").Replace("audit(1791831270.000:99)", "audit(1791831262.000:98)");
        var r = Assert.Single(Run(UserStart(13, "/usr/sbin/sshd"),
            Syscall(30, 13, "(none)", "bash"), Execve(30, "a0=\"bash\" a1=\"-c\" a2=\"x\""), Cwd(30, "/home/claude-rw"),
            sudoStart, Syscall(31, 13, "(none)", "cat").Replace("uid=1002 gid", "uid=0 gid"), Execve(31, "a0=\"cat\" a1=\"/etc/shadow\""), Cwd(31, "/home/claude-rw"),
            sudoEnd,
            Syscall(32, 13, "(none)", "rm"), Execve(32, "a0=\"rm\" a1=\"f\""), Cwd(32, "/tmp"),
            UserEnd(13)));
        Assert.Equal("claude-rw", r.GetProperty("sessionUser").GetString());
        string input = r.GetProperty("input").GetString()!;
        Assert.Contains("cat /etc/shadow", input);
        Assert.Contains("rm f", input);
    }

    [Fact]
    public void Interactive_and_cron_sessions_do_not_get_exec_records()
    {
        // С терминалом — ввод пишет pam_tty_audit, запуски не дублируем.
        var tty = Run(UserStart(8, "/usr/sbin/sshd"), Syscall(10, 8, "pts0"), Execve(10, "a0=\"ls\""), UserEnd(8));
        Assert.Empty(tty);
        // cron: auid есть, но это не человек.
        var cron = Run(UserStart(9, "/usr/sbin/cron"), Syscall(11, 9, "(none)"), Execve(11, "a0=\"logrotate\""), UserEnd(9));
        Assert.Empty(cron);
    }

    [Fact]
    public void Tailer_reads_the_rest_of_the_rotated_log_then_the_new_one()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wta-tail-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        string log = Path.Combine(dir, "audit.log");
        var tailer = new AuditLogTailer(log, Path.Combine(dir, "bookmark.txt"));
        File.WriteAllText(log, "a1\na2\n");
        Assert.Equal(new[] { "a1", "a2" }, tailer.ReadNewLines());
        File.AppendAllText(log, "a3\n");
        // Ротация: старый файл → audit.log.1, новый уже длиннее старой закладки.
        File.Move(log, log + ".1");
        File.WriteAllText(log, "b1-long-line-longer-than-before\nb2\n");
        Assert.Equal(new[] { "a3", "b1-long-line-longer-than-before", "b2" }, tailer.ReadNewLines());
        Assert.Empty(tailer.ReadNewLines());
    }
}
