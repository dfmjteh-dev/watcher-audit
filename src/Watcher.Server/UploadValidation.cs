namespace Watcher.Server;

public static class UploadValidation
{
    // Задача F2: источник записи консоли — транскрипт PowerShell (Windows-агент),
    // у его вывода нет .timing.
    public const string SourceWinTranscript = "win-transcript";

    public static bool IsValidFilename(string filename) =>
        !string.IsNullOrWhiteSpace(filename)
        && !filename.Contains("..")
        && !filename.Contains('/')
        && !filename.Contains('\\');

    public static string SanitizeHost(string host) => SafeName.ForPath(host);

    // Агенты до 0.13 форматировали процент причины по языку станции: «motion 0,52%»
    // на русской Windows, «motion 0.52%» на Linux. Приводим к точке — одна запись,
    // одинаково в UI и поиске; уже лежащее в БД правит миграция 9.
    private static readonly System.Text.RegularExpressions.Regex MotionCommaReason =
        new(@"^motion (\d+),(\d+)%$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // 0.13: раскладка мониторов из sidecar сегмента — [{x,y,w,h,primary}] в
    // координатах кадра. Текст от агента: только числа в разумных пределах, не
    // больше 16 мониторов; что-то не так — раскладки нет (запись не теряем).
    public const int MaxMonitors = 16;

    public static string? ParseMonitors(System.Text.Json.JsonElement value)
    {
        if (value.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
        int n = value.GetArrayLength();
        if (n < 2 || n > MaxMonitors) return null;   // один монитор — переключать нечего
        var list = new List<object>();
        foreach (var m in value.EnumerateArray())
        {
            if (m.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            int? Int(string k) => m.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt32(out int i) && i >= 0 && i <= 16384 ? i : null;
            if (Int("x") is not { } x || Int("y") is not { } y || Int("w") is not { } w || Int("h") is not { } h || w == 0 || h == 0) return null;
            bool primary = m.TryGetProperty("primary", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.True;
            list.Add(new { x, y, w, h, primary });
        }
        return System.Text.Json.JsonSerializer.Serialize(list);
    }

    public static string NormalizeStartReason(string reason) => MotionCommaReason.Replace(reason, "motion $1.$2%");

    // host/sessionUser/startReason попадают в веб-UI и присылаются агентом без
    // контроля сервера, то есть любым, кто знает ApiToken (A1, docs/roadmap-next.md).
    // UI выводит их только через Preact, который экранирует сам (защита первого
    // слоя, с Задачи E1 innerHTML в UI нет вовсе), это — второй слой:
    // отрезаем сами HTML-ломающие символы и ограничиваем длину до того, как
    // значение попадёт в БД, а не только в момент рендера.
    // H9: смены активного окна из sidecar сегмента. Заголовок — текст окна
    // пользователя, отказывать из-за символов нельзя (потеряли бы запись) —
    // управляющие символы заменяются пробелом, длина обрезается, лишнее и
    // кривое отбрасывается молча. UI экранирует при выводе (Preact).
    public const int MaxActivityEntries = 5000;
    public const int MaxActivityTextLength = 300;

    public static List<ActivityEntry> ParseActivity(System.Text.Json.JsonElement value, double durationSec)
    {
        var list = new List<ActivityEntry>();
        if (value.ValueKind != System.Text.Json.JsonValueKind.Array) return list;
        // Сегмент может быть длиннее заявленной длительности (pre-roll) — запас.
        double maxOffset = Math.Max(0, durationSec) + 600;
        foreach (var e in value.EnumerateArray())
        {
            if (list.Count >= MaxActivityEntries) break;
            if (e.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            if (!e.TryGetProperty("t", out var t) || t.ValueKind != System.Text.Json.JsonValueKind.Number || !t.TryGetDouble(out double off)) continue;
            if (double.IsNaN(off) || off < 0 || off > maxOffset) continue;
            string? process = CleanText(e, "process");
            string? title = CleanText(e, "title");
            if (process is null && title is null) continue;
            list.Add(new ActivityEntry(Math.Round(off, 1), process, title, Site(CleanText(e, "site"))));
        }
        return list;
    }

    // Домен от агента (0.14.3): только имя хоста — буквы, цифры, точка, дефис и
    // порт; иначе не сохраняем (агент шлёт хост из адресной строки, не URL).
    public static string? Site(string? s)
    {
        if (s is null || s.Length > 253) return null;
        s = s.ToLowerInvariant();
        foreach (char ch in s)
            if (!(char.IsLetterOrDigit(ch) || ch is '.' or '-' or ':' or '[' or ']')) return null;
        return s.Contains('.') || s.Contains(':') || s == "localhost" ? s : null;
    }

    private static string? CleanText(System.Text.Json.JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != System.Text.Json.JsonValueKind.String) return null;
        string s = v.GetString()!;
        if (s.Length > MaxActivityTextLength) s = s[..MaxActivityTextLength];
        s = new string(s.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return s.Length == 0 ? null : s;
    }

    public static bool IsSafeMetaField(string? value, int maxLength = 128)
    {
        if (value is null) return true;
        if (value.Length > maxLength) return false;
        foreach (char c in value)
        {
            if (c < 0x20) return false;
            if (c is '<' or '>' or '"' or '\'' or '`') return false;
        }
        return true;
    }
}
