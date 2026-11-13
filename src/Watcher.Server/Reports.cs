namespace Watcher.Server;

// 0.13 (п.1 обратной связи 2026-10-14): отчёты — активность ПК, самые
// используемые приложения, самые активные пользователи. Чистый расчёт по
// выборке из БД (Db.Reports.cs), без I/O — проверяется тестами.
//
// «Активное время» — время в записи экрана: агент пишет, пока на экране есть
// движение (и до IdleSecondsToCloseSegment после), так что это приближение
// «за компьютером работали». У хоста — объединение интервалов всех записей
// (параллельные RDS-сессии не удваивают время), у пользователя — его записей
// на каждом хосте. Дни, часы и «рабочее время» — в зоне сервера (ServerClock;
// для расчёта по сырым данным — TzOffsetMinutes). Расчёт — ReportFacts.cs.
public sealed record ReportSegment(string Host, string? User, DateTime StartUtc, DateTime EndUtc);

public sealed record ReportActivity(string Host, string? User, string Process, DateTime StartUtc, double Seconds, string? Title = null, string? Site = null);

public sealed record ReportOptions(DateTime FromUtc, DateTime ToUtc, int TzOffsetMinutes, bool WorkHoursOnly);

public sealed record HostActivity(string Host, double ActiveSec, int ActiveDays, DateTime? LastActiveUtc, IReadOnlyDictionary<string, double> PerDay);

public sealed record AppUsage(string Process, double Seconds, int Hosts, int Users);

// 0.14.3: что открывали — сайты, документы, RDP-серверы (AppKinds). Variants —
// сколько разных заголовков слилось (отличались только цифрами).
public sealed record SubjectUsage(string Kind, string Subject, string Example, int Variants, double Seconds, int Hosts, int Users);

public sealed record UserActivity(string User, double ActiveSec, int Hosts, IReadOnlyList<string> TopHosts, int Segments, IReadOnlyList<AppUsage> TopApps);

// BrowserNoSiteSec (0.14.4) — время в браузерах, для которого агент не прислал
// домен (агент до 0.14.3, Linux, браузер не ответил): в «Сайты» не попадает.
// PerHour — активное время по часам в зоне сервера, ключ «yyyy-MM-ddTHH» (лента
// активности в отчёте; заменила тепловую карту «день недели × час»).
public sealed record ActivityReport(double TotalActiveSec, IReadOnlyList<HostActivity> Hosts, IReadOnlyDictionary<string, double> PerHour,
    IReadOnlyDictionary<string, double> PerDay, IReadOnlyList<AppUsage> Apps, IReadOnlyList<UserActivity> Users,
    IReadOnlyList<SubjectUsage> Subjects, double BrowserNoSiteSec = 0);

public static class Reports
{
    public static readonly TimeSpan WorkFrom = TimeSpan.FromHours(9), WorkTo = TimeSpan.FromHours(18);

    // Отчёт по сырым данным: факты за окно периода (ReportFacts.Compute) и сборка
    // из них — тот же путь, что у отчёта из заготовок в БД.
    public static ActivityReport Build(IReadOnlyList<ReportSegment> segments, IReadOnlyList<ReportActivity> activity, ReportOptions o,
        AppKinds.Map? kinds = null)
    {
        var offset = TimeSpan.FromMinutes(o.TzOffsetMinutes);
        var facts = ReportFacts.Compute(segments, activity, o.FromUtc + offset, o.ToUtc + offset, offset, kinds ?? new AppKinds.Map(null));
        return ReportFacts.Aggregate(facts, o.WorkHoursOnly);
    }

    // Рабочее время — пн–пт 9:00–18:00 (местное время сервера).
    internal static List<(DateTime S, DateTime E)> Clip(DateTime s, DateTime e, bool workHoursOnly)
    {
        if (!workHoursOnly) return [(s, e)];
        var list = new List<(DateTime, DateTime)>();
        for (var day = s.Date; day < e; day = day.AddDays(1))
        {
            if (IsoDay(day) > 5) continue;
            var ws = Max(s, day + WorkFrom);
            var we = Min(e, day + WorkTo);
            if (we > ws) list.Add((ws, we));
        }
        return list;
    }

    internal static List<(DateTime S, DateTime E)> Merge(IEnumerable<(DateTime S, DateTime E)> intervals)
    {
        var list = new List<(DateTime S, DateTime E)>();
        foreach (var (s, e) in intervals.OrderBy(i => i.S))
        {
            if (list.Count > 0 && s <= list[^1].E) list[^1] = (list[^1].S, Max(list[^1].E, e));
            else list.Add((s, e));
        }
        return list;
    }

    private static int IsoDay(DateTime d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
