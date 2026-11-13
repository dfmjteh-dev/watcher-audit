using System.Globalization;

namespace Watcher.Server;

// 0.13 (п.1): отчёты. Политика Recordings (admin/viewer): кто чем пользовался —
// не здоровье парка, а содержимое; viewer — в своих группах. В журнал доступа.
public static class ReportEndpoints
{
    public const int MaxDays = 366;

    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        // from/to — даты (yyyy-MM-dd, to включительно) — сутки в зоне сервера.
        // 0.14.4: отчёт складывается из заготовок (ReportFactsService, раз в 10 мин);
        // computedAt — когда они последний раз обновлялись, pending — сколько
        // «хост × сутки» ещё ждут пересчёта (после обновления сервера — все).
        app.MapGet("/api/reports/activity", (HttpContext ctx, Db db, AuditLog audit, ReportFactsService facts, string from, string to,
            long? groupId = null, string? host = null, string? user = null, bool workHours = false, string? process = null) =>
        {
            if (!DateTime.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ||
                !DateTime.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) || t < f || f.Year < 1971 || t.Year > 9997)
                return Results.BadRequest(new { error = "from/to: yyyy-MM-dd, from <= to" });
            if ((t - f).TotalDays >= MaxDays) return Results.BadRequest(new { error = $"period: at most {MaxDays} days" });
            if (host is not null && !ViewerScope.CanSeeHost(ctx, db, host)) return Results.Forbid();

            facts.RefreshForReport(from, to, TimeSpan.FromSeconds(2));
            var allowed = ViewerScope.AllowedGroupIds(ctx, db);
            string? u = string.IsNullOrWhiteSpace(user) ? null : user.Trim();
            var source = db.LoadReportFacts(from, to, allowed, groupId, host, u);
            if (!string.IsNullOrEmpty(process)) source = source with { Apps = source.Apps.Where(a => string.Equals(a.Process, process, StringComparison.OrdinalIgnoreCase)).ToList(), Subjects = [] };
            var report = ReportFacts.Aggregate(source, workHours, u);
            double Seconds(AppDayFact a) => workHours ? a.SecWork : a.Sec;
            var appAccounts = source.Apps.GroupBy(a => (a.Host, User: a.User.ToUpperInvariant())).ToDictionary(g => g.Key, g => g.ToList());
            var accounts = source.UserHours.Where(a => !workHours || ReportFacts.IsWorkHour(a.Hour))
                .GroupBy(a => (a.Host, User: a.User.ToUpperInvariant())).Select(g =>
                {
                    var apps = appAccounts.GetValueOrDefault(g.Key) ?? [];
                    return new
                    {
                        user = g.First().User,
                        accountHost = g.Key.Host,
                        activeSec = g.Sum(a => a.ActiveSec),
                        windowSec = apps.Sum(Seconds),
                        hosts = 1,
                        topHosts = new[] { g.Key.Host },
                        segments = g.Sum(a => a.Segments),
                        topApps = apps.GroupBy(a => a.Process, StringComparer.OrdinalIgnoreCase).Select(a => new { process = a.First().Process, seconds = a.Sum(Seconds) }).OrderByDescending(a => a.seconds).Take(3).ToList()
                    };
                }).Where(a => string.IsNullOrEmpty(process) || a.windowSec > 0).OrderByDescending(a => a.activeSec).Take(1001).ToList();
            var state = facts.LoadState();
            audit.Write(ctx, "report.activity", host is null ? null : $"host:{host}", new { from, to, groupId, user, workHours });
            return Results.Ok(new
            {
                from,
                to,
                workHours,
                truncated = false,
                timeZone = ServerClock.ZoneId,
                computedAt = state?.ComputedAt?.ToString("o"),
                pending = db.ReportDirtyCount(from, to, allowed, groupId, host),
                process,
                accounts = accounts.Take(1000),
                accountsTruncated = accounts.Count > 1000,
                coverage = "unknown",
                // Подсказки поля «Пользователь» — все за период, без фильтра по пользователю.
                knownUsers = db.ReportUsers(from, to, allowed, groupId, host),
                totalActiveSec = Math.Round(report.TotalActiveSec),
                perHour = report.PerHour.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value)),
                perDay = report.PerDay.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value)),
                hosts = report.Hosts.Select(h => new
                {
                    h.Host,
                    activeSec = Math.Round(h.ActiveSec),
                    h.ActiveDays,
                    lastActive = h.LastActiveUtc?.ToString("o"),
                    perDay = h.PerDay.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value)),
                }),
                apps = report.Apps.Select(a => new { a.Process, seconds = Math.Round(a.Seconds), a.Hosts, a.Users, accounts = source.Apps.Where(x => x.User.Length > 0 && Seconds(x) > 0 && string.Equals(x.Process, a.Process, StringComparison.OrdinalIgnoreCase)).Select(x => (x.Host, User: x.User.ToUpperInvariant())).Distinct().Count() }),
                browserNoSiteSec = Math.Round(report.BrowserNoSiteSec),
                subjects = report.Subjects.Select(s => new { s.Kind, s.Subject, s.Example, s.Variants, seconds = Math.Round(s.Seconds), s.Hosts, s.Users }),
                users = report.Users.Select(u => new
                {
                    u.User,
                    activeSec = Math.Round(u.ActiveSec),
                    u.Hosts,
                    u.TopHosts,
                    u.Segments,
                    topApps = u.TopApps.Select(a => new { a.Process, seconds = Math.Round(a.Seconds) }),
                }),
            });
        }).RequireAuthorization("Recordings");
    }
}
