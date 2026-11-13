using System.Text;
using System.Text.Json;
using Xunit;

namespace Watcher.Server.Tests;

// Приём записей без HTTP (RecordingIngest) — то, ради чего его вынесли из
// эндпоинтов: коды ответа агенту и что остаётся на диске/в БД. Сквозные
// HTTP-сценарии — UploadEndpointTests / TerminalUploadEndpointTests.
public class RecordingIngestTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-ingest-{Guid.NewGuid():N}");
    private readonly Db _db;
    private readonly RecordingIngest _ingest;
    private static readonly AgentCaller SharedToken = new(null);
    private static readonly DateTime Now = new(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);

    public RecordingIngestTests()
    {
        Directory.CreateDirectory(_dataDir);
        _db = new Db(Path.Combine(_dataDir, "watcher.db"));
        _ingest = new RecordingIngest(_db, new ServerConfig { DataDir = _dataDir });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private static string VideoMeta(string host = "pc-1") => JsonSerializer.Serialize(new
    {
        host,
        startedAt = "2026-10-12T09:59:00Z",
        endedAt = "2026-10-12T10:00:00Z",
        durationSec = 60,
        frames = 120,
    });

    private Task<IngestResult> Video(string filename, byte[] body, string? clientSha = null, string? meta = null) =>
        _ingest.VideoAsync(SharedToken, filename, meta ?? VideoMeta(), clientSha, () => new MemoryStream(body), null, Now);

    private static string Sha(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public async Task Video_is_stored_then_repeat_is_409_and_other_content_422()
    {
        var body = Encoding.UTF8.GetBytes("mp4-bytes");
        var first = await Video("seg-1.mp4", body, Sha(body));
        Assert.Equal(200, first.StatusCode);
        var seg = _db.ByFilename("seg-1.mp4")!;
        Assert.True(File.Exists(Path.Combine(_dataDir, seg.FilePath)));

        Assert.Equal(409, (await Video("seg-1.mp4", body, Sha(body))).StatusCode);
        var other = Encoding.UTF8.GetBytes("other");
        Assert.Equal(422, (await Video("seg-1.mp4", other, Sha(other))).StatusCode);
    }

    [Fact]
    public async Task Video_with_wrong_checksum_leaves_nothing_behind()
    {
        var r = await Video("seg-2.mp4", Encoding.UTF8.GetBytes("mp4"), clientSha: new string('0', 64));

        Assert.Equal(400, r.StatusCode);
        Assert.Null(_db.ByFilename("seg-2.mp4"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dataDir, "blobs"), "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"startedAt\": 5}")]
    public async Task Bad_meta_is_400_not_an_exception(string meta)
    {
        Assert.Equal(400, (await Video("seg-3.mp4", new byte[] { 1 }, meta: meta)).StatusCode);
        Assert.Equal(400, (await _ingest.TerminalAsync(SharedToken, meta, null, null, null, Now)).StatusCode);
    }

    [Fact]
    public async Task Terminal_input_repeat_is_409()
    {
        string meta = JsonSerializer.Serialize(new
        {
            kind = "terminal-input",
            host = "srv-1",
            sessionUser = "alice",
            sessionId = 7,
            startedAt = "2026-10-12T09:00:00Z",
            endedAt = "2026-10-12T09:05:00Z",
            input = "ls -la",
        });
        Assert.Equal(200, (await _ingest.TerminalAsync(SharedToken, meta, null, null, null, Now)).StatusCode);
        Assert.Equal(409, (await _ingest.TerminalAsync(SharedToken, meta, null, null, null, Now)).StatusCode);
    }

    [Fact]
    public async Task Terminal_record_of_excluded_user_is_not_stored()
    {
        long groupId = _db.CreateGroup("servers");
        _db.SetGroupConfig(groupId, "{\"excludeUsers\":[\"svc\"]}");
        _db.UpsertHeartbeat("srv-1", null, null);
        _db.SetHostGroup(_db.ListFleetHosts(null, 10, 0).Single().Id, groupId);
        string Meta(string user, int ses) => JsonSerializer.Serialize(new
        {
            kind = "terminal-input",
            host = "srv-1",
            sessionUser = user,
            sessionId = ses,
            startedAt = "2026-10-12T09:00:00Z",
            endedAt = "2026-10-12T09:05:00Z",
            input = "id",
        });

        // Доменное имя совпадает с коротким в списке.
        var skipped = await _ingest.TerminalAsync(SharedToken, Meta("CORP\\svc", 1), null, null, null, Now);
        Assert.Equal(200, skipped.StatusCode);
        Assert.Contains("\"skipped\":true", JsonSerializer.Serialize(skipped.Body));
        Assert.Null(_db.TerminalByExternalId("srv-1:in:1:20261012T090000.000Z"));

        var stored = await _ingest.TerminalAsync(SharedToken, Meta("alice", 2), null, null, null, Now);
        Assert.Equal(200, stored.StatusCode);
        Assert.DoesNotContain("skipped", JsonSerializer.Serialize(stored.Body));
        Assert.NotNull(_db.TerminalByExternalId("srv-1:in:2:20261012T090000.000Z"));
    }
}
