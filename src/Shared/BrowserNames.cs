namespace Watcher.Capture;

// 0.14.3: какие процессы — браузеры (для домена вкладки, BrowserSite). Без
// жёсткого списка: Windows-агент добавляет к встроенным браузеры,
// зарегистрированные в системе (HKLM/HKCU\SOFTWARE\Clients\StartMenuInternet —
// туда пишется любой браузер, который можно выбрать браузером по умолчанию).
// Встроенный список — на случай, если браузер там не зарегистрирован
// (портативный, поставлен только пользователю без регистрации).
public static class BrowserNames
{
    public static readonly IReadOnlyCollection<string> BuiltIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "browser", "opera", "brave", "vivaldi", "chromium", "firefox",
        "yandex", "iexplore", "waterfox", "librewolf", "floorp", "thorium", "maxthon", "palemoon",
    };

    // Firefox и форки (Gecko): адресная строка — поле с AutomationId «urlbar-input».
    private static readonly HashSet<string> Gecko = new(StringComparer.OrdinalIgnoreCase)
        { "firefox", "waterfox", "librewolf", "floorp", "palemoon" };

    public static bool IsGecko(string? process) => process is not null && Gecko.Contains(process);

    // «"C:\Program Files\Google\Chrome\Application\chrome.exe" --single-argument %1» → chrome.
    public static string? ProcessFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string c = command.Trim();
        string path;
        if (c.StartsWith('"'))
        {
            int end = c.IndexOf('"', 1);
            if (end < 0) return null;
            path = c[1..end];
        }
        else
        {
            int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = exe >= 0 ? c[..(exe + 4)] : c.Split(' ')[0];
        }
        string name = path.Replace('/', '\\');
        name = name[(name.LastIndexOf('\\') + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Length == 0 ? null : name;
    }
}
