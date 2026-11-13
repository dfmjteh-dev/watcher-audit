using System.Text.Json;
using Watcher.Capture;
using Xunit;

namespace Watcher.Capture.Linux.Tests;

// 0.13: отметка процесса записи — её читает Watcher.Uploader.Core/CaptureStatus.cs
// (тот же формат: user, configHash, recording, at).
public sealed class CaptureStatusFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"watcher-capstat-{Guid.NewGuid():N}");

    public CaptureStatusFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Writes_one_file_per_user_with_hash_and_state()
    {
        CaptureStatusFile.Write(_dir, "DOMAIN\\Иван Петров", "abc123", recording: true);
        CaptureStatusFile.Write(_dir, "DOMAIN\\Иван Петров", "def456", recording: false);

        var file = Assert.Single(Directory.GetFiles(_dir));
        Assert.StartsWith(CaptureStatusFile.Prefix, Path.GetFileName(file));
        Assert.EndsWith(CaptureStatusFile.Suffix, file);
        Assert.DoesNotContain("\\", Path.GetFileName(file));
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal("DOMAIN\\Иван Петров", doc.RootElement.GetProperty("user").GetString());
        Assert.Equal("def456", doc.RootElement.GetProperty("configHash").GetString());
        Assert.False(doc.RootElement.GetProperty("recording").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("at", out _));
    }

    [Fact]
    public void Missing_queue_dir_is_not_an_error()
    {
        CaptureStatusFile.Write(Path.Combine(_dir, "nope"), "u", null, true);
    }
}
