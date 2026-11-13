using System.ServiceProcess;
using System.Text.Json;

namespace Watcher.SessionService;

// Задача D3: allow/exclude из конфига хоста/группы с сервера (кэш, который пишет
// heartbeat). null-списки — не заданы.
public sealed record UserFilter(IReadOnlyList<string>? AllowUsers, IReadOnlyList<string>? ExcludeUsers);

public enum SessionChangeAction
{
    Launch,
    StopTracking,
    LogDisconnect,
    Ignore,
}

// Чистая логика SessionWatcherService, вынесенная отдельно, чтобы её можно было
// юнит-тестировать без ServiceBase/SCM и без P/Invoke (CreateProcessAsUser,
// WTSQueryUserToken и т.п.) — тот же приём, что ConfigReload у Watcher.Capture.
// P/Invoke-часть (TryLaunchForSession) этим не покрывается, см.
// docs/development.md, "Стратегия тестирования", известное ограничение.
public static class SessionLogic
{
    public static SessionChangeAction ClassifyChange(SessionChangeReason reason) => reason switch
    {
        SessionChangeReason.SessionLogon => SessionChangeAction.Launch,
        SessionChangeReason.ConsoleConnect => SessionChangeAction.Launch,
        SessionChangeReason.RemoteConnect => SessionChangeAction.Launch,
        SessionChangeReason.SessionLogoff => SessionChangeAction.StopTracking,
        SessionChangeReason.ConsoleDisconnect => SessionChangeAction.LogDisconnect,
        SessionChangeReason.RemoteDisconnect => SessionChangeAction.LogDisconnect,
        _ => SessionChangeAction.Ignore,
    };

    // Задача D1: в сессии ещё (или уже) нет пользователя — экран входа консоли
    // после загрузки, RemoteConnect до ввода пароля. WTSQuerySessionInformation
    // отвечает успешно с пустым именем; WTSQueryUserToken там всегда падает
    // (GLE=1008), раньше это давало WRN на каждый вход. null — сам запрос имени
    // не удался: пробуем запустить, как раньше (WTSQueryUserToken решит).
    public static bool SessionHasUser(string? userName) => userName != "";

    // userName null/"?" — QueryUserName не нашёл имя (см. SessionWatcherService) —
    // не передаём --session-user как мусорный тег, лучше пусто.
    public static string BuildCaptureCommandLine(string captureExePath, string? userName)
    {
        if (string.IsNullOrEmpty(userName) || userName == "?")
        {
            return $"\"{captureExePath}\"";
        }
        return $"\"{captureExePath}\" --session-user \"{userName}\"";
    }

    // Задача D3: тот же формат, что читает Watcher.Capture (ConfigReload.TryParse) —
    // эффективный конфиг хоста/группы, ключи allowUsers/excludeUsers. Битый файл —
    // null (как «конфига нет»): лучше писать всех, чем перестать писать из-за
    // ошибки в кэше.
    public static UserFilter? ParseUserFilter(string json)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<UserFilterDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return dto is null ? null : new UserFilter(dto.AllowUsers, dto.ExcludeUsers);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record UserFilterDto(List<string>? AllowUsers, List<string>? ExcludeUsers);

    // Те же правила, что Watcher.Capture ConfigReload.IsCaptureAllowedForUser (Фаза 5):
    // непустой allowUsers — только они (главнее excludeUsers); иначе непустой
    // excludeUsers — все, кроме них; иначе или конфига нет — все. Без учёта
    // регистра. Менять — вместе: Capture проверяет то же самое у себя (если
    // пользователя исключили уже после запуска процесса).
    public static bool IsUserAllowed(UserFilter? filter, string userName) =>
        UserNames.IsAllowed(filter?.AllowUsers, filter?.ExcludeUsers, userName);

    // Задача D1: Capture, уже работающий в этой сессии, — если он есть, служба
    // берёт его под наблюдение вместо запуска второго. Без этого перезапуск
    // службы (Restart-Service, перезапуск SCM после падения) давал два Capture
    // в каждой сессии: остановка службы своих детей не убивает, а после старта
    // служба знает о процессах только из памяти (найдено проверкой D1).
    public static int? ExistingCaptureInSession(IEnumerable<(int Pid, int SessionId)> captures, int sessionId)
    {
        foreach (var (pid, sid) in captures)
        {
            if (sid == sessionId) return pid;
        }
        return null;
    }

    // Снимок sessionId->pid + внедряемая "живой ли процесс" (вместо реального
    // Process.GetProcessById, который не вызвать в тесте за несуществующий pid
    // предсказуемо) -> какие сессии нужно респаунить.
    public static IReadOnlyList<int> SessionsNeedingRespawn(
        IEnumerable<KeyValuePair<int, int>> runningBySession,
        Func<int, bool> isAlive)
    {
        var result = new List<int>();
        foreach (var kv in runningBySession)
        {
            if (!isAlive(kv.Value)) result.Add(kv.Key);
        }
        return result;
    }
}
