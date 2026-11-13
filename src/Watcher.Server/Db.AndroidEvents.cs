using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// Этап 3 Android-агента (docs/android-plan.md): события приложений и устройства с
// телефона — запуск/закрытие приложений, установка/удаление, состояние устройства
// и трафик по приложениям. Одна таблица на все типы: сервер их не интерпретирует,
// type-специфичные поля лежат в detail как непрозрачный JSON-блоб (его читает
// веб-UI Этапа 4, не сервер). Контракт приёма — POST /api/events
// (Endpoints/EventsEndpoints.cs), по учётке агента вида android.

// Одно событие, как его прислал агент и проверил эндпоинт: ts уже нормализован в
// UTC (строка "o"), type из AndroidEventTypes, package/detail — уже проверены.
public sealed record AndroidEventInput(string Ts, string Type, string? Package, string? Detail, string EventId);

// Строка таблицы для чтения (веб-UI Этапа 4, тесты).
public sealed record AndroidEvent(long Id, string Host, DateTime Ts, string Type, string? Package, string? Detail, DateTime ReceivedAt, string EventId);

public sealed partial class Db
{
    // Типы событий Android-агента. Сервер их не трактует — только проверяет, что
    // прислан известный (чужой/опечатка → 400, как у прочих агентских данных).
    public static readonly string[] AndroidEventTypes =
        ["app_foreground", "app_background", "app_installed", "app_removed", "device_state", "app_traffic", "app_inventory",
         // Этап 6 (владелец устройства): сетевой журнал и журнал безопасности.
         "net_dns", "net_connect", "security",
         // Этап 11: отчёт применения политики и отказы (пакет из защитного списка и т.п.).
         "policy"];

    // Этап 6: типы, у которых пакет приложения живёт в detail (или его нет),
    // а верхнеуровневый package должен быть null — вместе с device_state/app_inventory.
    public static readonly string[] PackagelessTypes =
        ["device_state", "app_inventory", "net_dns", "net_connect", "security", "policy"];

    // Пределы пачки и полей (валидация на эндпоинте). Пачка больше предела — 400,
    // остальное агент дошлёт следующими запросами (как MaxLossesPerHeartbeat).
    public const int MaxEventsPerRequest = 500;
    // Имя пакета Android (applicationId) бывает длиннее 128 — до 255 символов.
    public const int MaxPackageLength = 255;
    // detail — наш же JSON-блоб (rx/tx, версия ОС и т.п.); с запасом, но не без края.
    // Предел в UTF-8 байтах, не символах.
    public const int MaxDetailLength = 16 * 1024;

    // Миграция 20 (Этап 3): таблица событий Android. Индексы под выборки по
    // хосту/времени и по хосту/типу/времени (лента устройства, фильтр по типу —
    // development.md, «целевой масштаб ~300 агентов»: индексы закладываются сразу).
    private static void AndroidEvents(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE android_events (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  host TEXT NOT NULL,
  ts TEXT NOT NULL,
  type TEXT NOT NULL,
  package TEXT,
  detail TEXT,
  received_at TEXT NOT NULL
);
CREATE INDEX ix_android_events_host_ts ON android_events(host, ts);
CREATE INDEX ix_android_events_host_type_ts ON android_events(host, type, ts);";
        cmd.ExecuteNonQuery();
    }

    // Отдельная миграция: v20 могла уже примениться на стенде. Старым строкам
    // присваиваем уникальные серверные ключи, не меняя payload.
    private static void AndroidEventIds(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
ALTER TABLE android_events ADD COLUMN event_id TEXT;
UPDATE android_events SET event_id = 'legacy-' || id;
CREATE UNIQUE INDEX ix_android_events_host_event ON android_events(host, event_id);";
        cmd.ExecuteNonQuery();
    }

    // Миграция 22 (Этап 5): роль владельца устройства у Android-агента.
    // android_owner_active — последнее, что сообщил агент (1/0/NULL — не Android
    // или ещё не сообщал). android_clear_owner_at — админ попросил снять роль
    // (clearDeviceOwnerApp); сервер отдаёт команду в heartbeat, пока агент
    // владелец, и снимает флаг, когда роль ушла.
    private static void AndroidOwner(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
ALTER TABLE hosts ADD COLUMN android_owner_active INTEGER;
ALTER TABLE hosts ADD COLUMN android_clear_owner_at TEXT;";
        cmd.ExecuteNonQuery();
    }

    // Этап 5: отчёт агента о роли владельца. Обновляет android_owner_active;
    // если админ просил снять роль и агент всё ещё владелец — возвращает true
    // (сервер ответит clearOwner). Когда роль ушла (ownerActive=false), гасит
    // флаг команды — она исполнена. Хоста нет (удалён между auth и этим) — false.
    public bool SyncAndroidOwner(string host, bool ownerActive)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
UPDATE hosts
   SET android_owner_active = $o,
       android_clear_owner_at = CASE WHEN $o = 0 THEN NULL ELSE android_clear_owner_at END
 WHERE name = $n
RETURNING CASE WHEN $o = 1 AND android_clear_owner_at IS NOT NULL THEN 1 ELSE 0 END";
        cmd.Parameters.AddWithValue("$o", ownerActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$n", host);
        var r = cmd.ExecuteScalar();
        return r is not null && Convert.ToInt64(r) == 1;
    }

    // Миграция 24 (Этап 12): удалённая блокировка устройства. android_lock_requested —
    // желаемое состояние (админ запросил блокировку), android_locked — что сообщил агент.
    private static void AndroidLock(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
ALTER TABLE hosts ADD COLUMN android_lock_requested INTEGER;
ALTER TABLE hosts ADD COLUMN android_locked INTEGER;";
        cmd.ExecuteNonQuery();
    }

    // Этап 12: из heartbeat — агент сообщил фактическое состояние блокировки;
    // возвращаем желаемое (блокировать ли). reportedLocked null — агент не сообщил.
    public bool SyncAndroidLock(string host, bool? reportedLocked)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
UPDATE hosts SET android_locked = CASE WHEN $has = 1 THEN $l ELSE android_locked END
 WHERE name = $n
RETURNING COALESCE(android_lock_requested, 0)";
        cmd.Parameters.AddWithValue("$has", reportedLocked is null ? 0 : 1);
        cmd.Parameters.AddWithValue("$l", reportedLocked == true ? 1 : 0);
        cmd.Parameters.AddWithValue("$n", host);
        var r = cmd.ExecuteScalar();
        return r is not null && Convert.ToInt64(r) == 1;
    }

    // Этап 12: админ запросил блокировку/разблокировку устройства (желаемое состояние).
    public bool RequestAndroidLock(long hostId, bool locked)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE hosts SET android_lock_requested = $l WHERE id = $id AND agent_platform = 'android'";
        cmd.Parameters.AddWithValue("$l", locked ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", hostId);
        return cmd.ExecuteNonQuery() > 0;
    }

    // Этап 12+: офлайн-админ снял блокировку на устройстве — гасим запрос блокировки.
    public void ClearAndroidLockRequest(string host)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE hosts SET android_lock_requested = 0 WHERE name = $n";
        cmd.Parameters.AddWithValue("$n", host);
        cmd.ExecuteNonQuery();
    }

    // Этап 5: админ просит снять роль владельца. Флаг держится до следующего
    // heartbeat владельца (там отдаётся clearOwner) и гаснет, когда роль ушла.
    // Только у Android-хоста, который сейчас владелец, иначе снимать нечего.
    public bool RequestAndroidClearOwner(long hostId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
UPDATE hosts SET android_clear_owner_at = $at
 WHERE id = $id AND android_owner_active = 1";
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", hostId);
        return cmd.ExecuteNonQuery() > 0;
    }

    // Пачка — одна транзакция. Уникальный (host,event_id) делает повтор после
    // потерянного ответа безопасным; payload существующего события не меняем.
    // Имя читаем под write-транзакцией: удаление/переименование между auth и
    // INSERT не оставляет события под удалённым/старым именем. -1 — хост удалён.
    public int InsertAndroidEvents(long hostId, IReadOnlyList<AndroidEventInput> events)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        string? host;
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT name FROM hosts WHERE id = $id";
            q.Parameters.AddWithValue("$id", hostId);
            host = q.ExecuteScalar() as string;
        }
        if (host is null) return -1;
        string now = DateTime.UtcNow.ToString("o");
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO android_events (host, ts, type, package, detail, event_id, received_at)
VALUES ($h, $ts, $type, $pkg, $detail, $event, $now)
ON CONFLICT(host, event_id) DO NOTHING";
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.Add("$ts", SqliteType.Text);
        cmd.Parameters.Add("$type", SqliteType.Text);
        cmd.Parameters.Add("$pkg", SqliteType.Text);
        cmd.Parameters.Add("$detail", SqliteType.Text);
        cmd.Parameters.Add("$event", SqliteType.Text);
        int inserted = 0;
        foreach (var e in events)
        {
            cmd.Parameters["$ts"].Value = e.Ts;
            cmd.Parameters["$type"].Value = e.Type;
            cmd.Parameters["$pkg"].Value = (object?)e.Package ?? DBNull.Value;
            cmd.Parameters["$detail"].Value = (object?)e.Detail ?? DBNull.Value;
            cmd.Parameters["$event"].Value = e.EventId;
            inserted += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return inserted;
    }

    // Лента событий хоста, новые сверху. Пагинация и лимит (cap 1000) —
    // обязательны на любом списковом API (development.md). Для веб-UI Этапа 4
    // и тестов.
    public List<AndroidEvent> ListAndroidEvents(string host, int limit, int offset)
    {
        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(0, offset);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT id, host, ts, type, package, detail, received_at, event_id FROM android_events
WHERE host = $h ORDER BY ts DESC, id DESC LIMIT $lim OFFSET $off";
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$lim", limit);
        cmd.Parameters.AddWithValue("$off", offset);
        return ReadAndroidEvents(cmd);
    }

    // Этап 4: события периода [from, to) по возрастанию — вход AndroidSummary.
    // Не больше max строк (+1, чтобы узнать об обрезке). ts хранится строкой "o"
    // в UTC — сравнение строк совпадает с порядком времени, индекс host_ts работает.
    public (List<AndroidEvent> Events, bool Truncated) ListAndroidEventsRange(string host, DateTime from, DateTime to, int max)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT id, host, ts, type, package, detail, received_at, event_id FROM android_events
WHERE host = $h AND ts >= $from AND ts < $to ORDER BY ts, id LIMIT $lim";
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$from", from.ToUniversalTime().ToString("o"));
        cmd.Parameters.AddWithValue("$to", to.ToUniversalTime().ToString("o"));
        cmd.Parameters.AddWithValue("$lim", max + 1);
        var list = ReadAndroidEvents(cmd);
        bool truncated = list.Count > max;
        if (truncated) list.RemoveAt(list.Count - 1);
        return (list, truncated);
    }

    // Последнее событие типа до момента before (последнее состояние устройства
    // на конец выбранного дня, а не только внутри него).
    public AndroidEvent? LatestAndroidEvent(string host, string type, DateTime before)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT id, host, ts, type, package, detail, received_at, event_id FROM android_events
WHERE host = $h AND type = $type AND ts < $before ORDER BY ts DESC, id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$before", before.ToUniversalTime().ToString("o"));
        return ReadAndroidEvents(cmd).FirstOrDefault();
    }

    // Части последнего снимка перечня приложений (один snapshotId) до before.
    // Части одного снимка агент ставит в очередь одним временем ts; окно в час —
    // запас, а не расчёт: ищем по snapshotId, ts только сужает выборку индексом.
    public List<AndroidEvent> LatestAndroidInventory(string host, DateTime before)
    {
        if (LatestAndroidEvent(host, "app_inventory", before) is not { } last) return [];
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT id, host, ts, type, package, detail, received_at, event_id FROM android_events
WHERE host = $h AND type = 'app_inventory' AND ts >= $since AND ts <= $ts
  AND json_extract(detail, '$.snapshotId') IS json_extract($detail, '$.snapshotId')
ORDER BY id LIMIT 1000";
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$since", last.Ts.AddHours(-1).ToString("o"));
        cmd.Parameters.AddWithValue("$ts", last.Ts.ToString("o"));
        cmd.Parameters.AddWithValue("$detail", (object?)last.Detail ?? DBNull.Value);
        return ReadAndroidEvents(cmd);
    }

    private static List<AndroidEvent> ReadAndroidEvents(SqliteCommand cmd)
    {
        var list = new List<AndroidEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new AndroidEvent(
                r.GetInt64(0), r.GetString(1), ParseUtc(r.GetString(2)), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), ParseUtc(r.GetString(6)), r.GetString(7)));
        return list;
    }

    public int CountAndroidEvents(string host)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM android_events WHERE host = $h";
        cmd.Parameters.AddWithValue("$h", host);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
