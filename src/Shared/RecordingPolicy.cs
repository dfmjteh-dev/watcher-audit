using System.Globalization;

namespace Watcher.Capture;

// H8 (roadmap-next.md): минимизация данных — когда и что НЕ записывать. Из
// конфига хоста/группы (тот же блоб, что allowUsers/excludeUsers):
//   schedule         — окна записи по дням недели и времени станции; нет или
//                      пусто — запись всегда;
//   excludeProcesses — программы на переднем плане, при которых запись на паузе
//                      (имя процесса без учёта регистра, «.exe» необязателен);
//   excludeTitles    — то же по подстроке заголовка окна;
//   excludeSites     — (0.14.3) то же по домену активной вкладки браузера: домен
//                      или его поддомен («sberbank.ru» — и «online.sberbank.ru»).
//                      Домен знает только Windows-агент (BrowserSite); первые
//                      доли секунды нового окна браузера, пока домен ещё не
//                      прочитан, правило не срабатывает — для строгого исключения
//                      добавьте и правило по заголовку.
// Пауза — кадры не снимаются, сегмент закрывается, pre-roll очищается: ни
// кадров исключённого окна, ни его имени в активности (H9) не остаётся.
// Один файл на оба агента захвата (src/Shared, подключается ссылкой в
// Watcher.Capture и Watcher.Capture.Linux); те же правила проверяет сервер
// (AgentConfigValidation).
public sealed record ScheduleWindow(List<int>? Days, string? From, string? To);

// NotIncluded (0.13) — задан «писать только при этих программах», а на переднем
// плане другая (или окно не определить).
public enum PauseReason { None, Schedule, ExcludedProcess, ExcludedTitle, NotIncluded, ExcludedSite }

public sealed class RecordingPolicy
{
    public static readonly RecordingPolicy RecordAlways = new(null, null, null);

    // Linux: /proc/<pid>/comm обрезан до 15 символов.
    private const int CommLength = 15;

    private readonly List<(HashSet<int> Days, TimeSpan From, TimeSpan To)> _schedule = new();
    private readonly List<string> _processes;
    private readonly List<string> _titles;
    // 0.13 (п.4): обратное исключениям — писать ТОЛЬКО когда на переднем плане
    // одна из этих программ (или заголовок содержит подстроку). Исключения
    // действуют поверх: включённое, но исключённое окно не пишется.
    private readonly List<string> _includeProcesses;
    private readonly List<string> _includeTitles;
    private readonly List<string> _sites, _includeSites;

    public int InvalidScheduleEntries { get; }
    public bool HasIncludes => _includeProcesses.Count > 0 || _includeTitles.Count > 0 || _includeSites.Count > 0;
    public bool HasExclusions => _processes.Count > 0 || _titles.Count > 0 || _sites.Count > 0 || HasIncludes;
    public bool HasSiteRules => _sites.Count > 0 || _includeSites.Count > 0;

    public RecordingPolicy(List<ScheduleWindow>? schedule, List<string>? excludeProcesses, List<string>? excludeTitles,
        List<string>? includeProcesses = null, List<string>? includeTitles = null,
        List<string>? excludeSites = null, List<string>? includeSites = null)
    {
        foreach (var w in schedule ?? new())
        {
            if (Parse(w) is { } p) _schedule.Add(p);
            else InvalidScheduleEntries++;
        }
        // Расписание было задано, но ни одно окно не разобралось — не писать
        // совсем: «записывать всегда» по ошибке хуже, чем пропуск записи.
        if (schedule is { Count: > 0 } && _schedule.Count == 0) _schedule.Add((new HashSet<int>(), TimeSpan.Zero, TimeSpan.Zero));
        _processes = (excludeProcesses ?? new()).Select(NormalizeProcess).Where(p => p.Length > 0).ToList();
        _titles = (excludeTitles ?? new()).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        _includeProcesses = (includeProcesses ?? new()).Select(NormalizeProcess).Where(p => p.Length > 0).ToList();
        _includeTitles = (includeTitles ?? new()).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        _sites = (excludeSites ?? new()).Select(NormalizeSite).Where(s => s.Length > 0).ToList();
        _includeSites = (includeSites ?? new()).Select(NormalizeSite).Where(s => s.Length > 0).ToList();
    }

    public static RecordingPolicy From(CapturedConfigPayload? payload) =>
        payload is null ? RecordAlways : new RecordingPolicy(payload.Schedule, payload.ExcludeProcesses, payload.ExcludeTitles,
            payload.IncludeProcesses, payload.IncludeTitles, payload.ExcludeSites, payload.IncludeSites);

    // localNow — часы станции: «рабочие часы» — местное время офиса.
    public bool InSchedule(DateTime localNow)
    {
        if (_schedule.Count == 0) return true;
        int today = IsoDay(localNow.DayOfWeek);
        int yesterday = today == 1 ? 7 : today - 1;
        var t = localNow.TimeOfDay;
        foreach (var (days, from, to) in _schedule)
        {
            if (from < to)
            {
                if (days.Contains(today) && t >= from && t < to) return true;
            }
            else if (from > to)
            {
                // Через полночь: 22:00–06:00 «пн» — с вечера понедельника до утра вторника.
                if ((days.Contains(today) && t >= from) || (days.Contains(yesterday) && t < to)) return true;
            }
            else if (days.Contains(today) && from == TimeSpan.Zero)
            {
                return true;   // 00:00–00:00 — весь день
            }
        }
        return false;
    }

    // site — домен активной вкладки (null — не браузер или домен неизвестен).
    public PauseReason Excluded(ActiveWindowInfo? window, string? site = null)
    {
        // Окно не определить — при «только эти программы» не пишем: записать
        // лишнее хуже (тот же принцип, что у битого расписания).
        if (window is null) return HasIncludes ? PauseReason.NotIncluded : PauseReason.None;
        if (!HasExclusions) return PauseReason.None;
        if (HasIncludes && !MatchesProcess(window.Process, _includeProcesses) && !MatchesTitle(window.Title, _includeTitles)
            && !MatchesSite(site, _includeSites))
            return PauseReason.NotIncluded;
        if (MatchesProcess(window.Process, _processes)) return PauseReason.ExcludedProcess;
        if (MatchesTitle(window.Title, _titles)) return PauseReason.ExcludedTitle;
        if (MatchesSite(site, _sites)) return PauseReason.ExcludedSite;
        return PauseReason.None;
    }

    // Домен или поддомен: правило «sberbank.ru» — и «online.sberbank.ru», но не «notsberbank.ru».
    private static bool MatchesSite(string? site, List<string> rules)
    {
        if (string.IsNullOrEmpty(site) || rules.Count == 0) return false;
        string host = site.ToLowerInvariant();
        int colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(']') < colon) host = host[..colon];   // порт не важен
        foreach (var rule in rules)
            if (host == rule || host.EndsWith("." + rule, StringComparison.Ordinal))
                return true;
        return false;
    }

    // Правило как его вписали: «https://www.sberbank.ru/x», «*.sberbank.ru», «.sberbank.ru» → «sberbank.ru».
    public static string NormalizeSite(string s)
    {
        s = s.Trim().ToLowerInvariant();
        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        s = s.TrimStart('*', '.');
        if (s.StartsWith("www.", StringComparison.Ordinal)) s = s[4..];
        int colon = s.LastIndexOf(':');
        if (colon > 0) s = s[..colon];
        return s;
    }

    private static bool MatchesProcess(string? proc, List<string> rules)
    {
        if (proc is null || rules.Count == 0) return false;
        string p = NormalizeProcess(proc);
        foreach (var rule in rules)
            if (p == rule || (p.Length == CommLength && rule.StartsWith(p, StringComparison.Ordinal)))
                return true;
        return false;
    }

    private static bool MatchesTitle(string? title, List<string> rules)
    {
        if (title is null) return false;
        foreach (var rule in rules)
            if (title.Contains(rule, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Окно активности дешёвое, но если исключений нет — не спрашиваем его вовсе;
    // домен вкладки — только если есть правила по сайтам.
    public PauseReason Check(DateTime localNow, Func<ActiveWindowInfo?> probe, Func<ActiveWindowInfo, string?>? site = null)
    {
        if (!InSchedule(localNow)) return PauseReason.Schedule;
        if (!HasExclusions) return PauseReason.None;
        ActiveWindowInfo? w;
        try { w = probe(); }
        catch (Exception) { w = null; }
        string? s = null;
        if (w is not null && HasSiteRules && site is not null)
        {
            try { s = site(w); }
            catch (Exception) { s = null; }
        }
        return Excluded(w, s);
    }

    private static string NormalizeProcess(string s)
    {
        s = s.Trim().ToLowerInvariant();
        return s.EndsWith(".exe", StringComparison.Ordinal) ? s[..^4] : s;
    }

    private static int IsoDay(DayOfWeek d) => d == DayOfWeek.Sunday ? 7 : (int)d;

    private static (HashSet<int>, TimeSpan, TimeSpan)? Parse(ScheduleWindow w)
    {
        if (w.Days is not { Count: > 0 } days || days.Any(d => d < 1 || d > 7)) return null;
        if (!TryTime(w.From, out var from) || !TryTime(w.To, out var to)) return null;
        return (days.ToHashSet(), from, to);
    }

    public static bool TryTime(string? s, out TimeSpan t) =>
        TimeSpan.TryParseExact(s, @"hh\:mm", CultureInfo.InvariantCulture, out t) && t < TimeSpan.FromDays(1);
}
