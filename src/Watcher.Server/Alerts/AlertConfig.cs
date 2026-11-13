namespace Watcher.Server;

// H4 (docs/roadmap-next.md): встроенные алерты — для организаций без Prometheus.
// Секция Server:Alerts (env: Server__Alerts__WebhookUrl и т.п.). Выключены, пока
// не задан ни один канал. Описание — docs/configuration.md, docs/operations.md.
public sealed class AlertConfig
{
    // Имя сервера в тексте уведомлений — чтобы различать несколько установок.
    public string Title { get; set; } = "Watcher";
    public int CheckIntervalSeconds { get; set; } = 60;

    // Хост молчит дольше N часов. Рабочие станции выключают на ночь и выходные —
    // поэтому по умолчанию 72, а не час. 0 — не следить.
    public int HostSilentHours { get; set; } = 72;
    // Свободно на томе с DataDir меньше N% — предупредить заранее, до того как
    // retention начнёт удалять записи раньше срока (MinFreeDiskPercent). 0 — выкл.
    public double DiskLowPercent { get; set; } = 10;
    // Напоминание об алертах, которые всё ещё активны. 0 — только при
    // срабатывании и решении.
    public int RepeatHours { get; set; } = 24;
    // Сколько хостов перечислять в одном сообщении, остальные — «и ещё N».
    public int MaxListedHosts { get; set; } = 20;

    // POST JSON {"text": "...", "alerts": [...]}: поле text понимают Mattermost,
    // Slack, Rocket.Chat; Telegram — https://api.telegram.org/bot<token>/sendMessage?chat_id=<id>.
    public string WebhookUrl { get; set; } = "";
    public SmtpConfig Smtp { get; set; } = new();

    public bool AnyChannel => !string.IsNullOrWhiteSpace(WebhookUrl) || Smtp.IsConfigured;
}

public sealed class SmtpConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    // STARTTLS. Неявный TLS (порт 465) System.Net.Mail не поддерживает.
    public bool UseTls { get; set; } = true;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    // Через запятую.
    public string To { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From) && Recipients.Length > 0;
    public string[] Recipients => To.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
