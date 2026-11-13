using System.Text;
using System.Text.Json;
using Xunit;

namespace Watcher.Server.Tests;

// 0.12: консольная сессия кусками, пока идёт (RecordingIngest.Live.cs).
public class TerminalLiveIngestTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-live-{Guid.NewGuid():N}");
    private readonly Db _db;
    private readonly RecordingIngest _ingest;
    private static readonly AgentCaller SharedToken = new(null);
    private static readonly DateTime Now = new(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);

    public TerminalLiveIngestTests()
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

    private Task<IngestResult> Input(string text, long offset, bool live, string endedAt = "2026-10-12T09:05:00Z") =>
        _ingest.TerminalAsync(SharedToken, JsonSerializer.Serialize(new
        {
            kind = "terminal-input",
            host = "srv-1",
            sessionUser = "alice",
            sessionId = 7,
            startedAt = "2026-10-12T09:00:00Z",
            endedAt,
            input = text,
            inputOffset = offset,
            live,
        }), null, null, null, Now);

    private static UploadPart Part(string s) { var b = Encoding.UTF8.GetBytes(s); return new UploadPart(() => new MemoryStream(b), b.Length); }

    private Task<IngestResult> Output(string @out, long outOffset, string timing, long timingOffset, bool live, int? exitCode = null) =>
        _ingest.TerminalAsync(SharedToken, JsonSerializer.Serialize(new
        {
            kind = "terminal-output",
            host = "srv-1",
            sessionUser = "alice",
            auditSession = 7,
            startedAt = "2026-10-12T09:00:02Z",
            endedAt = "2026-10-12T09:06:00Z",
            outFile = "alice-1.out",
            timingFile = "alice-1.timing",
            outOffset,
            timingOffset,
            live,
            exitCode,
            source = "linux-script",
        }), Part(@out), Part(timing), null, Now);

    private static JsonElement Body(IngestResult r) => JsonSerializer.SerializeToElement(r.Body);

    [Fact]
    public async Task Input_chunks_append_skip_repeats_and_report_gaps()
    {
        Assert.Equal(200, (await Input("ls\n", 0, true)).StatusCode);
        var row = _db.TerminalByExternalId("srv-1:in:7:20261012T090000.000Z")!;
        Assert.True(row.Live);

        // Повтор с начала (агент перезапустился) — принятая часть не задваивается.
        Assert.Equal(200, (await Input("ls\npwd\n", 0, true)).StatusCode);
        var gap = await Input("x", 50, true);
        Assert.Equal(416, gap.StatusCode);
        Assert.Equal(7, Body(gap).GetProperty("inputLength").GetInt32());

        Assert.Equal(200, (await Input("exit\n", 7, false, "2026-10-12T09:10:00Z")).StatusCode);
        row = _db.TerminalById(row.Id)!;
        Assert.Equal("ls\npwd\nexit\n", row.Input);
        Assert.False(row.Live);
        Assert.Equal(600, row.DurationSec);
        Assert.Equal(409, (await Input("more", 12, true)).StatusCode);
    }

    [Fact]
    public async Task Output_chunks_append_files_count_active_time_and_hash_at_the_end()
    {
        Assert.Equal(200, (await Output("abc", 0, "0.5 3\n", 0, true)).StatusCode);
        // Второй кусок прислан вместе с уже принятым началом — дописывается только новое.
        var r = await Output("abcdef", 0, "0.5 3\n70.0 3\n", 0, true);
        Assert.Equal(200, r.StatusCode);
        Assert.Equal(6, Body(r).GetProperty("outSize").GetInt64());

        var row = _db.TerminalByExternalId("srv-1:out:alice-1.out")!;
        Assert.True(row.Live);
        Assert.Null(row.Sha256);
        Assert.Equal(0.5, row.ActiveSec);   // пауза 70 с — простой
        Assert.Equal(new DateTime(2026, 10, 12, 9, 1, 12, 500, DateTimeKind.Utc), row.LastActivityAt);
        Assert.Equal("abcdef", File.ReadAllText(Path.Combine(_dataDir, row.OutFilePath!)));

        Assert.Equal(416, (await Output("zz", 10, "", 0, true)).StatusCode);
        Assert.Equal(200, (await Output("ghi", 6, "1.0 3\n", 13, false, exitCode: 2)).StatusCode);
        row = _db.TerminalById(row.Id)!;
        Assert.False(row.Live);
        Assert.Equal(2, row.ExitCode);
        Assert.Equal(1.5, row.ActiveSec);
        Assert.Equal("abcdefghi", File.ReadAllText(Path.Combine(_dataDir, row.OutFilePath!)));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("abcdefghi"))).ToLowerInvariant(), row.Sha256);
        Assert.Equal(9 + 19, row.SizeBytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Output_is_linked_to_input_of_the_same_audit_session_whichever_comes_first(bool inputFirst)
    {
        if (inputFirst) { await Input("ls\n", 0, true); await Output("hello-from-output", 0, "0.1 17\n", 0, true); }
        else { await Output("hello-from-output", 0, "0.1 17\n", 0, true); await Input("ls\n", 0, true); }

        var input = _db.TerminalByExternalId("srv-1:in:7:20261012T090000.000Z")!;
        var output = _db.TerminalByExternalId("srv-1:out:alice-1.out")!;
        Assert.Equal(input.Id, output.ParentId);

        // Список «одна строка на сессию»: только ввод, и поиск по тексту вывода находит его.
        var rows = _db.QueryTerminal(null, null, null, null, null, "from-output", null, 10, sessions: true);
        Assert.Equal(input.Id, Assert.Single(rows).Id);
        Assert.Equal(output.Id, Assert.Single(_db.TerminalChildren([input.Id])).Id);
    }

    [Fact]
    public async Task Output_of_another_session_is_not_linked()
    {
        await Input("ls\n", 0, false);   // закрыта в 09:05
        await _ingest.TerminalAsync(SharedToken, JsonSerializer.Serialize(new
        {
            kind = "terminal-output",
            host = "srv-1",
            sessionUser = "alice",
            auditSession = 7,
            startedAt = "2026-10-12T11:00:00Z",
            endedAt = "2026-10-12T11:01:00Z",
            outFile = "alice-2.out",
            timingFile = "alice-2.timing",
            live = true,
        }), Part("x"), Part("0.1 1\n"), null, Now);
        Assert.Null(_db.TerminalByExternalId("srv-1:out:alice-2.out")!.ParentId);
    }

    [Fact]
    public async Task Session_without_chunks_stops_being_live_but_a_late_chunk_is_still_accepted()
    {
        await Input("ls\n", 0, true);
        Assert.Equal(0, _db.CloseStaleTerminalSessions(TimeSpan.FromHours(48)));
        Assert.Equal(1, _db.CloseStaleTerminalSessions(TimeSpan.Zero));
        var row = _db.TerminalByExternalId("srv-1:in:7:20261012T090000.000Z")!;
        Assert.False(row.Live);
        Assert.True(row.Stale);

        Assert.Equal(200, (await Input("pwd\n", 3, false)).StatusCode);
        row = _db.TerminalById(row.Id)!;
        Assert.Equal("ls\npwd\n", row.Input);
        Assert.False(row.Stale);
        Assert.Equal(409, (await Input("x", 7, true)).StatusCode);
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData("{}", 60)]
    [InlineData("{\"consoleLiveIntervalSec\":300}", 300)]
    [InlineData("{\"consoleLiveIntervalSec\":1}", 60)]
    public void Live_interval_comes_from_config(string? config, int expected) =>
        Assert.Equal(expected, RecordingIngest.LiveIntervalFor(config));

    // 0.13: срок хранения невыгруженного — консольному агенту заголовком.
    [Theory]
    [InlineData(null, 30)]
    [InlineData("{}", 30)]
    [InlineData("{\"queueMaxAgeDays\":7}", 7)]
    [InlineData("{\"queueMaxAgeDays\":0}", 0)]
    [InlineData("{\"queueMaxAgeDays\":99999}", 30)]
    [InlineData("{\"queueMaxAgeDays\":\"7\"}", 30)]
    public void Queue_max_age_comes_from_config(string? config, int expected) =>
        Assert.Equal(expected, RecordingIngest.QueueMaxAgeFor(config));
}
