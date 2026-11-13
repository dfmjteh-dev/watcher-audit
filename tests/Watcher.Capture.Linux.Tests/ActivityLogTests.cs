using Watcher.Capture.Linux;
using Xunit;

namespace Watcher.Capture.Linux.Tests;

// H9: смены активного окна в сегменте. Копия Watcher.Capture.Tests/ActivityLogTests.cs.
public class ActivityLogTests
{
    private readonly DateTime _t0 = new(2026, 10, 1, 9, 0, 0);
    private ActiveWindowInfo? _window = new("chrome", "Почта");

    private ActivityLog Log()
    {
        var log = new ActivityLog(() => _window);
        log.Start();
        return log;
    }

    [Fact]
    public void Records_changes_only_with_video_time()
    {
        var log = Log();
        log.Sample(_t0, 5.0, "full");                       // первый опрос — кадры pre-roll
        log.Sample(_t0.AddSeconds(2), 7.0, "full");          // то же окно
        _window = new("EXCEL", "Отчёт.xlsx");
        log.Sample(_t0.AddSeconds(4), 9.04, "full");
        Assert.Equal(new[] { new ActivitySample(0, "chrome", "Почта"), new ActivitySample(9.0, "EXCEL", "Отчёт.xlsx") }, log.Entries);
    }

    [Fact]
    public void First_window_seen_later_keeps_its_own_time()
    {
        _window = null;                                      // экран заблокирован
        var log = Log();
        log.Sample(_t0, 0, "full");
        _window = new("chrome", "Почта");
        log.Sample(_t0.AddSeconds(30), 30, "full");
        Assert.Equal(30, Assert.Single(log.Entries).T);
    }

    [Fact]
    public void Samples_at_most_once_per_second()
    {
        var log = Log();
        log.Sample(_t0, 0, "full");
        _window = new("EXCEL", "x");
        log.Sample(_t0.AddMilliseconds(500), 0.5, "full");
        Assert.Single(log.Entries);
        log.Sample(_t0.AddMilliseconds(1000), 1, "full");
        Assert.Equal(2, log.Entries.Count);
    }

    [Fact]
    public void Process_mode_drops_titles_and_off_records_nothing()
    {
        var log = Log();
        log.Sample(_t0, 0, "process");
        _window = new("chrome", "Другая вкладка");
        log.Sample(_t0.AddSeconds(2), 2, "process");         // процесс тот же — не смена
        Assert.Equal(new ActivitySample(0, "chrome", null), Assert.Single(log.Entries));

        var off = Log();
        off.Sample(_t0, 0, "off");
        off.Sample(_t0.AddSeconds(2), 2, "unknown-mode");
        Assert.Empty(off.Entries);
    }

    [Fact]
    public void Probe_failure_does_not_throw()
    {
        var log = new ActivityLog(() => throw new InvalidOperationException("X error"));
        log.Start();
        log.Sample(_t0, 0, "full");
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Long_titles_are_clipped_and_entries_capped()
    {
        _window = new("p", new string('я', 1000));
        var log = Log();
        log.Sample(_t0, 0, "full");
        Assert.Equal(ActivityLog.MaxTextLength, log.Entries[0].Title!.Length);

        for (int i = 1; i <= ActivityLog.MaxEntries + 5; i++)
        {
            _window = new("p", "окно " + i);
            log.Sample(_t0.AddSeconds(i), i, "full");
        }
        Assert.Equal(ActivityLog.MaxEntries, log.Entries.Count);
        Assert.True(log.Truncated);

        log.Start();
        Assert.Empty(log.Entries);
        Assert.False(log.Truncated);
    }

    // 0.14.3: домен вкладки — только при смене окна/заголовка и только в режиме full.
    [Fact]
    public void Site_probe_runs_on_change_in_full_mode_only()
    {
        int calls = 0;
        var log = new ActivityLog(() => _window, w => { calls++; return (true, w.Process == "chrome" ? "mail.example.com" : null); });
        log.Start();
        log.Sample(_t0, 0, "full");
        log.Sample(_t0.AddSeconds(2), 2, "full");            // то же окно — зонд не зовём
        Assert.Equal(1, calls);
        Assert.Equal("mail.example.com", log.Entries[0].Site);
        Assert.Contains("\"site\":\"mail.example.com\"", System.Text.Json.JsonSerializer.Serialize(log.ToJson()));

        var proc = new ActivityLog(() => _window, w => { calls++; return (true, "x.example"); });
        proc.Start();
        proc.Sample(_t0, 0, "process");
        Assert.Equal(1, calls);                               // без заголовков — и без сайта
        Assert.Null(proc.Entries[0].Site);
        Assert.DoesNotContain("site", System.Text.Json.JsonSerializer.Serialize(proc.ToJson()));
    }

    // 0.14.4: браузер не ответил вовремя — домен дописывается в ту же запись на
    // следующих опросах, пока окно то же; узнали (или «нет домена») — больше не спрашиваем.
    [Fact]
    public void Unknown_site_is_asked_again_until_known()
    {
        var answers = new Queue<(bool, string?)>(new (bool, string?)[] { (false, null), (false, null), (true, "mail.example.com") });
        int calls = 0;
        var log = new ActivityLog(() => _window, _ => { calls++; return answers.Count > 0 ? answers.Dequeue() : (true, "other.example"); });
        log.Start();
        log.Sample(_t0, 0, "full");
        Assert.Null(log.Entries.Single().Site);
        log.Sample(_t0.AddSeconds(0.5), 0.5, "full");   // чаще раза в секунду — не опрашиваем
        Assert.Equal(1, calls);
        log.Sample(_t0.AddSeconds(1), 1, "full");
        log.Sample(_t0.AddSeconds(2), 2, "full");
        Assert.Equal(3, calls);
        Assert.Equal(new ActivitySample(0, "chrome", "Почта", "mail.example.com"), log.Entries.Single());
        log.Sample(_t0.AddSeconds(3), 3, "full");
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Failing_site_probe_does_not_lose_the_entry()
    {
        var log = new ActivityLog(() => _window, _ => throw new InvalidOperationException("uia"));
        log.Start();
        log.Sample(_t0, 0, "full");
        Assert.Equal(new ActivitySample(0, "chrome", "Почта"), log.Entries.Single());
    }

    [Theory]
    [InlineData("https://mail.example.com/owa/?ae=Item", "mail.example.com")]
    [InlineData("mail.example.com/owa", "mail.example.com")]          // Chromium прячет схему
    [InlineData("www.example.com", "example.com")]
    [InlineData("http://10.0.0.5:8080/login", "10.0.0.5:8080")]
    [InlineData("10.0.0.5:8443", "10.0.0.5:8443")]
    [InlineData("localhost:5050", "localhost:5050")]
    [InlineData("HTTPS://Example.COM", "example.com")]
    [InlineData("почта.рф/входящие", "почта.рф")]
    [InlineData("edge://settings", null)]
    [InlineData("chrome://newtab", null)]
    [InlineData("about:blank", null)]
    [InlineData("file:///C:/x.pdf", null)]
    [InlineData("как оформить возврат", null)]                        // набирают поиск
    [InlineData("intranet", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Site_from_address_bar(string? value, string? expected) =>
        Assert.Equal(expected, ActivityLog.SiteFromAddress(value));
}
