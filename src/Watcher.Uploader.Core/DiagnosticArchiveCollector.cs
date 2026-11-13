using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Watcher.Uploader;

// Проекция по закрытой схеме. Исходные строки, пути, имена файлов и исключения не отправляются.
public static class DiagnosticArchiveCollector
{
    public static DiagnosticArchiveReport Collect(DiagnosticArchiveRequest request, string version, IEnumerable<AgentDiagnostic> events,
        string root, string? binding, string kind, string? logDirectory, DateTime now, string? updateDirectory = null)
    {
        var updates = new List<UpdateProgressReport>(); var logs = new List<DiagnosticLogSummary>();
        string states = Path.Combine(root, "update-progress", "state");
        if (binding is not null && Directory.Exists(states))
            foreach (string path in Directory.EnumerateFiles(states, "*.json").Take(64))
                try
                {
                    var item = JsonSerializer.Deserialize<GuardProgressEnvelope>(ReadBounded(path, 4096, out _), GuardPaths.Json);
                    if (item is not null && item.Binding == binding && item.Kind == kind && UpdateProgressContract.Valid(item.Report)) updates.Add(item.Report);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        void Logs(string? directory, string pattern, string source)
        {
            if (directory is null || !Directory.Exists(directory)) return;
            try
            {
                if (Linked(directory)) return;
                foreach (string path in Directory.EnumerateFiles(directory, pattern).Take(512).OrderByDescending(File.GetLastWriteTimeUtc).Take(2))
                    try { logs.Add(Summarize(ReadBounded(path, 1048576, out bool cut), source, cut)); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        Logs(logDirectory, kind == "terminal" ? "watcher-term-*.log" : "uploader-*.log", kind == "terminal" ? "terminal" : "agent");
        if (OperatingSystem.IsWindows()) Logs(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Watcher", "session-service-logs"), "watcher-session-service-*.log", "service");
        Logs(Path.Combine(root, "logs"), "msi-*.log", "msi");
        Logs(Path.Combine(root, "logs"), "rollback-msi.log", "msi");
        if (updateDirectory is not null && Directory.Exists(updateDirectory))
            foreach (string directory in Directory.EnumerateDirectories(updateDirectory).Take(512).OrderByDescending(Directory.GetLastWriteTimeUtc).Take(2))
                Logs(directory, "msi.log", "msi");
        return new(request.Id, now, version, OperatingSystem.IsWindows() ? "windows" : "linux", events.Where(DiagnosticContract.Valid).Take(128).ToList(), updates.TakeLast(16).ToList(), logs.Take(8).ToList());
    }
    private static bool Linked(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
    private static byte[] ReadBounded(string path, int maximum, out bool truncated)
    {
        if (Linked(path)) throw new IOException("Linked diagnostic input");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        truncated = file.Length > maximum;
        if (truncated) file.Seek(-maximum, SeekOrigin.End);
        byte[] bytes = new byte[Math.Min(file.Length, maximum)]; int count = 0, read;
        while (count < bytes.Length && (read = file.Read(bytes, count, bytes.Length - count)) > 0) count += read;
        return bytes.AsSpan(0, count).ToArray();
    }
    public static DiagnosticLogSummary Summarize(byte[] bytes, string source, bool truncated)
    {
        // MSI verbose-журналы обычно UTF-16; нельзя отправлять произвольный текст даже после regex redaction.
        bool bom = bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe;
        bool utf16 = bom || bytes.Take(64).Count(b => b == 0) > 8;
        string text = utf16 ? Encoding.Unicode.GetString(bytes, bom ? 2 : 0, bytes.Length - (bom ? 2 : 0)) : Encoding.UTF8.GetString(bytes);
        if (text.Length > 1048576) text = text[^1048576..];
        using var reader = new StringReader(text); int lines = 0, warnings = 0, errors = 0; var codes = new List<int>(); string? line;
        while (lines < 20000 && (line = reader.ReadLine()) is not null)
        {
            lines++;
            if (line.Length > 8192) continue;
            if (line.Contains("[WRN]", StringComparison.Ordinal)) warnings++;
            if (line.Contains("[ERR]", StringComparison.Ordinal) || line.Contains("[FTL]", StringComparison.Ordinal)) errors++;
            const string marker = "MainEngineThread is returning "; int index = line.IndexOf(marker, StringComparison.Ordinal);
            if (source == "msi" && index >= 0 && codes.Count < 32 && int.TryParse(line.AsSpan(index + marker.Length).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int code) && code is >= 0 and <= 65535) codes.Add(code);
        }
        return new(source, lines, warnings, errors, truncated || lines == 20000, codes);
    }
}
