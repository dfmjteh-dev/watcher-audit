using Watcher.TerminalAgent;
using Xunit;

namespace Watcher.TerminalAgent.Tests;

public class AuditLineParserTests
{
    // Реальная строка (без изменений), снятая живьём на watcher-test-1,
    // 2026-10-06 — см. roadmap.md, Фаза 8. Хвост "UID=... AUID=..." без
    // пробела перед UID — так пишет auditd в ENRICHED-режиме на Debian 12,
    // это не опечатка в тесте.
    private const string TtyLine =
        "type=TTY msg=audit(1791283588.096:96): tty pid=52900 uid=1003 auid=1003 ses=295 major=136 minor=0 " +
        "comm=\"bash\" data=6563686F20494E534944455F5054595F53455353494F4E5F34320D657869740D" +
        "UID=\"watcher-term-test\" AUID=\"watcher-term-test\"";

    private const string UserStartLine =
        "type=USER_START msg=audit(1791283583.400:95): pid=52843 uid=0 auid=1003 ses=295 subj=unconfined " +
        "msg='op=PAM:session_open grantors=pam_unix acct=\"watcher-term-test\" exe=\"/usr/sbin/sshd\" " +
        "hostname=192.0.2.30 addr=192.0.2.30 terminal=ssh res=success'UID=\"root\" AUID=\"watcher-term-test\"";

    private const string UserEndLine =
        "type=USER_END msg=audit(1791283588.100:98): pid=52843 uid=0 auid=1003 ses=295 subj=unconfined " +
        "msg='op=PAM:session_close grantors=pam_unix acct=\"watcher-term-test\" exe=\"/usr/sbin/sshd\" " +
        "hostname=192.0.2.30 addr=192.0.2.30 terminal=ssh res=success'UID=\"root\" AUID=\"watcher-term-test\"";

    [Fact]
    public void Parses_tty_line_and_isolates_hex_data_despite_no_separator_before_UID()
    {
        var line = AuditLineParser.TryParse(TtyLine);
        Assert.NotNull(line);
        Assert.Equal(AuditLineType.Tty, line!.Type);
        Assert.Equal(295, line.Ses);
        Assert.Equal("6563686F20494E534944455F5054595F53455353494F4E5F34320D657869740D", line.DataHex);
    }

    [Fact]
    public void Decodes_hex_to_original_typed_text()
    {
        var line = AuditLineParser.TryParse(TtyLine)!;
        string decoded = AuditLineParser.DecodeHex(line.DataHex!);
        Assert.Equal("echo INSIDE_PTY_SESSION_42\rexit\r", decoded);
    }

    [Fact]
    public void Parses_user_start_with_acct_and_addr()
    {
        var line = AuditLineParser.TryParse(UserStartLine);
        Assert.NotNull(line);
        Assert.Equal(AuditLineType.UserStart, line!.Type);
        Assert.Equal("watcher-term-test", line.Acct);
        Assert.Equal("192.0.2.30", line.Addr);
        Assert.Equal(295, line.Ses);
    }

    [Fact]
    public void Parses_user_end()
    {
        var line = AuditLineParser.TryParse(UserEndLine);
        Assert.NotNull(line);
        Assert.Equal(AuditLineType.UserEnd, line!.Type);
        Assert.Equal(295, line.Ses);
    }

    [Fact]
    public void Ignores_unrelated_line_types()
    {
        Assert.Null(AuditLineParser.TryParse("type=CONFIG_CHANGE msg=audit(1.0:1): ses=295 op=tty_set"));
        Assert.Null(AuditLineParser.TryParse("not an audit line at all"));
    }

    [Fact]
    public void Returns_null_without_session_id()
    {
        Assert.Null(AuditLineParser.TryParse("type=TTY msg=audit(1.0:1): data=6869"));
    }

    // Задача 17 (roadmap-fixes.md): реальные строки сессии через
    // watcher-term-wrap.sh, сняты на тестовом стенде 2026-10-08 (data укорочен,
    // адрес клиента заменён на документационный 192.0.2.10).
    private const string UserLoginLine =
        "type=USER_LOGIN msg=audit(1791438649.616:4000): pid=67395 uid=0 auid=1003 ses=545 subj=unconfined " +
        "msg='op=login id=1003 exe=\"/usr/sbin/sshd\" hostname=192.0.2.10 addr=192.0.2.10 terminal=/dev/pts/0 res=success'" +
        "UID=\"root\" AUID=\"watcher-term-test\" ID=\"watcher-term-test\"";

    private const string TtyInnerLine =
        "type=TTY msg=audit(1791438665.152:4001): tty pid=67473 uid=1003 auid=1003 ses=545 major=136 minor=1 " +
        "comm=\"bash\" data=6563686F0DUID=\"watcher-term-test\" AUID=\"watcher-term-test\"";

    [Fact]
    public void Parses_user_login_terminal_as_tty_device()
    {
        var line = AuditLineParser.TryParse(UserLoginLine);
        Assert.NotNull(line);
        Assert.Equal(AuditLineType.UserLogin, line!.Type);
        Assert.Equal(545, line.Ses);
        Assert.Equal("136:0", line.LoginTty);
    }

    [Fact]
    public void Parses_tty_major_minor()
    {
        Assert.Equal("136:1", AuditLineParser.TryParse(TtyInnerLine)!.Tty);
        Assert.Equal("136:0", AuditLineParser.TryParse(TtyLine)!.Tty);
    }

    // Неудачный вход: ses не назначен (u32 -1) — не бросать OverflowException.
    [Fact]
    public void Unset_session_id_is_ignored_not_thrown()
    {
        string failed = UserLoginLine.Replace("ses=545", "ses=4294967295").Replace("res=success", "res=failed");
        Assert.Null(AuditLineParser.TryParse(failed));
    }

    [Theory]
    [InlineData("/dev/pts/0", "136:0")]
    [InlineData("pts/7", "136:7")]
    [InlineData("/dev/pts/300", "137:44")]
    [InlineData("/dev/tty1", "4:1")]
    [InlineData("tty2", "4:2")]
    [InlineData("/dev/ttyS0", "4:64")]
    [InlineData("ssh", null)]
    [InlineData("?", null)]
    [InlineData(null, null)]
    public void Maps_login_terminal_to_device_numbers(string? terminal, string? expected)
    {
        Assert.Equal(expected, AuditLineParser.TerminalToTty(terminal));
    }
}
