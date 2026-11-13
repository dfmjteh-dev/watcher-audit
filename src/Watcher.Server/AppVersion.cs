using System.Reflection;

namespace Watcher.Server;

// Версия из Directory.Build.props (Version → AssemblyInformationalVersion через
// стандартный механизм SDK) — пишется в стартовый лог, чтобы было видно, какая
// версия сервера реально развёрнута.
public static class AppVersion
{
    public static readonly string Current =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";

    // Этап E2: SDK дописывает к InformationalVersion коммит сборки
    // ("0.8.0+1be3ed0…"), агенты одной и той же версии, собранные с другого
    // коммита, отличаются только этим хвостом — для сравнения и показа он лишний.
    public static string Core(string version)
    {
        int plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    // «Версия агента старше target» — target теперь версия, назначенная группе
    // или последний пакет (AgentVersionPolicy), не версия сервера: они раздельные.
    // Именно старше, не «не равен». Нераспознаваемые версии — по неравенству строк.
    public static bool IsAgentOutdated(string? agentVersion, string targetVersion)
    {
        if (string.IsNullOrEmpty(agentVersion)) return false;
        string agent = Core(agentVersion), target = Core(targetVersion);
        if (Version.TryParse(agent, out var a) && Version.TryParse(target, out var t)) return a < t;
        return agent != target;
    }
}
