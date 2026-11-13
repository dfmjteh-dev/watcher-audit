using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

public sealed class UploadValidationRecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "watcher-validation-" + Guid.NewGuid().ToString("N"));
    public UploadValidationRecoveryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }
    private sealed class Inspector(bool? valid) : IVideoTranscoder
    {
        public Task<string?> CheckAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<bool> EncodeAsync(string input, string output, CancellationToken ct) => Task.FromResult(false);
        public Task<double?> DurationAsync(string path, CancellationToken ct) => Task.FromResult<double?>(null);
        public Task<VideoInspection> InspectAsync(string path, bool fullDecode, CancellationToken ct) => Task.FromResult(new VideoInspection(valid, "invalid-source"));
    }
    [Theory]
    [InlineData(false, true, 415)]
    [InlineData(null, true, 503)]
    [InlineData(true, true, 200)]
    [InlineData(false, false, 200)]
    public async Task Validation_is_negotiated_and_rejection_leaves_no_record_or_part(bool? valid, bool negotiate, int expected)
    {
        var db = new Db(Path.Combine(_dir, "watcher.db"));
        var cfg = new ServerConfig { DataDir = _dir };
        var ingest = new RecordingIngest(db, cfg, new Inspector(valid));
        var result = await ingest.VideoAsync(new AgentCaller(null), "clip.mp4", "{}", null,
            () => new MemoryStream("invalid"u8.ToArray()), null, DateTime.UtcNow, negotiate);
        Assert.Equal(expected, result.StatusCode);
        Assert.Equal(expected == 200, db.ByFilename("clip.mp4") is not null);
        Assert.Empty(Directory.GetFiles(_dir, "*.part", SearchOption.AllDirectories));
        if (expected == 415) Assert.Contains("invalid-video", JsonSerializer.Serialize(result.Body));
    }

    [Fact]
    public async Task Missing_inspector_does_not_label_source_as_corrupt()
    {
        string path = Path.Combine(_dir, "bad.mp4"); File.WriteAllText(path, "broken");
        var cfg = new ServerConfig { DataDir = _dir, Transcode = new() { FfmpegPath = "/definitely/missing/ffmpeg" } };
        var inspector = new FfmpegTranscoder(cfg, NullLogger<FfmpegTranscoder>.Instance);
        Assert.Null((await inspector.InspectAsync(path, true, CancellationToken.None)).Valid);
        // Реальный ffmpeg проверяется отдельным локальным smoke-сценарием, тест переносим между ОС.
    }
}
