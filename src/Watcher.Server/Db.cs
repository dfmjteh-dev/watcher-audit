using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// SQLite-индекс сервера: подключение (WAL), схема и нумерованные миграции
// (DbMigrations, R12), общие помощники. Запросы по сущностям — в Db.*.cs:
// Users, Groups (группы и конфиги), Hosts (heartbeat, реестр), Segments
// (видео), Terminal (консоль), Audit (журнал доступа), Agents, Activity,
// Retention, Updates.

public sealed partial class Db
{
    private readonly string _connStr;
    private readonly string _path;
    public Db(string path)
    {
        _path = path;
        _connStr = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        Init();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connStr);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private void Init()
    {
        using var c = Open();
        DbMigrations.Apply(c, Migrations, _path);
    }

    // R12 (docs/code-review-2026-10.md): миграции схемы — см. DbMigrations.cs и
    // docs/development.md. Только дописывать в конец, выпущенные не менять.
    public static readonly IReadOnlyList<DbMigration> Migrations =
    [
        new(1, "baseline", Baseline),
        new(2, "users-ldap", UsersLdap),
        new(3, "users-can-export", UsersCanExport),
        new(4, "segment-activity", SegmentActivity),
        new(5, "retention-and-holds", RetentionAndHolds),
        new(6, "agent-updates", AgentUpdates),
        new(7, "terminal-live", TerminalLive),
        new(8, "server-settings", ServerSettings),
        new(9, "motion-reason-decimal-point", MotionReasonDecimalPoint),
        new(10, "agent-status", AgentStatus),
        new(11, "host-delete", HostDelete),
        new(12, "segment-monitors", SegmentMonitors),
        new(13, "terminal-service", TerminalService),
        new(14, "host-resources", HostResourcesColumn),
        new(15, "segment-transcode", SegmentTranscode),
        new(16, "segment-activity-site", SegmentActivitySite),
        new(17, "terminal-size", TerminalSize),
        new(18, "report-facts", ReportFactsTables),
        new(19, "host-person", HostPerson),
        new(20, "android-events", AndroidEvents),
        new(21, "android-event-ids", AndroidEventIds),
        new(22, "android-owner", AndroidOwner),
        new(23, "users-can-manage-phones", UsersCanManagePhones),
        new(24, "android-lock", AndroidLock),
        new(25, "transcript-loss-detail", TranscriptLossDetail),
        new(26, "transcode-recovery", TranscodeRecovery),
        new(27, "list-page-indexes", ListPageIndexes),
        new(28, "agent-diagnostics", AgentDiagnostics),
        new(29, "agent-maintenance", AgentMaintenance),
        new(30, "agent-update-progress", AgentUpdateProgress),
        new(31, "agent-diagnostic-archives", AgentDiagnosticArchives),
        new(32, "agent-version-core", AgentVersionCore),
        new(33, "transcript-origins", TranscriptOriginsMigration),
    ];

    // Миграция 9: «motion 0,52%» от Windows-агентов до 0.13 (процент по языку
    // станции) → «motion 0.52%», как у Linux и как теперь при приёме
    // (UploadValidation.NormalizeStartReason). Данные, схема не меняется.
    private static void MotionReasonDecimalPoint(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE segments SET start_reason = replace(start_reason, ',', '.') WHERE start_reason GLOB 'motion [0-9]*,[0-9]*%'";
        cmd.ExecuteNonQuery();
    }

    // Миграция 3 (H7): отдельное право на выгрузку записей.
    // Этап 11: право «управление телефонами» (редактирование Android-политик).
    private static void UsersCanManagePhones(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "ALTER TABLE users ADD COLUMN can_manage_phones INTEGER NOT NULL DEFAULT 0";
        cmd.ExecuteNonQuery();
    }

    private static void UsersCanExport(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "ALTER TABLE users ADD COLUMN can_export INTEGER NOT NULL DEFAULT 0";
        cmd.ExecuteNonQuery();
    }

    // Миграция 2 (H1): источник учётки и DN в каталоге.
    private static void UsersLdap(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
ALTER TABLE users ADD COLUMN auth_source TEXT NOT NULL DEFAULT 'local';
ALTER TABLE users ADD COLUMN ldap_dn TEXT;
CREATE UNIQUE INDEX ix_users_ldap_dn ON users(ldap_dn) WHERE ldap_dn IS NOT NULL;";
        cmd.ExecuteNonQuery();
    }

    // Миграция 1 — схема на момент R12 в прежнем идемпотентном виде (IF NOT
    // EXISTS + EnsureColumn): так она доводит до текущей схемы и пустую БД, и
    // БД любой версии до R12, где schema_migrations ещё не было. Новые
    // изменения схемы сюда не добавлять — только следующими миграциями.
    private static void Baseline(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS segments (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  host TEXT NOT NULL,
  filename TEXT NOT NULL UNIQUE,
  started_at TEXT NOT NULL,
  ended_at TEXT NOT NULL,
  duration_sec REAL NOT NULL,
  frames INTEGER NOT NULL,
  motion_events INTEGER NOT NULL,
  start_reason TEXT NOT NULL,
  size_bytes INTEGER NOT NULL,
  sha256 TEXT NOT NULL,
  file_path TEXT NOT NULL,
  uploaded_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_segments_host_started ON segments(host, started_at);
CREATE INDEX IF NOT EXISTS ix_segments_started ON segments(started_at);

CREATE TABLE IF NOT EXISTS users (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  username TEXT NOT NULL UNIQUE,
  password_hash TEXT NOT NULL,
  role TEXT NOT NULL,
  disabled INTEGER NOT NULL DEFAULT 0
);

-- Этап C1 (roadmap-next.md): ограничение viewer'а списком групп — опционально,
-- отсутствие строк для user_id означает без ограничений, а не ничего не видно
-- (см. ViewerScope.AllowedGroupIds). Без FK-констрейнта — тот же стиль
-- слабой связи по id, что у host_groups/hosts в этом файле.
CREATE TABLE IF NOT EXISTS user_groups (
  user_id INTEGER NOT NULL,
  group_id INTEGER NOT NULL,
  PRIMARY KEY (user_id, group_id)
);

CREATE TABLE IF NOT EXISTS host_groups (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL UNIQUE,
  config_json TEXT,
  config_version INTEGER
);

CREATE TABLE IF NOT EXISTS hosts (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL UNIQUE,
  ip_address TEXT,
  group_id INTEGER,
  last_heartbeat_at TEXT,
  agent_version TEXT,
  config_json TEXT,
  config_version INTEGER,
  dropped_segments INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_hosts_group ON hosts(group_id);
CREATE INDEX IF NOT EXISTS ix_hosts_last_heartbeat ON hosts(last_heartbeat_at);

-- Фаза 8 (roadmap.md): два kind в одной таблице, не две отдельных — обе
-- описывают одну и ту же консольную/SSH-сессию, просто с разных сторон
-- (terminal-input от pam_tty_audit, terminal-output от script(1)). input
-- хранится прямо в строке (короткий текст, нет смысла в отдельном blob'е,
-- в отличие от video в segments); out_file_path/timing_file_path — только
-- для terminal-output, у terminal-input их нет (NULL). search_text — то,
-- по чему ищем (см. QueryTerminal): для input это сам input, для output —
-- содержимое .out блоба (с ANSI-мусором как есть, без стриппера — см.
-- roadmap.md; подстрока находится независимо от escape-кодов вокруг нужного
-- слова). Простой LIKE, не FTS5 — при масштабе проекта (~300 агентов,
-- см. development.md) полнотекстовый индекс избыточен.
CREATE TABLE IF NOT EXISTS terminal_sessions (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  host TEXT NOT NULL,
  session_user TEXT NOT NULL,
  kind TEXT NOT NULL,
  remote_addr TEXT,
  started_at TEXT NOT NULL,
  ended_at TEXT NOT NULL,
  duration_sec REAL NOT NULL,
  exit_code INTEGER,
  input TEXT,
  search_text TEXT,
  out_file_path TEXT,
  timing_file_path TEXT,
  size_bytes INTEGER NOT NULL DEFAULT 0,
  sha256 TEXT,
  external_id TEXT NOT NULL UNIQUE,
  uploaded_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_terminal_sessions_host_started ON terminal_sessions(host, started_at);
CREATE INDEX IF NOT EXISTS ix_terminal_sessions_user_started ON terminal_sessions(session_user, started_at);

-- Этап E2 (roadmap-next.md): «heartbeat-история» на вкладке хоста «Инфо».
-- Не строка на каждый heartbeat (300 хостов × раз в минуту = 432k строк в
-- сутки), а отметка «был на связи в этот час» — не больше 24 строк на хост в
-- сутки, INSERT OR IGNORE. hour — UTC, 'yyyy-MM-ddTHH'. Чистится Retention.cs.
CREATE TABLE IF NOT EXISTS host_presence (
  host TEXT NOT NULL,
  hour TEXT NOT NULL,
  PRIMARY KEY (host, hour)
);

-- Этап C3 (roadmap-next.md): журнал доступа. Отдельный retention
-- (AuditRetentionDays, по умолчанию дольше, чем у записей — см. Retention.cs):
-- вопрос «кто смотрел запись» задают и после того, как сама запись удалена.
CREATE TABLE IF NOT EXISTS audit_log (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  ts TEXT NOT NULL,
  username TEXT,
  ip TEXT,
  action TEXT NOT NULL,
  target TEXT,
  details TEXT
);
CREATE INDEX IF NOT EXISTS ix_audit_log_ts ON audit_log(ts);
CREATE INDEX IF NOT EXISTS ix_audit_log_user_ts ON audit_log(username, ts);
";
        cmd.ExecuteNonQuery();

        // Апгрейд существующих БД с Фазы 3 (hosts/host_groups уже есть, но без
        // config_json/config_version) — CREATE TABLE IF NOT EXISTS выше их не трогает.
        // SQLite не умеет ADD COLUMN IF NOT EXISTS, поэтому проверяем PRAGMA table_info.
        EnsureColumn(c, "host_groups", "config_json", "TEXT");
        EnsureColumn(c, "host_groups", "config_version", "INTEGER");
        EnsureColumn(c, "hosts", "config_json", "TEXT");
        EnsureColumn(c, "hosts", "config_version", "INTEGER");
        EnsureColumn(c, "hosts", "dropped_segments", "INTEGER NOT NULL DEFAULT 0");
        // B1 (roadmap-next.md): видно по самому запросу heartbeat — Request.
        // IsHttps сервера (после ForwardedHeaders) правдивее, чем верить
        // самоотчёту агента о своём конфиге. NULL — heartbeat ещё не было.
        EnsureColumn(c, "hosts", "last_heartbeat_https", "INTEGER");

        // Фаза 6 (roadmap.md) — пусто для обычных одно-сессионных станций,
        // задано для сегментов от RDS-хостов (Watcher.SessionService). Composite-
        // индекс сразу, не задним числом (см. development.md — индексы под
        // host+дата закладываются с самого начала).
        EnsureColumn(c, "segments", "session_user", "TEXT");
        EnsureColumn(c, "terminal_sessions", "source", "TEXT");
        EnsureColumn(c, "terminal_sessions", "shell", "TEXT");
        // R7 (code-review-2026-10.md) — см. StationClock.cs.
        EnsureColumn(c, "segments", "station_started_at", "TEXT");
        EnsureColumn(c, "terminal_sessions", "station_started_at", "TEXT");
        EnsureColumn(c, "hosts", "clock_skew_sec", "REAL");
        using (var idx = c.CreateCommand())
        {
            idx.CommandText = "CREATE INDEX IF NOT EXISTS ix_segments_host_user_started ON segments(host, session_user, started_at)";
            idx.ExecuteNonQuery();
        }

        InitAgents(c);
    }

    [SuppressMessage("Security", "CA2100", Justification = "Таблица/колонка/тип — константы из кода миграций, не ввод; значение колонки — параметр.")]
    private static void EnsureColumn(SqliteConnection c, string table, string column, string sqlType)
    {
        using var check = c.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $col";
        check.Parameters.AddWithValue("$col", column);
        if (Convert.ToInt32(check.ExecuteScalar()) > 0) return;

        using var alter = c.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {sqlType}";
        alter.ExecuteNonQuery();
    }

    // SQLite не умеет биндить массив одним параметром для IN (...) — генерирует
    // $prefix0,$prefix1,... и добавляет их в cmd, возвращает готовую строку для
    // подстановки внутрь "IN (...)". Используется Inventory/Query/QueryTerminal
    // для фильтра viewer'а по группам (Этап C1, roadmap-next.md).
    private static string AddInParams(SqliteCommand cmd, string prefix, IReadOnlyCollection<string> values)
    {
        var names = new List<string>(values.Count);
        int i = 0;
        foreach (string v in values)
        {
            string name = $"${prefix}{i++}";
            cmd.Parameters.AddWithValue(name, v);
            names.Add(name);
        }
        return string.Join(",", names);
    }

    // Ограничение viewer'а группами (ViewerScope): пустой список — ни одного хоста.
    private static string GroupIn(SqliteCommand cmd, string prefix, IReadOnlyCollection<long> groupIds) =>
        groupIds.Count == 0 ? "0" : "h.group_id IN (" + AddInParams(cmd, prefix, groupIds) + ")";

    private static string AddInParams(SqliteCommand cmd, string prefix, IReadOnlyCollection<long> values)
    {
        var names = new List<string>(values.Count);
        int i = 0;
        foreach (long v in values)
        {
            string name = $"${prefix}{i++}";
            cmd.Parameters.AddWithValue(name, v);
            names.Add(name);
        }
        return string.Join(",", names);
    }

    [SuppressMessage("Security", "CA2100", Justification = "table — константа из кода (DeleteSegmentRow/DeleteTerminalSessionRow), id — параметр.")]
    private void DeleteRow(string table, long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

}
