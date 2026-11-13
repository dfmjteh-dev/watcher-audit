using System.Text.Json;
using Microsoft.Extensions.Logging;
using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

public sealed class DiagnosticTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "watcher-diag-" + Guid.NewGuid().ToString("N"));
    public DiagnosticTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);
    [Fact]
    public void Logger_does_not_render_or_persist_secrets_or_exception_text()
    {
        string path = Path.Combine(_dir, "events.json"); var journal = DiagnosticJournal.At(path);
        using var provider = new DiagnosticLoggerProvider(journal, "service"); var logger = provider.CreateLogger("source");
        logger.Log(LogLevel.Error, new EventId(1), new[] { new KeyValuePair<string, object?>("{OriginalFormat}", "upload failed {Password}"), new("Password", "NEVER-EXPORT-SECRET") }, new Exception("TRANSCRIPT-PRIVATE"), (s, e) => throw new Exception("formatter called"));
        string persisted = File.ReadAllText(path);
        Assert.DoesNotContain("NEVER-EXPORT-SECRET", persisted); Assert.DoesNotContain("TRANSCRIPT-PRIVATE", persisted); Assert.DoesNotContain("Password", persisted);
        Assert.Equal("upload-failed", Assert.Single(journal.Pending()).Code);
        string reopened = Path.Combine(_dir, "reopened.json"); File.Copy(path, reopened);
        Assert.Single(DiagnosticJournal.At(reopened).Pending());
    }
    [Fact]
    public void Ack_only_removes_confirmed_ids_and_queue_stays_bounded()
    {
        string path = Path.Combine(_dir, "events.json"); var journal = DiagnosticJournal.At(path); var now = DateTime.UtcNow;
        for (int i = 0; i < 300; i++) journal.Record("service", "agent-error", true, now.AddSeconds(i * 31));
        Assert.Equal(256, JsonSerializer.Deserialize<List<Watcher.AgentDiagnostic>>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Count);
        var sent = journal.Pending(); journal.Record("service", "agent-error", true, now.AddSeconds(10000));
        journal.Acknowledge(sent.Select(e => e.Id));
        Assert.DoesNotContain(journal.Pending(), e => sent.Any(s => s.Id == e.Id));
        Assert.True(new FileInfo(path).Length < DiagnosticJournal.MaxFileBytes);
    }
    [Fact]
    public void Capture_reader_ignores_links_and_never_deletes_producer_files()
    {
        string folder = _dir; Directory.CreateDirectory(folder);
        string id = Guid.NewGuid().ToString("N"); string file = Path.Combine(folder, "watcher-diagnostic-test-" + id + ".json");
        File.WriteAllText(file, JsonSerializer.Serialize(new Watcher.AgentDiagnostic(id, DateTime.UtcNow, "error", "capture", "capture-stopped"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        string ack = Path.Combine(_dir, "private", "acked.json");
        Assert.Single(DiagnosticSpool.Read(_dir, ack)); DiagnosticSpool.Acknowledge(ack, [id]);
        Assert.Empty(DiagnosticSpool.Read(_dir, ack)); Assert.True(File.Exists(file));
        if (!OperatingSystem.IsWindows())
        {
            string link = Path.Combine(folder, "watcher-diagnostic-link-" + Guid.NewGuid().ToString("N") + ".json"); File.CreateSymbolicLink(link, file);
            Assert.Empty(DiagnosticSpool.Read(_dir, ack));
        }
    }
}
