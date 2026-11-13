using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Watcher.Server.Tests;

// H5 (roadmap-next.md): --backup / --restore.
public class BackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"watcher-backup-{Guid.NewGuid():N}");
    private readonly string _dataDir;
    private readonly string _backups;
    private readonly ServerConfig _cfg;
    private readonly List<string> _log = new();

    public BackupTests()
    {
        _dataDir = Path.Combine(_root, "data");
        _backups = Path.Combine(_root, "backups");
        Directory.CreateDirectory(_dataDir);
        _cfg = new ServerConfig { DataDir = _dataDir, DbPath = Path.Combine(_dataDir, "watcher.db") };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    // БД с пользователем, сегментом видео и консольной сессией вывода + их файлы.
    private Db Seed()
    {
        var db = new Db(_cfg.DbPath);
        db.InsertUser("admin", "hash", "admin");
        byte[] video = "video-bytes"u8.ToArray();
        string vRel = "blobs/pc-1/2026-10-09/a.mp4";
        Directory.CreateDirectory(Path.Combine(_dataDir, "blobs/pc-1/2026-10-09"));
        File.WriteAllBytes(Path.Combine(_dataDir, vRel), video);
        var now = DateTime.UtcNow;
        db.Insert(new Segment(0, "pc-1", "a.mp4", now, now.AddSeconds(3), 3, 6, 1, "motion", video.Length, Sha(video), vRel, now));
        string outRel = "blobs/terminal/srv/2026-10-09/s.out", timingRel = "blobs/terminal/srv/2026-10-09/s.timing";
        Directory.CreateDirectory(Path.Combine(_dataDir, "blobs/terminal/srv/2026-10-09"));
        File.WriteAllText(Path.Combine(_dataDir, outRel), "hello");
        File.WriteAllText(Path.Combine(_dataDir, timingRel), "0.1 5");
        db.InsertTerminalSession(new TerminalSession(0, "srv", "root", "terminal-output", null, now, now, 1, 0,
            null, outRel, timingRel, 10, Sha("hello"u8.ToArray()), "srv:out:s.out", now), "hello");
        return db;
    }

    private string DoBackup(bool includeBlobs = false) => ServerBackup.Backup(_cfg, _backups, includeBlobs, _log.Add);

    [Fact]
    public void Backup_writes_snapshot_blob_list_and_manifest()
    {
        Seed();

        string dir = DoBackup();

        var m = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json"))).RootElement;
        Assert.Equal(1, m.GetProperty("format").GetInt32());
        Assert.Equal(Db.Migrations.Count, m.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(3, m.GetProperty("blobCount").GetInt32());
        Assert.False(m.GetProperty("blobsIncluded").GetBoolean());
        Assert.Equal(Sha(File.ReadAllBytes(Path.Combine(dir, "watcher.db"))), m.GetProperty("dbSha256").GetString());
        var blobs = ServerBackup.ReadBlobList(dir).ToList();
        Assert.Contains(blobs, b => b.Path.EndsWith("a.mp4") && b.Size == 11 && b.Sha256 == Sha("video-bytes"u8.ToArray()));
        Assert.Contains(blobs, b => b.Path.EndsWith("s.timing") && b.Size == 5);
        Assert.False(Directory.Exists(Path.Combine(dir, "blobs")));
    }

    [Fact]
    public void Restore_brings_data_back_and_keeps_current_db_aside()
    {
        var db = Seed();
        string dir = DoBackup();
        db.InsertUser("after-backup", "hash", "viewer");
        SqliteConnection.ClearAllPools();

        ServerBackup.Restore(_cfg, dir, _log.Add);

        var restored = new Db(_cfg.DbPath);
        Assert.NotNull(restored.UserByUsername("admin"));
        Assert.Null(restored.UserByUsername("after-backup"));
        Assert.Single(Directory.GetFiles(_dataDir, "watcher.db.pre-restore-*"), f => !f.EndsWith("-wal") && !f.EndsWith("-shm"));
        Assert.Contains(_log, l => l.Contains("Все файлы записей") && l.Contains("на месте"));
    }

    [Fact]
    public void Backup_with_blobs_restores_lost_files()
    {
        Seed();
        string dir = DoBackup(includeBlobs: true);
        Directory.Delete(Path.Combine(_dataDir, "blobs"), recursive: true);
        SqliteConnection.ClearAllPools();

        ServerBackup.Restore(_cfg, dir, _log.Add);

        Assert.Equal("video-bytes", File.ReadAllText(Path.Combine(_dataDir, "blobs/pc-1/2026-10-09/a.mp4")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_dataDir, "blobs/terminal/srv/2026-10-09/s.out")));
        Assert.Contains(_log, l => l.Contains("восстановлено 3"));
    }

    [Fact]
    public void Restore_warns_about_blobs_missing_on_disk()
    {
        Seed();
        string dir = DoBackup();
        File.Delete(Path.Combine(_dataDir, "blobs/pc-1/2026-10-09/a.mp4"));
        SqliteConnection.ClearAllPools();

        ServerBackup.Restore(_cfg, dir, _log.Add);

        Assert.Contains(_log, l => l.Contains("ВНИМАНИЕ: 1 файлов"));
    }

    [Fact]
    public void Restore_refuses_corrupted_snapshot()
    {
        Seed();
        string dir = DoBackup();
        File.AppendAllText(Path.Combine(dir, "watcher.db"), "x");
        string before = Sha(File.ReadAllBytes(_cfg.DbPath));

        var ex = Assert.Throws<InvalidOperationException>(() => ServerBackup.Restore(_cfg, dir, _log.Add));

        Assert.Contains("повреждён", ex.Message);
        Assert.Equal(before, Sha(File.ReadAllBytes(_cfg.DbPath)));   // живая БД не тронута
    }

    [Fact]
    public void Restore_refuses_backup_from_newer_schema()
    {
        Seed();
        string dir = DoBackup();
        string mPath = Path.Combine(dir, "manifest.json");
        File.WriteAllText(mPath, File.ReadAllText(mPath).Replace($"\"schemaVersion\": {Db.Migrations.Count}", "\"schemaVersion\": 999"));

        var ex = Assert.Throws<InvalidOperationException>(() => ServerBackup.Restore(_cfg, dir, _log.Add));

        Assert.Contains("v999", ex.Message);
    }

    [Fact]
    public void Restore_refuses_blob_path_outside_data_dir()
    {
        Seed();
        string dir = DoBackup(includeBlobs: true);
        File.AppendAllText(Path.Combine(dir, "blobs.jsonl"), "{\"path\":\"../../evil.sh\",\"size\":1,\"sha256\":null}\n");
        SqliteConnection.ClearAllPools();

        Assert.Throws<InvalidOperationException>(() => ServerBackup.Restore(_cfg, dir, _log.Add));
        Assert.False(File.Exists(Path.Combine(_root, "evil.sh")));
    }

    [Fact]
    public void Inside_rejects_escapes()
    {
        Assert.NotNull(ServerBackup.Inside(_dataDir, "blobs/a/b.mp4"));
        Assert.Null(ServerBackup.Inside(_dataDir, "../x"));
        Assert.Null(ServerBackup.Inside(_dataDir, "blobs/../../x"));
        Assert.Null(ServerBackup.Inside(_dataDir, "/etc/passwd"));
    }

    // Копия читает БД только на чтение: живая БД не меняется (в т.ч. не
    // мигрируется более новым бинарником).
    [Fact]
    public void Backup_does_not_modify_live_db()
    {
        Seed();
        SqliteConnection.ClearAllPools();
        string before = Sha(File.ReadAllBytes(_cfg.DbPath));

        DoBackup();
        SqliteConnection.ClearAllPools();

        Assert.Equal(before, Sha(File.ReadAllBytes(_cfg.DbPath)));
    }
}
