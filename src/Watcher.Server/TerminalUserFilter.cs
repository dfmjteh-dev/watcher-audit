using System.Text.Json;

namespace Watcher.Server;

// Кого не записывать в консоли — те же allowUsers/excludeUsers из конфига
// группы/хоста, что и для экрана (ConfigReload.IsCaptureAllowedForUser у
// агентов захвата): непустой allowUsers — только они, иначе непустой
// excludeUsers — все, кроме них, иначе все. Агенты консоли пишут всех
// пользователей станции, а фильтр применяет сервер при приёме: список правится
// только в веб-UI и действует сразу, без переустановки агентов.
//
// Сверх правил экрана: доменное имя (CORP\ivanov, ivanov@corp) совпадает и с
// коротким «ivanov» в списке — на Linux с winbind/sssd $USER бывает доменным.
public static class TerminalUserFilter
{
    public static bool IsAllowed(string? effectiveConfigJson, string sessionUser)
    {
        if (string.IsNullOrEmpty(effectiveConfigJson)) return true;
        List<string>? allow, exclude;
        try
        {
            using var doc = JsonDocument.Parse(effectiveConfigJson);
            allow = Strings(doc.RootElement, "allowUsers");
            exclude = Strings(doc.RootElement, "excludeUsers");
        }
        catch (JsonException)
        {
            return true;   // битый конфиг — лучше записать, чем молча потерять аудит
        }
        return UserNames.IsAllowed(allow, exclude, sessionUser);
    }

    public static bool UserMatches(string listed, string user) => UserNames.Matches(listed, user);

    private static List<string>? Strings(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : null;

    // 0.16: skipTranscriptOrigins — происхождения транскриптов PowerShell, которые
    // сервер не сохраняет (по умолчанию сохраняются все). Без метки (Linux, старые
    // агенты) — не фильтруется.
    public static bool OriginSkipped(string? effectiveConfigJson, string? origin)
    {
        if (origin is null || string.IsNullOrEmpty(effectiveConfigJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(effectiveConfigJson);
            return Strings(doc.RootElement, "skipTranscriptOrigins")?.Contains(origin, StringComparer.OrdinalIgnoreCase) == true;
        }
        catch (JsonException) { return false; }
    }

    // 0.13: pauseConsole (+ pauseUntil) в итоговом конфиге хоста.
    public static bool ConsolePaused(string? effectiveConfigJson, DateTime now) =>
        PauseOf(effectiveConfigJson, "pauseConsole", now);

    public static bool VideoPaused(string? effectiveConfigJson, DateTime now) =>
        PauseOf(effectiveConfigJson, "pauseVideo", now);

    private static bool PauseOf(string? json, string key, DateTime now)
    {
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool? flag = root.TryGetProperty(key, out var v) && v.ValueKind is System.Text.Json.JsonValueKind.True ? true : null;
            string? until = root.TryGetProperty("pauseUntil", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.String ? u.GetString() : null;
            return RecordingPause.IsActive(flag, until, now);
        }
        catch (System.Text.Json.JsonException) { return false; }
    }
}
