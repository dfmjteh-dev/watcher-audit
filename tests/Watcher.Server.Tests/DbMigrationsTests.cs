using Microsoft.Data.Sqlite;
using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

// R12 (docs/code-review-2026-10.md): нумерованные миграции схемы.
public class DbMigrationsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public DbMigrationsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"watcher-migr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "watcher.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SqliteConnection Connect()
    {
        var c = new SqliteConnection($"Data Source={_dbPath}");
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static bool HasColumn(SqliteConnection c, string table, string column) =>
        Scalar(c, $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'") > 0;

    private string[] Backups() => Directory.GetFiles(_dir, "*.bak");

    [Fact]
    public void Diagnostics_and_maintenance_upgrade_preserves_existing_hosts_and_events()
    {
        using var c = Connect(); DbMigrations.Apply(c, Db.Migrations.Take(27).ToList(), _dbPath);
        Exec(c, "INSERT INTO hosts(name) VALUES ('preserved-host')");
        DbMigrations.Apply(c, Db.Migrations.Take(28).ToList(), _dbPath);
        Exec(c, "INSERT INTO agent_diagnostic_events(credential_id,host_id,event_id,received_at,at,level,component,code) VALUES (1,1,'event','2026-11-03','2026-11-03','error','uploader','upload-failed')");
        DbMigrations.Apply(c, Db.Migrations, _dbPath);
        Assert.Equal(1, Scalar(c, "SELECT COUNT(*) FROM hosts WHERE name='preserved-host'"));
        Assert.Equal(1, Scalar(c, "SELECT COUNT(*) FROM agent_diagnostic_events WHERE event_id='event'"));
        Assert.True(HasColumn(c, "agent_diagnostic_state", "maintenance_key_id"));
        Assert.True(HasColumn(c, "agent_maintenance", "grant_json"));
        Assert.NotEmpty(Backups());
    }
    [Fact]
    public void Fresh_db_is_at_latest_version()
    {
        _ = new Db(_dbPath);

        using var c = Connect();
        Assert.Equal(Db.Migrations.Count, DbMigrations.CurrentVersion(c));
        Assert.True(HasColumn(c, "segments", "station_started_at"));
        Assert.True(HasColumn(c, "hosts", "last_auth_legacy"));
    }

    // БД от сервера до R12: schema_migrations нет, части колонок нет. Baseline
    // доводит её до текущей схемы, данные на месте.
    [Fact]
    public void Pre_migrations_db_is_upgraded_by_baseline_and_keeps_data()
    {
        using (var c = Connect())
        {
            Exec(c, @"
CREATE TABLE segments (
  id INTEGER PRIMARY KEY AUTOINCREMENT, host TEXT NOT NULL, filename TEXT NOT NULL UNIQUE,
  started_at TEXT NOT NULL, ended_at TEXT NOT NULL, duration_sec REAL NOT NULL, frames INTEGER NOT NULL,
  motion_events INTEGER NOT NULL, start_reason TEXT NOT NULL, size_bytes INTEGER NOT NULL,
  sha256 TEXT NOT NULL, file_path TEXT NOT NULL, uploaded_at TEXT NOT NULL);
CREATE TABLE hosts (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE, ip_address TEXT,
  group_id INTEGER, last_heartbeat_at TEXT, agent_version TEXT);
INSERT INTO segments (host, filename, started_at, ended_at, duration_sec, frames, motion_events, start_reason, size_bytes, sha256, file_path, uploaded_at)
VALUES ('h', 'old.mp4', '2026-01-01T00:00:00Z', '2026-01-01T00:01:00Z', 60, 1, 0, 'motion', 10, 'aa', 'blobs/h/old.mp4', '2026-01-01T00:02:00Z');");
        }

        var db = new Db(_dbPath);

        Assert.NotNull(db.ByFilename("old.mp4"));
        using var c2 = Connect();
        Assert.True(HasColumn(c2, "segments", "session_user"));
        Assert.True(HasColumn(c2, "hosts", "config_json"));
        Assert.Equal(Db.Migrations.Count, DbMigrations.CurrentVersion(c2));
        Assert.Empty(Backups());
    }

    [Fact]
    public void Reopening_applies_nothing_twice()
    {
        _ = new Db(_dbPath);
        _ = new Db(_dbPath);

        using var c = Connect();
        Assert.Equal(Db.Migrations.Count, Scalar(c, "SELECT COUNT(*) FROM schema_migrations"));
        Assert.Empty(Backups());
    }

    // Откатили сервер на старую версию поверх БД, которую уже мигрировал новый:
    // не стартуем, а не пишем в незнакомую схему.
    [Fact]
    public void Db_from_newer_server_refuses_to_open()
    {
        _ = new Db(_dbPath);
        using (var c = Connect())
            Exec(c, "INSERT INTO schema_migrations (version, name, applied_at) VALUES (999, 'future', '2030-01-01T00:00:00Z')");

        var ex = Assert.Throws<InvalidOperationException>(() => new Db(_dbPath));
        Assert.Contains("999", ex.Message);
    }

    // Новая миграция поверх размеченной БД: применяется, перед ней — копия БД.
    [Fact]
    public void New_migration_is_applied_after_backup()
    {
        var v1 = new DbMigration(1, "t", c => Exec(c, "CREATE TABLE t (a TEXT); INSERT INTO t VALUES ('keep')"));
        var v2 = new DbMigration(2, "t.b", c => Exec(c, "ALTER TABLE t ADD COLUMN b TEXT"));
        using var c = Connect();
        DbMigrations.Apply(c, [v1], _dbPath);

        DbMigrations.Apply(c, [v1, v2], _dbPath);

        Assert.Equal(2, DbMigrations.CurrentVersion(c));
        Assert.True(HasColumn(c, "t", "b"));
        string backup = Assert.Single(Backups());
        Assert.EndsWith("watcher.db.v1.bak", backup);
        using var bc = new SqliteConnection($"Data Source={backup};Mode=ReadOnly");
        bc.Open();
        Assert.False(HasColumn(bc, "t", "b"));
        Assert.Equal(1, Scalar(bc, "SELECT COUNT(*) FROM t WHERE a = 'keep'"));
    }

    // Упавшая миграция откатывается целиком: и её DDL, и номер версии —
    // следующий старт повторит её с начала.
    [Fact]
    public void Failed_migration_is_rolled_back_entirely()
    {
        var v1 = new DbMigration(1, "t", c => Exec(c, "CREATE TABLE t (a TEXT)"));
        var v2 = new DbMigration(2, "broken", c =>
        {
            Exec(c, "CREATE TABLE half_done (x INTEGER)");
            throw new InvalidOperationException("boom");
        });
        using var c = Connect();

        Assert.Throws<InvalidOperationException>(() => DbMigrations.Apply(c, [v1, v2], _dbPath));

        Assert.Equal(1, DbMigrations.CurrentVersion(c));
        Assert.Equal(0, Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'half_done'"));
        // Соединение осталось пригодным — транзакция не висит.
        Exec(c, "CREATE TABLE after_fail (x INTEGER)");
    }

    // Миграция 19 (Этап 2): колонка hosts.person. Применяется поверх схемы v18,
    // данные хоста на месте; повторный старт её не трогает (идемпотентность
    // раннера — ровно один раз).
    [Fact]
    public void Migration_19_adds_person_column()
    {
        using (var c = Connect())
        {
            DbMigrations.Apply(c, Db.Migrations.Take(18).ToList(), _dbPath);
            Assert.False(HasColumn(c, "hosts", "person"));
            Exec(c, "INSERT INTO hosts (name) VALUES ('Pixel-7')");
        }
        SqliteConnection.ClearAllPools();

        _ = new Db(_dbPath);
        _ = new Db(_dbPath); // второй старт ничего не применяет повторно

        using var c2 = Connect();
        Assert.True(HasColumn(c2, "hosts", "person"));
        Assert.Equal(1, Scalar(c2, "SELECT COUNT(*) FROM hosts WHERE name = 'Pixel-7'"));
        Assert.Equal(Db.Migrations.Count, DbMigrations.CurrentVersion(c2));
    }

    [Fact]
    public void Android_event_migrations_preserve_existing_hosts_and_v20_events()
    {
        using (var c = Connect())
        {
            DbMigrations.Apply(c, Db.Migrations.Take(19).ToList(), _dbPath);
            Exec(c, "INSERT INTO hosts (name, person) VALUES ('PHONE', 'Employee')");
            DbMigrations.Apply(c, Db.Migrations.Take(20).ToList(), _dbPath);
            Exec(c, "INSERT INTO android_events (host, ts, type, received_at) VALUES ('PHONE', '2026-10-22T12:00:00.0000000Z', 'device_state', '2026-10-22T12:00:00.0000000Z')");
        }
        SqliteConnection.ClearAllPools();
        _ = new Db(_dbPath);
        _ = new Db(_dbPath);
        using var c2 = Connect();
        Assert.Equal(1, Scalar(c2, "SELECT COUNT(*) FROM hosts WHERE name = 'PHONE' AND person = 'Employee'"));
        Assert.Equal(1, Scalar(c2, "SELECT COUNT(*) FROM android_events WHERE host = 'PHONE' AND event_id IS NOT NULL"));
        Assert.Equal(Db.Migrations.Count, DbMigrations.CurrentVersion(c2));
    }

    [Fact]
    public void Migration_numbers_must_be_contiguous()
    {
        using var c = Connect();
        Assert.Throws<InvalidOperationException>(() =>
            DbMigrations.Apply(c, [new DbMigration(1, "a", _ => { }), new DbMigration(3, "c", _ => { })], _dbPath));
    }

    // Миграция 9: запятая в проценте причины (Windows-агенты до 0.13) → точка;
    // остальные причины не трогаются.
    [Fact]
    public void Migration_9_normalizes_motion_reason_decimal_comma()
    {
        using (var c = Connect())
        {
            DbMigrations.Apply(c, Db.Migrations.Take(8).ToList(), _dbPath);
            foreach (var (file, reason) in new[] { ("a.mp4", "motion 0,52%"), ("b.mp4", "motion 12.30%"), ("c.mp4", "recovered"), ("d.mp4", "motion, 1,2") })
                Exec(c, $@"INSERT INTO segments (host, filename, started_at, ended_at, duration_sec, frames, motion_events, start_reason, size_bytes, sha256, file_path, uploaded_at)
VALUES ('h', '{file}', '2026-10-13T00:00:00Z', '2026-10-13T00:01:00Z', 60, 1, 0, '{reason}', 1, 'x', 'p/{file}', '2026-10-13T00:02:00Z')");
        }
        SqliteConnection.ClearAllPools();

        _ = new Db(_dbPath);

        using var c2 = Connect();
        string Reason(string f)
        {
            using var cmd = c2.CreateCommand();
            cmd.CommandText = $"SELECT start_reason FROM segments WHERE filename = '{f}'";
            return (string)cmd.ExecuteScalar()!;
        }
        Assert.Equal("motion 0.52%", Reason("a.mp4"));
        Assert.Equal("motion 12.30%", Reason("b.mp4"));
        Assert.Equal("recovered", Reason("c.mp4"));
        Assert.Equal("motion, 1,2", Reason("d.mp4"));
    }
    [Fact]
    public void Migration_25_preserves_old_losses_and_adds_optional_detail()
    {
        using (var c = Connect())
        {
            DbMigrations.Apply(c, Db.Migrations.Take(24).ToList(), _dbPath);
            Exec(c, "INSERT INTO host_losses(host,file,size_bytes,reason,reported_at) VALUES ('h','old.txt',12,'transcript-no-4688','2026-11-02T10:00:00Z')");
        }
        var db = new Db(_dbPath);
        // Миграция 33 подтверждает transcript-no-4688 (с 0.16 такие транскрипты
        // выгружаются), сама строка журнала сохраняется — поэтому смотрим все.
        var loss = Assert.Single(db.ListLosses("h", false));
        Assert.Equal("old.txt", loss.File);
        Assert.Equal(12, loss.SizeBytes);
        Assert.Null(loss.Detail);
        Assert.Equal(1, Assert.Single(db.LossGroups("h", false)).Count);
        Assert.Empty(db.ListLosses("h", true));
    }

    [Fact]
    public void Migration_32_strips_commit_from_stored_agent_version()
    {
        using (var c = Connect())
        {
            DbMigrations.Apply(c, Db.Migrations.Take(31).ToList(), _dbPath);
            Exec(c, "INSERT INTO hosts(name, agent_version) VALUES ('pc-old', '0.15.0+abc'), ('pc-new', '0.15.3')");
        }
        var hosts = new Db(_dbPath).Inventory().ToDictionary(h => h.Name, h => h.AgentVersion);
        Assert.Equal("0.15.0", hosts["pc-old"]);
        Assert.Equal("0.15.3", hosts["pc-new"]);
    }

}
