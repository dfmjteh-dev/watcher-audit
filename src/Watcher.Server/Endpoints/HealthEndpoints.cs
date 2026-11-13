using System.Globalization;
using System.Text;

namespace Watcher.Server;

// H4 (docs/roadmap-next.md): наблюдаемость сервера.
// /healthz — для Docker HEALTHCHECK и внешнего мониторинга, без авторизации и
// без цифр: наружу только ok/fail по каждой проверке.
// /metrics — формат Prometheus, Bearer MetricsToken; без токена в конфиге
// эндпоинт выключен. Текст пишется вручную — формат простой, а отдельная
// NuGet-зависимость ради него — лишняя поверхность (H11).
public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", (Db db, ServerConfig cfg, ServerHealth health) =>
        {
            var checks = Check(db, cfg, health, DateTime.UtcNow);
            bool ok = checks.Values.All(v => v != "fail");
            return Results.Json(new { status = ok ? "ok" : "fail", checks }, statusCode: ok ? 200 : 503);
        });

        app.MapGet("/metrics", (HttpContext ctx, Db db, ServerConfig cfg, ServerHealth health) =>
        {
            if (string.IsNullOrEmpty(cfg.MetricsToken)) return Results.NotFound();
            string? presented = AgentAuth.BearerToken(ctx.Request.Headers.Authorization.ToString());
            if (presented is null || !AgentAuth.MatchesSharedToken(presented, cfg.MetricsToken))
                return Results.Unauthorized();
            return Results.Text(Render(db, cfg, health, DateTime.UtcNow), "text/plain; version=0.0.4; charset=utf-8");
        });
    }

    // ok | fail; disk — "unknown", если статистики тома нет (не считается отказом).
    internal static Dictionary<string, string> Check(Db db, ServerConfig cfg, ServerHealth health, DateTime nowUtc)
    {
        var checks = new Dictionary<string, string>();
        try
        {
            using var c = db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.ExecuteScalar();
            checks["db"] = "ok";
        }
        catch (Exception)
        {
            checks["db"] = "fail";
        }

        // Ниже MinFreeDiskPercent retention уже удаляет записи раньше срока —
        // это отказ, который должен увидеть мониторинг (R8).
        checks["disk"] = RetentionService.DriveSpace(cfg.DataDir) is { Total: > 0 } d
            ? (cfg.MinFreeDiskPercent > 0 && d.Free * 100.0 / d.Total < cfg.MinFreeDiskPercent ? "fail" : "ok")
            : "unknown";

        checks["retention"] = health.IsRetentionAlive(nowUtc, RetentionInterval(cfg)) ? "ok" : "fail";
        return checks;
    }

    // То же, что ждёт RetentionService между проходами.
    internal static TimeSpan RetentionInterval(ServerConfig cfg) =>
        TimeSpan.FromMinutes(Math.Max(5, cfg.RetentionScanIntervalMinutes));

    internal static string Render(Db db, ServerConfig cfg, ServerHealth health, DateTime nowUtc)
    {
        var w = new MetricsWriter();
        var hosts = db.Inventory();

        w.Gauge("watcher_build_info", "Версия сервера.", ("version", AppVersion.Core(AppVersion.Current)), 1);
        w.Gauge("watcher_start_time_seconds", "Время старта процесса сервера (Unix).", Unix(health.StartedAtUtc));

        bool Online(InventoryHost h) => h.LastHeartbeatAt is { } t && nowUtc - t < InventoryEndpoints.OnlineWindow;
        w.Family("watcher_hosts", "gauge", "Хосты по состоянию heartbeat (окно «онлайн» — как на Обзоре).");
        w.Sample("watcher_hosts", ("state", "online"), hosts.Count(Online));
        w.Sample("watcher_hosts", ("state", "offline"), hosts.Count(h => h.LastHeartbeatAt is not null && !Online(h)));
        w.Sample("watcher_hosts", ("state", "never_seen"), hosts.Count(h => h.LastHeartbeatAt is null));
        var versions = AgentVersionPolicy.Load(db);
        w.Gauge("watcher_hosts_outdated_agent", "Хосты, где агент не дошёл до версии, назначенной группе.",
            hosts.Count(h => versions.IsOutdated(h.Name, h.GroupId, h.AgentVersion)));
        w.Gauge("watcher_hosts_plain_http", "Хосты, чей последний heartbeat пришёл по HTTP.", hosts.Count(h => h.LastHeartbeatHttps == false));
        w.Gauge("watcher_hosts_legacy_auth", "Хосты без своей учётки агента (анонимный режим разработки).", hosts.Count(h => h.LegacyAuth == true));

        // По хосту — только для хостов с heartbeat: на этом строятся алерты
        // «хост молчит дольше N» и «растут потери сегментов».
        var seen = hosts.Where(h => h.LastHeartbeatAt is not null).ToList();
        w.Family("watcher_host_last_heartbeat_timestamp_seconds", "gauge", "Последний heartbeat хоста (Unix).");
        foreach (var h in seen) w.Sample("watcher_host_last_heartbeat_timestamp_seconds", ("host", h.Name), Unix(h.LastHeartbeatAt!.Value));
        w.Family("watcher_host_dropped_segments", "gauge", "Сегменты, удалённые агентом по квоте диска станции (из heartbeat).");
        foreach (var h in seen) w.Sample("watcher_host_dropped_segments", ("host", h.Name), h.DroppedSegments);
        w.Family("watcher_host_clock_skew_seconds", "gauge", "Сдвиг часов станции относительно сервера (R7), со знаком.");
        foreach (var h in seen.Where(h => h.ClockSkewSec is not null))
            w.Sample("watcher_host_clock_skew_seconds", ("host", h.Name), h.ClockSkewSec!.Value);

        w.Family("watcher_records", "gauge", "Записи на сервере.");
        w.Sample("watcher_records", ("kind", "video"), hosts.Sum(h => (long)h.Segments));
        w.Sample("watcher_records", ("kind", "terminal"), hosts.Sum(h => (long)h.TerminalSessions));
        w.Family("watcher_storage_bytes", "gauge", "Объём записей на сервере.");
        w.Sample("watcher_storage_bytes", ("kind", "video"), hosts.Sum(h => h.SegmentBytes));
        w.Sample("watcher_storage_bytes", ("kind", "terminal"), hosts.Sum(h => h.TerminalBytes));

        if (RetentionService.DriveSpace(cfg.DataDir) is { Total: > 0 } d)
        {
            w.Gauge("watcher_disk_free_bytes", "Свободно на томе с DataDir.", d.Free);
            w.Gauge("watcher_disk_total_bytes", "Размер тома с DataDir.", d.Total);
        }
        w.Gauge("watcher_disk_min_free_percent", "Порог MinFreeDiskPercent: ниже него retention удаляет записи раньше срока.", cfg.MinFreeDiskPercent);

        if (health.RetentionLastSuccessUtc is { } last)
            w.Gauge("watcher_retention_last_success_timestamp_seconds", "Последний успешный проход retention (Unix).", Unix(last));
        w.Family("watcher_retention_runs_total", "counter", "Проходы retention.");
        w.Sample("watcher_retention_runs_total", ("result", "ok"), health.RetentionRunsOk);
        w.Sample("watcher_retention_runs_total", ("result", "error"), health.RetentionRunsError);
        w.Family("watcher_retention_deleted_total", "counter", "Записи, удалённые retention: age — по сроку, space — по месту (раньше срока).");
        foreach (var ((reason, kind), v) in health.RetentionDeleted.OrderBy(x => x.Key))
            w.Sample("watcher_retention_deleted_total", new[] { ("reason", reason), ("kind", kind) }, v);

        if (cfg.Ldap.Enabled || db.GetSetting(DirectoryAuth.SettingKey) is not null)
        {
            w.Family("watcher_ldap_errors_total", "counter", "Каталог LDAP/AD недоступен при входе или перепроверке сессии.");
            w.Sample("watcher_ldap_errors_total", Array.Empty<(string, string)>(), health.LdapErrors);
        }

        if (cfg.Siem.Enabled)
        {
            w.Family("watcher_siem_forwarded_total", "counter", "Записи журнала доступа, отправленные в SIEM.");
            w.Sample("watcher_siem_forwarded_total", Array.Empty<(string, string)>(), health.SiemForwarded);
            w.Family("watcher_siem_errors_total", "counter", "Неудачные попытки отправки в SIEM.");
            w.Sample("watcher_siem_errors_total", Array.Empty<(string, string)>(), health.SiemErrors);
            w.Gauge("watcher_siem_lag_entries", "Записи журнала доступа, ещё не отправленные в SIEM.", health.SiemLag);
            if (health.SiemLastSuccessUtc is { } siemLast)
                w.Gauge("watcher_siem_last_success_timestamp_seconds", "Последняя успешная отправка в SIEM (Unix).", Unix(siemLast));
        }

        w.Family("watcher_uploads_total", "counter", "Загрузки от агентов по результату.");
        foreach (var ((kind, result), v) in health.Uploads.OrderBy(x => x.Key))
            w.Sample("watcher_uploads_total", new[] { ("kind", kind), ("result", result) }, v);
        w.Family("watcher_heartbeats_total", "counter", "Принятые heartbeat агентов.");
        w.Sample("watcher_heartbeats_total", Array.Empty<(string, string)>(), health.Heartbeats);
        w.Family("watcher_logins_total", "counter", "Попытки входа в веб-UI по результату.");
        foreach (var (result, v) in health.Logins.OrderBy(x => x.Key))
            w.Sample("watcher_logins_total", ("result", result), v);

        if (cfg.Alerts.AnyChannel)
        {
            w.Gauge("watcher_alerts_firing", "Активные встроенные алерты (Server:Alerts).", health.AlertsFiring);
            w.Family("watcher_alert_notifications_total", "counter", "Отправленные уведомления встроенных алертов по каналу и результату.");
            foreach (var ((channel, result), v) in health.AlertNotifications.OrderBy(x => x.Key))
                w.Sample("watcher_alert_notifications_total", new[] { ("channel", channel), ("result", result) }, v);
        }

        return w.ToString();
    }

    private static double Unix(DateTime utc) => (utc - DateTime.UnixEpoch).TotalSeconds;

    // Prometheus text exposition format 0.0.4.
    public sealed class MetricsWriter
    {
        private readonly StringBuilder _sb = new();

        public void Family(string name, string type, string help) =>
            _sb.Append("# HELP ").Append(name).Append(' ').Append(EscapeHelp(help)).Append('\n')
               .Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');

        public void Gauge(string name, string help, double value)
        {
            Family(name, "gauge", help);
            Sample(name, Array.Empty<(string, string)>(), value);
        }

        public void Gauge(string name, string help, (string, string) label, double value)
        {
            Family(name, "gauge", help);
            Sample(name, label, value);
        }

        public void Sample(string name, (string Name, string Value) label, double value) => Sample(name, new[] { label }, value);

        public void Sample(string name, IReadOnlyList<(string Name, string Value)> labels, double value)
        {
            _sb.Append(name);
            if (labels.Count > 0)
            {
                _sb.Append('{');
                for (int i = 0; i < labels.Count; i++)
                {
                    if (i > 0) _sb.Append(',');
                    _sb.Append(labels[i].Name).Append("=\"").Append(EscapeLabel(labels[i].Value)).Append('"');
                }
                _sb.Append('}');
            }
            _sb.Append(' ').Append(value.ToString("G17", CultureInfo.InvariantCulture)).Append('\n');
        }

        public static string EscapeLabel(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        private static string EscapeHelp(string v) => v.Replace("\\", "\\\\").Replace("\n", "\\n");

        public override string ToString() => _sb.ToString();
    }
}
