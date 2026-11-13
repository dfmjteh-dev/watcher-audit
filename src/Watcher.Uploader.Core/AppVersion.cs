using System.Reflection;

namespace Watcher.Uploader;

// Версия из Directory.Build.props (Version → AssemblyInformationalVersion через
// стандартный механизм SDK) — пишется в стартовый лог, чтобы было видно, какая
// версия агента реально развёрнута на хосте.
public static class AppVersion
{
    public static readonly string Current =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";
}
