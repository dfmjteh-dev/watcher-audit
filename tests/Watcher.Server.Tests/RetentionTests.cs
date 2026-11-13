using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

public class RetentionTests : IDisposable
{
    private readonly string _dataDir;
    private readonly Db _db;

    public RetentionTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _db = new Db(Path.Combine(_dataDir, "watcher.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    // uploadedAt по умолчанию = startedAt: запись выгружена сразу после записи,
    // как в нормальной работе (R7 — retention смотрит и на uploaded_at).
    private long InsertSegment(DateTime startedAt, long size = 1024, string? filename = null, DateTime? uploadedAt = null)
    {
        filename ??= $"seg-{Guid.NewGuid():N}.mp4";
        var seg = new Segment(
            Id: 0, Host: "host-a", Filename: filename,
            StartedAt: startedAt, EndedAt: startedAt.AddMinutes(1), DurationSec: 60,
            Frames: 10, MotionEvents: 1, StartReason: "motion",
            SizeBytes: size, Sha256: "deadbeef",
            FilePath: filename,
            UploadedAt: uploadedAt ?? startedAt);
        return _db.Insert(seg);
    }

    // disk — подменённая статистика тома (R8); по умолчанию «нет статистики»,
    // чтобы тесты не зависели от свободного места на машине, где их гоняют.
    private RetentionService MakeService(int retentionDays, long retentionMaxBytes,
        (long Free, long Total)? disk = null, double minFreeDiskPercent = 5) =>
        new(_db,
            new ServerConfig { DataDir = _dataDir, RetentionDays = retentionDays, RetentionMaxBytes = retentionMaxBytes, MinFreeDiskPercent = minFreeDiskPercent },
            NullLogger<RetentionService>.Instance)
        { DiskSpace = () => disk };

    [Fact]
    public void RetentionDays_zero_disables_age_based_pruning()
    {
        InsertSegment(DateTime.UtcNow.AddYears(-5));
        var svc = MakeService(retentionDays: 0, retentionMaxBytes: 0);

        svc.RunOnce();

        Assert.Single(_db.Query(null, null, null, false, 100));
    }

    [Fact]
    public void RetentionMaxBytes_zero_disables_size_based_pruning()
    {
        InsertSegment(DateTime.UtcNow, size: 10_000_000_000);
        var svc = MakeService(retentionDays: 0, retentionMaxBytes: 0);

        svc.RunOnce();

        Assert.Single(_db.Query(null, null, null, false, 100));
    }

    [Fact]
    public void RetentionDays_deletes_segments_older_than_cutoff_keeps_recent()
    {
        long oldId = InsertSegment(DateTime.UtcNow.AddDays(-10));
        long newId = InsertSegment(DateTime.UtcNow);
        var svc = MakeService(retentionDays: 5, retentionMaxBytes: 0);

        svc.RunOnce();

        Assert.Null(_db.ById(oldId));
        Assert.NotNull(_db.ById(newId));
    }

    [Fact]
    public void RetentionMaxBytes_deletes_oldest_first_until_under_cap()
    {
        long id1 = InsertSegment(DateTime.UtcNow.AddMinutes(-30), size: 100);
        long id2 = InsertSegment(DateTime.UtcNow.AddMinutes(-20), size: 100);
        long id3 = InsertSegment(DateTime.UtcNow, size: 100);
        var svc = MakeService(retentionDays: 0, retentionMaxBytes: 250);

        svc.RunOnce();

        Assert.Null(_db.ById(id1));
        Assert.NotNull(_db.ById(id2));
        Assert.NotNull(_db.ById(id3));
    }

    [Fact]
    public void RunOnce_actually_deletes_blob_file_from_disk()
    {
        string filename = "to-delete.mp4";
        string blobPath = Path.Combine(_dataDir, filename);
        File.WriteAllText(blobPath, "fake video bytes");
        InsertSegment(DateTime.UtcNow.AddDays(-10), size: new FileInfo(blobPath).Length, filename: filename);
        var svc = MakeService(retentionDays: 5, retentionMaxBytes: 0);

        svc.RunOnce();

        Assert.False(File.Exists(blobPath));
    }

    // R9: брошенный .part (сервер упал посреди загрузки) удаляется, свежий —
    // нет (это может быть идущая прямо сейчас загрузка), обычные блобы не трогаются.
    [Fact]
    public void RunOnce_deletes_stale_part_files_only()
    {
        string dir = Path.Combine(_dataDir, "blobs", "host-a", "2026-10-09");
        Directory.CreateDirectory(dir);
        string stale = Path.Combine(dir, "a.mp4.0123.part");
        string fresh = Path.Combine(dir, "b.mp4.4567.part");
        string blob = Path.Combine(dir, "c.mp4");
        foreach (var f in new[] { stale, fresh, blob }) File.WriteAllText(f, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - RetentionService.StalePartAge - TimeSpan.FromMinutes(1));
        File.SetLastWriteTimeUtc(blob, DateTime.UtcNow.AddDays(-30));

        MakeService(retentionDays: 0, retentionMaxBytes: 0).RunOnce();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(blob));
    }

    // Фаза 8 (roadmap.md) — тот же RunOnce() чистит terminal_sessions тоже,
    // тот же RetentionDays/RetentionMaxBytes, не отдельный конфиг.
    private long InsertTerminalInput(DateTime startedAt, string? externalId = null, DateTime? uploadedAt = null) =>
        _db.InsertTerminalSession(new TerminalSession(
            Id: 0, Host: "host-a", SessionUser: "alice", Kind: "terminal-input", RemoteAddr: null,
            StartedAt: startedAt, EndedAt: startedAt.AddSeconds(5), DurationSec: 5, ExitCode: null,
            Input: "echo hi", OutFilePath: null, TimingFilePath: null,
            SizeBytes: 0, Sha256: null, ExternalId: externalId ?? Guid.NewGuid().ToString(), UploadedAt: uploadedAt ?? startedAt),
            searchText: "echo hi");

    private long InsertTerminalOutput(DateTime startedAt, long size, string outPath, string timingPath, string? externalId = null)
    {
        string outFull = Path.Combine(_dataDir, outPath);
        string timingFull = Path.Combine(_dataDir, timingPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outFull)!);
        File.WriteAllText(outFull, "fake typescript");
        File.WriteAllText(timingFull, "0.1 5");
        return _db.InsertTerminalSession(new TerminalSession(
            Id: 0, Host: "host-a", SessionUser: "alice", Kind: "terminal-output", RemoteAddr: null,
            StartedAt: startedAt, EndedAt: startedAt.AddSeconds(5), DurationSec: 5, ExitCode: 0,
            Input: null, OutFilePath: outPath, TimingFilePath: timingPath,
            SizeBytes: size, Sha256: "deadbeef", ExternalId: externalId ?? Guid.NewGuid().ToString(), UploadedAt: startedAt),
            searchText: "fake typescript");
    }

    // R7: станция с датой 2000 года — запись только что выгружена, удалять её
    // при первом же проходе retention нельзя.
    [Fact]
    public void RetentionDays_keeps_record_with_old_started_at_until_it_is_old_by_upload_time_too()
    {
        long segId = InsertSegment(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), uploadedAt: DateTime.UtcNow);
        long termId = InsertTerminalInput(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), uploadedAt: DateTime.UtcNow);
        var svc = MakeService(retentionDays: 5, retentionMaxBytes: 0);

        svc.RunOnce();

        Assert.NotNull(_db.ById(segId));
        Assert.NotNull(_db.TerminalById(termId));
    }

    [Fact]
    public void Terminal_RetentionDays_deletes_old_input_row_keeps_recent()
    {
        long oldId = InsertTerminalInput(DateTime.UtcNow.AddDays(-10));
        long newId = InsertTerminalInput(DateTime.UtcNow);
        var svc = MakeService(retentionDays: 5, retentionMaxBytes: 0);

        svc.RunOnce();

        Assert.Null(_db.TerminalById(oldId));
        Assert.NotNull(_db.TerminalById(newId));
    }

    [Fact]
    public void Terminal_RetentionDays_deletes_both_out_and_timing_blobs()
    {
        string outPath = "blobs/terminal/host-a/2020-01-01/a.out";
        string timingPath = "blobs/terminal/host-a/2020-01-01/a.timing";
        InsertTerminalOutput(DateTime.UtcNow.AddDays(-10), size: 100, outPath: outPath, timingPath: timingPath);
        var svc = MakeService(retentionDays: 5, retentionMaxBytes: 0);

        svc.RunOnce();

        Assert.False(File.Exists(Path.Combine(_dataDir, outPath)));
        Assert.False(File.Exists(Path.Combine(_dataDir, timingPath)));
    }

    [Fact]
    public void Terminal_RetentionMaxBytes_deletes_oldest_first_until_under_cap()
    {
        long id1 = InsertTerminalOutput(DateTime.UtcNow.AddMinutes(-30), size: 100, outPath: "blobs/terminal/h/1/a.out", timingPath: "blobs/terminal/h/1/a.timing");
        long id2 = InsertTerminalOutput(DateTime.UtcNow.AddMinutes(-20), size: 100, outPath: "blobs/terminal/h/2/b.out", timingPath: "blobs/terminal/h/2/b.timing");
        long id3 = InsertTerminalOutput(DateTime.UtcNow, size: 100, outPath: "blobs/terminal/h/3/c.out", timingPath: "blobs/terminal/h/3/c.timing");
        var svc = MakeService(retentionDays: 0, retentionMaxBytes: 250);

        svc.RunOnce();

        Assert.Null(_db.TerminalById(id1));
        Assert.NotNull(_db.TerminalById(id2));
        Assert.NotNull(_db.TerminalById(id3));
    }

    // R8 (docs/code-review-2026-10.md): RetentionMaxBytes — один бюджет на обе
    // таблицы. Раньше каждая по 60k укладывалась в 100k сама по себе, и вместе
    // они занимали 120k. Удаляется самая старая запись, из какой бы таблицы она
    // ни была.
    [Fact]
    public void RetentionMaxBytes_is_shared_between_segments_and_terminal()
    {
        long oldTerm = InsertTerminalOutput(DateTime.UtcNow.AddMinutes(-30), size: 60_000, outPath: "blobs/terminal/h/1/a.out", timingPath: "blobs/terminal/h/1/a.timing");
        long newSeg = InsertSegment(DateTime.UtcNow, size: 60_000);
        var svc = MakeService(retentionDays: 0, retentionMaxBytes: 100_000);

        svc.RunOnce();

        Assert.Null(_db.TerminalById(oldTerm));
        Assert.False(File.Exists(Path.Combine(_dataDir, "blobs/terminal/h/1/a.out")));
        Assert.NotNull(_db.ById(newSeg));
    }

    // R8: ввод консоли места не занимает (size_bytes = 0) — удалять его ради
    // места бессмысленно, даже если он самый старый.
    [Fact]
    public void Space_pruning_skips_terminal_input_rows()
    {
        long oldInput = InsertTerminalInput(DateTime.UtcNow.AddDays(-3));
        long seg1 = InsertSegment(DateTime.UtcNow.AddMinutes(-20), size: 100);
        long seg2 = InsertSegment(DateTime.UtcNow, size: 100);
        var svc = MakeService(retentionDays: 0, retentionMaxBytes: 150);

        svc.RunOnce();

        Assert.NotNull(_db.TerminalById(oldInput));
        Assert.Null(_db.ById(seg1));
        Assert.NotNull(_db.ById(seg2));
    }

    // R8: свободно меньше MinFreeDiskPercent — удаляются самые старые записи,
    // хотя ни срок, ни лимит не превышены (RetentionMaxBytes = 0), ровно
    // столько, сколько не хватает до порога.
    [Fact]
    public void Low_free_disk_deletes_oldest_records_regardless_of_age()
    {
        long seg1 = InsertSegment(DateTime.UtcNow.AddMinutes(-30), size: 300);
        long term = InsertTerminalOutput(DateTime.UtcNow.AddMinutes(-20), size: 300, outPath: "blobs/terminal/h/1/b.out", timingPath: "blobs/terminal/h/1/b.timing");
        long seg3 = InsertSegment(DateTime.UtcNow, size: 300);
        // Том 10000, свободно 100 (1%), порог 5% → не хватает 400 → две самые старые.
        var svc = MakeService(retentionDays: 30, retentionMaxBytes: 0, disk: (100, 10_000), minFreeDiskPercent: 5);

        svc.RunOnce();

        Assert.Null(_db.ById(seg1));
        Assert.Null(_db.TerminalById(term));
        Assert.NotNull(_db.ById(seg3));
    }

    [Fact]
    public void Enough_free_disk_or_threshold_off_deletes_nothing()
    {
        long seg = InsertSegment(DateTime.UtcNow, size: 300);

        MakeService(retentionDays: 0, retentionMaxBytes: 0, disk: (5_000, 10_000)).RunOnce();
        MakeService(retentionDays: 0, retentionMaxBytes: 0, disk: (100, 10_000), minFreeDiskPercent: 0).RunOnce();

        Assert.NotNull(_db.ById(seg));
    }
}
