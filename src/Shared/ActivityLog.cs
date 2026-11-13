namespace Watcher.Capture;

// H9 (roadmap-next.md): смены активного окна за сегмент — уходят в sidecar
// (.mp4.json, поле activity), сервер раскладывает их для поиска и подписей в
// плеере. Один файл на оба агента захвата (src/Shared, подключается ссылкой).
//
// Время — «время видео», а не часы: число уже записанных кадров / OutputFps.
// Плеер считает момент как startedAt + currentTime, а в начале видео идут
// кадры pre-roll — по часам подпись съехала бы на длину pre-roll. Окно в
// pre-roll неизвестно (кадры сняты раньше открытия сегмента) — первая запись
// получает t = 0.
// Window — дескриптор окна (Windows: HWND) для зонда сайта; 0 — неизвестен.
public sealed record ActiveWindowInfo(string? Process, string? Title, long Window = 0);

// Site (0.14.3) — домен активной вкладки браузера, если агент умеет его узнать
// (Windows: адресная строка через UI Automation, BrowserSite.cs). Только домен,
// не адрес целиком: в пути и параметрах — токены, поисковые запросы.
public sealed record ActivitySample(double T, string? Process, string? Title, string? Site = null);

public sealed class ActivityLog
{
    public const int MaxEntries = 2000;
    public const int MaxTextLength = 300;
    public static readonly string[] Modes = ["full", "process", "off"];

    // Не чаще раза в секунду: зонд дешёвый, но на каждый тик (500 мс) и на каждый
    // кадр pre-roll, которые пишутся пачкой, смысла нет.
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly Func<ActiveWindowInfo?> _probe;
    private readonly Func<ActiveWindowInfo, (bool Done, string? Site)>? _site;
    private readonly List<ActivitySample> _entries = new();
    private DateTime _lastSample;
    private bool _probed;
    // 0.14.4: домен для последней записи ещё не узнали (браузер не ответил за
    // отведённое время) — переспрашиваем на следующих опросах, пока окно то же.
    // Раньше запись так и оставалась без домена: на проде так было у ~3/4 строк
    // браузеров, и отчёт показывал вместо сайтов заголовки страниц.
    private bool _sitePending;

    // site — зонд домена; зовётся при смене окна или заголовка и только в режиме
    // full (как заголовок), не на каждом тике и не в режиме process. Done = false —
    // ответа пока нет: спросим снова на следующем опросе (не чаще MinInterval).
    public ActivityLog(Func<ActiveWindowInfo?> probe, Func<ActiveWindowInfo, (bool Done, string? Site)>? site = null)
    {
        _probe = probe;
        _site = site;
    }

    public IReadOnlyList<ActivitySample> Entries => _entries;
    public bool Truncated { get; private set; }

    public void Start()
    {
        _entries.Clear();
        Truncated = false;
        _lastSample = DateTime.MinValue;
        _probed = false;
        _sitePending = false;
    }

    public void Sample(DateTime now, double videoTimeSec, string mode)
    {
        if (mode == "off" || !Modes.Contains(mode)) return;
        if (now - _lastSample < MinInterval) return;
        _lastSample = now;
        // Первый опрос сегмента приходится на кадры pre-roll — t = 0 (см. выше).
        double t = _probed ? Math.Round(Math.Max(0, videoTimeSec), 1) : 0;
        _probed = true;

        ActiveWindowInfo? w;
        try { w = _probe(); }
        catch (Exception) { return; }   // зонд не должен ронять запись экрана
        if (w is null) return;

        string? process = Clip(w.Process);
        string? title = mode == "process" ? null : Clip(w.Title);
        if (process is null && title is null) return;
        if (_entries.Count > 0 && _entries[^1].Process == process && _entries[^1].Title == title)
        {
            if (_sitePending && mode == "full" && ProbeSite(w) is (true, var late))
            {
                _sitePending = false;
                if (late is not null) _entries[^1] = _entries[^1] with { Site = late };
            }
            return;
        }
        if (_entries.Count >= MaxEntries)
        {
            Truncated = true;
            return;
        }
        string? site = null;
        _sitePending = false;
        if (mode == "full")
        {
            var (done, s) = ProbeSite(w);
            site = s;
            _sitePending = !done;
        }
        _entries.Add(new ActivitySample(t, process, title, site));
    }

    private (bool Done, string? Site) ProbeSite(ActiveWindowInfo w)
    {
        if (_site is null) return (true, null);
        try { return _site(w); }
        catch (Exception) { return (true, null); }   // как и зонд окна — не роняет запись
    }

    // Для sidecar — короткие имена полей: t, process, title, site (если есть).
    public object[] ToJson() => _entries.Select(e => e.Site is null
        ? (object)new { t = e.T, process = e.Process, title = e.Title }
        : new { t = e.T, process = e.Process, title = e.Title, site = e.Site }).ToArray();

    // Домен из адресной строки браузера: «https://mail.example.com/x», без схемы
    // («example.com/path» — Chromium её прячет). Не http(s) (edge://, about:,
    // file:), набираемый текст (с пробелом), имя без точки — null. www. отбрасываем.
    public static string? SiteFromAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string v = value.Trim();
        if (v.Length > 2048) v = v[..2048];
        int slash = v.IndexOf('/');
        if ((slash < 0 ? v : v[..slash]).Any(char.IsWhiteSpace)) return null;
        bool hasScheme = v.Contains("://", StringComparison.Ordinal);
        if (!hasScheme)
        {
            // «about:blank», «mailto:x» — не сайт; двоеточие перед цифрами — порт.
            int colon = v.IndexOf(':');
            if (colon >= 0 && (colon + 1 >= v.Length || !char.IsDigit(v[colon + 1]))) return null;
        }
        if (!Uri.TryCreate(hasScheme ? v : "http://" + v, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        string host;
        try { host = uri.Host.ToLowerInvariant(); }
        catch (Exception) { return null; }
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (host.Length == 0 || host.Length > 253) return null;
        if (!host.Contains('.') && host != "localhost" && !host.Contains(':')) return null;
        if (!uri.IsDefaultPort) host += ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return host;
    }

    private static string? Clip(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= MaxTextLength ? s : s[..MaxTextLength];
    }
}
