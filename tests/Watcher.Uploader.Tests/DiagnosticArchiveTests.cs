using System.Text;
using System.Text.Json;
using Watcher;
using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

public sealed class DiagnosticArchiveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Log_projection_retains_counts_and_native_codes_without_original_text(bool unicode)
    {
        string text = "10:00:00.000 [WRN] token=PRIVATE\n10:00:01.000 [ERR] user=session-content\nMSI (s) (00:00): MainEngineThread is returning 1603\nMainEngineThread is returning token=PRIVATE\n";
        byte[] bytes = unicode ? [0xff, 0xfe, .. Encoding.Unicode.GetBytes(text)] : Encoding.UTF8.GetBytes(text);
        var summary = DiagnosticArchiveCollector.Summarize(bytes, "msi", false);
        Assert.Equal(1, summary.Warnings); Assert.Equal(1, summary.Errors); Assert.Equal([1603], summary.InstallerCodes);
        string json = JsonSerializer.Serialize(summary); Assert.DoesNotContain("PRIVATE", json); Assert.DoesNotContain("session-content", json);
    }
    [Fact]
    public void Ordinary_msi_update_uses_bounded_tail_and_never_copies_large_raw_log()
    {
        string root = Path.Combine(Path.GetTempPath(), "watcher-archive-tail-" + Guid.NewGuid().ToString("N"));
        string version = Path.Combine(root, "updates", "0.15.1"); Directory.CreateDirectory(version);
        try
        {
            File.WriteAllText(Path.Combine(version, "msi.log"), new string('x', 1100000) + "\nMainEngineThread is returning 3010\n");
            var report = DiagnosticArchiveCollector.Collect(new(Guid.NewGuid().ToString("N"), DateTime.UtcNow.AddHours(1)), "0.15.1", [], root, null, "video", null, DateTime.UtcNow, Path.Combine(root, "updates"));
            var log = Assert.Single(report.Logs); Assert.True(log.Truncated); Assert.Equal([3010], log.InstallerCodes);
            Assert.True(JsonSerializer.Serialize(report).Length < 2048);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void Collector_does_not_read_configs_credentials_sessions_or_arbitrary_files()
    {
        string root = Path.Combine(Path.GetTempPath(), "watcher-archive-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "secret.json"), "PRIVATE"); File.WriteAllText(Path.Combine(root, "session.txt"), "SESSION");
            File.WriteAllText(Path.Combine(root, "uploader-20261105.log"), "00:00:00.000 [ERR] PRIVATE");
            var report = DiagnosticArchiveCollector.Collect(new(Guid.NewGuid().ToString("N"), DateTime.UtcNow.AddHours(1)), "0.15.1", [], root, "binding", "video", root, DateTime.UtcNow);
            Assert.True(DiagnosticArchiveContract.Valid(report)); Assert.Single(report.Logs); Assert.Equal(1, report.Logs[0].Errors);
            string json = JsonSerializer.Serialize(report); Assert.DoesNotContain("PRIVATE", json); Assert.DoesNotContain("SESSION", json); Assert.DoesNotContain(root, json);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void Collector_refuses_symbolic_log_file()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "watcher-archive-link-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "outside"), "[ERR] private"); File.CreateSymbolicLink(Path.Combine(root, "uploader-1.log"), Path.Combine(root, "outside"));
            var report = DiagnosticArchiveCollector.Collect(new(Guid.NewGuid().ToString("N"), DateTime.UtcNow.AddHours(1)), "0.15.1", [], root, null, "video", root, DateTime.UtcNow);
            Assert.Empty(report.Logs);
        }
        finally { Directory.Delete(root, true); }
    }
}
