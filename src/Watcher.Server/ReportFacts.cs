namespace Watcher.Server;

// 0.14.4: отчёты из заготовок. Раньше каждый запрос отчёта читал все сегменты и
// смены окон за период (на проде 7 дней — сотни тысяч строк, секунды) и считал
// заново, а поле «Пользователь» пересчитывало отчёт на каждую букву. Теперь
// служба ReportFactsService раскладывает записи в «факты» (Compute) — активность
// хоста и пользователя по часам, программы и «что открывали» по дням с отдельной
// суммой за рабочее время — и хранит их в БД; запрос отчёта только складывает
// готовые строки (Aggregate). Часы и сутки — в зоне сервера (ServerClock).
//
// Reports.Build (расчёт по сырым данным) остаётся тем же API: внутри он делает
// Compute + Aggregate, и прежние тесты проверяют, что из фактов получается тот же
// отчёт, что считался напрямую.

// Hour/Day — местное время сервера (Kind Unspecified), начало часа/суток.
public sealed record HostHourFact(string Host, DateTime Hour, double ActiveSec, DateTime LastEndUtc);

public sealed record UserHourFact(string Host, string User, DateTime Hour, double ActiveSec, int Segments, DateTime LastEndUtc);

public sealed record AppDayFact(string Host, string User, DateTime Day, string Process, double Sec, double SecWork, double NoSite, double NoSiteWork);

public sealed record SubjectDayFact(string Host, string User, DateTime Day, string Kind, string Key, string Variant, double Sec, double SecWork);

public sealed record ReportFactSet(List<HostHourFact> HostHours, List<UserHourFact> UserHours, List<AppDayFact> Apps, List<SubjectDayFact> Subjects)
{
    public static ReportFactSet Empty() => new([], [], [], []);
}

public static class ReportFacts
{
    // Факты за местное окно [fromLocal, toLocal) (целые часы). offset — смещение
    // зоны сервера (местное = UTC + offset). Сегменты и смены окон — уже отобраны
    // по хостам и пересекают окно.
    public static ReportFactSet Compute(IReadOnlyList<ReportSegment> segments, IReadOnlyList<ReportActivity> activity,
        DateTime fromLocal, DateTime toLocal, TimeSpan offset, AppKinds.Map kinds)
    {
        var set = ReportFactSet.Empty();
        (DateTime S, DateTime E)? Local(DateTime su, DateTime eu)
        {
            var s = Max(su + offset, fromLocal);
            var e = Min(eu + offset, toLocal);
            return e > s ? (s, e) : null;
        }
        List<(DateTime S, DateTime E)> Locals(IEnumerable<ReportSegment> segs) =>
            segs.Select(s => Local(s.StartUtc, s.EndUtc)).Where(x => x is not null).Select(x => x!.Value).ToList();

        foreach (var host in segments.GroupBy(s => s.Host))
        {
            var merged = Reports.Merge(Locals(host));
            foreach (var (hour, sec, last) in ByHour(merged))
                set.HostHours.Add(new HostHourFact(host.Key, hour, sec, last - offset));

            foreach (var user in host.Where(s => !string.IsNullOrEmpty(s.User)).GroupBy(s => s.User!, StringComparer.OrdinalIgnoreCase))
            {
                var mergedUser = Reports.Merge(Locals(user));
                var hours = ByHour(mergedUser).ToDictionary(h => h.Hour);
                // Сегмент считается в часе, где он начался (или в первом часе окна).
                var counts = Locals(user)
                    .GroupBy(x => HourOf(x.S)).ToDictionary(g => g.Key, g => g.Count());
                foreach (var hour in hours.Keys.Union(counts.Keys).Order())
                {
                    var h = hours.GetValueOrDefault(hour);
                    set.UserHours.Add(new UserHourFact(host.Key, user.Key, hour, h.Sec, counts.GetValueOrDefault(hour),
                        (h.Last == default ? hour : h.Last) - offset));
                }
            }
        }

        var apps = new Dictionary<(string Host, string User, DateTime Day, string Process), double[]>(new AppKeyComparer());
        var subjects = new Dictionary<(string Host, string User, DateTime Day, string Kind, string Key, string Variant), double[]>();
        foreach (var a in activity)
        {
            if (Local(a.StartUtc, a.StartUtc.AddSeconds(Math.Max(0, a.Seconds))) is not { } iv) continue;
            var kind = kinds.KindOf(a.Process, !string.IsNullOrEmpty(a.Site));
            if (kind == AppKind.Ignore) continue;   // оболочка Windows, RemoteApp — не работа пользователя
            string user = a.User ?? "";
            bool noSite = kind == AppKind.Browser && string.IsNullOrEmpty(a.Site);
            string? subject = kind != AppKind.App ? AppKinds.Subject(kind, a.Process, a.Title, a.Site) : null;
            string? key = subject is null ? null
                : AppKinds.DigitsAreNoise(kind, a.Site) ? WindowTitles.Key(subject) : subject.ToLowerInvariant();
            foreach (var (day, sec, work) in ByDay(iv.S, iv.E))
            {
                var acc = apps.TryGetValue((a.Host, user, day, a.Process), out var x) ? x : apps[(a.Host, user, day, a.Process)] = new double[4];
                acc[0] += sec;
                acc[1] += work;
                if (noSite) { acc[2] += sec; acc[3] += work; }
                if (subject is not null)
                {
                    var sk = (a.Host, user, day, AppKinds.Names[kind], key!, subject);
                    var s = subjects.TryGetValue(sk, out var y) ? y : subjects[sk] = new double[2];
                    s[0] += sec;
                    s[1] += work;
                }
            }
        }
        foreach (var (k, v) in apps) set.Apps.Add(new AppDayFact(k.Host, k.User, k.Day, k.Process, v[0], v[1], v[2], v[3]));
        foreach (var (k, v) in subjects) set.Subjects.Add(new SubjectDayFact(k.Host, k.User, k.Day, k.Kind, k.Key, k.Variant, v[0], v[1]));
        return set;
    }

    // Отчёт из фактов. user — фильтр «Пользователь» (без регистра): активность
    // хоста тогда — только его записи.
    public static ActivityReport Aggregate(ReportFactSet f, bool workHoursOnly, string? user = null)
    {
        bool U(string u) => user is null || string.Equals(u, user, StringComparison.OrdinalIgnoreCase);
        bool Work(DateTime hour) => !workHoursOnly || IsWorkHour(hour);

        var hostHours = user is null
            ? f.HostHours.Where(h => Work(h.Hour)).Select(h => (h.Host, h.Hour, h.ActiveSec, h.LastEndUtc))
            : f.UserHours.Where(h => U(h.User) && Work(h.Hour)).Select(h => (h.Host, h.Hour, h.ActiveSec, h.LastEndUtc));

        var perHour = new SortedDictionary<string, double>(StringComparer.Ordinal);
        var perDayAll = new SortedDictionary<string, double>(StringComparer.Ordinal);
        var hosts = new List<HostActivity>();
        double total = 0;
        foreach (var g in hostHours.Where(h => h.ActiveSec > 0).GroupBy(h => h.Host))
        {
            var perDay = new SortedDictionary<string, double>(StringComparer.Ordinal);
            foreach (var h in g)
            {
                string hour = h.Hour.ToString("yyyy-MM-dd'T'HH", System.Globalization.CultureInfo.InvariantCulture);
                perHour[hour] = perHour.GetValueOrDefault(hour) + h.ActiveSec;
                string day = h.Hour.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                perDay[day] = perDay.GetValueOrDefault(day) + h.ActiveSec;
                perDayAll[day] = perDayAll.GetValueOrDefault(day) + h.ActiveSec;
            }
            double active = g.Sum(h => h.ActiveSec);
            total += active;
            hosts.Add(new HostActivity(g.Key, active, perDay.Count, g.Max(h => h.LastEndUtc), perDay));
        }

        double Sec(AppDayFact a) => workHoursOnly ? a.SecWork : a.Sec;
        var appRows = f.Apps.Where(a => U(a.User) && Sec(a) > 0).ToList();
        var apps = appRows.GroupBy(a => a.Process, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppUsage(g.First().Process, g.Sum(Sec), g.Select(a => a.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                g.Where(a => a.User.Length > 0).Select(a => a.User).Distinct(StringComparer.OrdinalIgnoreCase).Count()))
            .OrderByDescending(a => a.Seconds).Take(100).ToList();
        double noSite = f.Apps.Where(a => U(a.User)).Sum(a => workHoursOnly ? a.NoSiteWork : a.NoSite);

        // Пользователи: его время на каждом хосте — сумма часов, хосты — сумма.
        // Число сегментов — без учёта «рабочего времени», как и раньше.
        var users = f.UserHours.Where(h => U(h.User)).GroupBy(h => h.User, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var byHost = g.Where(h => Work(h.Hour)).GroupBy(h => h.Host)
                    .Select(h => (Host: h.Key, Sec: h.Sum(x => x.ActiveSec))).Where(h => h.Sec > 0).OrderByDescending(h => h.Sec).ToList();
                var top = appRows.Where(a => string.Equals(a.User, g.Key, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(a => a.Process, StringComparer.OrdinalIgnoreCase)
                    .Select(a => (Process: a.First().Process, Sec: a.Sum(Sec))).OrderByDescending(a => a.Sec).Take(3)
                    .Select(a => new AppUsage(a.Process, a.Sec, 1, 1)).ToList();
                return new UserActivity(g.First().User, byHost.Sum(h => h.Sec), byHost.Count, byHost.Take(3).Select(h => h.Host).ToList(), g.Sum(h => h.Segments), top);
            })
            .Where(u => u.ActiveSec > 0).OrderByDescending(u => u.ActiveSec).Take(200).ToList();

        // «Что открывали»: подпись группы — постоянная часть (WindowTitles.Display);
        // группы с одинаковой подписью — одна строка.
        double SubSec(SubjectDayFact s) => workHoursOnly ? s.SecWork : s.Sec;
        var subjects = f.Subjects.Where(s => U(s.User) && SubSec(s) > 0).GroupBy(s => (s.Kind, s.Key))
            .Select(g =>
            {
                var variants = g.GroupBy(s => s.Variant, StringComparer.Ordinal).ToDictionary(v => v.Key, v => v.Sum(SubSec), StringComparer.Ordinal);
                string topVariant = variants.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.Ordinal).First().Key;
                return (g.Key.Kind, Rows: g.ToList(), Variants: variants, Top: topVariant, Display: WindowTitles.Display(variants.Keys.ToList(), topVariant));
            })
            .GroupBy(x => (x.Kind, x.Display.ToLowerInvariant()))
            .Select(g =>
            {
                var variants = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var x in g) foreach (var (v, sec) in x.Variants) variants[v] = variants.GetValueOrDefault(v) + sec;
                var rows = g.SelectMany(x => x.Rows).ToList();
                string top = variants.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.Ordinal).First().Key;
                string display = g.MaxBy(x => x.Variants.Values.Sum())!.Display;
                return new SubjectUsage(g.Key.Kind, display, top, variants.Count, variants.Values.Sum(),
                    rows.Select(r => r.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    rows.Where(r => r.User.Length > 0).Select(r => r.User).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            })
            .OrderByDescending(s => s.Seconds).Take(500).ToList();

        return new ActivityReport(total, hosts.OrderByDescending(h => h.ActiveSec).ToList(), perHour, perDayAll, apps, users, subjects, noSite);
    }

    // Рабочее время — пн–пт 9:00–18:00 местного времени сервера.
    internal static bool IsWorkHour(DateTime hour) =>
        IsoDay(hour) <= 5 && hour.TimeOfDay >= Reports.WorkFrom && hour.TimeOfDay < Reports.WorkTo;

    internal static DateTime HourOf(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind);

    // Слитые интервалы → (час, секунд в нём, конец последнего интервала в часе).
    private static IEnumerable<(DateTime Hour, double Sec, DateTime Last)> ByHour(List<(DateTime S, DateTime E)> merged)
    {
        var acc = new SortedDictionary<DateTime, (double Sec, DateTime Last)>();
        foreach (var (s, e) in merged)
            for (var h = HourOf(s); h < e; h = h.AddHours(1))
            {
                var hs = Max(s, h);
                var he = Min(e, h.AddHours(1));
                if (he <= hs) continue;
                var cur = acc.GetValueOrDefault(h);
                acc[h] = (cur.Sec + (he - hs).TotalSeconds, Max(cur.Last, he));
            }
        return acc.Select(kv => (kv.Key, kv.Value.Sec, kv.Value.Last));
    }

    // Интервал → (сутки, секунд в них, из них в рабочее время).
    private static IEnumerable<(DateTime Day, double Sec, double Work)> ByDay(DateTime s, DateTime e)
    {
        for (var day = s.Date; day < e; day = day.AddDays(1))
        {
            var ds = Max(s, day);
            var de = Min(e, day.AddDays(1));
            if (de <= ds) continue;
            double work = Reports.Clip(ds, de, workHoursOnly: true).Sum(x => (x.E - x.S).TotalSeconds);
            yield return (day, (de - ds).TotalSeconds, work);
        }
    }

    private static int IsoDay(DateTime d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    // Программа — без регистра (msedge и MSEDGE — одна), остальное — точно.
    private sealed class AppKeyComparer : IEqualityComparer<(string Host, string User, DateTime Day, string Process)>
    {
        public bool Equals((string Host, string User, DateTime Day, string Process) a, (string Host, string User, DateTime Day, string Process) b) =>
            a.Host == b.Host && a.User == b.User && a.Day == b.Day && string.Equals(a.Process, b.Process, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Host, string User, DateTime Day, string Process) k) =>
            HashCode.Combine(k.Host, k.User, k.Day, StringComparer.OrdinalIgnoreCase.GetHashCode(k.Process));
    }
}
