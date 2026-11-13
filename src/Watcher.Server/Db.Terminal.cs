using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// Консольные/SSH-сессии (Фаза 8, F2): приём, поиск, склейка ввода и вывода.

// Фаза 8 (roadmap.md) — одна запись на консольную/SSH-сессию, Kind различает
// terminal-input (pam_tty_audit, см. Watcher.TerminalAgent/SessionCollector.cs)
// и terminal-output (script(1) через /etc/profile.d, см. watcher-term-wrap.sh).
public sealed record TerminalSession(
    long Id,
    string Host,
    string SessionUser,
    string Kind,
    string? RemoteAddr,
    DateTime StartedAt,
    DateTime EndedAt,
    double DurationSec,
    int? ExitCode,
    string? Input,
    string? OutFilePath,
    string? TimingFilePath,
    long SizeBytes,
    string? Sha256,
    string ExternalId,
    DateTime UploadedAt,
    // Задача F2 (roadmap-next.md): откуда запись — linux-tty-audit, linux-script,
    // win-transcript (NULL у записей до F2 — это Linux) — и какой shell.
    string? Source = null,
    string? Shell = null,
    // R7 — как у Segment.
    DateTime? StationStartedAt = null,
    // 0.12: сессия ещё идёт — агент шлёт её кусками (RecordingIngest.TerminalChunkAsync).
    bool Live = false,
    // audit ses= (Linux) / PID PowerShell (Windows): по нему вывод связывается
    // с вводом той же сессии (ParentId у вывода — id записи ввода).
    long? AuditSession = null,
    long? ParentId = null,
    long OutSize = 0,
    long TimingSize = 0,
    // По .timing: сумма пауз между выводом не длиннее ActiveGapSec — сколько
    // человек реально работал; последняя активность — время последнего вывода.
    double? ActiveSec = null,
    DateTime? LastActivityAt = null,
    // Идущая сессия, от которой давно нет кусков (агент перезапустился и потерял
    // её, станция выключена): в списке уже не «идёт», но кусок, если всё же
    // придёт, принимается (live = 2 в БД, см. CloseStaleTerminalSessions).
    bool Stale = false,
    // 0.13: совпала с правилом «служебное подключение» (ServiceConnections) —
    // в списке скрыта, пока не включить «служебные».
    bool Service = false,
    // 0.14.4: размер терминала на старте из метаданных обёртки — для старого
    // script(1) (util-linux < 2.35), который не пишет его ни в .out, ни в тайминг.
    int? Cols = null,
    int? Rows = null,
    // 0.16: происхождение транскрипта PowerShell (ssh, automation, local…) и режим
    // (interactive/exec) — от агента; ввод с расшифрованным -EncodedCommand.
    string? Origin = null,
    string? Mode = null,
    string? InputDecoded = null);

public sealed partial class Db
{
    // Миграция 7 (0.12): выгрузка незакрытых сессий кусками, связь ввода и вывода,
    // активное время.
    private static void TerminalLive(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
ALTER TABLE terminal_sessions ADD COLUMN live INTEGER NOT NULL DEFAULT 0;
ALTER TABLE terminal_sessions ADD COLUMN audit_session INTEGER;
ALTER TABLE terminal_sessions ADD COLUMN parent_id INTEGER;
ALTER TABLE terminal_sessions ADD COLUMN out_size INTEGER NOT NULL DEFAULT 0;
ALTER TABLE terminal_sessions ADD COLUMN timing_size INTEGER NOT NULL DEFAULT 0;
ALTER TABLE terminal_sessions ADD COLUMN active_sec REAL;
ALTER TABLE terminal_sessions ADD COLUMN last_activity_at TEXT;
CREATE INDEX ix_terminal_sessions_host_audit ON terminal_sessions(host, audit_session) WHERE audit_session IS NOT NULL;
CREATE INDEX ix_terminal_sessions_parent ON terminal_sessions(parent_id) WHERE parent_id IS NOT NULL;";
        cmd.ExecuteNonQuery();
    }

    // Миграция 13 (0.13): пометка служебных подключений.
    private static void TerminalService(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "ALTER TABLE terminal_sessions ADD COLUMN service INTEGER NOT NULL DEFAULT 0;";
        cmd.ExecuteNonQuery();
    }

    // Миграция 17 (0.14.4): размер терминала из метаданных (TerminalSession.Cols/Rows).
    private static void TerminalSize(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "ALTER TABLE terminal_sessions ADD COLUMN term_cols INTEGER; ALTER TABLE terminal_sessions ADD COLUMN term_rows INTEGER;";
        cmd.ExecuteNonQuery();
    }

    // Ввод хоста для пересчёта пометки после правки правил (ServiceConnections.Reclassify).
    public List<(long Id, string User, string? Addr, string? Input, bool Service)> TerminalInputsOfHost(string host)
    {
        var list = new List<(long, string, string?, string?, bool)>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, session_user, remote_addr, input, service FROM terminal_sessions WHERE host = $h AND kind = 'terminal-input'";
        cmd.Parameters.AddWithValue("$h", host);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4) != 0));
        return list;
    }

    // Без правил на хосте — просто снять все пометки, не читая ввод.
    public int ClearTerminalService(string host)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE terminal_sessions SET service = 0 WHERE host = $h AND service <> 0";
        cmd.Parameters.AddWithValue("$h", host);
        return cmd.ExecuteNonQuery();
    }

    public void SetTerminalService(IReadOnlyCollection<long> ids, bool service)
    {
        if (ids.Count == 0) return;
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE terminal_sessions SET service = $s WHERE id = $id";
        var pid = cmd.Parameters.Add("$id", SqliteType.Integer);
        cmd.Parameters.AddWithValue("$s", service ? 1 : 0);
        foreach (long id in ids) { pid.Value = id; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    public void DeleteTerminalSessionRow(long id) => DeleteRow("terminal_sessions", id);

    // Записи вывода, привязанные к удаляемой записи ввода, снова показываются
    // в списке сами по себе (иначе пропали бы вместе с ней).
    public static void UnlinkTerminalChildren(SqliteConnection c, long parentId)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE terminal_sessions SET parent_id = NULL WHERE parent_id = $p";
        cmd.Parameters.AddWithValue("$p", parentId);
        cmd.ExecuteNonQuery();
    }

    // Изменяемые поля идущей сессии — после каждого куска (RecordingIngest).
    public void UpdateTerminalLive(TerminalSession s, string? searchText)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
UPDATE terminal_sessions SET ended_at = $ea, duration_sec = $dur, exit_code = $exit, input = $input, search_text = $search,
  size_bytes = $size, sha256 = $sha, live = $live, out_size = $os, timing_size = $tsz, active_sec = $act, last_activity_at = $la,
  remote_addr = COALESCE(remote_addr, $addr), uploaded_at = $up, service = $svc, input_decoded = $dec
WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$ea", s.EndedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$dur", s.DurationSec);
        cmd.Parameters.AddWithValue("$exit", (object?)s.ExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$input", (object?)s.Input ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$search", (object?)searchText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$size", s.SizeBytes);
        cmd.Parameters.AddWithValue("$sha", (object?)s.Sha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$live", s.Live ? 1 : 0);
        cmd.Parameters.AddWithValue("$os", s.OutSize);
        cmd.Parameters.AddWithValue("$tsz", s.TimingSize);
        cmd.Parameters.AddWithValue("$act", (object?)s.ActiveSec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$la", (object?)s.LastActivityAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$addr", (object?)s.RemoteAddr ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$svc", s.Service ? 1 : 0);
        cmd.Parameters.AddWithValue("$dec", (object?)s.InputDecoded ?? DBNull.Value);
        // uploaded_at — время последнего куска: по нему видно, что сессия брошена.
        cmd.Parameters.AddWithValue("$up", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    // Идущие записи без кусков дольше staleAfter перестают быть «идёт» (агент
    // закрывает брошенную сессию сам через сутки, но если он перезапустился или
    // станцию выключили — некому). Вызывается из Retention.
    public int CloseStaleTerminalSessions(TimeSpan staleAfter)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE terminal_sessions SET live = 2 WHERE live = 1 AND uploaded_at < $cut";
        cmd.Parameters.AddWithValue("$cut", (DateTime.UtcNow - staleAfter).ToString("o"));
        return cmd.ExecuteNonQuery();
    }

    // Связь вывода с вводом одной сессии: тот же хост и audit ses, вывод начался
    // внутри интервала ввода (ses ядро нумерует заново после перезагрузки —
    // время различает). Вызывается и при новом выводе, и при новом/обновлённом
    // вводе: что из них придёт на сервер первым, заранее неизвестно.
    private static readonly TimeSpan LinkSlack = TimeSpan.FromSeconds(5);

    public void LinkTerminalSession(TerminalSession s)
    {
        if (s.AuditSession is not { } ses) return;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.Parameters.AddWithValue("$h", s.Host);
        cmd.Parameters.AddWithValue("$s", ses);
        if (s.Kind == "terminal-output")
        {
            cmd.CommandText = @"
UPDATE terminal_sessions SET parent_id = (
  SELECT p.id FROM terminal_sessions p
  WHERE p.host = $h AND p.kind = 'terminal-input' AND p.audit_session = $s
    AND p.started_at <= $hi AND (p.live = 1 OR p.ended_at >= $lo)
  ORDER BY p.started_at DESC LIMIT 1)
WHERE id = $id AND parent_id IS NULL";
            cmd.Parameters.AddWithValue("$id", s.Id);
            cmd.Parameters.AddWithValue("$hi", (s.StartedAt + LinkSlack).ToString("o"));
            cmd.Parameters.AddWithValue("$lo", (s.StartedAt - LinkSlack).ToString("o"));
        }
        else
        {
            cmd.CommandText = @"
UPDATE terminal_sessions SET parent_id = $id
WHERE host = $h AND kind = 'terminal-output' AND audit_session = $s AND parent_id IS NULL
  AND started_at >= $lo AND ($live = 1 OR started_at <= $hi)";
            cmd.Parameters.AddWithValue("$id", s.Id);
            cmd.Parameters.AddWithValue("$lo", (s.StartedAt - LinkSlack).ToString("o"));
            cmd.Parameters.AddWithValue("$hi", (s.EndedAt + LinkSlack).ToString("o"));
            cmd.Parameters.AddWithValue("$live", s.Live ? 1 : 0);
        }
        cmd.ExecuteNonQuery();
    }

    // Записи вывода, привязанные к этим записям ввода (для строки списка и панели сессии).
    [SuppressMessage("Security", "CA2100", Justification = "IN-список — только имена параметров (AddInParams).")]
    public List<TerminalSession> TerminalChildren(IReadOnlyCollection<long> parentIds, bool compact = false)
    {
        var list = new List<TerminalSession>();
        if (parentIds.Count == 0) return list;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT " + (compact ? CompactTerminalColumns : "ts.*") + " FROM terminal_sessions ts WHERE parent_id IN (" + AddInParams(cmd, "p", parentIds) + ") ORDER BY started_at";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapTerminal(r));
        return list;
    }

    // searchText отдельно от s.Input — для terminal-output это содержимое .out
    // блоба (сырое, с ANSI-мусором), а не то, что хранится/отдаётся как Input
    // (который у terminal-output всегда NULL, см. TerminalSession). Решает,
    // кто и как читает .out, вызывающий код (RecordingIngest.cs), не Db.
    public long InsertTerminalSession(TerminalSession s, string? searchText)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO terminal_sessions (host, session_user, kind, remote_addr, started_at, ended_at, duration_sec, exit_code, input, search_text, out_file_path, timing_file_path, size_bytes, sha256, external_id, uploaded_at, source, shell, station_started_at,
  live, audit_session, out_size, timing_size, active_sec, last_activity_at, service, term_cols, term_rows, origin, mode, input_decoded)
VALUES ($host, $user, $kind, $addr, $sa, $ea, $dur, $exit, $input, $search, $out, $timing, $size, $sha, $ext, $up, $src, $shell, $ssa,
  $live, $ases, $os, $tsz, $act, $la, $svc, $cols, $rows, $origin, $mode, $dec)
RETURNING id;";
        cmd.Parameters.AddWithValue("$host", s.Host);
        cmd.Parameters.AddWithValue("$user", s.SessionUser);
        cmd.Parameters.AddWithValue("$kind", s.Kind);
        cmd.Parameters.AddWithValue("$addr", (object?)s.RemoteAddr ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sa", s.StartedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$ea", s.EndedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$dur", s.DurationSec);
        cmd.Parameters.AddWithValue("$exit", (object?)s.ExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$input", (object?)s.Input ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$search", (object?)searchText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$out", (object?)s.OutFilePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$timing", (object?)s.TimingFilePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$size", s.SizeBytes);
        cmd.Parameters.AddWithValue("$sha", (object?)s.Sha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ext", s.ExternalId);
        cmd.Parameters.AddWithValue("$up", s.UploadedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$src", (object?)s.Source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$shell", (object?)s.Shell ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ssa", (object?)s.StationStartedAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$live", s.Live ? 1 : 0);
        cmd.Parameters.AddWithValue("$ases", (object?)s.AuditSession ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$os", s.OutSize);
        cmd.Parameters.AddWithValue("$tsz", s.TimingSize);
        cmd.Parameters.AddWithValue("$act", (object?)s.ActiveSec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$la", (object?)s.LastActivityAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$svc", s.Service ? 1 : 0);
        cmd.Parameters.AddWithValue("$cols", (object?)s.Cols ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rows", (object?)s.Rows ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$origin", (object?)s.Origin ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mode", (object?)s.Mode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dec", (object?)s.InputDecoded ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    public TerminalSession? TerminalByExternalId(string externalId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM terminal_sessions WHERE external_id = $e LIMIT 1";
        cmd.Parameters.AddWithValue("$e", externalId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapTerminal(r) : null;
    }

    public TerminalSession? TerminalById(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM terminal_sessions WHERE id = $i LIMIT 1";
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapTerminal(r) : null;
    }

    // Задача E4 (roadmap-next.md): записи другого kind той же консольной сессии.
    // Общего id у ввода (pam_tty_audit, ses= аудита) и вывода (script(1) из
    // watcher-term-wrap.sh) нет — связываем по host + user + пересечению
    // интервалов: script стартует внутри SSH-сессии и кончается раньше неё, так
    // что интервал вывода лежит внутри интервала ввода. slack — запас на
    // секундную точность времени в watcher-term-wrap.sh (date +%s).
    public List<TerminalSession> TerminalOverlapping(string host, string user, string kind, DateTime startedAt, DateTime endedAt, TimeSpan slack, int limit)
    {
        var list = new List<TerminalSession>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        // started_at/ended_at хранятся ISO-8601 UTC ("o") — строковое сравнение
        // совпадает с хронологическим, как и в QueryTerminal.
        cmd.CommandText = @"
SELECT * FROM terminal_sessions
WHERE host = $h AND session_user = $u AND kind = $k
  AND started_at <= $to AND ended_at >= $from
ORDER BY started_at LIMIT $lim";
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$u", user);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$from", (startedAt - slack).ToUniversalTime().ToString("o"));
        cmd.Parameters.AddWithValue("$to", (endedAt + slack).ToUniversalTime().ToString("o"));
        cmd.Parameters.AddWithValue("$lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapTerminal(r));
        return list;
    }

    // groupId фильтрует через JOIN hosts ON hosts.name = terminal_sessions.host
    // (см. EnsureHostExists/UpsertHeartbeat — тот же слабый "по имени" линк,
    // что весь остальной проект использует между host-таблицами, не FK).
    // search — LIKE по search_text (см. InsertTerminalSession), не FTS5.
    // os (Задача F2): "windows" — source win-*, "linux" — linux-* и записи до F2
    // (source NULL, тогда были только Linux-агенты).
    [SuppressMessage("Security", "CA2100", Justification = "WHERE/JOIN собираются только из констант кода и имён параметров ($x, AddInParams); значения пользователя — только через Parameters.")]
    //
    // sessions (0.12, список «Консоль»): одна строка на сессию — вывод,
    // привязанный к вводу (parent_id), в выдачу не попадает сам, а поиск по его
    // тексту находит строку ввода.
    //
    // service (0.13): false — без служебных подключений, true — только они,
    // null — все.
    //
    // origin (0.16, транскрипты PowerShell): "no-automation" — без задач/служб/SQL
    // Agent (вид «Консоли» по умолчанию), код происхождения — только он, null/"all" —
    // все. У записей Linux и старых агентов происхождения нет — они не скрываются.
    public List<TerminalSession> QueryTerminal(string? host, string? user, string? kind, DateTime? from, DateTime? to, string? search, long? groupId, int limit, IReadOnlyCollection<long>? allowedGroupIds = null, string? os = null, bool sessions = false, bool? service = null, string? type = null, string? beforeAt = null, long beforeId = 0, bool compact = false, string? origin = null)
    {
        var list = new List<TerminalSession>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        if (host is not null) { where.Add("ts.host = $h"); cmd.Parameters.AddWithValue("$h", host); }
        // 0.14.4: пользователь — подстрока без учёта регистра (поле в «Консоли» ищет по набранному).
        if (!string.IsNullOrWhiteSpace(user))
        {
            where.Add("ts.session_user LIKE $u ESCAPE '\\'");
            cmd.Parameters.AddWithValue("$u", "%" + user.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        }
        if (kind is not null) { where.Add("ts.kind = $k"); cmd.Parameters.AddWithValue("$k", kind); }
        if (from is not null) { where.Add("ts.started_at >= $from"); cmd.Parameters.AddWithValue("$from", from.Value.ToString("o")); }
        if (to is not null) { where.Add("ts.started_at < $to"); cmd.Parameters.AddWithValue("$to", to.Value.ToString("o")); }
        if (!string.IsNullOrWhiteSpace(search))
        {
            // ESCAPE обязателен — без него литерал '\' в параметре не включает
            // экранирование для SQLite LIKE, он бы просто совпадал как символ.
            where.Add(sessions
                ? "(ts.search_text LIKE $q ESCAPE '\\' OR EXISTS (SELECT 1 FROM terminal_sessions ch WHERE ch.parent_id = ts.id AND ch.search_text LIKE $q ESCAPE '\\'))"
                : "ts.search_text LIKE $q ESCAPE '\\'");
            cmd.Parameters.AddWithValue("$q", "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        }
        if (beforeAt is not null)
        {
            where.Add("(ts.started_at < $before OR (ts.started_at = $before AND ts.id < $bid))");
            cmd.Parameters.AddWithValue("$before", beforeAt);
            cmd.Parameters.AddWithValue("$bid", beforeId);
        }
        if (sessions) where.Add("ts.parent_id IS NULL");
        if (service is { } svc) where.Add(svc ? "ts.service = 1" : "ts.service = 0");
        // 0.14.4: интерактивные — с терминалом (ввод pam_tty_audit, вывод script,
        // транскрипт PowerShell); exec — команды без терминала (auditd, linux-exec).
        // 0.15.0: ввод из auditd, но к сессии привязан вывод script(1) — терминал
        // был (pam_tty_audit ввод не записал), запись воспроизводится — интерактивная.
        const string hasOutput = "(ts.out_file_path IS NOT NULL OR EXISTS (SELECT 1 FROM terminal_sessions o WHERE o.parent_id = ts.id AND o.out_file_path IS NOT NULL))";
        // 0.16: транскрипт PowerShell одной команды (ssh host "cmd", -EncodedCommand,
        // -File) — тоже «без терминала» (mode = exec от агента).
        if (type == "interactive") where.Add($"((ts.source IS NULL OR ts.source <> 'linux-exec' OR {hasOutput}) AND COALESCE(ts.mode, '') <> 'exec')");
        else if (type == "exec") where.Add($"((ts.source = 'linux-exec' AND NOT {hasOutput}) OR ts.mode = 'exec')");
        if (origin == "no-automation") where.Add("(ts.origin IS NULL OR ts.origin NOT IN ('automation', 'sql-agent'))");
        else if (origin is not null && origin != "all" && TerminalOrigins.IsKnown(origin))
        { where.Add("ts.origin = $origin"); cmd.Parameters.AddWithValue("$origin", origin); }
        if (os == "windows") where.Add("ts.source LIKE 'win-%'");
        else if (os == "linux") where.Add("(ts.source IS NULL OR ts.source LIKE 'linux-%')");
        // groupId (явный фильтр из UI) и allowedGroupIds (ограничение viewer'а,
        // Этап C1) независимы и могут сочетаться — один JOIN на оба условия.
        // Если явный groupId не входит в allowedGroupIds, оба условия вместе
        // дают пустой результат, а не ошибку — это корректное поведение, не баг.
        string joinSql = "";
        if (groupId is not null || allowedGroupIds is not null)
        {
            joinSql = "JOIN hosts h ON h.name = ts.host";
        }
        if (groupId is not null)
        {
            where.Add("h.group_id = $gid");
            cmd.Parameters.AddWithValue("$gid", groupId.Value);
        }
        if (allowedGroupIds is not null)
        {
            where.Add(GroupIn(cmd, "ag", allowedGroupIds));
        }
        string whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $"SELECT {(compact ? CompactTerminalColumns : "ts.*")} FROM terminal_sessions ts {joinSql} {whereSql} ORDER BY ts.started_at DESC, ts.id DESC LIMIT $lim";
        cmd.Parameters.AddWithValue("$lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapTerminal(r));
        return list;
    }

    private const string CompactTerminalColumns = "ts.id, ts.host, ts.session_user, ts.kind, ts.remote_addr, ts.started_at, ts.ended_at, ts.duration_sec, ts.exit_code, substr(ts.input,1,769) AS input, ts.out_file_path, ts.timing_file_path, ts.size_bytes, ts.sha256, ts.external_id, ts.uploaded_at, ts.source, ts.shell, ts.live, ts.audit_session, ts.parent_id, ts.out_size, ts.timing_size, ts.active_sec, ts.last_activity_at, ts.service, ts.term_cols, ts.term_rows, ts.station_started_at, ts.origin, ts.mode, substr(ts.input_decoded,1,769) AS input_decoded";

    private static TerminalSession MapTerminal(SqliteDataReader r) => new(
        r.GetInt64(r.GetOrdinal("id")),
        r.GetString(r.GetOrdinal("host")),
        r.GetString(r.GetOrdinal("session_user")),
        r.GetString(r.GetOrdinal("kind")),
        r.IsDBNull(r.GetOrdinal("remote_addr")) ? null : r.GetString(r.GetOrdinal("remote_addr")),
        DateTime.Parse(r.GetString(r.GetOrdinal("started_at")), null, System.Globalization.DateTimeStyles.RoundtripKind),
        DateTime.Parse(r.GetString(r.GetOrdinal("ended_at")), null, System.Globalization.DateTimeStyles.RoundtripKind),
        r.GetDouble(r.GetOrdinal("duration_sec")),
        r.IsDBNull(r.GetOrdinal("exit_code")) ? null : r.GetInt32(r.GetOrdinal("exit_code")),
        r.IsDBNull(r.GetOrdinal("input")) ? null : r.GetString(r.GetOrdinal("input")),
        r.IsDBNull(r.GetOrdinal("out_file_path")) ? null : r.GetString(r.GetOrdinal("out_file_path")),
        r.IsDBNull(r.GetOrdinal("timing_file_path")) ? null : r.GetString(r.GetOrdinal("timing_file_path")),
        r.GetInt64(r.GetOrdinal("size_bytes")),
        r.IsDBNull(r.GetOrdinal("sha256")) ? null : r.GetString(r.GetOrdinal("sha256")),
        r.GetString(r.GetOrdinal("external_id")),
        DateTime.Parse(r.GetString(r.GetOrdinal("uploaded_at")), null, System.Globalization.DateTimeStyles.RoundtripKind),
        r.IsDBNull(r.GetOrdinal("source")) ? null : r.GetString(r.GetOrdinal("source")),
        r.IsDBNull(r.GetOrdinal("shell")) ? null : r.GetString(r.GetOrdinal("shell")),
        ReadStationStartedAt(r),
        Live: r.GetInt64(r.GetOrdinal("live")) == 1,
        Stale: r.GetInt64(r.GetOrdinal("live")) == 2,
        AuditSession: r.IsDBNull(r.GetOrdinal("audit_session")) ? null : r.GetInt64(r.GetOrdinal("audit_session")),
        ParentId: r.IsDBNull(r.GetOrdinal("parent_id")) ? null : r.GetInt64(r.GetOrdinal("parent_id")),
        OutSize: r.GetInt64(r.GetOrdinal("out_size")),
        TimingSize: r.GetInt64(r.GetOrdinal("timing_size")),
        ActiveSec: r.IsDBNull(r.GetOrdinal("active_sec")) ? null : r.GetDouble(r.GetOrdinal("active_sec")),
        LastActivityAt: r.IsDBNull(r.GetOrdinal("last_activity_at")) ? null
            : DateTime.Parse(r.GetString(r.GetOrdinal("last_activity_at")), null, System.Globalization.DateTimeStyles.RoundtripKind),
        Service: r.GetInt64(r.GetOrdinal("service")) != 0,
        Cols: r.IsDBNull(r.GetOrdinal("term_cols")) ? null : r.GetInt32(r.GetOrdinal("term_cols")),
        Rows: r.IsDBNull(r.GetOrdinal("term_rows")) ? null : r.GetInt32(r.GetOrdinal("term_rows")),
        Origin: r.IsDBNull(r.GetOrdinal("origin")) ? null : r.GetString(r.GetOrdinal("origin")),
        Mode: r.IsDBNull(r.GetOrdinal("mode")) ? null : r.GetString(r.GetOrdinal("mode")),
        InputDecoded: r.IsDBNull(r.GetOrdinal("input_decoded")) ? null : r.GetString(r.GetOrdinal("input_decoded"))
    );
}
