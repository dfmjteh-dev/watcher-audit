using Watcher.Capture.Linux;
using Xunit;

namespace Watcher.Capture.Linux.Tests;

// H8: расписание записи и исключённые окна. Копия Watcher.Capture.Tests/RecordingPolicyTests.cs.
public class RecordingPolicyTests
{
    // 2026-10-05 — понедельник.
    private static DateTime At(int day, int h, int m = 0) => new(2026, 10, 4 + day, h, m, 0);
    private static RecordingPolicy Schedule(params ScheduleWindow[] w) => new(w.ToList(), null, null);

    [Fact]
    public void Working_hours()
    {
        var p = Schedule(new ScheduleWindow([1, 2, 3, 4, 5], "09:00", "18:00"));
        Assert.True(p.InSchedule(At(1, 9)));
        Assert.True(p.InSchedule(At(5, 17, 59)));
        Assert.False(p.InSchedule(At(1, 8, 59)));
        Assert.False(p.InSchedule(At(1, 18)));
        Assert.False(p.InSchedule(At(6, 12)));
    }

    [Fact]
    public void Night_shift_crosses_midnight_and_whole_day()
    {
        var p = Schedule(new ScheduleWindow([5], "22:00", "06:00"), new ScheduleWindow([7], "00:00", "00:00"));
        Assert.True(p.InSchedule(At(5, 23)));          // пятница вечер
        Assert.True(p.InSchedule(At(6, 5, 59)));       // утро субботы — продолжение пятницы
        Assert.False(p.InSchedule(At(6, 6)));
        Assert.False(p.InSchedule(At(5, 3)));          // утро пятницы — четверг не в расписании
        Assert.True(p.InSchedule(At(7, 0)));
        Assert.True(p.InSchedule(At(7, 23, 59)));
    }

    [Fact]
    public void No_schedule_records_always_but_broken_schedule_records_never()
    {
        Assert.True(RecordingPolicy.RecordAlways.InSchedule(At(6, 3)));
        Assert.True(Schedule().InSchedule(At(6, 3)));
        var broken = Schedule(new ScheduleWindow([9], "09:00", "18:00"), new ScheduleWindow([1], "9", "18:00"));
        Assert.Equal(2, broken.InvalidScheduleEntries);
        Assert.False(broken.InSchedule(At(1, 12)));
    }

    [Fact]
    public void Excluded_windows_by_process_and_title()
    {
        var p = new RecordingPolicy(null, ["KeePass.exe", "telegram-desktop"], ["сбербанк онлайн"]);
        Assert.Equal(PauseReason.ExcludedProcess, p.Excluded(new("KeePass", "База.kdbx")));
        Assert.Equal(PauseReason.ExcludedProcess, p.Excluded(new("keepass.exe", null)));
        Assert.Equal(PauseReason.ExcludedProcess, p.Excluded(new("telegram-deskto", "Telegram")));   // comm обрезан до 15
        Assert.Equal(PauseReason.ExcludedTitle, p.Excluded(new("chrome", "Сбербанк Онлайн — Google Chrome")));
        Assert.Equal(PauseReason.None, p.Excluded(new("chrome", "Почта")));
        Assert.Equal(PauseReason.None, p.Excluded(null));
    }

    // Агенты собраны с InvariantGlobalization (без ICU) — регистр кириллицы в
    // именах программ и заголовках должен работать и так (тестовый проект в том же режиме).
    [Fact]
    public void Cyrillic_rules_are_case_insensitive_without_icu()
    {
        Assert.True(System.Globalization.CultureInfo.CurrentCulture.Equals(System.Globalization.CultureInfo.InvariantCulture));
        var p = new RecordingPolicy(null, ["Блокнот.exe"], ["ЛИЧНАЯ ПОЧТА"]);
        Assert.Equal(PauseReason.ExcludedProcess, p.Excluded(new("БЛОКНОТ.EXE", null)));
        Assert.Equal(PauseReason.ExcludedTitle, p.Excluded(new("chrome", "личная почта — входящие")));
    }

    [Fact]
    public void Check_asks_window_only_when_needed()
    {
        int probes = 0;
        ActiveWindowInfo? Probe() { probes++; return new("KeePass", "x"); }
        Assert.Equal(PauseReason.None, RecordingPolicy.RecordAlways.Check(At(1, 12), Probe));
        Assert.Equal(0, probes);

        var p = new RecordingPolicy([new([1], "09:00", "18:00")], ["keepass"], null);
        Assert.Equal(PauseReason.Schedule, p.Check(At(1, 20), Probe));
        Assert.Equal(0, probes);
        Assert.Equal(PauseReason.ExcludedProcess, p.Check(At(1, 12), Probe));
        Assert.Equal(1, probes);
        Assert.Equal(PauseReason.None, p.Check(At(1, 12), () => throw new InvalidOperationException()));
    }

    [Fact]
    public void Built_from_config_payload()
    {
        var payload = ConfigReload.TryParse("""{"schedule":[{"days":[1],"from":"09:00","to":"10:00"}],"excludeTitles":["банк"]}""",
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var p = RecordingPolicy.From(payload);
        Assert.False(p.InSchedule(At(1, 11)));
        Assert.Equal(PauseReason.ExcludedTitle, p.Excluded(new("x", "Интернет-БАНК")));
        Assert.True(RecordingPolicy.From(null).InSchedule(At(1, 3)));
    }

    // 0.13 (п.4): писать только при этих программах; исключения — поверх.
    [Fact]
    public void Include_programs_record_only_when_active()
    {
        var p = new RecordingPolicy(null, ["keepass"], null, ["1cv8", "WINWORD.exe"], ["Банк-клиент"]);
        Assert.Equal(PauseReason.None, p.Excluded(new("1cv8", "1С:Предприятие")));
        Assert.Equal(PauseReason.None, p.Excluded(new("winword", "Договор")));
        Assert.Equal(PauseReason.None, p.Excluded(new("chrome", "Банк-клиент — вход")));
        Assert.Equal(PauseReason.NotIncluded, p.Excluded(new("chrome", "Почта")));
        Assert.Equal(PauseReason.NotIncluded, p.Excluded(null));   // окно не определить — не пишем
        var both = new RecordingPolicy(null, ["1cv8"], null, ["1cv8"], null);
        Assert.Equal(PauseReason.ExcludedProcess, both.Excluded(new("1cv8", "x")));
        Assert.Equal(PauseReason.None, new RecordingPolicy(null, null, null).Excluded(null));
    }
}

// 0.14.3: правила по домену активной вкладки и определение браузеров.
public class SiteRulesTests
{
    private static readonly ActiveWindowInfo Chrome = new("chrome", "Сбербанк Онлайн — вход", 1);

    [Theory]
    [InlineData("online.sberbank.ru", PauseReason.ExcludedSite)]
    [InlineData("sberbank.ru", PauseReason.ExcludedSite)]
    [InlineData("sberbank.ru:443", PauseReason.ExcludedSite)]
    [InlineData("notsberbank.ru", PauseReason.None)]
    [InlineData(null, PauseReason.None)]          // домен неизвестен — правило не срабатывает
    public void Exclude_site_matches_domain_and_subdomains(string? site, PauseReason expected)
    {
        var p = new RecordingPolicy(null, null, null, excludeSites: ["https://www.sberbank.ru/ru/person"]);
        Assert.Equal(expected, p.Excluded(Chrome, site));
    }

    [Fact]
    public void Include_site_is_or_with_processes_and_titles()
    {
        var p = new RecordingPolicy(null, null, null, includeProcesses: ["1cv8"], includeSites: ["*.crm.example.ru"]);
        Assert.Equal(PauseReason.None, p.Excluded(Chrome, "app.crm.example.ru"));
        Assert.Equal(PauseReason.None, p.Excluded(new("1cv8", "1С", 2), null));
        Assert.Equal(PauseReason.NotIncluded, p.Excluded(Chrome, "mail.example.ru"));
        // Linux / старый агент: домена нет — по сайту не включается.
        Assert.Equal(PauseReason.NotIncluded, p.Excluded(Chrome, null));
    }

    [Fact]
    public void Site_probe_only_when_site_rules_exist()
    {
        int calls = 0;
        string? Site(ActiveWindowInfo _) { calls++; return "hh.ru"; }
        var noSites = new RecordingPolicy(null, null, ["Зарплата"]);
        Assert.Equal(PauseReason.None, noSites.Check(DateTime.Now, () => Chrome, Site));
        Assert.Equal(0, calls);
        var sites = new RecordingPolicy(null, null, null, excludeSites: ["hh.ru"]);
        Assert.Equal(PauseReason.ExcludedSite, sites.Check(DateTime.Now, () => Chrome, Site));
        Assert.Equal(1, calls);
        Assert.Equal(PauseReason.None, sites.Check(DateTime.Now, () => Chrome, _ => throw new InvalidOperationException()));
    }

    [Theory]
    [InlineData("HTTPS://WWW.Example.COM/path?x=1", "example.com")]
    [InlineData("*.example.com", "example.com")]
    [InlineData(".example.com", "example.com")]
    [InlineData("example.com:8443", "example.com")]
    [InlineData("  example.com  ", "example.com")]
    public void Site_rule_is_normalized(string rule, string expected) =>
        Assert.Equal(expected, RecordingPolicy.NormalizeSite(rule));

    [Theory]
    [InlineData("\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\"", "chrome")]
    [InlineData("\"C:\\Users\\u\\AppData\\Local\\Yandex\\YandexBrowser\\Application\\browser.exe\" --single-argument %1", "browser")]
    [InlineData("C:\\Program Files\\Arc\\Arc.exe --x", "Arc")]
    [InlineData("C:\\Program Files\\Mozilla Firefox\\firefox.exe", "firefox")]
    [InlineData("", null)]
    [InlineData("\"unterminated", null)]
    public void Browser_process_from_registry_command(string command, string? expected) =>
        Assert.Equal(expected, BrowserNames.ProcessFromCommand(command));

    [Fact]
    public void Gecko_family_uses_firefox_address_bar() =>
        Assert.True(BrowserNames.IsGecko("LibreWolf") && !BrowserNames.IsGecko("chrome"));
}
