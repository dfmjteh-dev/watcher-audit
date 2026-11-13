using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Watcher.Uploader;

// Один защищённый файл на служебного производителя. Никаких исходных строк логов.
public sealed class DiagnosticJournal
{
    public const int MaxEvents = 256, MaxFileBytes = 131072;
    private static readonly ConcurrentDictionary<string, DiagnosticJournal> Journals = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly string _path;
    public string AcknowledgementPath => _path + ".capture-acked";
    private readonly Dictionary<string, DateTime> _last = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DiagnosticJournal(string path) => _path = path;
    public static DiagnosticJournal At(string path) => Journals.GetOrAdd(Path.GetFullPath(path), p => new(p));
    public static DiagnosticJournal For(string credentialPath) => At(Path.Combine(Path.GetDirectoryName(credentialPath)!, "diagnostics.json"));
    private List<AgentDiagnostic> Load()
    {
        if (!File.Exists(_path)) return [];
        // Ошибку чтения не превращаем в пустую очередь и не перетираем повреждённый файл.
        if (new FileInfo(_path).Length > MaxFileBytes) throw new IOException("Diagnostic journal exceeds limit");
        var parsed = JsonSerializer.Deserialize<List<AgentDiagnostic>>(File.ReadAllBytes(_path), Json) ?? [];
        if (parsed.Count > MaxEvents || parsed.Any(e => !DiagnosticContract.Valid(e))) throw new IOException("Invalid diagnostic journal");
        return parsed;
    }
    private void Save(List<AgentDiagnostic> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, values, Json); stream.Flush(true); }
            File.Move(temp, _path, overwrite: true);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }
    public void Record(string component, string code, bool error, DateTime now)
    {
        var entry = new AgentDiagnostic(Guid.NewGuid().ToString("N"), now.ToUniversalTime(), error ? "error" : "warning", component, code);
        if (!DiagnosticContract.Valid(entry)) return;
        lock (_gate)
        {
            using var lease = LockFile();
            string key = component + ":" + code + ":" + error;
            if (_last.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(30)) return;
            var copy = Load().ToList(); copy.Add(entry);
            if (copy.Count > MaxEvents) copy.RemoveRange(0, copy.Count - MaxEvents);
            Save(copy); _last[key] = now;
        }
    }
    public List<AgentDiagnostic> Pending()
    { lock (_gate) { using var lease = LockFile(); return Load().Take(32).ToList(); } }
    public void Acknowledge(IEnumerable<string> ids)
    { lock (_gate) { using var lease = LockFile(); var accepted = ids.ToHashSet(StringComparer.Ordinal); Save(Load().Where(e => !accepted.Contains(e.Id)).ToList()); } }

    private FileStream LockFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 5) { Thread.Sleep(20); }
        }
    }
    public void Capture(string component, bool error, string? template)
    {
        string code = template switch
        {
            var s when s?.Contains("heartbeat", StringComparison.OrdinalIgnoreCase) == true => "server-unreachable",
            var s when s?.Contains("credential", StringComparison.OrdinalIgnoreCase) == true => "credential-rejected",
            var s when s?.Contains("transcript", StringComparison.OrdinalIgnoreCase) == true => "transcript-unclassified",
            var s when s?.Contains("audit", StringComparison.OrdinalIgnoreCase) == true => "audit-read-failed",
            var s when s?.Contains("upload", StringComparison.OrdinalIgnoreCase) == true => "upload-failed",
            var s when s?.Contains("update", StringComparison.OrdinalIgnoreCase) == true => "update-failed",
            var s when s?.Contains("config", StringComparison.OrdinalIgnoreCase) == true => "config-invalid",
            _ => error ? "agent-error" : "agent-warning"
        };
        try { Record(component, code, error, DateTime.UtcNow); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { /* Локальный лог продолжает работать; не рекурсируем в logger. */ }
    }
}

public sealed class DiagnosticLoggerProvider(DiagnosticJournal journal, string component) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(journal, component);
    public void Dispose() { }
    private sealed class Logger(DiagnosticJournal journal, string component) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level is LogLevel.Warning or LogLevel.Error or LogLevel.Critical;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            string? template = state is IEnumerable<KeyValuePair<string, object?>> fields
                ? fields.FirstOrDefault(p => p.Key == "{OriginalFormat}").Value as string : null;
            // formatter, args и exception.Message сознательно не читаются.
            journal.Capture(component, level >= LogLevel.Error, template);
        }
    }
}
