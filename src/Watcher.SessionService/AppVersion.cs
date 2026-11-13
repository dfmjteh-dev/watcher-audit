using System.Reflection;

namespace Watcher.SessionService;

public static class AppVersion
{
    public static readonly string Current =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";
}
