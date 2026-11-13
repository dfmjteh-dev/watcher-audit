using Xunit;

namespace Watcher.Server.Tests;

// 0.13 (п.1): отчёты — активность ПК, приложения, пользователи.
public class ReportsTests
{
    private static readonly DateTime Mon = new(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);   // понедельник
    private static ReportOptions Opt(int tz = 0, bool work = false) => new(Mon, Mon.AddDays(7), tz, work);
    private static ReportSegment Seg(string host, string? user, double fromH, double toH) => new(host, user, Mon.AddHours(fromH), Mon.AddHours(toH));

    [Fact]
    public void Parallel_rds_sessions_do_not_double_host_time_but_count_per_user()
    {
        var r = Reports.Build([Seg("RDS", "ivan", 10, 12), Seg("RDS", "petr", 11, 13), Seg("PC", "ivan", 10, 11)], [], Opt());
        var rds = r.Hosts.Single(h => h.Host == "RDS");
        Assert.Equal(3 * 3600, rds.ActiveSec);                       // 10–13, не 4 ч
        Assert.Equal(4 * 3600, r.TotalActiveSec);                    // + PC 1 ч
        var ivan = r.Users.Single(u => u.User == "ivan");
        Assert.Equal(3 * 3600, ivan.ActiveSec);                      // 2 ч RDS + 1 ч PC
        Assert.Equal(2, ivan.Hosts);
        Assert.Equal("RDS", ivan.TopHosts[0]);
        Assert.Equal(2 * 3600, r.Users.Single(u => u.User == "petr").ActiveSec);
        Assert.Equal(2 * 3600, r.PerHour["2026-10-12T10"]);          // пн 10:00 — RDS 1 ч + PC 1 ч
    }

    [Fact]
    public void Work_hours_and_timezone()
    {
        // 07:00–20:00 UTC понедельник; МСК (+3) — 10:00–23:00, в рабочее 10–18 = 8 ч.
        var r = Reports.Build([Seg("PC", "u", 7, 20)], [], Opt(tz: 180, work: true));
        Assert.Equal(8 * 3600, r.TotalActiveSec);
        Assert.Equal(3600, r.PerHour["2026-10-12T10"]);
        Assert.False(r.PerHour.ContainsKey("2026-10-12T19"));       // вне рабочего времени — нет точки
        // Суббота не рабочая.
        var sat = Reports.Build([Seg("PC", "u", 5 * 24 + 10, 5 * 24 + 12)], [], Opt(work: true));
        Assert.Equal(0, sat.TotalActiveSec);
        Assert.Empty(sat.Hosts);
    }

    [Fact]
    public void Day_split_across_midnight_in_viewer_zone()
    {
        // 20:00–23:00 UTC пн = 23:00 пн – 02:00 вт по МСК.
        var r = Reports.Build([Seg("PC", "u", 20, 23)], [], Opt(tz: 180));
        Assert.Equal(3600, r.PerDay["2026-10-12"]);
        Assert.Equal(2 * 3600, r.PerDay["2026-10-13"]);
        Assert.Equal(2, r.Hosts[0].ActiveDays);
        Assert.Equal(3600, r.PerHour["2026-10-13T01"]);              // вт 01:00 по МСК
    }

    [Fact]
    public void Apps_by_time_with_hosts_and_users()
    {
        var acts = new List<ReportActivity>
        {
            new("PC1", "ivan", "1cv8", Mon.AddHours(10), 3600),
            new("PC2", "petr", "1cv8", Mon.AddHours(10), 1800),
            new("PC1", "ivan", "chrome", Mon.AddHours(11), 600),
            new("PC1", "ivan", "1cv8", Mon.AddDays(-1), 9999),           // вне периода
        };
        var r = Reports.Build([Seg("PC1", "ivan", 10, 12), Seg("PC2", "petr", 10, 11)], acts, Opt());
        var c1 = r.Apps[0];
        Assert.Equal(("1cv8", 5400.0, 2, 2), (c1.Process, c1.Seconds, c1.Hosts, c1.Users));
        Assert.Equal("chrome", r.Apps[1].Process);
        Assert.Equal("1cv8", r.Users.Single(u => u.User == "ivan").TopApps[0].Process);
    }

    // 0.14.3: «Сайты и документы» по типу программы; служебные окна — нигде.
    [Fact]
    public void Subjects_by_app_kind_and_ignored_windows_dropped()
    {
        var acts = new List<ReportActivity>
        {
            new("PC1", "ivan", "chrome", Mon.AddHours(10), 600, "(3) Входящие - Google Chrome", "mail.example.com"),
            new("PC2", "petr", "msedge", Mon.AddHours(10), 300, "Входящие и еще 2 страницы — Личный: Microsoft Edge", "mail.example.com"),
            new("PC1", "ivan", "chrome", Mon.AddHours(11), 120, "(3) Лиды - Google Chrome"),
            new("PC1", "ivan", "chrome", Mon.AddHours(11.1), 60, "(5) Лиды - Google Chrome"),
            new("PC1", "ivan", "EXCEL", Mon.AddHours(11.2), 200, "План  [Только для чтения] - Excel"),
            new("PC1", "ivan", "1cv8c", Mon.AddHours(11.3), 900, "Отель - 17.10.2026 - Управление"),
            new("PC1", "ivan", "rdpinit", Mon.AddHours(11.6), 999, "RemoteApp Marker Window"),
        };
        var r = Reports.Build([Seg("PC1", "ivan", 10, 12), Seg("PC2", "petr", 10, 11)], acts, Opt());

        var site = r.Subjects[0];
        Assert.Equal(("browser", "mail.example.com", 900.0, 2, 2), (site.Kind, site.Subject, site.Seconds, site.Hosts, site.Users));
        // 0.14.4: браузер без домена — не «сайт» по заголовку, а общая сумма.
        Assert.DoesNotContain(r.Subjects, s => s.Subject.Contains("Лиды"));
        Assert.Equal(180.0, r.BrowserNoSiteSec);
        Assert.Contains(r.Subjects, s => s is { Kind: "document", Subject: "План" });
        Assert.DoesNotContain(r.Subjects, s => s.Subject.Contains("Отель"));   // 1С — программа, заголовок не нужен
        Assert.DoesNotContain(r.Apps, a => a.Process == "rdpinit");
        Assert.Contains(r.Apps, a => a.Process == "1cv8c");
    }

    // 0.14.3: незнакомый процесс, для которого агент прислал домен, — браузер.
    [Fact]
    public void Unknown_process_with_site_counts_as_browser()
    {
        var acts = new List<ReportActivity> { new("PC1", "ivan", "arc", Mon.AddHours(10), 300, "Почта", "mail.example.com") };
        var r = Reports.Build([Seg("PC1", "ivan", 10, 11)], acts, Opt());
        Assert.Equal(("browser", "mail.example.com"), (r.Subjects.Single().Kind, r.Subjects.Single().Subject));
    }
}
