using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

public class DbTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;

    public DbTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"watcher-test-{Guid.NewGuid():N}.db");
        _db = new Db(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnectionClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    private static void SqliteConnectionClearAllPools() =>
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private static Segment MakeSegment(string host, string filename, DateTime startedAt, int motionEvents = 0, long size = 1024, string? sessionUser = null) =>
        new(Id: 0, Host: host, Filename: filename,
            StartedAt: startedAt, EndedAt: startedAt.AddMinutes(1), DurationSec: 60,
            Frames: 10, MotionEvents: motionEvents, StartReason: "motion",
            SizeBytes: size, Sha256: "deadbeef",
            FilePath: $"blobs/{host}/{startedAt:yyyy-MM-dd}/{filename}",
            UploadedAt: DateTime.UtcNow, SessionUser: sessionUser);

    [Fact]
    public void Insert_then_ByFilename_roundtrips()
    {
        var seg = MakeSegment("host-a", "a.mp4", DateTime.UtcNow);
        long id = _db.Insert(seg);

        var found = _db.ByFilename("a.mp4");

        Assert.NotNull(found);
        Assert.Equal(id, found!.Id);
        Assert.Equal("host-a", found.Host);
    }

    [Fact]
    public void ByFilename_unknown_returns_null()
    {
        Assert.Null(_db.ByFilename("does-not-exist.mp4"));
    }

    [Fact]
    public void Query_filters_by_host()
    {
        _db.Insert(MakeSegment("host-a", "a1.mp4", DateTime.UtcNow));
        _db.Insert(MakeSegment("host-b", "b1.mp4", DateTime.UtcNow));

        var rows = _db.Query(host: "host-a", from: null, to: null, motionOnly: false, limit: 200);

        Assert.Single(rows);
        Assert.Equal("host-a", rows[0].Host);
    }

    [Fact]
    public void Query_filters_by_date_range()
    {
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _db.Insert(MakeSegment("host-a", "old.mp4", baseTime));
        _db.Insert(MakeSegment("host-a", "new.mp4", baseTime.AddDays(10)));

        var rows = _db.Query(host: null, from: baseTime.AddDays(5), to: null, motionOnly: false, limit: 200);

        Assert.Single(rows);
        Assert.Equal("new.mp4", rows[0].Filename);
    }

    [Fact]
    public void Query_motionOnly_excludes_zero_motion_segments()
    {
        _db.Insert(MakeSegment("host-a", "quiet.mp4", DateTime.UtcNow, motionEvents: 0));
        _db.Insert(MakeSegment("host-a", "active.mp4", DateTime.UtcNow, motionEvents: 3));

        var rows = _db.Query(host: null, from: null, to: null, motionOnly: true, limit: 200);

        Assert.Single(rows);
        Assert.Equal("active.mp4", rows[0].Filename);
    }

    // Фаза 6 (roadmap.md) — сегменты от RDS-хостов различаются sessionUser,
    // обычные станции (sessionUser = null) не затрагиваются фильтром по умолчанию.
    [Fact]
    public void Query_filters_by_sessionUser()
    {
        _db.Insert(MakeSegment("rds-host", "alice.mp4", DateTime.UtcNow, sessionUser: "alice"));
        _db.Insert(MakeSegment("rds-host", "bob.mp4", DateTime.UtcNow, sessionUser: "bob"));

        var rows = _db.Query(host: "rds-host", from: null, to: null, motionOnly: false, limit: 200, sessionUser: "alice");

        Assert.Single(rows);
        Assert.Equal("alice.mp4", rows[0].Filename);
        Assert.Equal("alice", rows[0].SessionUser);
    }

    [Fact]
    public void Query_without_sessionUser_filter_returns_all_including_plain_stations()
    {
        _db.Insert(MakeSegment("host-a", "plain.mp4", DateTime.UtcNow));
        _db.Insert(MakeSegment("rds-host", "alice.mp4", DateTime.UtcNow, sessionUser: "alice"));

        var rows = _db.Query(host: null, from: null, to: null, motionOnly: false, limit: 200);

        Assert.Equal(2, rows.Count);
        Assert.Null(rows.Single(r => r.Filename == "plain.mp4").SessionUser);
    }

    [Fact]
    public void Query_respects_limit()
    {
        for (int i = 0; i < 5; i++)
            _db.Insert(MakeSegment("host-a", $"seg{i}.mp4", DateTime.UtcNow.AddSeconds(i)));

        var rows = _db.Query(host: null, from: null, to: null, motionOnly: false, limit: 2);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Inventory_aggregates_segments_per_host()
    {
        _db.Insert(MakeSegment("host-a", "a1.mp4", DateTime.UtcNow, size: 100));
        _db.Insert(MakeSegment("host-a", "a2.mp4", DateTime.UtcNow, size: 200));
        _db.Insert(MakeSegment("host-b", "b1.mp4", DateTime.UtcNow, size: 50));

        var a = _db.Inventory().Single(h => h.Name == "host-a");

        Assert.Equal(2, a.Segments);
        Assert.Equal(300, a.SegmentBytes);
        Assert.Equal(2, a.Segments24h);
    }

    // R13 (code-review-2026-10.md): хост без видео — только терминальный агент
    // или только heartbeat — тоже в списке хостов, не только в «Парке».
    [Fact]
    public void Inventory_includes_hosts_without_video()
    {
        _db.Insert(MakeSegment("video-host", "v.mp4", DateTime.UtcNow));
        var now = DateTime.UtcNow;
        _db.InsertTerminalSession(new TerminalSession(
            0, "term-host", "alice", "output", null, now.AddMinutes(-5), now, 300, 0,
            null, null, null, 10, null, "r13-term", now), searchText: null);
        _db.UpsertHeartbeat("heartbeat-host", "10.0.0.5", "1.0.0");

        var names = _db.Inventory().Select(h => h.Name).ToList();

        Assert.Equal(new[] { "heartbeat-host", "term-host", "video-host" }, names);
        var term = _db.Inventory().Single(h => h.Name == "term-host");
        Assert.Equal(0, term.Segments);
        Assert.Equal(1, term.TerminalSessions);
    }

    // Пользователь хоста виден и на станции с одним пользователем: последний по
    // времени из видео и консоли, записи без пользователя и «unknown» не в счёт.
    [Fact]
    public void Inventory_last_user_is_most_recent_of_video_and_console()
    {
        var now = DateTime.UtcNow;
        _db.Insert(MakeSegment("ws", "ws1.mp4", now.AddHours(-3), sessionUser: "ivanov"));
        _db.Insert(MakeSegment("ws", "ws2.mp4", now.AddHours(-2), sessionUser: "petrov"));
        _db.Insert(MakeSegment("ws", "ws3.mp4", now.AddHours(-1)));
        _db.Insert(MakeSegment("solo", "solo.mp4", now, sessionUser: "sidorov"));
        _db.InsertTerminalSession(new TerminalSession(
            0, "srv", "unknown", "output", null, now.AddMinutes(-1), now, 60, 0,
            null, null, null, 10, null, "lu-1", now), searchText: null);
        _db.InsertTerminalSession(new TerminalSession(
            0, "srv", "admin", "output", null, now.AddMinutes(-10), now.AddMinutes(-9), 60, 0,
            null, null, null, 10, null, "lu-2", now), searchText: null);
        _db.Insert(MakeSegment("srv", "srv.mp4", now.AddHours(-5), sessionUser: "operator"));

        var inv = _db.Inventory().ToDictionary(h => h.Name);

        Assert.Equal("petrov", inv["ws"].LastUser);
        Assert.Equal("sidorov", inv["solo"].LastUser);
        Assert.Equal("admin", inv["srv"].LastUser);
    }

    // Переименование машины (0.13): история переезжает целиком, файлы не трогаются.
    [Fact]
    public void RenameHost_moves_history_and_refuses_taken_name()
    {
        var now = DateTime.UtcNow;
        _db.UpsertHeartbeat("OLD", "10.0.0.1", "1.0.0");
        _db.UpsertHeartbeat("OTHER", "10.0.0.2", "1.0.0");
        long segId = _db.Insert(MakeSegment("OLD", "old.mp4", now));
        _db.InsertActivity(segId, "OLD", now, [new ActivityEntry(0, "chrome", "Почта")]);
        _db.CreateLegalHold("OLD", null, null, "проверка", "admin");
        _db.InsertLosses("OLD", [new LossReport("old_20261001_100000.mp4", null, 10, "quota", null)]);
        long oldId = _db.Inventory().Single(h => h.Name == "OLD").Id!.Value;

        Assert.Equal(RenameOutcome.Conflict, _db.RenameHost(oldId, "OTHER"));
        Assert.Contains(_db.Inventory(), h => h.Name == "OLD");

        Assert.Equal(RenameOutcome.Renamed, _db.RenameHost(oldId, "NEW"));
        var inv = _db.Inventory().ToDictionary(h => h.Name);
        Assert.False(inv.ContainsKey("OLD"));
        Assert.Equal(1, inv["NEW"].Segments);
        Assert.Equal(oldId, inv["NEW"].Id);
        Assert.Single(_db.ActivityForSegment(segId), a => a.Host == "NEW");
        Assert.Single(_db.ListLegalHolds("NEW", activeOnly: true));
        Assert.Single(_db.ListLosses("NEW", unacknowledgedOnly: true));
        Assert.NotEmpty(_db.HostPresenceHours("NEW", now.AddHours(-1)));
        Assert.Empty(_db.HostPresenceHours("OLD", now.AddHours(-1)));
        Assert.Equal(RenameOutcome.NotFound, _db.RenameHost(999_999, "X"));
    }

    // 0.13 (п.4): время по приложениям — от смены окна до следующей в сегменте,
    // последней — до конца сегмента; пользователи видео и консоли вместе.
    [Fact]
    public void Host_apps_and_users()
    {
        var now = DateTime.UtcNow;
        long s1 = _db.Insert(MakeSegment("ws", "a.mp4", now.AddMinutes(-10), sessionUser: "ivan") with { DurationSec = 100 });
        _db.InsertActivity(s1, "ws", now.AddMinutes(-10), [new ActivityEntry(0, "chrome", "Почта"), new ActivityEntry(30, "WINWORD", "Договор"), new ActivityEntry(90, "chrome", "Поиск")]);
        long s2 = _db.Insert(MakeSegment("ws", "b.mp4", now.AddMinutes(-5), sessionUser: "petr") with { DurationSec = 50 });
        _db.InsertActivity(s2, "ws", now.AddMinutes(-5), [new ActivityEntry(0, "WINWORD", "Отчёт")]);
        long old = _db.Insert(MakeSegment("ws", "old.mp4", now.AddDays(-100)) with { DurationSec = 1000 });
        _db.InsertActivity(old, "ws", now.AddDays(-100), [new ActivityEntry(0, "game", "x")]);
        _db.InsertTerminalSession(new TerminalSession(0, "ws", "root", "input", null, now, now, 1, 0, null, null, null, 1, null, "hp-1", now), searchText: null);

        var apps = _db.HostApps("ws", now.AddDays(-30)).ToDictionary(a => a.Process, a => a.Seconds);
        Assert.Equal(110, apps["WINWORD"]);   // 60 + 50
        Assert.Equal(40, apps["chrome"]);     // 30 + 10
        Assert.False(apps.ContainsKey("game"));

        var users = _db.HostUsers("ws").ToDictionary(u => u.User);
        Assert.Equal(1, users["ivan"].Segments);
        Assert.Equal(1, users["root"].TerminalSessions);
        Assert.Equal(0, users["root"].Segments);

        var (knownUsers, knownProcesses, _, _) = _db.KnownForHosts(["ws", "other"]);
        Assert.Contains("ivan", knownUsers);
        Assert.Contains("root", knownUsers);
        Assert.Equal("chrome", knownProcesses[0]);   // чаще всех (2 смены окна)
        Assert.DoesNotContain("game", knownProcesses);
    }

    [Fact]
    public void CountUsers_empty_db_returns_zero()
    {
        Assert.Equal(0, _db.CountUsers());
    }

    [Fact]
    public void InsertUser_then_UserByUsername_roundtrips()
    {
        long id = _db.InsertUser("admin", "hash-value", "admin");

        var found = _db.UserByUsername("admin");

        Assert.NotNull(found);
        Assert.Equal(id, found!.Id);
        Assert.Equal("admin", found.Username);
        Assert.Equal("hash-value", found.PasswordHash);
        Assert.Equal("admin", found.Role);
        Assert.False(found.Disabled);
        Assert.Equal(1, _db.CountUsers());
    }

    [Fact]
    public void UserByUsername_unknown_returns_null()
    {
        Assert.Null(_db.UserByUsername("nobody"));
    }

    [Fact]
    public void InsertUser_duplicate_username_throws()
    {
        _db.InsertUser("admin", "hash1", "admin");

        Assert.ThrowsAny<Exception>(() => _db.InsertUser("admin", "hash2", "admin"));
    }

    // Задача E3: плеер грузит сутки страницами (offset). Одинаковый started_at
    // (RDS — сессии одного хоста стартуют одновременно) не должен ни дублироваться,
    // ни теряться на границе страниц.
    [Fact]
    public void Query_offset_pages_are_complete_and_disjoint_with_tied_start_times()
    {
        var t = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 7; i++)
        {
            _db.Insert(new Segment(
                Id: 0, Host: "rds-1", Filename: $"tie-{i}.mp4", StartedAt: i < 5 ? t : t.AddMinutes(i),
                EndedAt: t.AddMinutes(10), DurationSec: 600, Frames: 1, MotionEvents: 0, StartReason: "x",
                SizeBytes: 1, Sha256: "x", FilePath: "x", UploadedAt: t, SessionUser: $"user{i}"));
        }
        var seen = new List<long>();
        for (int page = 0; page < 4; page++)
            seen.AddRange(_db.Query("rds-1", null, null, false, 2, offset: page * 2).Select(s => s.Id));
        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct().Count());
    }
}
