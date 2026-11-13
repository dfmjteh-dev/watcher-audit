using System.Collections.Concurrent;

namespace Watcher.Server;

// H4 (docs/roadmap-next.md): состояние сервера для /healthz и счётчики для
// /metrics. Счётчики живут в памяти процесса и обнуляются при перезапуске —
// Prometheus это штатно учитывает (rate/increase переживают сброс счётчика).
public sealed class ServerHealth
{
    public ServerHealth(DateTime? startedAtUtc = null) => StartedAtUtc = startedAtUtc ?? DateTime.UtcNow;

    public DateTime StartedAtUtc { get; }

    // --- retention ---
    private long _retentionLastSuccessTicks;
    private long _retentionRunsOk, _retentionRunsError;
    private readonly ConcurrentDictionary<(string Reason, string Kind), long> _retentionDeleted = new();

    public DateTime? RetentionLastSuccessUtc =>
        Interlocked.Read(ref _retentionLastSuccessTicks) is var t && t > 0 ? new DateTime(t, DateTimeKind.Utc) : null;
    public long RetentionRunsOk => Interlocked.Read(ref _retentionRunsOk);
    public long RetentionRunsError => Interlocked.Read(ref _retentionRunsError);
    public IReadOnlyDictionary<(string Reason, string Kind), long> RetentionDeleted => _retentionDeleted;

    public void RetentionSucceeded(DateTime nowUtc)
    {
        Interlocked.Exchange(ref _retentionLastSuccessTicks, nowUtc.Ticks);
        Interlocked.Increment(ref _retentionRunsOk);
    }

    public void RetentionFailed() => Interlocked.Increment(ref _retentionRunsError);

    // reason: age | space; kind: video | terminal.
    public void RetentionDeletedRecords(string reason, string kind, int count)
    {
        if (count > 0) _retentionDeleted.AddOrUpdate((reason, kind), count, (_, v) => v + count);
    }

    // Retention жив, если последний успешный проход был не позже двух интервалов
    // назад (+ запас на сам проход). До первого прохода — пока не вышли за
    // первый интервал после старта: первый проход идёт через 10 сек после старта.
    public bool IsRetentionAlive(DateTime nowUtc, TimeSpan interval)
    {
        var grace = interval * 2 + TimeSpan.FromMinutes(10);
        return RetentionLastSuccessUtc is { } last
            ? nowUtc - last <= grace
            : nowUtc - StartedAtUtc <= grace;
    }

    // --- LDAP (H1) ---
    private long _ldapErrors;
    public long LdapErrors => Interlocked.Read(ref _ldapErrors);
    public void CountLdapError() => Interlocked.Increment(ref _ldapErrors);

    // --- SIEM-экспорт (H4) ---
    private long _siemForwarded, _siemErrors, _siemLag, _siemLastSuccessTicks;

    public long SiemForwarded => Interlocked.Read(ref _siemForwarded);
    public long SiemErrors => Interlocked.Read(ref _siemErrors);
    public long SiemLag => Interlocked.Read(ref _siemLag);
    public DateTime? SiemLastSuccessUtc =>
        Interlocked.Read(ref _siemLastSuccessTicks) is var t && t > 0 ? new DateTime(t, DateTimeKind.Utc) : null;

    public void CountSiemForwarded(int count, DateTime nowUtc)
    {
        Interlocked.Add(ref _siemForwarded, count);
        Interlocked.Exchange(ref _siemLastSuccessTicks, nowUtc.Ticks);
    }

    public void CountSiemError() => Interlocked.Increment(ref _siemErrors);
    public void SetSiemLag(long entries) => Interlocked.Exchange(ref _siemLag, Math.Max(0, entries));

    // --- запросы агентов и входы ---
    private readonly ConcurrentDictionary<(string Kind, string Result), long> _uploads = new();
    private readonly ConcurrentDictionary<string, long> _logins = new();
    private long _heartbeats;

    public IReadOnlyDictionary<(string Kind, string Result), long> Uploads => _uploads;
    public IReadOnlyDictionary<string, long> Logins => _logins;
    public long Heartbeats => Interlocked.Read(ref _heartbeats);

    // kind: video | terminal. Результат — по HTTP-коду ответа, см. UploadResult.
    public void CountUpload(string kind, int statusCode) =>
        _uploads.AddOrUpdate((kind, UploadResult(statusCode)), 1, (_, v) => v + 1);

    public void CountHeartbeat() => Interlocked.Increment(ref _heartbeats);

    // result: ok | failed | rate_limited.
    public void CountLogin(string result) => _logins.AddOrUpdate(result, 1, (_, v) => v + 1);

    // --- алерты (H4) ---
    private readonly ConcurrentDictionary<(string Channel, string Result), long> _alertNotifications = new();
    private int _alertsFiring;

    public IReadOnlyDictionary<(string Channel, string Result), long> AlertNotifications => _alertNotifications;
    public int AlertsFiring => Volatile.Read(ref _alertsFiring);

    public void CountAlertNotification(string channel, bool ok) =>
        _alertNotifications.AddOrUpdate((channel, ok ? "ok" : "error"), 1, (_, v) => v + 1);

    public void SetAlertsFiring(int count) => Volatile.Write(ref _alertsFiring, count);

    public static string UploadResult(int statusCode) => statusCode switch
    {
        200 => "ok",
        409 => "duplicate",   // повтор уже принятой записи — агент удалит локальную копию
        422 => "collision",   // то же имя у другой записи (R2)
        401 or 403 => "unauthorized",
        >= 500 => "error",
        _ => "rejected",      // 400/413 и прочие ошибки запроса
    };
}
