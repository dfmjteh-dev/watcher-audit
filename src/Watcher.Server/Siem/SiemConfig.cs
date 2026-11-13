namespace Watcher.Server;

// H4 (docs/roadmap-next.md): экспорт журнала доступа (audit_log, Этап C3) в
// SIEM заказчика по syslog. Секция Server:Siem; выключено, пока не задан Host.
public sealed class SiemConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 514;
    // udp | tcp | tls. В журнале логины и IP — по сети лучше tls.
    public string Protocol { get; set; } = "tcp";
    // cef (ArcSight, QRadar, Splunk, MaxPatrol и др.) | json.
    public string Format { get; set; } = "cef";
    // Только для tcp/tls: octet — RFC 6587 «длина пробел сообщение»
    // (rsyslog, syslog-ng); lf — сообщения через перевод строки.
    public string Framing { get; set; } = "octet";
    // Syslog facility: 10 — authpriv (журнал безопасности), 13 — log audit,
    // 16–23 — local0–local7.
    public int Facility { get; set; } = 10;
    // HOSTNAME в syslog. В контейнере MachineName — случайный id, задайте имя.
    public string Hostname { get; set; } = "";
    public string AppName { get; set; } = "watcher";
    // tls: корневой CA сервера syslog, если он не из системного хранилища.
    public string CaCertPath { get; set; } = "";
    // false — при первом запуске начать с конца журнала, без истории.
    public bool SendHistory { get; set; } = false;
    public int PollSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 200;

    public bool Enabled => !string.IsNullOrWhiteSpace(Host);

    // null — конфигурация корректна.
    public string? Validate()
    {
        if (Protocol is not ("udp" or "tcp" or "tls")) return "Server:Siem:Protocol — udp, tcp или tls";
        if (Format is not ("cef" or "json")) return "Server:Siem:Format — cef или json";
        if (Framing is not ("octet" or "lf")) return "Server:Siem:Framing — octet или lf";
        if (Facility is < 0 or > 23) return "Server:Siem:Facility — 0..23";
        if (Port is < 1 or > 65535) return "Server:Siem:Port — 1..65535";
        if (!string.IsNullOrEmpty(CaCertPath) && !File.Exists(CaCertPath)) return $"Server:Siem:CaCertPath — файл не найден: {CaCertPath}";
        return null;
    }
}
