using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;

namespace Watcher.Server;

public interface IAlertChannel
{
    string Name { get; }
    Task SendAsync(string subject, string text, AlertCycle cycle, CancellationToken ct);
}

// POST JSON. Поле text — то, что показывают Mattermost/Slack/Rocket.Chat и
// Telegram (sendMessage), остальное — для своих обработчиков.
public sealed class WebhookAlertChannel : IAlertChannel
{
    private readonly HttpClient _http;
    private readonly string _url;
    private readonly string _title;

    public WebhookAlertChannel(HttpClient http, string url, string title)
    {
        _http = http;
        _url = url;
        _title = title;
    }

    public string Name => "webhook";

    public async Task SendAsync(string subject, string text, AlertCycle cycle, CancellationToken ct)
    {
        static IEnumerable<object> Items(IEnumerable<Alert> alerts, string state) =>
            alerts.Select(a => new { key = a.Key, kind = a.Kind, host = a.Host, state, text = a.Text });
        var body = new
        {
            text,
            source = "watcher",
            server = _title,
            alerts = Items(cycle.Fired, "firing").Concat(Items(cycle.Events, "event"))
                .Concat(Items(cycle.Reminder, "reminder")).Concat(Items(cycle.Resolved, "resolved")),
        };
        // Не PostAsJsonAsync: тот шлёт тело потоком (chunked, без Content-Length),
        // и простой приёмник/старый прокси получал пустое тело — найдено живой
        // проверкой. Целиком строкой — Content-Length есть всегда.
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(_url, content, ct);
        resp.EnsureSuccessStatusCode();
    }
}

// System.Net.Mail: STARTTLS (587) есть, неявного TLS (465) нет. Отдельная
// библиотека ради этого — лишняя зависимость (H11).
public sealed class SmtpAlertChannel : IAlertChannel
{
    private readonly SmtpConfig _cfg;

    public SmtpAlertChannel(SmtpConfig cfg) => _cfg = cfg;

    public string Name => "smtp";

    public async Task SendAsync(string subject, string text, AlertCycle cycle, CancellationToken ct)
    {
        using var client = new SmtpClient(_cfg.Host, _cfg.Port) { EnableSsl = _cfg.UseTls };
        if (!string.IsNullOrEmpty(_cfg.User))
            client.Credentials = new NetworkCredential(_cfg.User, _cfg.Password);
        using var msg = new MailMessage { From = new MailAddress(_cfg.From), Subject = subject, Body = text };
        foreach (var to in _cfg.Recipients) msg.To.Add(to);
        await client.SendMailAsync(msg, ct);
    }
}

// H4: раз в CheckIntervalSeconds — снимок состояния, оценка, одно уведомление
// на цикл во все каналы. Неудачная отправка — в лог и /metrics, повтора нет:
// активный алерт придёт ещё раз напоминанием (RepeatHours). Надёжная доставка —
// Prometheus + Alertmanager по /metrics.
public sealed class AlertService : BackgroundService
{
    private readonly Db _db;
    private readonly ServerConfig _cfg;
    private readonly ServerHealth _health;
    private readonly ILogger<AlertService> _log;
    private readonly AlertEvaluator _evaluator;
    private readonly IReadOnlyList<IAlertChannel> _channels;

    public AlertService(Db db, ServerConfig cfg, ServerHealth health, ILogger<AlertService> log, IHttpClientFactory httpFactory)
    {
        _db = db;
        _cfg = cfg;
        _health = health;
        _log = log;
        _evaluator = new AlertEvaluator(cfg.Alerts);
        var channels = new List<IAlertChannel>();
        if (!string.IsNullOrWhiteSpace(cfg.Alerts.WebhookUrl))
            channels.Add(new WebhookAlertChannel(httpFactory.CreateClient("alerts"), cfg.Alerts.WebhookUrl, cfg.Alerts.Title));
        if (cfg.Alerts.Smtp.IsConfigured)
            channels.Add(new SmtpAlertChannel(cfg.Alerts.Smtp));
        _channels = channels;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_channels.Count == 0) return;
        _log.LogInformation("Alerts enabled: {Channels}", string.Join(", ", _channels.Select(c => c.Name)));
        var interval = TimeSpan.FromSeconds(Math.Max(15, _cfg.Alerts.CheckIntervalSeconds));
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
            try { await CheckOnceAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "alert check failed"); }
        }
    }

    private long? _lastLossId;

    public async Task CheckOnceAsync(CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        // Журнал потерь: первая проверка после старта — только точка отсчёта
        // (старые события уже видны в UI, повторять их уведомлением не нужно).
        long maxLoss = _db.MaxLossId();
        var newLosses = _lastLossId is { } after && maxLoss > after ? _db.LossesSince(after) : [];
        _lastLossId = maxLoss;
        var snapshot = new AlertSnapshot(now, _db.Inventory(), RetentionService.DriveSpace(_cfg.DataDir),
            _cfg.MinFreeDiskPercent, _health.IsRetentionAlive(now, HealthEndpoints.RetentionInterval(_cfg)), newLosses);
        var cycle = _evaluator.Evaluate(snapshot);
        if (cycle.IsEmpty) return;

        string text = _evaluator.Format(cycle);
        string subject = text.Split('\n')[0];
        foreach (var ch in _channels)
        {
            try
            {
                await ch.SendAsync(subject, text, cycle, ct);
                _health.CountAlertNotification(ch.Name, ok: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _health.CountAlertNotification(ch.Name, ok: false);
                _log.LogWarning("alert notification via {Channel} failed: {Error}", ch.Name, ex.Message);
            }
        }
        _health.SetAlertsFiring(_evaluator.Firing.Count);
    }
}
