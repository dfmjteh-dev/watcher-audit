using System.Text;
using System.Text.Json;
using Watcher.TerminalAgent;
using Xunit;

namespace Watcher.TerminalAgent.Tests;

// 0.14.3, прод (AlmaLinux 8, MobaXterm): в одной SSH-сессии — терминал и фоновый
// канал клиента (мониторинг сервера). Строки — в формате того хоста: ENRICHED
// (UID=/AUID= через \x1d в конце), второй USER_START «op=login» без acct.
public class MixedSessionTests
{
    private const int Ses = 1862;
    private const string G = "\u001d";

    private static string Enriched(string user = "root") => $"{G}UID=\"{user}\" AUID=\"{user}\"";

    private static string UserStartPam(double ts) =>
        $"type=USER_START msg=audit({ts:F3}:43406): pid=100 uid=0 auid=0 ses={Ses} subj=kernel msg='op=PAM:session_open grantors=pam_unix,pam_tty_audit acct=\"root\" exe=\"/usr/sbin/sshd\" hostname=10.0.0.5 addr=10.0.0.5 terminal=ssh res=success'" + Enriched();
    private static string UserLogin(double ts) =>
        $"type=USER_LOGIN msg=audit({ts:F3}:43413): pid=100 uid=0 auid=0 ses={Ses} subj=kernel msg='op=login id=0 exe=\"/usr/sbin/sshd\" hostname=? addr=10.0.0.5 terminal=/dev/pts/0 res=success'" + Enriched() + " ID=\"root\"";
    private static string UserStartLogin(double ts) =>
        $"type=USER_START msg=audit({ts:F3}:43414): pid=100 uid=0 auid=0 ses={Ses} subj=kernel msg='op=login id=0 exe=\"/usr/sbin/sshd\" hostname=? addr=10.0.0.5 terminal=/dev/pts/0 res=success'" + Enriched() + " ID=\"root\"";
    private static string[] Exec(double ts, long serial, string cmd) =>
    [
        $"type=SYSCALL msg=audit({ts:F3}:{serial}): arch=c000003e syscall=59 success=yes exit=0 items=2 ppid=10 pid=11 auid=0 uid=0 gid=0 euid=0 tty=(none) ses={Ses} comm=\"bash\" exe=\"/usr/bin/bash\" subj=kernel key=\"watcher-exec\"" + G + "ARCH=x86_64 SYSCALL=execve AUID=\"root\" UID=\"root\"",
        $"type=EXECVE msg=audit({ts:F3}:{serial}): argc=3 a0=\"bash\" a1=\"-c\" a2=\"{cmd}\"",
        $"type=CWD msg=audit({ts:F3}:{serial}): cwd=\"/root\"",
    ];
    private static string Tty(double ts, string typed) =>
        $"type=TTY msg=audit({ts:F3}:43727): tty pid=200 uid=0 auid=0 ses={Ses} major=136 minor=1 comm=\"bash\" data={Convert.ToHexString(Encoding.UTF8.GetBytes(typed))}" + Enriched();
    private static string UserEnd(double ts) =>
        $"type=USER_END msg=audit({ts:F3}:43900): pid=100 uid=0 auid=0 ses={Ses} subj=kernel msg='op=PAM:session_close grantors=pam_unix acct=\"root\" exe=\"/usr/sbin/sshd\" hostname=10.0.0.5 addr=10.0.0.5 terminal=ssh res=success'" + Enriched();

    private const double T0 = 1792472704.0;

    private static List<JsonElement> Records(string queue) =>
        Directory.Exists(queue)
            ? Directory.GetFiles(queue).OrderBy(f => f).Select(f => JsonDocument.Parse(File.ReadAllText(f)).RootElement).ToList()
            : [];

    private static void Feed(SessionCollector c, IEnumerable<string> lines)
    {
        foreach (var l in lines)
            if (AuditLineParser.TryParse(l) is { } p) c.Feed(p);
    }

    [Fact]
    public void Second_user_start_without_acct_keeps_the_user()
    {
        string queue = Path.Combine(Path.GetTempPath(), "wta-mixed-" + Guid.NewGuid());
        var c = new SessionCollector(queue, "appliance");
        Feed(c, [UserStartPam(T0), UserLogin(T0 + 0.05), UserStartLogin(T0 + 0.05), Tty(T0 + 30, "ls -la\r"), UserEnd(T0 + 54)]);

        var r = Records(queue).Single();
        Assert.Equal("root", r.GetProperty("sessionUser").GetString());
        Assert.Equal("10.0.0.5", r.GetProperty("remoteAddr").GetString());
        Assert.StartsWith("2026-10-20T05:05:04", r.GetProperty("startedAt").GetString());   // время PAM, не второго USER_START
    }

    [Fact]
    public void Background_channel_is_a_separate_record_and_terminal_input_stays()
    {
        string queue = Path.Combine(Path.GetTempPath(), "wta-mixed-" + Guid.NewGuid());
        var c = new SessionCollector(queue, "appliance");
        Feed(c, [UserStartPam(T0), UserLogin(T0 + 0.05), UserStartLogin(T0 + 0.05),
            .. Exec(T0 + 0.1, 50, "while true; do sleep 1; head /proc/meminfo; done"),
            Tty(T0 + 33, "systemctl status sshd\r"), UserEnd(T0 + 54)]);

        var records = Records(queue);
        var term = records.Single(r => r.GetProperty("source").GetString() == "linux-tty-audit");
        var bg = records.Single(r => r.GetProperty("source").GetString() == "linux-exec");
        Assert.Contains("systemctl status sshd", term.GetProperty("input").GetString());
        Assert.DoesNotContain("meminfo", term.GetProperty("input").GetString());
        Assert.Equal(Ses, term.GetProperty("auditSession").GetInt64());   // к нему привяжется вывод терминала
        Assert.Contains("meminfo", bg.GetProperty("input").GetString());
        Assert.Equal(0, bg.GetProperty("auditSession").GetInt64());       // фоновая — ни с чем
        Assert.Equal("root", bg.GetProperty("sessionUser").GetString());
    }

    [Fact]
    public void Live_chunks_of_background_commands_do_not_swallow_later_terminal_input()
    {
        // Прод: первый кусок ушёл до ввода с терминала (ядро сбрасывает TTY позже)
        // — раньше это была строка фоновых команд, и ввод терминала (короче) потом
        // не отправлялся вовсе.
        string queue = Path.Combine(Path.GetTempPath(), "wta-mixed-" + Guid.NewGuid());
        var c = new SessionCollector(queue, "appliance", liveInterval: () => 1);
        Feed(c, [UserStartPam(T0), UserLogin(T0 + 0.05), UserStartLogin(T0 + 0.05), .. Exec(T0 + 0.1, 50, "while true; do head /proc/meminfo; done")]);
        c.FlushLive(T0 + 5);
        Feed(c, [Tty(T0 + 33, "systemctl status sshd\r")]);
        c.FlushLive(T0 + 40);
        Feed(c, [UserEnd(T0 + 54)]);
        c.SweepAbandoned(T0 + 100, 86400);

        var term = Records(queue).Where(r => r.GetProperty("source").GetString() == "linux-tty-audit").ToList();
        Assert.NotEmpty(term);
        Assert.Equal("systemctl status sshd\r", string.Concat(term.Select(r => r.GetProperty("input").GetString())));
        Assert.All(Records(queue).Where(r => r.GetProperty("source").GetString() == "linux-exec"),
            r => Assert.Equal(0, r.GetProperty("auditSession").GetInt64()));
    }
}
