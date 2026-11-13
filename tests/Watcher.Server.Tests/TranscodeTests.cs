using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

public class TranscodeTests : IDisposable
{
    private readonly string _dataDir;
    private readonly Db _db;
    private readonly ServerConfig _cfg;

    public TranscodeTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-transcode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _db = new Db(Path.Combine(_dataDir, "watcher.db"));
        _cfg = new ServerConfig { DataDir = _dataDir, Transcode = new TranscodeConfig { MinAgeMinutes = 30 } };
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    // Подставной ffmpeg: пишет outSize байт, длительность — по словарю.
    private sealed class FakeTranscoder : IVideoTranscoder
    {
        public int OutSize = 100;
        public bool Fail;
        public bool? Valid = true;
        public bool Remux;
        public Action? DuringInspect;
        public Task<VideoInspection> InspectAsync(string path, bool fullDecode, CancellationToken ct)
        { DuringInspect?.Invoke(); return Task.FromResult(new VideoInspection(path.Contains(".part", StringComparison.Ordinal) && Remux ? true : Valid, "invalid-source", Duration: 60)); }
        public async Task<bool> RemuxAsync(string input, string output, CancellationToken ct)
        { if (Remux) await File.WriteAllBytesAsync(output, new byte[200], ct); return Remux; }
        public double InDuration = 60, OutDuration = 60;
        public Action? DuringEncode;
        public int Calls;
        public int Delay;
        public bool WritePartialBeforeDelay;
        public Func<string, int>? DelayFor;   // задержка по входному файлу (долгий сегмент)
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> Finished = new();
        private int _running;
        public int MaxConcurrent;
        public Task<string?> CheckAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public async Task<bool> EncodeAsync(string input, string output, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            int now = Interlocked.Increment(ref _running);
            lock (this) MaxConcurrent = Math.Max(MaxConcurrent, now);
            try
            {
                if (WritePartialBeforeDelay) await File.WriteAllBytesAsync(output, new byte[10], ct);
                DuringEncode?.Invoke();
                int delay = DelayFor?.Invoke(input) ?? Delay;
                if (delay > 0) await Task.Delay(delay, ct);
                if (Fail) return false;
                await File.WriteAllBytesAsync(output, new byte[OutSize], ct);
                Finished[input] = DateTime.UtcNow;
                return true;
            }
            finally { Interlocked.Decrement(ref _running); }
        }
        public Task<double?> DurationAsync(string path, CancellationToken ct) =>
            Task.FromResult<double?>(path.Contains(".part", StringComparison.Ordinal) ? OutDuration : InDuration);
    }

    private (long Id, string Rel) InsertSegment(DateTime uploadedAt, int size = 1000, string host = "host-a")
    {
        string name = $"seg-{Guid.NewGuid():N}.mp4";
        string rel = $"blobs/{host}/2026-10-18/{name}";
        string full = Path.Combine(_dataDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[size]);
        long id = _db.Insert(new Segment(
            Id: 0, Host: host, Filename: name,
            StartedAt: uploadedAt.AddMinutes(-5), EndedAt: uploadedAt, DurationSec: 60,
            Frames: 120, MotionEvents: 1, StartReason: "motion",
            SizeBytes: size, Sha256: "aaaaaaaaaaaaaaaaaaaa", FilePath: rel, UploadedAt: uploadedAt));
        return (id, rel);
    }

    private sealed class AbundantMemory : ITranscodeMemorySource
    {
        public TranscodeMemorySnapshot? Read() => new(64L << 30, 60L << 30);
    }

    private TranscodeService Service(FakeTranscoder t) =>
        new(_db, _cfg, t, NullLogger<TranscodeService>.Instance, memory: new AbundantMemory());

    [Fact]
    public async Task Transcodes_old_enough_segment_and_deletes_original()
    {
        var (id, rel) = InsertSegment(DateTime.UtcNow.AddHours(-2), size: 1000);
        var t = new FakeTranscoder { OutSize = 200 };

        var res = await Service(t).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, res.Done);
        var s = _db.ById(id)!;
        Assert.Equal("done", s.TranscodeStatus);
        Assert.Equal(TranscodeService.NewRelPath(rel), s.FilePath);
        Assert.Equal(200, s.SizeBytes);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaa", s.SourceSha256);
        Assert.NotEqual(s.SourceSha256, s.Sha256);
        Assert.True(File.Exists(Path.Combine(_dataDir, s.FilePath)));
        Assert.False(File.Exists(Path.Combine(_dataDir, rel)));
        Assert.Empty(Directory.GetFiles(_dataDir, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Stats_and_state_show_progress_for_overview()
    {
        InsertSegment(DateTime.UtcNow.AddHours(-2), size: 1000);
        InsertSegment(DateTime.UtcNow.AddMinutes(-5), size: 500);   // ещё рано — в очереди
        var state = new TranscodeState();
        var svc = new TranscodeService(_db, _cfg, new FakeTranscoder { OutSize = 250 }, NullLogger<TranscodeService>.Instance, state, new AbundantMemory());

        await svc.RunOnceAsync(CancellationToken.None);

        var st = _db.TranscodeStats();
        Assert.Equal(1, st.Done);
        Assert.Equal(1000, st.SourceBytes);
        Assert.Equal(250, st.DoneBytes);
        Assert.Equal(1, st.Pending);
        Assert.Equal(500, st.PendingBytes);
        string snap = System.Text.Json.JsonSerializer.Serialize(state.Snapshot());
        Assert.Contains("\"running\":false", snap);
        Assert.Contains("\"savedBytes\":750", snap);
        Assert.Contains("\"nextRunAt\":\"", snap);
    }

    [Fact]
    public async Task Several_files_are_encoded_in_parallel_each_once()
    {
        _cfg.Transcode.Workers = 3;
        var ids = Enumerable.Range(0, 9).Select(_ => InsertSegment(DateTime.UtcNow.AddHours(-2), size: 1000).Id).ToList();
        var t = new FakeTranscoder { OutSize = 200, Delay = 150 };

        var res = await Service(t).RunOnceAsync(CancellationToken.None);

        Assert.Equal(9, res.Done);
        Assert.Equal(9, t.Calls);
        Assert.Equal(9000, res.BytesBefore);
        Assert.Equal(1800, res.BytesAfter);
        Assert.InRange(t.MaxConcurrent, 2, 3);
        Assert.All(ids, id => Assert.Equal("done", _db.ById(id)!.TranscodeStatus));
        Assert.Equal(0, _db.TranscodePendingCount());
    }

    // bug-watcher.md (прод 0.14.1): пачка ждала свой самый долгий файл, и
    // остальные воркеры простаивали до следующей. Непрерывный пул: долгий файл
    // занимает одного воркера, остальные берут следующие сразу.
    [Fact]
    public async Task Long_file_does_not_stall_other_workers()
    {
        _cfg.Transcode.Workers = 2;
        var (_, slowRel) = InsertSegment(DateTime.UtcNow.AddHours(-1), size: 1000);   // самый новый — первый в очереди
        for (int i = 0; i < 30; i++) InsertSegment(DateTime.UtcNow.AddHours(-2), size: 1000);
        string slow = Path.GetFullPath(Path.Combine(_dataDir, slowRel));
        var t = new FakeTranscoder { OutSize = 200, DelayFor = f => f == slow ? 1500 : 15 };

        var res = await Service(t).RunOnceAsync(CancellationToken.None);

        Assert.Equal(31, res.Done);
        DateTime slowDone = t.Finished[slow];
        // 30 коротких по 15 мс на одном воркере — ~0,5 с: все раньше долгого (1,5 с).
        Assert.All(t.Finished.Where(kv => kv.Key != slow), kv => Assert.True(kv.Value < slowDone, "короткий файл ждал долгий"));
    }

    [Theory]
    [InlineData(8, 14L << 30, 1, 600, 7)]    // прод 4.140: ядра − 1
    [InlineData(16, 14L << 30, 1, 600, 15)]  // прод после 16 ядер — память позволяет
    [InlineData(4, 7L << 30, 1, 600, 3)]     // test-2
    [InlineData(8, 14L << 30, 2, 600, 3)]    // по 2 потока на файл
    [InlineData(16, 2L << 30, 1, 600, 2)]    // мало памяти: 1,5 ГБ на ffmpeg / 600 МБ
    [InlineData(1, 1L << 30, 1, 600, 1)]     // одно ядро — всё равно один
    public void Auto_workers_follow_cores_threads_and_memory(int cores, long mem, int threads, int perWorker, int expected) =>
        Assert.Equal(expected, TranscodeConfig.AutoWorkers(cores, mem, threads, perWorker));

    [Fact]
    public void Explicit_workers_win()
    {
        Assert.Equal(5, new TranscodeConfig { Workers = 5 }.EffectiveWorkers);
        Assert.InRange(new TranscodeConfig().EffectiveWorkers, 1, Environment.ProcessorCount);
    }

    private sealed class MutableMemory : ITranscodeMemorySource
    {
        public volatile bool Critical;
        public TranscodeMemorySnapshot? Read() => new(16L << 30, Critical ? 64L << 20 : 12L << 30);
    }

    [Fact]
    public async Task Pressure_cancellation_releases_lease_and_retry_preserves_source()
    {
        _cfg.Transcode.Workers = 1;
        var (id, rel) = InsertSegment(DateTime.UtcNow.AddHours(-2));
        var source = new MutableMemory();
        var fake = new FakeTranscoder { Delay = 30000, WritePartialBeforeDelay = true, DuringEncode = () => source.Critical = true };
        var state = new TranscodeState();
        var service = new TranscodeService(_db, _cfg, fake, NullLogger<TranscodeService>.Instance, state, source);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await service.RunOnceAsync(timeout.Token);
        Assert.Equal(0, result.Failed);
        Assert.Equal(new byte[1000], await File.ReadAllBytesAsync(Path.Combine(_dataDir, rel)));
        Assert.Null(_db.ById(id)!.TranscodeStatus);
        Assert.Single(_db.TranscodeCandidates(DateTime.UtcNow, 10));
        Assert.Empty(Directory.GetFiles(_dataDir, "*.part", SearchOption.AllDirectories));
        source.Critical = false;
        fake.WritePartialBeforeDelay = false;
        fake.DuringEncode = null;
        fake.Delay = 0;
        result = await service.RunOnceAsync(timeout.Token);
        Assert.Equal(1, result.Done);
        Assert.Equal(0, result.Failed);
        Assert.False(File.Exists(Path.Combine(_dataDir, rel)));
    }

    [Fact]
    public async Task Fresh_segment_is_not_touched()
    {
        var (id, _) = InsertSegment(DateTime.UtcNow.AddMinutes(-5));
        var t = new FakeTranscoder();

        await Service(t).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, t.Calls);
        Assert.Null(_db.ById(id)!.TranscodeStatus);
    }

    [Fact]
    public async Task No_saving_keeps_original_and_is_not_retried()
    {
        var (id, rel) = InsertSegment(DateTime.UtcNow.AddHours(-2), size: 1000);
        var t = new FakeTranscoder { OutSize = 950 };   // меньше чем на 10 %
        var svc = Service(t);

        await svc.RunOnceAsync(CancellationToken.None);
        await svc.RunOnceAsync(CancellationToken.None);

        var s = _db.ById(id)!;
        Assert.Equal("kept", s.TranscodeStatus);
        Assert.Equal(rel, s.FilePath);
        Assert.Equal(1, t.Calls);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Path.Combine(_dataDir, rel))!));
    }

    [Fact]
    public async Task Ffmpeg_failure_schedules_retry_and_keeps_original()
    {
        var (id, rel) = InsertSegment(DateTime.UtcNow.AddHours(-2));
        var t = new FakeTranscoder { Fail = true };

        var res = await Service(t).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, res.Failed);
        var s = _db.ById(id)!;
        Assert.Null(s.TranscodeStatus);
        Assert.Equal("retry", Assert.Single(_db.TranscodeIssues()).Status);
        Assert.Equal(rel, s.FilePath);
        Assert.True(File.Exists(Path.Combine(_dataDir, rel)));
        Assert.Empty(Directory.GetFiles(_dataDir, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Duration_mismatch_fails_and_keeps_original()
    {
        var (id, rel) = InsertSegment(DateTime.UtcNow.AddHours(-2));
        var t = new FakeTranscoder { OutDuration = 30 };

        var res = await Service(t).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, res.Failed);
        var s = _db.ById(id)!;
        Assert.Null(s.TranscodeStatus);
        Assert.Equal("retry", Assert.Single(_db.TranscodeIssues()).Status);
        Assert.Equal(rel, s.FilePath);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaa", s.Sha256);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Path.Combine(_dataDir, rel))!));
    }

    [Theory]
    [InlineData(true, "kept")]
    [InlineData(null, "failed")]
    public async Task Retry_schedule_survives_reopen_and_finishes_without_losing_source(bool? valid, string status)
    {
        var now = DateTime.UtcNow;
        var (id, rel) = InsertSegment(now.AddHours(-2)); var fake = new FakeTranscoder { Fail = true, Valid = valid };
        foreach (int minute in new[] { 0, 5, 35, 155 })
        {
            var reopened = new Db(Path.Combine(_dataDir, "watcher.db"));
            await new TranscodeService(reopened, _cfg, fake, NullLogger<TranscodeService>.Instance, memory: new AbundantMemory()).RunOnceAsync(CancellationToken.None, now.AddMinutes(minute));
            if (minute == 0)
            {
                await Service(fake).RunOnceAsync(CancellationToken.None, now.AddMinutes(4)); Assert.Equal(1, fake.Calls);
            }
        }
        Assert.Equal(4, fake.Calls); Assert.Equal(status, _db.ById(id)!.TranscodeStatus);
        var issue = Assert.Single(_db.TranscodeIssues()); Assert.Equal(4, issue.Attempts); Assert.Null(issue.NextAt);
        Assert.True(File.Exists(Path.Combine(_dataDir, rel)));
    }

    [Fact]
    public async Task Invalid_source_is_copied_verified_and_audited_before_removal()
    {
        var now = DateTime.UtcNow; var (id, rel) = InsertSegment(now.AddHours(-2));
        await Service(new FakeTranscoder { Fail = true, Valid = false }).RunOnceAsync(CancellationToken.None, now);
        Assert.Null(_db.ById(id)); Assert.False(File.Exists(Path.Combine(_dataDir, rel)));
        var issue = Assert.Single(_db.TranscodeIssues()); Assert.Equal("quarantined", issue.Status);
        string folder = Path.Combine(_dataDir, issue.QuarantinePath!);
        Assert.Equal(new byte[1000], File.ReadAllBytes(Path.Combine(folder, "source.mp4")));
        Assert.Contains("Sha256", File.ReadAllText(Path.Combine(folder, "manifest.json")));
        Assert.Equal(1, _db.TranscodeStats().Quarantined);
        Assert.False(_db.CleanupTranscodeQuarantine(id, _dataDir, now, "admin", true).Eligible);
        Assert.True(_db.CleanupTranscodeQuarantine(id, _dataDir, now.AddDays(8), "admin").Eligible);
        Assert.True(Directory.Exists(folder));
        _db.EnsureHostExists("host-a");
        Assert.Equal(RenameOutcome.Renamed, _db.RenameHost(Assert.Single(_db.Inventory()).Id!.Value, "renamed-host"));
        Assert.Equal("renamed-host", Assert.Single(_db.TranscodeIssues()).Host);
        _db.CreateLegalHold("renamed-host", null, null, "hold archive", "admin");
        Assert.False(_db.CleanupTranscodeQuarantine(id, _dataDir, now.AddDays(8), "admin", true).Eligible);
    }

    [Fact]
    public async Task Cleanup_rollback_restores_archive_then_expired_archive_can_be_removed()
    {
        var now = DateTime.UtcNow; var (id, _) = InsertSegment(now.AddHours(-2));
        await Service(new FakeTranscoder { Fail = true, Valid = false }).RunOnceAsync(CancellationToken.None, now);
        string folder = Path.Combine(_dataDir, Assert.Single(_db.TranscodeIssues()).QuarantinePath!);
        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "CREATE TRIGGER cleanup_unavailable BEFORE UPDATE ON transcode_jobs WHEN NEW.status='expired' BEGIN SELECT RAISE(ABORT,'cleanup unavailable'); END";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _db.CleanupTranscodeQuarantine(id, _dataDir, now.AddDays(8), "admin", true));
        Assert.True(Directory.Exists(folder)); Assert.False(Directory.Exists(folder + ".deleting"));
        Assert.Empty(_db.QueryAudit(null, "transcode.quarantine-cleanup", null, null, 10, 0));
        Assert.Equal("quarantined", Assert.Single(_db.TranscodeIssues()).Status);
        using (var c = _db.Open())
        using (var cmd = c.CreateCommand()) { cmd.CommandText = "DROP TRIGGER cleanup_unavailable"; cmd.ExecuteNonQuery(); }
        Assert.True(_db.CleanupTranscodeQuarantine(id, _dataDir, now.AddDays(8), "admin", true).Applied);
        Assert.False(Directory.Exists(folder)); Assert.Equal("expired", Assert.Single(_db.TranscodeIssues()).Status);
        Assert.Single(_db.QueryAudit(null, "transcode.quarantine-cleanup", null, null, 10, 0));
    }

    [Fact]
    public async Task Invalid_source_remux_is_validated_before_replacement()
    {
        var (id, rel) = InsertSegment(DateTime.UtcNow.AddHours(-2));
        string collision = Path.Combine(_dataDir, Path.ChangeExtension(rel, ".repaired.mp4"));
        File.WriteAllText(collision, "another recording");
        await Service(new FakeTranscoder { Fail = true, Valid = false, Remux = true }).RunOnceAsync(CancellationToken.None);
        var segment = _db.ById(id)!; Assert.Equal("repaired", segment.TranscodeStatus);
        Assert.True(File.Exists(Path.Combine(_dataDir, segment.FilePath))); Assert.False(File.Exists(Path.Combine(_dataDir, rel)));
        Assert.Equal("another recording", File.ReadAllText(collision));
    }

    [Fact]
    public async Task Quarantine_limit_keeps_original()
    {
        var (id, rel) = InsertSegment(DateTime.UtcNow.AddHours(-2), 2 * 1024 * 1024);
        _cfg.Transcode.QuarantineMaxBytes = 1024 * 1024;
        await Service(new FakeTranscoder { Fail = true, Valid = false }).RunOnceAsync(CancellationToken.None);
        Assert.NotNull(_db.ById(id)); Assert.True(File.Exists(Path.Combine(_dataDir, rel)));
        Assert.Equal("quarantine-full", Assert.Single(_db.TranscodeIssues()).Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hold_or_audit_failure_during_quarantine_never_loses_original(bool hold)
    {
        var (id, rel) = InsertSegment(DateTime.UtcNow.AddHours(-2));
        if (!hold)
        {
            using var connection = _db.Open(); using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER audit_unavailable BEFORE INSERT ON audit_log BEGIN SELECT RAISE(ABORT,'audit unavailable'); END"; cmd.ExecuteNonQuery();
        }
        var fake = new FakeTranscoder { Fail = true, Valid = false, DuringInspect = hold ? () => _db.CreateLegalHold("host-a", null, null, "during inspection", "admin") : null };
        if (hold) await Service(fake).RunOnceAsync(CancellationToken.None);
        else await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => Service(fake).RunOnceAsync(CancellationToken.None));
        Assert.NotNull(_db.ById(id)); Assert.True(File.Exists(Path.Combine(_dataDir, rel)));
    }

    [Fact]
    public async Task Separate_services_cannot_encode_same_segment_twice()
    {
        InsertSegment(DateTime.UtcNow.AddHours(-2)); var fake = new FakeTranscoder { Delay = 100 };
        await Task.WhenAll(Service(fake).RunOnceAsync(CancellationToken.None), Service(fake).RunOnceAsync(CancellationToken.None));
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task Segment_under_legal_hold_is_not_transcoded()
    {
        var (id, _) = InsertSegment(DateTime.UtcNow.AddHours(-2));
        _db.CreateLegalHold("host-a", null, null, "расследование", "admin");
        var t = new FakeTranscoder();

        await Service(t).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, t.Calls);
        Assert.Null(_db.ById(id)!.TranscodeStatus);
    }

    [Fact]
    public async Task Segment_deleted_while_encoding_leaves_no_new_file()
    {
        var (id, rel) = InsertSegment(DateTime.UtcNow.AddHours(-2));
        var t = new FakeTranscoder { DuringEncode = () => _db.DeleteSegmentRow(id) };

        await Service(t).RunOnceAsync(CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(_dataDir, TranscodeService.NewRelPath(rel))));
    }

    [Fact]
    public async Task Original_left_after_crash_is_removed_by_reconcile()
    {
        var (id, rel) = InsertSegment(DateTime.UtcNow.AddHours(-2));
        var svc = Service(new FakeTranscoder());
        await svc.RunOnceAsync(CancellationToken.None);
        // Как будто сервер упал между записью в БД и удалением исходника.
        File.WriteAllBytes(Path.Combine(_dataDir, rel), new byte[1000]);

        Assert.Equal(1, svc.ReconcileOriginals(null));

        Assert.False(File.Exists(Path.Combine(_dataDir, rel)));
        Assert.True(File.Exists(Path.Combine(_dataDir, _db.ById(id)!.FilePath)));
    }

    [Fact]
    public void Stale_transcode_parts_are_swept_but_upload_parts_kept()
    {
        string dir = Path.Combine(_dataDir, "blobs/h/2026-10-19");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.av1.mp4.0123abcd.part"), [1]);
        File.WriteAllBytes(Path.Combine(dir, "b.mp4.4567cdef.part"), [1]);   // загрузка агента

        Assert.Equal(1, Service(new FakeTranscoder()).SweepStaleParts());

        Assert.False(File.Exists(Path.Combine(dir, "a.av1.mp4.0123abcd.part")));
        Assert.True(File.Exists(Path.Combine(dir, "b.mp4.4567cdef.part")));
    }

    [Fact]
    public void Retention_removes_orphan_transcoded_file_with_record()
    {
        var (id, rel) = InsertSegment(DateTime.UtcNow.AddDays(-10));
        // Падение между переносом файла и записью в БД: пережатый лежит, строка — на исходнике.
        string orphan = Path.Combine(_dataDir, TranscodeService.NewRelPath(rel));
        File.WriteAllBytes(orphan, new byte[10]);

        new RetentionService(_db, new ServerConfig { DataDir = _dataDir, RetentionDays = 5 }, NullLogger<RetentionService>.Instance) { DiskSpace = () => null }.RunOnce();

        Assert.Null(_db.ById(id));
        Assert.False(File.Exists(orphan));
        Assert.False(File.Exists(Path.Combine(_dataDir, rel)));
    }

    [Fact]
    public void File_variants_cover_original_and_transcoded()
    {
        Assert.Equal(["blobs/h/d/a.mp4", "blobs/h/d/a.av1.mp4"], TranscodeService.FileVariants("blobs/h/d/a.mp4"));
        Assert.Equal(["blobs/h/d/a.av1.mp4", "blobs/h/d/a.mp4"], TranscodeService.FileVariants("blobs/h/d/a.av1.mp4"));
    }

    [Fact]
    public void Encode_args_keep_faststart_mp4_and_video_only()
    {
        var args = FfmpegTranscoder.EncodeArgs("in.mp4", "out.part", new TranscodeConfig { Crf = 35, Preset = 8, Threads = 4 });
        Assert.Contains("+faststart", args);
        Assert.Contains("libsvtav1", args);
        Assert.Contains("lp=4:scm=1", args);
        Assert.Equal("out.part", args[^1]);
        Assert.Equal("mp4", args[^2]);
    }

    [Fact]
    public void New_path_stays_in_same_day_dir()
    {
        Assert.Equal("blobs/h/2026-10-18/a.av1.mp4", TranscodeService.NewRelPath("blobs/h/2026-10-18/a.mp4"));
    }
}

// URL видео с версией файла (?v=): после подмены файла плеер со старой версией
// получает 410, а не куски другого файла; текущая версия и без v — 206/200.
public class TranscodeVideoEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;
    private readonly Db _db;

    public TranscodeVideoEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-transcode-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        Environment.SetEnvironmentVariable("Server__AllowAnonymousAgents", "true");
        Environment.SetEnvironmentVariable("Server__Transcode__Enabled", "false");
        _factory = new WebApplicationFactory<Program>();
        _admin = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _admin.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        _admin.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123" })
            .GetAwaiter().GetResult().Dispose();
        _db = new Db(Path.Combine(_dataDir, "watcher.db"));
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var key in new[] { "Server__DataDir", "Server__DbPath", "Server__AdminUser", "Server__AdminPassword", "Server__CookieSecure", "Server__AllowAnonymousAgents", "Server__Transcode__Enabled" })
            Environment.SetEnvironmentVariable(key, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Stale_version_gets_410_current_version_is_served()
    {
        string oldRel = "blobs/h/2026-10-18/seg.mp4", newRel = "blobs/h/2026-10-18/seg.av1.mp4";
        Directory.CreateDirectory(Path.Combine(_dataDir, "blobs/h/2026-10-18"));
        await File.WriteAllBytesAsync(Path.Combine(_dataDir, oldRel), new byte[4096]);
        long id = _db.Insert(new Segment(
            Id: 0, Host: "h", Filename: "seg.mp4",
            StartedAt: DateTime.UtcNow.AddHours(-2), EndedAt: DateTime.UtcNow.AddHours(-2).AddMinutes(5),
            DurationSec: 300, Frames: 10, MotionEvents: 0, StartReason: "scheduled",
            SizeBytes: 4096, Sha256: "0123456789abcdef0123", FilePath: oldRel, UploadedAt: DateTime.UtcNow.AddHours(-2)));
        var list = await _admin.GetFromJsonAsync<List<System.Text.Json.JsonElement>>("/api/segments?host=h");
        string vOld = list![0].GetProperty("v").GetString()!;
        Assert.Equal("0123456789ab", vOld);

        // Подмена, как её делает TranscodeService.
        await File.WriteAllBytesAsync(Path.Combine(_dataDir, newRel), new byte[1024]);
        Assert.True(_db.CommitTranscode(id, oldRel, new TranscodedFile(newRel, "fedcba9876543210fedc", 1024), DateTime.UtcNow));
        File.Delete(Path.Combine(_dataDir, oldRel));

        using (var stale = await _admin.GetAsync($"/api/segments/{id}/video?v={vOld}"))
            Assert.Equal(HttpStatusCode.Gone, stale.StatusCode);
        using (var req = new HttpRequestMessage(HttpMethod.Get, $"/api/segments/{id}/video?v=fedcba987654"))
        {
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 99);
            using var cur = await _admin.SendAsync(req);
            Assert.Equal(HttpStatusCode.PartialContent, cur.StatusCode);
        }
        using (var noV = await _admin.GetAsync($"/api/segments/{id}/video"))
        {
            Assert.Equal(HttpStatusCode.OK, noV.StatusCode);
            Assert.Equal(1024, (await noV.Content.ReadAsByteArrayAsync()).Length);
        }
        var meta = await _admin.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/segments/{id}/meta");
        Assert.Equal("fedcba9876543210fedc", meta.GetProperty("sha256").GetString());
        Assert.Equal("0123456789abcdef0123", meta.GetProperty("sourceSha256").GetString());
    }
}
