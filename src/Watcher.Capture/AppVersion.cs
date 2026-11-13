using System.Reflection;

namespace Watcher.Capture;

// Версия из Directory.Build.props (Version → AssemblyInformationalVersion через
// стандартный механизм SDK). Пишется в sidecar-метаданные сегмента и в стартовый
// лог — чтобы было видно, какая версия агента реально развёрнута на хосте, без
// ручной проверки хэша/таймстампа файла.
public static class AppVersion
{
    public static readonly string Current =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";
}
