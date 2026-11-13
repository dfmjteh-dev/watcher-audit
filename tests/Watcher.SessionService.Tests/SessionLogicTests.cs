using System.ServiceProcess;
using Watcher.SessionService;
using Xunit;

namespace Watcher.SessionService.Tests;

public class SessionLogicTests
{
    [Theory]
    [InlineData(SessionChangeReason.SessionLogon, SessionChangeAction.Launch)]
    [InlineData(SessionChangeReason.ConsoleConnect, SessionChangeAction.Launch)]
    [InlineData(SessionChangeReason.RemoteConnect, SessionChangeAction.Launch)]
    [InlineData(SessionChangeReason.SessionLogoff, SessionChangeAction.StopTracking)]
    [InlineData(SessionChangeReason.ConsoleDisconnect, SessionChangeAction.LogDisconnect)]
    [InlineData(SessionChangeReason.RemoteDisconnect, SessionChangeAction.LogDisconnect)]
    public void ClassifyChange_maps_known_reasons(SessionChangeReason reason, SessionChangeAction expected)
    {
        Assert.Equal(expected, SessionLogic.ClassifyChange(reason));
    }

    [Theory]
    [InlineData(SessionChangeReason.SessionLock)]
    [InlineData(SessionChangeReason.SessionUnlock)]
    [InlineData(SessionChangeReason.SessionRemoteControl)]
    public void ClassifyChange_ignores_unhandled_reasons(SessionChangeReason reason)
    {
        Assert.Equal(SessionChangeAction.Ignore, SessionLogic.ClassifyChange(reason));
    }

    [Fact]
    public void BuildCaptureCommandLine_with_known_user_appends_session_user()
    {
        string cmd = SessionLogic.BuildCaptureCommandLine(@"C:\Watcher\Watcher.Capture.exe", "alice");

        Assert.Equal("\"C:\\Watcher\\Watcher.Capture.exe\" --session-user \"alice\"", cmd);
    }

    [Theory]
    [InlineData("?")]
    [InlineData(null)]
    [InlineData("")]
    public void BuildCaptureCommandLine_without_known_user_omits_session_user(string? userName)
    {
        string cmd = SessionLogic.BuildCaptureCommandLine(@"C:\Watcher\Watcher.Capture.exe", userName);

        Assert.Equal("\"C:\\Watcher\\Watcher.Capture.exe\"", cmd);
        Assert.DoesNotContain("--session-user", cmd);
    }

    // D1: пустое имя — в сессии никого (экран входа, RemoteConnect до пароля),
    // запускать нечего; null — запрос имени не удался, пробуем как раньше.
    [Theory]
    [InlineData("", false)]
    [InlineData(null, true)]
    [InlineData("wt-alice", true)]
    public void SessionHasUser_skips_only_sessions_with_empty_user(string? userName, bool expected)
    {
        Assert.Equal(expected, SessionLogic.SessionHasUser(userName));
    }

    // D1: после перезапуска службы — подхватить работающий Capture сессии, не второй.
    [Fact]
    public void ExistingCaptureInSession_finds_capture_of_that_session_only()
    {
        var captures = new[] { (Pid: 100, SessionId: 2), (Pid: 200, SessionId: 5) };
        Assert.Equal(200, SessionLogic.ExistingCaptureInSession(captures, 5));
        Assert.Null(SessionLogic.ExistingCaptureInSession(captures, 3));
        Assert.Null(SessionLogic.ExistingCaptureInSession(Array.Empty<(int, int)>(), 2));
    }

    // D3: кэш — тот самый config_json с сервера (форма из веб-UI, camelCase).
    [Fact]
    public void ParseUserFilter_reads_server_config_blob()
    {
        const string json = """{"schemaVersion":1,"outputFps":3,"preset":"ultrafast","allowUsers":[],"excludeUsers":["wt-bob","Admin"]}""";
        var f = SessionLogic.ParseUserFilter(json);
        Assert.NotNull(f);
        Assert.Empty(f!.AllowUsers!);
        Assert.Equal(new[] { "wt-bob", "Admin" }, f.ExcludeUsers);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"excludeUsers\": \"wt-bob\"}")]
    public void ParseUserFilter_broken_cache_means_no_filter(string json)
    {
        Assert.Null(SessionLogic.ParseUserFilter(json));
        Assert.True(SessionLogic.IsUserAllowed(SessionLogic.ParseUserFilter(json), "anyone"));
    }

    // Те же правила, что Watcher.Capture ConfigReload.IsCaptureAllowedForUser.
    [Theory]
    [InlineData(null, null, "ivan", true)]                 // конфига нет — все
    [InlineData("", "", "ivan", true)]                     // пустые списки — все
    [InlineData("ivan", "", "IVAN", true)]                 // allow, без учёта регистра
    [InlineData("ivan", "", "petr", false)]
    [InlineData("", "petr", "petr", false)]                // exclude
    [InlineData("", "petr", "ivan", true)]
    [InlineData("ivan", "ivan", "ivan", true)]             // allow главнее exclude
    public void IsUserAllowed_matches_capture_rules(string? allow, string? exclude, string user, bool expected)
    {
        static IReadOnlyList<string>? L(string? csv) => csv is null ? null : csv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var filter = allow is null && exclude is null ? null : new UserFilter(L(allow), L(exclude));
        Assert.Equal(expected, SessionLogic.IsUserAllowed(filter, user));
    }

    [Fact]
    public void SessionsNeedingRespawn_returns_only_dead_sessions()
    {
        var running = new Dictionary<int, int> { [1] = 100, [2] = 200, [3] = 300 };
        bool IsAlive(int pid) => pid != 200;

        var result = SessionLogic.SessionsNeedingRespawn(running, IsAlive);

        Assert.Equal(new[] { 2 }, result);
    }

    [Fact]
    public void SessionsNeedingRespawn_empty_map_returns_empty()
    {
        var result = SessionLogic.SessionsNeedingRespawn(
            new Dictionary<int, int>(), _ => false);

        Assert.Empty(result);
    }

    [Fact]
    public void SessionsNeedingRespawn_all_alive_returns_empty()
    {
        var running = new Dictionary<int, int> { [1] = 100, [2] = 200 };

        var result = SessionLogic.SessionsNeedingRespawn(running, _ => true);

        Assert.Empty(result);
    }
}
