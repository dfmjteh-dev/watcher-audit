using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// Хосты: heartbeat и присутствие, реестр (инвентарь, флот), привязка к группам.

// Этап E2 (roadmap-next.md): одна строка на хост для раздела «Хосты» — слияние
// бывших «Видео» (статистика segments) и «Парка» (heartbeat из hosts). Id null —
// хост есть только в записях (heartbeat ещё не приходил, например старые данные
// до Фазы 3): назначить группу/конфиг ему пока нельзя, видеть записи — можно.
public sealed record InventoryHost(
    long? Id,
    string Name,
    string? IpAddress,
    long? GroupId,
    string? GroupName,
    DateTime? LastHeartbeatAt,
    string? AgentVersion,
    int DroppedSegments,
    bool? LastHeartbeatHttps,
    int Segments,
    long SegmentBytes,
    DateTime? LastSegmentAt,
    int Segments24h,
    long SegmentBytes24h,
    int TerminalSessions,
    long TerminalBytes,
    DateTime? LastTerminalAt,
    int TerminalSessions24h,
    // Задача G1 — см. FleetHost.
    bool? LegacyAuth = null,
    bool DuplicateCredential = false,
    // R7 — см. FleetHost.
    double? ClockSkewSec = null,
    // Кто последним попал в запись (видео или консоль) — для списка хостов и
    // «Инфо»: на станции с одним пользователем его иначе нигде не видно.
    string? LastUser = null,
    // 0.13: непринятые события журнала потерь; LossJournal — агент шлёт журнал
    // (иначе в UI старый счётчик DroppedSegments).
    int UnackedLosses = 0,
    bool LossJournal = false,
    // 0.13: дошёл ли итоговый конфиг — ok | pending | unknown (агент не сообщает).
    string ConfigState = "unknown",
    // 0.13: «Снять с записи» — что сейчас не пишется (null — пишется всё).
    HostPause? Pause = null,
    // 0.13 (PR9б): CPU/память агента и очередь — последний heartbeat.
    HostResources? Resources = null,
    // Этап 2: сотрудник, за которым закреплено устройство (см. FleetHost.Person).
    string? Person = null,
    // Этап 4: платформа агента из heartbeat (windows/linux/android) и для Android —
    // последнее событие и доступ к статистике использования из последнего
    // device_state (false — агент «ослеп»: не видит приложения на экране).
    string? Platform = null,
    DateTime? AndroidLastEventAt = null,
    bool? AndroidUsageAccess = null,
    // Этап 5: роль владельца устройства на месте (из heartbeat) и запрошено ли
    // её снятие админом (команда ждёт следующего heartbeat владельца).
    bool? AndroidOwnerActive = null,
    bool AndroidClearOwnerRequested = false,
    // Этап 12: удалённая блокировка устройства — запрошена админом и фактическая.
    bool AndroidLockRequested = false,
    bool? AndroidLocked = null,
    string CaptureState = "unknown");

public sealed record HostPause(bool Video, bool Console, string? Until, string? Reason);

public enum RenameOutcome { Renamed, Conflict, NotFound }

public sealed record FleetHost(
    long Id,
    string Name,
    string? IpAddress,
    long? GroupId,
    string? GroupName,
    DateTime? LastHeartbeatAt,
    string? AgentVersion,
    int DroppedSegments,
    bool? LastHeartbeatHttps,
    // Задача G1: последний heartbeat по общему ApiToken; у хоста есть
    // непросмотренная повторная учётка того же вида.
    bool? LegacyAuth = null,
    bool DuplicateCredential = false,
    // R7: насколько часы станции отстают (+) или спешат (−) по последнему
    // heartbeat, в секундах; null — агент до R7 время не присылает.
    double? ClockSkewSec = null,
    // Этап 2 (docs/android-plan.md): сотрудник, за которым закреплено
    // устройство. Задаётся в веб-UI (PATCH /api/fleet), не агентом. На телефоне
    // один пользователь — это заменяет AllowUsers/ExcludeUsers (android-develop.md);
    // для станций поле тоже доступно, но обычно пустое. null — не задан.
    string? Person = null);

public sealed partial class Db
{
    // Миграция 19 (Этап 2): привязка «устройство ↔ сотрудник» для Android-хостов.
    private static void HostPerson(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "ALTER TABLE hosts ADD COLUMN person TEXT;";
        cmd.ExecuteNonQuery();
    }

    // Миграция 32: версии, сохранённые до нормализации при приёме (UpsertHeartbeat), —
    // без «+<коммит>», чтобы и молчащие хосты показывались одинаково.
    private static void AgentVersionCore(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE hosts SET agent_version = substr(agent_version, 1, instr(agent_version, '+') - 1) WHERE instr(agent_version, '+') > 0;";
        cmd.ExecuteNonQuery();
    }

    // Upsert по имени хоста — agent_version/ip_address/dropped_segments перезаписываются
    // последним heartbeat'ом, group_id трогать нельзя (назначается через веб-UI, не агентом).
    // dropped_segments — счётчик агента с момента ЕГО старта (см. Watcher.Uploader, Фаза 5),
    // не накопительный на сервере: просто последнее сообщённое значение, сбрасывается на 0
    // при перезапуске агента так же, как agent_version не "накопительный".
    public void UpsertHeartbeat(string name, string? ipAddress, string? agentVersion, int droppedSegments = 0, bool? isHttps = null, double? clockSkewSec = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO hosts (name, ip_address, last_heartbeat_at, agent_version, dropped_segments, last_heartbeat_https, clock_skew_sec)
VALUES ($name, $ip, $now, $ver, $dropped, $https, $skew)
ON CONFLICT(name) DO UPDATE SET
  ip_address = excluded.ip_address,
  last_heartbeat_at = excluded.last_heartbeat_at,
  agent_version = excluded.agent_version,
  dropped_segments = excluded.dropped_segments,
  last_heartbeat_https = excluded.last_heartbeat_https,
  clock_skew_sec = excluded.clock_skew_sec;";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$ip", (object?)ipAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        // Агенты, собранные до IncludeSourceRevisionInInformationalVersion=false, присылают
        // «0.15.1+<коммит>» — хвост для показа и сравнения лишний.
        cmd.Parameters.AddWithValue("$ver", agentVersion is null ? DBNull.Value : AppVersion.Core(agentVersion));
        cmd.Parameters.AddWithValue("$dropped", droppedSegments);
        cmd.Parameters.AddWithValue("$https", isHttps is null ? DBNull.Value : (isHttps.Value ? 1 : 0));
        cmd.Parameters.AddWithValue("$skew", (object?)clockSkewSec ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        using var presence = c.CreateCommand();
        presence.CommandText = "INSERT OR IGNORE INTO host_presence (host, hour) VALUES ($name, $hour)";
        presence.Parameters.AddWithValue("$name", name);
        presence.Parameters.AddWithValue("$hour", DateTime.UtcNow.ToString("yyyy-MM-ddTHH"));
        presence.ExecuteNonQuery();
    }

    public List<string> HostPresenceHours(string name, DateTime sinceUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT hour FROM host_presence WHERE host = $n AND hour >= $since ORDER BY hour";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("yyyy-MM-ddTHH"));
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public int DeletePresenceOlderThan(DateTime cutoffUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM host_presence WHERE hour < $cut";
        cmd.Parameters.AddWithValue("$cut", cutoffUtc.ToString("yyyy-MM-ddTHH"));
        return cmd.ExecuteNonQuery();
    }

    // Этап E2: имена — объединение hosts (heartbeat) и хостов из самих записей:
    // сегменты не создают строку в hosts (в отличие от terminal-upload, см.
    // EnsureHostExists), а терять в списке хост только потому, что он ни разу не
    // слал heartbeat, нельзя. Агрегация по segments/terminal_sessions — полный
    // GROUP BY host (индексы host+started_at). R13 (code-review-2026-10.md): это
    // единственный список хостов для UI — прежний /api/hosts (Db.HostStats, только
    // segments.host) удалён, хост с одним терминальным агентом в нём пропадал.
    // allowedGroupIds — ограничение viewer'а (Этап C1): хост без строки в hosts
    // для него не виден, тот же opt-in, что ViewerScope.CanSeeHost.
    [SuppressMessage("Security", "CA2100", Justification = "WHERE/JOIN собираются только из констант кода и имён параметров ($x, AddInParams); значения пользователя — только через Parameters.")]
    public List<InventoryHost> Inventory(IReadOnlyCollection<long>? allowedGroupIds = null, string? onlyName = null, DateTime? since = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        if (allowedGroupIds is not null)
            where.Add(GroupIn(cmd, "ag", allowedGroupIds));
        if (onlyName is not null)
        {
            where.Add("n.name = $only");
            cmd.Parameters.AddWithValue("$only", onlyName);
        }
        string whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $@"
WITH seg AS (
  SELECT host, COUNT(*) AS n24, COALESCE(SUM(size_bytes),0) AS bytes24
  FROM segments WHERE started_at >= $cut GROUP BY host
),
names AS (
  SELECT name FROM hosts UNION SELECT host FROM seg UNION SELECT host FROM terminal_sessions
),
term AS (
  SELECT host, COUNT(*) AS n, COALESCE(SUM(size_bytes),0) AS bytes, MAX(started_at) AS last,
         SUM(CASE WHEN started_at >= $cut THEN 1 ELSE 0 END) AS n24
  FROM terminal_sessions GROUP BY host
)
SELECT h.id, n.name, h.ip_address, h.group_id, g.name, h.last_heartbeat_at, h.agent_version,
       COALESCE(h.dropped_segments,0), h.last_heartbeat_https,
       0, 0, NULL, COALESCE(seg.n24,0), COALESCE(seg.bytes24,0),
       COALESCE(term.n,0), COALESCE(term.bytes,0), term.last, COALESCE(term.n24,0),
       h.last_auth_legacy,
       EXISTS(SELECT 1 FROM agent_credentials a WHERE a.host_id = h.id AND a.duplicate_kind = 1 AND a.revoked_at IS NULL),
       h.clock_skew_sec,
       (SELECT s.session_user FROM segments s WHERE s.host = n.name AND s.session_user <> ''
        ORDER BY s.started_at DESC LIMIT 1),
       (SELECT t.session_user FROM terminal_sessions t WHERE t.host = n.name AND t.session_user NOT IN ('', 'unknown')
        ORDER BY t.started_at DESC LIMIT 1),
       h.config_json, g.config_json, h.config_hash, h.captures_json, COALESCE(h.loss_journal, 0),
       (SELECT COUNT(*) FROM host_losses l WHERE l.host = n.name AND l.acknowledged_at IS NULL),
       h.resources_json,
       h.person,
       h.agent_platform,
       (SELECT MAX(e.ts) FROM android_events e WHERE e.host = n.name),
       (SELECT json_extract(e.detail, '$.usageAccess') FROM android_events e
        WHERE e.host = n.name AND e.type = 'device_state' ORDER BY e.ts DESC LIMIT 1),
       h.android_owner_active,
       h.android_clear_owner_at,
       h.android_lock_requested,
       h.android_locked
FROM names n
LEFT JOIN hosts h ON h.name = n.name
LEFT JOIN host_groups g ON g.id = h.group_id
LEFT JOIN seg ON seg.host = n.name
LEFT JOIN term ON term.host = n.name
{whereSql}
ORDER BY n.name";
        // «За сутки» (…24h) — с since (полночь у смотрящего), без него — последние 24 часа.
        cmd.Parameters.AddWithValue("$cut", (since ?? DateTime.UtcNow.AddDays(-1)).ToUniversalTime().ToString("o"));
        static DateTime? Dt(SqliteDataReader r, int i) =>
            r.IsDBNull(i) ? null : DateTime.Parse(r.GetString(i), null, System.Globalization.DateTimeStyles.RoundtripKind);
        // Итоги по сегментам — из кэша (Db.HostTotals.cs), не GROUP BY по всей таблице.
        var totals = SegmentTotalsByHost();
        var list = new List<InventoryHost>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var seg = totals.GetValueOrDefault(r.GetString(1)) ?? new HostRecordTotals(0, 0, null);
            string merged = MergeConfig(r.IsDBNull(24) ? null : r.GetString(24), r.IsDBNull(23) ? null : r.GetString(23));
            list.Add(new InventoryHost(
                r.IsDBNull(0) ? null : r.GetInt64(0),
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                Dt(r, 5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.GetInt32(7),
                r.IsDBNull(8) ? null : r.GetInt64(8) != 0,
                seg.Count, seg.Bytes, seg.Last, r.GetInt32(12), r.GetInt64(13),
                r.GetInt32(14), r.GetInt64(15), Dt(r, 16), r.GetInt32(17),
                r.IsDBNull(18) ? null : r.GetInt64(18) != 0,
                r.GetInt64(19) != 0,
                r.IsDBNull(20) ? null : r.GetDouble(20),
                LastUser(seg.Last, r.IsDBNull(21) ? null : r.GetString(21), Dt(r, 16), r.IsDBNull(22) ? null : r.GetString(22)),
                r.GetInt32(28),
                r.GetInt64(27) != 0,
                r.IsDBNull(0) ? "unknown" : ConfigStateOf(ConfigHash(merged),
                    r.IsDBNull(25) ? null : r.GetString(25), r.IsDBNull(26) ? null : r.GetString(26), DateTime.UtcNow),
                PauseOf(merged),
                ParseResources(r.IsDBNull(29) ? null : r.GetString(29)),
                r.IsDBNull(30) ? null : r.GetString(30),
                r.IsDBNull(31) ? null : r.GetString(31),
                Dt(r, 32),
                r.IsDBNull(33) ? null : r.GetInt64(33) != 0,
                r.IsDBNull(34) ? null : r.GetInt64(34) != 0,
                !r.IsDBNull(35),
                !r.IsDBNull(36) && r.GetInt64(36) != 0,
                r.IsDBNull(37) ? null : r.GetInt64(37) != 0,
                CaptureStateOf(r.IsDBNull(26) ? null : r.GetString(26), DateTime.UtcNow)));
        }
        // Хосты только с прошлыми сегментами (без строки в hosts, без записей
        // сегодня и без консоли) — в SQL их нет, добавляем по итогам. Ограниченный
        // viewer их не видит (нет группы), как и раньше.
        if (allowedGroupIds is null)
        {
            var have = list.Select(h => h.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var (host, t) in totals)
                if (!have.Contains(host) && (onlyName is null || onlyName == host))
                    list.Add(new InventoryHost(null, host, null, null, null, null, null, 0, null, t.Count, t.Bytes, t.Last, 0, 0, 0, 0, null, 0));
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }
        return list;
    }

    // Пауза записи из итогового конфига; истёкшая (pauseUntil в прошлом) — нет паузы.
    internal static HostPause? PauseOf(string mergedConfigJson)
    {
        bool video = TerminalUserFilter.VideoPaused(mergedConfigJson, DateTime.UtcNow);
        bool console = TerminalUserFilter.ConsolePaused(mergedConfigJson, DateTime.UtcNow);
        if (!video && !console) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(mergedConfigJson);
        string? Str(string key) => doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
        return new HostPause(video, console, Str("pauseUntil"), Str("pauseReason"));
    }

    // Окно, в котором процесс записи считается живым: он отмечается при каждой
    // проверке конфига (ConfigCheckIntervalSeconds, 30 с), агент шлёт отметки
    // раз в heartbeat (60 с).
    internal static readonly TimeSpan CaptureAliveWindow = TimeSpan.FromMinutes(5);

    // ok — агент получил текущий итоговый конфиг и живые процессы записи его
    // применили; pending — нет (ещё не дошёл или процесс записи на старом);
    // unknown — агент о конфиге не сообщает (старше 0.13 или ещё не было heartbeat).
    internal static string ConfigStateOf(string expectedHash, string? deliveredHash, string? capturesJson, DateTime nowUtc)
    {
        if (deliveredHash is null) return "unknown";
        if (deliveredHash != expectedHash) return "pending";
        foreach (var cap in ParseCaptures(capturesJson))
            if (cap.At is { } at && nowUtc - at < CaptureAliveWindow && cap.ConfigHash is { } h && h != expectedHash)
                return "pending";
        return "ok";
    }

    internal static string CaptureStateOf(string? capturesJson, DateTime nowUtc)
    {
        if (capturesJson is null) return "unknown";
        var fresh = ParseCaptures(capturesJson).Where(c => c.At is { } at &&
            at <= nowUtc.AddMinutes(1) && nowUtc - at < CaptureAliveWindow).ToList();
        if (fresh.Count == 0) return "missing";
        if (fresh.Any(c => c.Recording == true)) return "active";
        return fresh.All(c => c.Recording == false) ? "paused" : "unknown";
    }

    internal static List<CaptureReport> ParseCaptures(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<CaptureReport>>(json,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? new();
        }
        catch (System.Text.Json.JsonException) { return new(); }
    }

    // Из последних пользователей видео и консоли — того, чья запись свежее (по
    // времени последней записи вида; запись без пользователя тут не учитывается,
    // приближения достаточно).
    internal static string? LastUser(DateTime? lastSegmentAt, string? segmentUser, DateTime? lastTerminalAt, string? terminalUser)
    {
        if (segmentUser is null) return terminalUser;
        if (terminalUser is null) return segmentUser;
        return (lastTerminalAt ?? DateTime.MinValue) > (lastSegmentAt ?? DateTime.MinValue) ? terminalUser : segmentUser;
    }

    // Машину переименовали (hostnamectl, «Свойства системы») — агент приходит с
    // той же учёткой, но с новым именем. Хост переименовывается вместе со всей
    // историей: записи, активность, присутствие, legal hold. Файлы записей не
    // переносятся — file_path относительный и от имени не зависит. Новое имя уже
    // занято (другой хост или старые записи без хоста) — Conflict, ничего не
    // меняется: сливать чужие истории молча нельзя.
    [SuppressMessage("Security", "CA2100", Justification = "Тексты UPDATE — константы кода из массива ниже; имена — только через Parameters.")]
    public RenameOutcome RenameHost(long hostId, string newName)
    {
        using var totals = TotalsChange();
        using var c = Open();
        using var tx = c.BeginTransaction();
        string? oldName;
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT name FROM hosts WHERE id = $id";
            q.Parameters.AddWithValue("$id", hostId);
            oldName = q.ExecuteScalar() as string;
        }
        if (oldName is null) return RenameOutcome.NotFound;
        if (oldName == newName) return RenameOutcome.Renamed;
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = @"
SELECT EXISTS(SELECT 1 FROM hosts WHERE name = $n AND id <> $id)
    OR EXISTS(SELECT 1 FROM segments WHERE host = $n)
    OR EXISTS(SELECT 1 FROM terminal_sessions WHERE host = $n)";
            q.Parameters.AddWithValue("$n", newName);
            q.Parameters.AddWithValue("$id", hostId);
            if (Convert.ToInt64(q.ExecuteScalar()) != 0) return RenameOutcome.Conflict;
        }
        foreach (var sql in new[]
        {
            "UPDATE hosts SET name = $new WHERE id = $id",
            "UPDATE segments SET host = $new WHERE host = $old",
            "UPDATE transcode_jobs SET host = $new WHERE host = $old",
            // Заготовки отчётов (Db.ReportFacts.cs) — под новым именем.
            "UPDATE OR IGNORE report_host_hour SET host = $new WHERE host = $old",
            "UPDATE OR IGNORE report_user_hour SET host = $new WHERE host = $old",
            "UPDATE report_app_day SET host = $new WHERE host = $old",
            "UPDATE report_subject_day SET host = $new WHERE host = $old",
            "UPDATE OR IGNORE report_dirty SET host = $new WHERE host = $old",
            // Совпали с уже бывшими под новым именем (редко: записи без heartbeat).
            "DELETE FROM report_host_hour WHERE host = $old",
            "DELETE FROM report_user_hour WHERE host = $old",
            "DELETE FROM report_dirty WHERE host = $old",
            "UPDATE segment_activity SET host = $new WHERE host = $old",
            "UPDATE terminal_sessions SET host = $new WHERE host = $old",
            "UPDATE host_presence SET host = $new WHERE host = $old",
            "UPDATE legal_holds SET host = $new WHERE host = $old",
            "UPDATE host_losses SET host = $new WHERE host = $old",
            "UPDATE android_events SET host = $new WHERE host = $old",   // Этап 3
        })
        {
            using var u = c.CreateCommand();
            u.Transaction = tx;
            u.CommandText = sql;
            u.Parameters.AddWithValue("$new", newName);
            u.Parameters.AddWithValue("$old", oldName);
            u.Parameters.AddWithValue("$id", hostId);
            u.ExecuteNonQuery();
        }
        tx.Commit();
        return RenameOutcome.Renamed;
    }

    public bool SetHostGroup(long hostId, long? groupId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE hosts SET group_id = $g WHERE id = $id";
        cmd.Parameters.AddWithValue("$g", (object?)groupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", hostId);
        return cmd.ExecuteNonQuery() > 0;
    }

    // Этап 2: закрепить устройство за сотрудником (веб-UI). person == null —
    // снять привязку. Валидация значения — на эндпоинте (FleetEndpoints).
    public bool SetHostPerson(long hostId, string? person)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE hosts SET person = $p WHERE id = $id";
        cmd.Parameters.AddWithValue("$p", (object?)person ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", hostId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public int CountFleetHosts(long? groupId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        string where = groupId is not null ? "WHERE group_id = $g" : "";
        cmd.CommandText = $"SELECT COUNT(*) FROM hosts {where}";
        if (groupId is not null) cmd.Parameters.AddWithValue("$g", groupId.Value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<FleetHost> ListFleetHosts(long? groupId, int limit, int offset)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        string where = groupId is not null ? "WHERE h.group_id = $g" : "";
        cmd.CommandText = $@"
SELECT h.id, h.name, h.ip_address, h.group_id, g.name, h.last_heartbeat_at, h.agent_version, h.dropped_segments, h.last_heartbeat_https,
       h.last_auth_legacy,
       EXISTS(SELECT 1 FROM agent_credentials a WHERE a.host_id = h.id AND a.duplicate_kind = 1 AND a.revoked_at IS NULL),
       h.clock_skew_sec,
       h.person
FROM hosts h
LEFT JOIN host_groups g ON g.id = h.group_id
{where}
ORDER BY h.name
LIMIT $lim OFFSET $off";
        if (groupId is not null) cmd.Parameters.AddWithValue("$g", groupId.Value);
        cmd.Parameters.AddWithValue("$lim", limit);
        cmd.Parameters.AddWithValue("$off", offset);
        var list = new List<FleetHost>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new FleetHost(
                r.GetInt64(0),
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : DateTime.Parse(r.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.GetInt32(7),
                r.IsDBNull(8) ? null : r.GetInt64(8) != 0,
                r.IsDBNull(9) ? null : r.GetInt64(9) != 0,
                r.GetInt64(10) != 0,
                r.IsDBNull(11) ? null : r.GetDouble(11),
                r.IsDBNull(12) ? null : r.GetString(12)));
        }
        return list;
    }

    // Та же слабая связь "по имени", что EnsureHostExists/QueryTerminal —
    // null, если хост ещё не появлялся в /api/heartbeat (нет строки в hosts)
    // или появлялся, но группа не назначена.
    public long? HostGroupId(string hostName)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT group_id FROM hosts WHERE name = $n LIMIT 1";
        cmd.Parameters.AddWithValue("$n", hostName);
        object? result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    public string? HostNameById(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM hosts WHERE id = $i LIMIT 1";
        cmd.Parameters.AddWithValue("$i", id);
        return cmd.ExecuteScalar() as string;
    }

    // Юзается только из /api/upload-terminal — хост с терминальным агентом,
    // но без своего Watcher.Uploader (например, сервер без захвата экрана),
    // должен появиться в /api/fleet для назначения группы, даже не отправив
    // ни одного heartbeat. ON CONFLICT DO NOTHING — не трогает ip_address/
    // last_heartbeat_at/agent_version, если хост уже существует (не должен
    // перетирать настоящие данные от heartbeat гонкой с терминальным аплоадом).
    public void EnsureHostExists(string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO hosts (name) VALUES ($n) ON CONFLICT(name) DO NOTHING;";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.ExecuteNonQuery();
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Только константные условия и GroupIn; ввод привязан параметрами.")]
    public List<string> HostNameSuggestions(string? search, int limit, IReadOnlyCollection<long>? allowedGroups)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        string scope = allowedGroups is null ? "1" : GroupIn(cmd, "hg", allowedGroups);
        cmd.CommandText = "SELECT h.name FROM hosts h WHERE " + scope + " AND instr(lower(h.name), lower($q)) > 0 ORDER BY h.name LIMIT $lim";
        cmd.Parameters.AddWithValue("$q", (search ?? "").Trim());
        cmd.Parameters.AddWithValue("$lim", limit);
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

}
