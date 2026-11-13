using System.Globalization;

namespace Watcher.Server;

// H4: пересылка audit_log в SIEM. Источник — сама таблица (id > курсор), не
// перехват записи в AuditLog: SIEM недоступен — записи ждут в БД и уходят,
// когда он вернётся; перезапуск сервера — продолжаем с курсора. Доставка
// «хотя бы один раз»: при сбое посреди пачки часть строк может уйти повторно,
// дубли SIEM различает по externalId (id записи).
public sealed class SiemForwarder : BackgroundService
{
    private readonly Db _db;
    private readonly ServerConfig _cfg;
    private readonly ServerHealth _health;
    private readonly ILogger<SiemForwarder> _log;
    private readonly Func<ISiemTransport> _transportFactory;
    private readonly string _cursorPath;
    private readonly string _hostname;
    private readonly string _version = AppVersion.Core(AppVersion.Current);
    private readonly Func<DateTime> _clock;

    // Подтверждений у syslog по TCP нет. «Подтверждённая» позиция — курсор на
    // момент проверки соединения, сделанной не раньше ConfirmDelay после
    // последней записи (за это время RST от мёртвого сервера успевает прийти).
    // Обрыв соединения — откат к ней и повторная отправка: возможны дубли
    // (SIEM различает по externalId), но не потери.
    internal static readonly TimeSpan ConfirmDelay = TimeSpan.FromMilliseconds(500);
    private long? _confirmed;
    private DateTime _lastWriteUtc = DateTime.MinValue;

    public SiemForwarder(Db db, ServerConfig cfg, ServerHealth health, ILogger<SiemForwarder> log)
        : this(db, cfg, health, log, () => CreateTransport(cfg.Siem)) { }

    // Для тестов — подмена транспорта и часов.
    public SiemForwarder(Db db, ServerConfig cfg, ServerHealth health, ILogger<SiemForwarder> log, Func<ISiemTransport> transportFactory, Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _db = db;
        _cfg = cfg;
        _health = health;
        _log = log;
        _transportFactory = transportFactory;
        _cursorPath = Path.Combine(cfg.DataDir, "siem.cursor");
        _hostname = string.IsNullOrWhiteSpace(cfg.Siem.Hostname) ? Environment.MachineName : cfg.Siem.Hostname;
    }

    public static ISiemTransport CreateTransport(SiemConfig s) =>
        s.Protocol == "udp" ? new UdpSiemTransport(s.Host, s.Port) : new StreamSiemTransport(s);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_cfg.Siem.Enabled) return;
        if (_cfg.Siem.Validate() is { } error)
        {
            _log.LogError("SIEM export disabled: {Error}", error);
            return;
        }
        _log.LogInformation("SIEM export: {Protocol}://{Host}:{Port}, {Format}", _cfg.Siem.Protocol, _cfg.Siem.Host, _cfg.Siem.Port, _cfg.Siem.Format);

        using var transport = _transportFactory();
        var interval = TimeSpan.FromSeconds(Math.Max(1, _cfg.Siem.PollSeconds));
        while (!ct.IsCancellationRequested)
        {
            int sent = 0;
            try { sent = await ForwardOnceAsync(transport, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _health.CountSiemError();
                _log.LogWarning("SIEM export failed, will retry: {Error}", ex.Message);
            }
            // Полная пачка — в журнале, вероятно, ещё есть: следующая сразу.
            if (sent >= _cfg.Siem.BatchSize) continue;
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Одна пачка: прочитать после курсора, отправить, сдвинуть курсор. Число отправленных.
    public async Task<int> ForwardOnceAsync(ISiemTransport transport, CancellationToken ct)
    {
        long maxId = _db.MaxAuditId();
        long cursor = ReadCursor(maxId);
        DateTime now = _clock();
        if (transport.ResetIfClosed())
            cursor = Rewind(cursor, "SIEM closed the connection");
        else if (now - _lastWriteUtc >= ConfirmDelay)
            _confirmed = cursor;
        _health.SetSiemLag(maxId - cursor);
        var batch = _db.AuditAfter(cursor, Math.Max(1, _cfg.Siem.BatchSize));
        if (batch.Count == 0) return 0;

        try
        {
            await transport.SendAsync(batch.Select(e => SiemFormatter.Syslog(e, _cfg.Siem, _hostname, _version)).ToList(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Rewind(cursor, "SIEM send failed");
            throw;
        }
        _lastWriteUtc = _clock();
        long last = batch[^1].Id;
        WriteCursor(last);
        _health.CountSiemForwarded(batch.Count, _lastWriteUtc);
        _health.SetSiemLag(maxId - last);
        return batch.Count;
    }

    // Отправленное после подтверждённой позиции могло уйти в мёртвый сокет.
    private long Rewind(long cursor, string reason)
    {
        if (_confirmed is not long back || back >= cursor) return cursor;
        _log.LogWarning("{Reason}: resending audit entries after id {From} (up to {Count} may be duplicates)", reason, back, cursor - back);
        WriteCursor(back);
        _confirmed = null;
        return back;
    }

    // Нет файла — первый запуск: с начала журнала (SendHistory) или с конца.
    // Курсор больше последнего id (БД восстановлена из старой копии) — с конца:
    // иначе новые записи с меньшими id не ушли бы никогда.
    private long ReadCursor(long maxId)
    {
        bool exists = File.Exists(_cursorPath);
        if (exists && long.TryParse(File.ReadAllText(_cursorPath).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long c))
        {
            if (c <= maxId) return c;
            _log.LogWarning("SIEM cursor {Cursor} is ahead of audit_log (max id {Max}) — database restored? Continuing from the end", c, maxId);
        }
        long start = !exists && _cfg.Siem.SendHistory ? 0 : maxId;
        WriteCursor(start);
        return start;
    }

    // Атомарно: временный файл + переименование — обрыв не оставит пустой курсор.
    private void WriteCursor(long id)
    {
        string tmp = _cursorPath + ".tmp";
        File.WriteAllText(tmp, id.ToString(CultureInfo.InvariantCulture));
        File.Move(tmp, _cursorPath, overwrite: true);
    }
}
