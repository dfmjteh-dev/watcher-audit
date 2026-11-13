using System.Text.Json;

namespace Watcher.Uploader;

// Пользовательский Capture пишет только отдельные неизменяемые события.
// Системный Uploader не изменяет и не удаляет файлы из пользовательского каталога.
public static class DiagnosticSpool
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static DateTime _last;
    public static void Write(string queueDir, string? template, bool error)
    {
        if (DateTime.UtcNow - _last < TimeSpan.FromSeconds(30)) return;
        _last = DateTime.UtcNow;
        string directory = queueDir;
        try
        {
            Directory.CreateDirectory(directory);
            string owner = OperatingSystem.IsWindows() ? System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "system" : Watcher.TerminalAgent.UnixFile.CurrentUid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string prefix = "watcher-diagnostic-" + owner + "-";
            var files = Directory.EnumerateFiles(directory, prefix + "*.json").Take(257).Select(f => new FileInfo(f)).OrderBy(f => f.LastWriteTimeUtc).ToList();
            foreach (var old in files.Take(Math.Max(0, files.Count - 255))) old.Delete();
            // Классифицируем без сохранения шаблона, аргументов и текста исключения.
            var e = new AgentDiagnostic(Guid.NewGuid().ToString("N"), DateTime.UtcNow, error ? "error" : "warning", "capture",
                template?.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase) == true ? "capture-stopped" : error ? "agent-error" : "agent-warning");
            string path = Path.Combine(directory, prefix + e.Id + ".json");
            string temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, e, Json); stream.Flush(true); }
            File.Move(temp, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    public static List<AgentDiagnostic> Read(string queueDir, string acknowledgementPath)
    {
        var seen = LoadAcknowledged(acknowledgementPath).ToHashSet(StringComparer.Ordinal);
        string directory = queueDir;
        var result = new List<AgentDiagnostic>();
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return result;
        foreach (string path in Directory.EnumerateFiles(directory, "watcher-diagnostic-*.json").Take(8192))
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            if (stem.Length < 32) continue;
            string id = stem[^32..];
            if (!Guid.TryParseExact(id, "N", out _) || seen.Contains(id)) continue;
            try
            {
                using var stream = OperatingSystem.IsLinux()
                    ? Watcher.TerminalAgent.UnixFile.OpenRegularNoFollow(path, out _, out _)
                    : OpenWindows(path);
                if (stream is null || !stream.CanSeek || stream.Length > 2048) continue;
                var e = JsonSerializer.Deserialize<AgentDiagnostic>(stream, Json);
                if (e is not null && e.Id == id && e.Component == "capture" && e.At >= DateTime.UtcNow.AddHours(-1) && DiagnosticContract.Valid(e)) result.Add(e);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
            if (result.Count == 16) break;
        }
        return result;
    }
    private static FileStream? OpenWindows(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
    }
    private static List<string> LoadAcknowledged(string path)
    {
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 1048576) throw new IOException("Diagnostic acknowledgement exceeds limit");
        return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path), Json) ?? [];
    }
    public static void Acknowledge(string path, IEnumerable<string> ids)
    {
        var values = LoadAcknowledged(path).Concat(ids).Distinct().TakeLast(16384).ToList();
        string temp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, values, Json); stream.Flush(true); }
        File.Move(temp, path, overwrite: true);
    }
}
