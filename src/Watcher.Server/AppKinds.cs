using System.Text.Json;
using System.Text.RegularExpressions;

namespace Watcher.Server;

// 0.14.3: что именно открыто в окне — «предмет» строки активности — по типу
// программы, а не одним фильтром по всем заголовкам. Заголовок пишет каждая
// программа как хочет (рабочая дата 1С, «(3) Входящие», «и ещё 5 страниц»),
// и нужен он не везде: 1С, кассе, OPERA хватает имени процесса, браузеру
// нужен сайт, RDP — сервер, Office — файл. Тип задаётся по имени процесса:
// таблица по умолчанию (Defaults) + правки админа в «Настройки → Программы».
// Исходный заголовок не меняется — поиск и подписи в плеере видят его целиком.
public enum AppKind { App, Title, Browser, Rdp, Document, Ignore }

public static partial class AppKinds
{
    public const string SettingKey = "app-kinds";

    public static readonly IReadOnlyDictionary<AppKind, string> Names = new Dictionary<AppKind, string>
    {
        [AppKind.App] = "app",
        [AppKind.Title] = "title",
        [AppKind.Browser] = "browser",
        [AppKind.Rdp] = "rdp",
        [AppKind.Document] = "document",
        [AppKind.Ignore] = "ignore",
    };

    public static AppKind? Parse(string? name)
    {
        foreach (var (kind, n) in Names)
            if (n == name) return kind;
        return null;
    }

    // Имена процессов как их пишут агенты: Windows — без .exe, Linux — comm.
    // Не перечисленное — «программа» (только имя процесса).
    public static readonly IReadOnlyDictionary<string, AppKind> Defaults = new Dictionary<string, AppKind>(StringComparer.OrdinalIgnoreCase)
    {
        // браузеры («browser» — Яндекс Браузер)
        ["msedge"] = AppKind.Browser,
        ["chrome"] = AppKind.Browser,
        ["browser"] = AppKind.Browser,
        ["firefox"] = AppKind.Browser,
        ["opera"] = AppKind.Browser,
        ["brave"] = AppKind.Browser,
        ["vivaldi"] = AppKind.Browser,
        ["chromium"] = AppKind.Browser,
        ["chromium-browser"] = AppKind.Browser,
        ["google-chrome"] = AppKind.Browser,
        ["yandex_browser"] = AppKind.Browser,
        ["iexplore"] = AppKind.Browser,
        // удалённый рабочий стол
        ["mstsc"] = AppKind.Rdp,
        ["msrdc"] = AppKind.Rdp,
        ["remmina"] = AppKind.Rdp,
        ["xfreerdp"] = AppKind.Rdp,
        // документы
        ["excel"] = AppKind.Document,
        ["winword"] = AppKind.Document,
        ["powerpnt"] = AppKind.Document,
        ["soffice.bin"] = AppKind.Document,
        ["soffice"] = AppKind.Document,
        ["acrord32"] = AppKind.Document,
        ["acrobat"] = AppKind.Document,
        ["foxitpdfreader"] = AppKind.Document,
        ["notepad"] = AppKind.Document,
        ["notepad++"] = AppKind.Document,
        // процесс общий, программа — в заголовке (приложения Магазина: калькулятор, фото)
        ["applicationframehost"] = AppKind.Title,
        // служебные окна оболочки и RemoteApp — не работа пользователя
        ["rdpinit"] = AppKind.Ignore,
        ["lockapp"] = AppKind.Ignore,
        ["shellexperiencehost"] = AppKind.Ignore,
        ["searchhost"] = AppKind.Ignore,
        ["searchapp"] = AppKind.Ignore,
        ["startmenuexperiencehost"] = AppKind.Ignore,
        ["textinputhost"] = AppKind.Ignore,
        ["shellhost"] = AppKind.Ignore,
        ["logonui"] = AppKind.Ignore,
    };

    public sealed class Map
    {
        public IReadOnlyDictionary<string, AppKind> Overrides { get; }

        public Map(IReadOnlyDictionary<string, AppKind>? overrides) =>
            Overrides = new Dictionary<string, AppKind>(overrides ?? new Dictionary<string, AppKind>(), StringComparer.OrdinalIgnoreCase);

        public AppKind KindOf(string? process) =>
            process is null ? AppKind.App
            : Overrides.TryGetValue(process, out var k) ? k
            : Defaults.TryGetValue(process, out var d) ? d
            : AppKind.App;

        // 0.14.3: агент прислал домен вкладки — это браузер, даже если процесса нет
        // в таблице (агент определяет браузеры по системе, BrowserNames). Явная
        // правка админа главнее.
        public AppKind KindOf(string? process, bool hasSite) =>
            hasSite && (process is null || !Overrides.ContainsKey(process)) ? AppKind.Browser : KindOf(process);
    }

    public static Map Load(Db db)
    {
        string? json = db.GetSetting(SettingKey);
        return new Map(json is null ? null : ParseOverrides(json));
    }

    public static Dictionary<string, AppKind>? ParseOverrides(string json)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (raw is null) return null;
            var map = new Dictionary<string, AppKind>(StringComparer.OrdinalIgnoreCase);
            foreach (var (p, k) in raw)
                if (Parse(k) is { } kind && IsValidProcess(p)) map[p.Trim()] = kind;
            return map;
        }
        catch (JsonException) { return null; }
    }

    public static bool IsValidProcess(string? p) =>
        !string.IsNullOrWhiteSpace(p) && p.Trim().Length <= 100 && !p.Any(char.IsControl);

    // Цифры в предмете — шум (дата, счётчик) или смысл (адрес сервера, домен)?
    // Шум — в заголовках страниц и документов; адреса не сливаем (10.0.0.2 ≠ 10.0.0.3).
    public static bool DigitsAreNoise(AppKind kind, string? site) =>
        kind is AppKind.Document or AppKind.Title || (kind == AppKind.Browser && string.IsNullOrEmpty(site));   // site null — название страницы (подсказки правил)

    // Предмет строки активности: что сгруппирует отчёт и что подскажет правило.
    // null — не учитывать. forTitleRule — для правил «заголовок содержит»: там
    // нужен текст из заголовка, домен от агента в заголовке не встречается.
    public static string? Subject(AppKind kind, string? process, string? title, string? site, bool forTitleRule = false)
    {
        string? t = string.IsNullOrWhiteSpace(title) ? null : Spaces().Replace(title, " ").Trim();
        string? s = kind switch
        {
            AppKind.Ignore => null,
            AppKind.App => process,
            AppKind.Title => t,
            // 0.14.4: в отчёте у браузера — только домен: заголовки страниц («2
            // непрочитанных чата», «Входящие (34)») сайтами не являются. Без домена
            // (старый агент, Linux) строка в «Сайты» не попадает — отчёт показывает
            // её время одной суммой. Правилу «заголовок содержит» — название страницы.
            AppKind.Browser => forTitleRule ? (t is null ? null : WindowTitles.BrowserPage(t)) : site,
            AppKind.Rdp => t is null ? null : RdpTarget(t),
            AppKind.Document => t is null ? null : DocumentName(t),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // «srv01 — Подключение к удаленному рабочему столу» — сервер; RemoteApp —
    // «Бэк Офис (rds.corp.local)»: программа на сервере, сервер — в скобках.
    [GeneratedRegex(@"^(?<t>.+?)\s+[—–-]\s+(?:Подключение к удал[её]нному рабочему столу|Remote Desktop Connection)$", RegexOptions.IgnoreCase)]
    private static partial Regex RdpWindow();

    [GeneratedRegex(@"\((?<h>[^()\s]+)\)$")]
    private static partial Regex RemoteAppServer();

    // Без адреса (диалоги mstsc, окна внутри RemoteApp без сервера в заголовке —
    // там бывают данные из программы, например ФИО гостя) — не учитываем.
    internal static string? RdpTarget(string title)
    {
        if (RdpWindow().Match(title) is { Success: true } m) return m.Groups["t"].Value;
        if (RemoteAppServer().Match(title) is { Success: true } r) return r.Groups["h"].Value;
        return null;
    }

    // Office пишет «Отчёт - Excel» или «Microsoft Excel - Отчёт», плюс режим
    // в квадратных скобках и состояние лицензии; Windows — «(Не отвечает)».
    [GeneratedRegex(@"\s*\[[^\]]*\]|\s*\((?:Сбой активации продукта|Нелицензированный продукт|Product Activation Failed|Unlicensed Product|Не отвечает|Not Responding)\)", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentNoise();

    [GeneratedRegex(@"^(?:Microsoft\s+\w+|LibreOffice\s+\w+|Adobe\s+Acrobat[\w\s]*?|Блокнот|Notepad\+*)\s+[\u2014\u2013-]\s+|\s+[\u2014\u2013-]\s+(?:Microsoft\s+)?(?:Excel|Word|PowerPoint|LibreOffice\s+\w+|Adobe\s+Acrobat[\w\s]*|Foxit\s+PDF\s+Reader|Блокнот|Notepad\+*)$", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentApp();

    // Документ — окно «файл - программа» (или «программа - файл»); без этого —
    // окно самой программы или диалог («Открытие», «Найти и заменить»), не файл.
    internal static string? DocumentName(string title)
    {
        string t = DocumentNoise().Replace(title, "");
        if (!DocumentApp().IsMatch(t)) return null;
        t = Spaces().Replace(DocumentApp().Replace(t, ""), " ").Trim();
        return t.Length == 0 ? null : t;
    }
}
