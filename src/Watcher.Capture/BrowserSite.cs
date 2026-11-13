using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Watcher.Capture;

// 0.14.3: домен активной вкладки браузера — для отчёта «Сайты и документы»
// (сервер, AppKinds). Заголовок страницы для этого плох: счётчики, «и ещё N
// страниц», разные названия у одного сайта. Адресная строка — через UI
// Automation: у Chromium (Edge, Chrome, Яндекс, Opera, Brave, Vivaldi) это
// первое поле ввода окна, у Firefox и форков — поле с AutomationId «urlbar-input».
// Какие процессы браузеры — BrowserNames: встроенные + зарегистрированные в
// системе (StartMenuInternet), перечитываются раз в час.
//
// Зовётся только при смене окна или заголовка (ActivityLog), не на каждом
// тике. UI Automation — межпроцессные вызовы к браузеру, зависший браузер
// держит их сколько угодно: поэтому свой поток и ожидание не дольше Timeout.
// Не дождались — запрос дорабатывает в фоне (первый поиск адресной строки в
// окне бывает дольше Timeout), результат — в кэше к следующему разу; только
// вызов дольше HungAfter — браузер висит, пауза HungBackoff. Пока в адресной
// строке фокус (там набирают адрес или поиск), её не читаем. Сохраняется
// только домен (ActivityLog.SiteFromAddress).
[SupportedOSPlatform("windows")]
public sealed class BrowserSite : IDisposable
{
    private static readonly TimeSpan BrowsersRefresh = TimeSpan.FromHours(1);
    private HashSet<string> _browsers = new(BrowserNames.BuiltIn, StringComparer.OrdinalIgnoreCase);
    private DateTime _browsersAt = DateTime.MinValue;

    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan HungAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HungBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NotFoundRetry = TimeSpan.FromSeconds(10);

    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _lock = new();
    private volatile bool _stop;
    private Request? _pending;
    private DateTime _backoffUntil;

    // Только поток _thread.
    private IUIAutomation? _uia;
    private IUIAutomationCondition? _edit, _firefoxBar;
    private long _barWindow;
    private IUIAutomationElement? _bar;
    private DateTime _notFoundAt;

    private sealed class Request(long window, bool firefox)
    {
        public readonly long Window = window;
        public readonly bool Firefox = firefox;
        public readonly ManualResetEventSlim Done = new(false);
        public string? Site;
    }

    public BrowserSite()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "browser-site" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public bool IsBrowser(string? process)
    {
        if (process is null) return false;
        if (DateTime.UtcNow - _browsersAt > BrowsersRefresh)
        {
            _browsersAt = DateTime.UtcNow;
            try { _browsers = RegisteredBrowsers(); }
            catch (Exception) { }   // реестр недоступен — остаётся прежний список
        }
        return _browsers.Contains(process);
    }

    private static HashSet<string> RegisteredBrowsers()
    {
        var set = new HashSet<string>(BrowserNames.BuiltIn, StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, path) in new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Clients\StartMenuInternet"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Clients\StartMenuInternet"),
            (Registry.CurrentUser, @"SOFTWARE\Clients\StartMenuInternet"),
        })
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) continue;
            foreach (string name in key.GetSubKeyNames())
            {
                using var cmd = key.OpenSubKey(name + @"\shell\open\command");
                if (BrowserNames.ProcessFromCommand(cmd?.GetValue(null) as string) is { } p) set.Add(p);
            }
        }
        return set;
    }

    // Последний ответ для окна и заголовка: правила записи (RecordingPolicy)
    // спрашивают на каждом тике, UI Automation — только когда окно или заголовок
    // сменились. Не дождались ответа — не кэшируем, спросим на следующем тике.
    private (long Window, string? Title, string? Site)? _last;

    public string? SiteFor(ActiveWindowInfo w) => Lookup(w).Site;

    // Для журнала окон (ActivityLog): Done = false — ответа пока нет, журнал
    // переспросит на следующем опросе, а не запишет окно без домена.
    public (bool Done, string? Site) Lookup(ActiveWindowInfo w)
    {
        if (_last is { } l && l.Window == w.Window && l.Title == w.Title) return (true, l.Site);
        var (done, site) = Probe(w);
        if (done) _last = (w.Window, w.Title, site);
        return (done, site);
    }

    // done = false — ответа нет (браузер занят, первый поиск адресной строки
    // не уложился в Timeout): домен неизвестен, а не «нет домена».
    public (bool Done, string? Site) Probe(ActiveWindowInfo w)
    {
        if (w.Window == 0 || !IsBrowser(w.Process)) return (true, null);
        Request req;
        lock (_lock)
        {
            if (DateTime.UtcNow < _backoffUntil || _pending is not null) return (false, null);   // прошлый запрос ещё висит
            _pending = req = new Request(w.Window, BrowserNames.IsGecko(w.Process));
        }
        _wake.Set();
        return req.Done.Wait(Timeout) ? (true, req.Site) : (false, null);
    }

    private void Loop()
    {
        try { Init(); }
        catch (Exception) { return; }   // нет UI Automation (урезанная ОС) — сайта не будет, запись идёт
        while (!_stop)
        {
            _wake.WaitOne();
            Request? req;
            lock (_lock) req = _pending;
            if (req is null) continue;
            var started = DateTime.UtcNow;
            try { req.Site = Read(req.Window, req.Firefox); }
            catch (Exception) { req.Site = null; ForgetBar(); }   // окно закрылось, браузер перезапустился
            lock (_lock)
            {
                _pending = null;
                if (DateTime.UtcNow - started > HungAfter) _backoffUntil = DateTime.UtcNow + HungBackoff;
            }
            req.Done.Set();
        }
    }

    private void Init()
    {
        _uia = (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(Uia.CUIAutomation, throwOnError: true)!)!;
        Check(_uia.CreatePropertyCondition(Uia.ControlTypePropertyId, Uia.EditControlTypeId, out var edit));
        Check(_uia.CreatePropertyCondition(Uia.AutomationIdPropertyId, "urlbar-input", out var id));
        Check(_uia.CreateAndCondition(edit, id, out var ff));
        (_edit, _firefoxBar) = (edit, ff);
    }

    private string? Read(long window, bool firefox)
    {
        if (window != _barWindow)
        {
            ForgetBar();
            _barWindow = window;
        }
        if (_bar is null)
        {
            if (DateTime.UtcNow - _notFoundAt < NotFoundRetry) return null;
            Check(_uia!.ElementFromHandle((IntPtr)window, out var root));
            root.FindFirst(Uia.TreeScopeDescendants, firefox ? _firefoxBar! : _edit!, out _bar);
            if (_bar is null)
            {
                _notFoundAt = DateTime.UtcNow;
                return null;
            }
        }
        if (_bar.GetCurrentPropertyValue(Uia.HasKeyboardFocusPropertyId, out var focus) == 0 && focus is true) return null;
        Check(_bar.GetCurrentPropertyValue(Uia.ValueValuePropertyId, out var value));
        return ActivityLog.SiteFromAddress(value as string);
    }

    private void ForgetBar()
    {
        _bar = null;
        _barWindow = 0;
        _notFoundAt = DateTime.MinValue;
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
    }
}
