using System.Text.Json;
using Watcher.TerminalAgent;
using Xunit;

namespace Watcher.TerminalAgent.Tests;

public class SessionCollectorTests
{
    private static string NewQueueDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wta-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Full_session_start_tty_end_writes_one_record()
    {
        string queueDir = NewQueueDir();
        try
        {
            var collector = new SessionCollector(queueDir, "testhost");
            collector.Feed(new AuditLine(AuditLineType.UserStart, 1, 1000, "alice", null, "10.0.0.5"));
            collector.Feed(new AuditLine(AuditLineType.Tty, 1, 1001, null, "6563686F", null));
            collector.Feed(new AuditLine(AuditLineType.UserEnd, 1, 1002, null, null, null));

            var files = Directory.GetFiles(queueDir);
            Assert.Single(files);

            using var doc = JsonDocument.Parse(File.ReadAllText(files[0]));
            Assert.Equal("terminal-input", doc.RootElement.GetProperty("kind").GetString());
            Assert.Equal("testhost", doc.RootElement.GetProperty("host").GetString());
            Assert.Equal("alice", doc.RootElement.GetProperty("sessionUser").GetString());
            Assert.Equal("10.0.0.5", doc.RootElement.GetProperty("remoteAddr").GetString());
            Assert.Equal("echo", doc.RootElement.GetProperty("input").GetString());
        }
        finally
        {
            Directory.Delete(queueDir, recursive: true);
        }
    }

    [Fact]
    public void Session_without_any_tty_input_is_not_written()
    {
        string queueDir = NewQueueDir();
        try
        {
            var collector = new SessionCollector(queueDir, "testhost");
            collector.Feed(new AuditLine(AuditLineType.UserStart, 2, 1000, "bob", null, null));
            collector.Feed(new AuditLine(AuditLineType.UserEnd, 2, 1001, null, null, null));

            Assert.Empty(Directory.GetFiles(queueDir));
        }
        finally
        {
            Directory.Delete(queueDir, recursive: true);
        }
    }

    [Fact]
    public void Tty_before_user_start_still_captured_best_effort()
    {
        // Воспроизводит рестарт агента посреди сессии (bookmark попал между
        // USER_START и первым TTY) — не должны терять уже идущий ввод.
        string queueDir = NewQueueDir();
        try
        {
            var collector = new SessionCollector(queueDir, "testhost");
            collector.Feed(new AuditLine(AuditLineType.Tty, 3, 1000, null, "6869", null));
            collector.Feed(new AuditLine(AuditLineType.UserEnd, 3, 1001, null, null, null));

            var files = Directory.GetFiles(queueDir);
            Assert.Single(files);
            using var doc = JsonDocument.Parse(File.ReadAllText(files[0]));
            Assert.Equal("hi", doc.RootElement.GetProperty("input").GetString());
            Assert.Equal("unknown", doc.RootElement.GetProperty("sessionUser").GetString());
        }
        finally
        {
            Directory.Delete(queueDir, recursive: true);
        }
    }

    [Fact]
    public void SweepAbandoned_flushes_sessions_with_no_recent_activity()
    {
        string queueDir = NewQueueDir();
        try
        {
            var collector = new SessionCollector(queueDir, "testhost");
            collector.Feed(new AuditLine(AuditLineType.UserStart, 4, 1000, "carol", "6869", null));
            collector.Feed(new AuditLine(AuditLineType.Tty, 4, 1000, null, "6869", null));

            collector.SweepAbandoned(nowEpoch: 1000 + 10, timeoutSeconds: 3600); // ещё не истекло
            Assert.Empty(Directory.GetFiles(queueDir));

            collector.SweepAbandoned(nowEpoch: 1000 + 3700, timeoutSeconds: 3600); // истекло
            Assert.Single(Directory.GetFiles(queueDir));
        }
        finally
        {
            Directory.Delete(queueDir, recursive: true);
        }
    }

    private static string InputOf(string queueDir)
    {
        var files = Directory.GetFiles(queueDir);
        Assert.Single(files);
        using var doc = JsonDocument.Parse(File.ReadAllText(files[0]));
        return doc.RootElement.GetProperty("input").GetString()!;
    }

    private static string Hex(string s) => Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(s));

    // Задача 17 (roadmap-fixes.md): SSH-сессия с watcher-term-wrap.sh —
    // нажатия приходят дважды (bash на pty script'а и script на pty входа).
    // Берём терминал входа из USER_LOGIN.
    [Fact]
    public void Input_copied_into_nested_pty_is_taken_once_from_login_terminal()
    {
        string queueDir = NewQueueDir();
        try
        {
            var c = new SessionCollector(queueDir, "testhost");
            c.Feed(new AuditLine(AuditLineType.UserStart, 7, 1000, "alice", null, "10.0.0.5"));
            c.Feed(new AuditLine(AuditLineType.UserLogin, 7, 1000.1, null, null, "10.0.0.5", LoginTty: "136:0"));
            c.Feed(new AuditLine(AuditLineType.Tty, 7, 1010, null, Hex("ls\rexit\r"), null, Tty: "136:1"));
            c.Feed(new AuditLine(AuditLineType.Tty, 7, 1010.1, null, Hex("ls\rexit\r"), null, Tty: "136:0"));
            c.Feed(new AuditLine(AuditLineType.UserEnd, 7, 1011, null, null, null));
            Assert.Equal("ls\rexit\r", InputOf(queueDir));
        }
        finally { Directory.Delete(queueDir, recursive: true); }
    }

    // Терминал входа неизвестен (агент стартовал посреди сессии, USER_LOGIN
    // уже позади) — всё подряд, как до Задачи 17: дубль лучше потери.
    [Fact]
    public void Without_login_terminal_all_ttys_are_kept()
    {
        string queueDir = NewQueueDir();
        try
        {
            var c = new SessionCollector(queueDir, "testhost");
            c.Feed(new AuditLine(AuditLineType.Tty, 8, 1010, null, Hex("a"), null, Tty: "136:1"));
            c.Feed(new AuditLine(AuditLineType.Tty, 8, 1010.1, null, Hex("b"), null, Tty: "136:0"));
            c.Feed(new AuditLine(AuditLineType.UserEnd, 8, 1011, null, null, null));
            Assert.Equal("ab", InputOf(queueDir));
        }
        finally { Directory.Delete(queueDir, recursive: true); }
    }

    // На терминале входа ввода нет (например, его TTY-запись потерялась) —
    // не отдаём пустоту, берём то, что есть.
    [Fact]
    public void Login_terminal_without_input_falls_back_to_other_ttys()
    {
        string queueDir = NewQueueDir();
        try
        {
            var c = new SessionCollector(queueDir, "testhost");
            c.Feed(new AuditLine(AuditLineType.UserLogin, 9, 1000, null, null, null, LoginTty: "136:0"));
            c.Feed(new AuditLine(AuditLineType.Tty, 9, 1010, null, Hex("whoami\r"), null, Tty: "136:4"));
            c.Feed(new AuditLine(AuditLineType.UserEnd, 9, 1011, null, null, null));
            Assert.Equal("whoami\r", InputOf(queueDir));
        }
        finally { Directory.Delete(queueDir, recursive: true); }
    }
}
