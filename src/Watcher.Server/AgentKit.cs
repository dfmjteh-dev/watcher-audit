using System.Text.Json;
using System.Text.Json.Serialization;

namespace Watcher.Server;

// Комплект установки агентов, который собирает install-server.sh
// (tools/agent-kit/build-kit.sh) в Server:AgentKitDir: MSI, zip, deb/rpm,
// сертификаты. Сервер его только раздаёт администраторам — страница
// «Установка агентов». Отдаются ровно файлы из манифеста kit.json, никакие
// другие пути каталога (там, например, лежит updates/packages.tsv).
public sealed record AgentKitFile(string Path, long Size, string Sha256);

public sealed record AgentKitManifest(
    string Version,
    string ServerUrl,
    DateTime BuiltAt,
    string? UpdateKey,
    string? EnrollTokenSha256,
    List<AgentKitFile> Files);

public static class AgentKit
{
    public const string ManifestName = "kit.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.Strict,
    };

    // null — каталог не задан, комплекта нет или манифест не читается.
    public static AgentKitManifest? Load(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        string path = Path.Combine(dir, ManifestName);
        try
        {
            if (!File.Exists(path)) return null;
            var m = JsonSerializer.Deserialize<AgentKitManifest>(File.ReadAllText(path), Json);
            return m is { Files: not null } ? m : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // Полный путь файла комплекта — только если он есть в манифесте и лежит
    // внутри каталога (манифест пишет install-server.sh, но и ему не доверяем
    // путь вида ../).
    public static string? Resolve(string dir, AgentKitManifest m, string relPath)
    {
        if (!m.Files.Any(f => f.Path == relPath)) return null;
        string root = Path.GetFullPath(dir);
        string full = Path.GetFullPath(Path.Combine(root, relPath));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;
        return File.Exists(full) ? full : null;
    }
}
