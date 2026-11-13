using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Watcher.Server;

// Запись audit_log → строка syslog RFC 5424 с CEF или JSON в MSG.
public static class SiemFormatter
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    // CEF 0–10: отказы и неудачи выше, изменения настроек и доступа — средне,
    // просмотры и входы — низко.
    public static int Severity(AuditEntry e)
    {
        if (e.Details is { } d && d.Contains("\"denied\":true", StringComparison.Ordinal)) return 6;
        if (e.Action is "login.failed" or "agent.enroll-failed") return 5;
        if (e.Action.StartsWith("user.", StringComparison.Ordinal) || e.Action.StartsWith("enroll-token.", StringComparison.Ordinal) ||
            e.Action is "agent.revoke" or "agent.acknowledge" or "agent.enroll" ||
            e.Action.StartsWith("group.", StringComparison.Ordinal) || e.Action.StartsWith("host.", StringComparison.Ordinal))
            return 4;
        return 2;
    }

    public static string Cef(AuditEntry e, string productVersion)
    {
        var sb = new StringBuilder("CEF:0|Watcher|Watcher Server|");
        sb.Append(Header(productVersion)).Append('|').Append(Header(e.Action)).Append('|')
          .Append(Header(e.Action)).Append('|').Append(Severity(e)).Append('|');
        var ext = new List<(string, string)>
        {
            ("rt", new DateTimeOffset(e.Ts).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
            ("act", e.Action),
            ("externalId", e.Id.ToString(CultureInfo.InvariantCulture)),
        };
        if (e.Username is not null) ext.Add(("suser", e.Username));
        // src в CEF — IP-адрес; что-то другое (нет адреса) — не пишем.
        if (e.Ip is not null && IPAddress.TryParse(e.Ip, out _)) ext.Add(("src", e.Ip));
        if (e.Target is not null) { ext.Add(("cs1Label", "target")); ext.Add(("cs1", e.Target)); }
        if (e.Details is not null) { ext.Add(("cs2Label", "details")); ext.Add(("cs2", e.Details)); }
        sb.Append(string.Join(' ', ext.Select(kv => kv.Item1 + "=" + Ext(kv.Item2))));
        return sb.ToString();
    }

    public static string JsonMessage(AuditEntry e)
    {
        JsonElement? details = null;
        if (e.Details is not null)
        {
            try { details = JsonDocument.Parse(e.Details).RootElement.Clone(); }
            catch (JsonException) { }
        }
        return JsonSerializer.Serialize(new
        {
            id = e.Id,
            ts = e.Ts.ToUniversalTime().ToString("o"),
            username = e.Username,
            ip = e.Ip,
            action = e.Action,
            target = e.Target,
            details,
            severity = Severity(e),
        }, Json);
    }

    // RFC 5424: <PRI>1 TIMESTAMP HOSTNAME APP-NAME PROCID MSGID SD MSG.
    public static string Syslog(AuditEntry e, SiemConfig cfg, string hostname, string productVersion)
    {
        int cef = Severity(e);
        int syslogSeverity = cef >= 6 ? 4 : cef >= 4 ? 5 : 6;   // warning / notice / info
        int pri = cfg.Facility * 8 + syslogSeverity;
        string msg = cfg.Format == "json" ? JsonMessage(e) : Cef(e, productVersion);
        return $"<{pri}>1 {e.Ts.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ} {Token(hostname, 255)} {Token(cfg.AppName, 48)} - audit - {msg}";
    }

    // CEF: в заголовке экранируются \ и |, в значениях расширения — \ и =,
    // переводы строк — \n / \r.
    public static string Header(string v) => v.Replace("\\", "\\\\").Replace("|", "\\|");

    public static string Ext(string v) =>
        v.Replace("\\", "\\\\").Replace("=", "\\=").Replace("\r", "\\r").Replace("\n", "\\n");

    // Поля заголовка syslog — печатный ASCII без пробелов, с лимитом длины
    // RFC 5424 (HOSTNAME 255, APP-NAME 48), пусто — «-».
    private static string Token(string v, int max)
    {
        var s = new string(v.Where(c => c > 32 && c < 127).Take(max).ToArray());
        return s.Length == 0 ? "-" : s;
    }
}
