using System.Security.Cryptography;
using System.Text;

namespace Watcher.Server;

// Задача G1 (roadmap-next.md): кто прислал запрос агента. Учётка агента
// (Identity задан) — host берётся из неё; анонимный режим разработки
// (Identity == null) — host из тела запроса.
public sealed record AgentCaller(AgentIdentity? Identity)
{
    public bool Legacy => Identity is null;
}

public static class AgentAuth
{
    // Виды агентов: video — сегменты экрана + heartbeat (служба Windows, Linux
    // Uploader), terminal — консольные сессии (Linux TerminalAgent), android —
    // Android-агент аудита (docs/android-plan.md): регистрация, heartbeat, поток
    // событий и — с Этапа 12+ — запись экрана по согласию (/api/upload, как video).
    // Консоль (/api/upload-terminal) — только terminal; terminal видео не шлёт.
    // Ограничения по виду проверяют сами data-эндпоинты.
    public const string KindVideo = "video";
    public const string KindTerminal = "terminal";
    public const string KindAndroid = "android";
    public static readonly string[] Kinds = { KindVideo, KindTerminal, KindAndroid };

    // Префиксы — чтобы по значению было видно, что это (в конфиге, в логе
    // утечки), и чтобы токен регистрации нельзя было перепутать с секретом.
    public const string EnrollPrefix = "wenr_";
    public const string SecretPrefix = "wagt_";

    public static string NewEnrollToken() => EnrollPrefix + RandomPart();
    public static string NewAgentSecret() => SecretPrefix + RandomPart();

    private static string RandomPart() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string? BearerToken(string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader) ||
            !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        string t = authorizationHeader[7..].Trim();
        return t.Length == 0 ? null : t;
    }

    public static bool MatchesSharedToken(string presented, string? sharedToken) =>
        !string.IsNullOrWhiteSpace(sharedToken) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(sharedToken));

    // Имя хоста при регистрации — то, что потом будет в UI и в путях blobs:
    // то же ограничение, что у host в meta (A1), плюс непустое.
    public static bool IsValidHostName(string? host) =>
        !string.IsNullOrWhiteSpace(host) && host.Trim() == host && UploadValidation.IsSafeMetaField(host);

    // Хост, под которым сохранять данные запроса. null — запрос надо отклонить:
    // учётка одного хоста прислала данные от имени другого. Пустое поле в meta у
    // агента с учёткой — не ошибка, берётся хост учётки.
    // С 0.13 агент с учёткой — это машина, а не имя: запись принадлежит хосту
    // учётки, что бы ни стояло в её meta. Раньше чужое имя давало 403 и вечные
    // повторы — после переименования машины агент молча переставал выгружать, а
    // записи, сделанные до переименования, не уходили никогда (watcher-debug
    // 2026-10-13). Подменить чужой хост так нельзя: секрет учётки свой у каждой
    // машины. Смену имени ведёт heartbeat (Db.RenameHost).
    public static string? ResolveHost(AgentCaller caller, string? claimedHost) =>
        caller.Identity is null ? claimedHost : caller.Identity.HostName;
}
