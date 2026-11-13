using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Watcher.Server.Tests;

// H4 (roadmap-next.md): встроенные алерты.
public class AlertTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private const long Gb = 1024L * 1024 * 1024;

    private static InventoryHost Host(string name, DateTime? lastHeartbeat, int dropped = 0, double? skew = null) =>
        new(1, name, null, null, null, lastHeartbeat, "0.9.0", dropped, true,
            0, 0, null, 0, 0, 0, 0, null, 0, ClockSkewSec: skew);

    private static AlertSnapshot Snap(DateTime now, IReadOnlyList<InventoryHost> hosts, double freePct = 50, bool retention = true) =>
        new(now, hosts, ((long)(freePct * Gb), 100 * Gb), 5, retention);

    private static AlertEvaluator Eval(Action<AlertConfig>? tune = null)
    {
        var cfg = new AlertConfig { Title = "test" };
        tune?.Invoke(cfg);
        return new AlertEvaluator(cfg);
    }

    [Fact]
    public void Silent_host_fires_once_then_resolves()
    {
        var e = Eval();
        var silent = Host("pc-1", Now.AddHours(-80));

        var c1 = e.Evaluate(Snap(Now, new[] { silent, Host("pc-2", Now.AddMinutes(-1)) }));
        var c2 = e.Evaluate(Snap(Now.AddMinutes(1), new[] { silent }));
        var c3 = e.Evaluate(Snap(Now.AddMinutes(2), new[] { Host("pc-1", Now.AddMinutes(2)) }));

        Assert.Equal("host-silent:pc-1", Assert.Single(c1.Fired).Key);
        Assert.True(c2.IsEmpty);
        Assert.Equal("host-silent:pc-1", Assert.Single(c3.Resolved).Key);
        Assert.Empty(e.Firing);
    }

    [Fact]
    public void Night_and_weekend_are_not_silence_by_default()
    {
        var e = Eval();

        var c = e.Evaluate(Snap(Now, new[] { Host("pc-1", Now.AddHours(-64)) }));   // пятница 20:00 → понедельник 12:00

        Assert.True(c.IsEmpty);
    }

    [Fact]
    public void Host_without_any_heartbeat_is_not_silent()
    {
        var c = Eval().Evaluate(Snap(Now, new[] { Host("term-only", null) }));
        Assert.True(c.IsEmpty);
    }

    [Fact]
    public void Dropped_segments_growth_is_an_event_from_second_check()
    {
        var e = Eval();

        var c1 = e.Evaluate(Snap(Now, new[] { Host("pc-1", Now, dropped: 5) }));
        var c2 = e.Evaluate(Snap(Now.AddMinutes(1), new[] { Host("pc-1", Now, dropped: 8) }));
        var c3 = e.Evaluate(Snap(Now.AddMinutes(2), new[] { Host("pc-1", Now, dropped: 8) }));
        var c4 = e.Evaluate(Snap(Now.AddMinutes(3), new[] { Host("pc-1", Now, dropped: 0) }));   // агент перезапущен

        Assert.True(c1.IsEmpty);
        var ev = Assert.Single(c2.Events);
        Assert.Contains("удалил 3 сегм.", ev.Text);
        Assert.True(c3.IsEmpty);
        Assert.True(c4.IsEmpty);
        Assert.Empty(c2.Resolved);
    }

    [Theory]
    [InlineData(700.0, "отстают")]
    [InlineData(-700.0, "спешат")]
    public void Clock_skew_uses_station_clock_tolerance(double skew, string word)
    {
        var e = Eval();
        var c = e.Evaluate(Snap(Now, new[] { Host("pc-1", Now, skew: skew), Host("pc-ok", Now, skew: 300) }));
        var a = Assert.Single(c.Fired);
        Assert.Equal("clock:pc-1", a.Key);
        Assert.Contains(word, a.Text);
    }

    [Fact]
    public void Disk_warns_before_retention_threshold_and_says_when_records_are_deleted()
    {
        var warn = Eval().Evaluate(Snap(Now, Array.Empty<InventoryHost>(), freePct: 8));
        var critical = Eval().Evaluate(Snap(Now, Array.Empty<InventoryHost>(), freePct: 3));
        var ok = Eval().Evaluate(Snap(Now, Array.Empty<InventoryHost>(), freePct: 30));

        Assert.Contains("порог предупреждения 10%", Assert.Single(warn.Fired).Text);
        Assert.Contains("retention уже удаляет записи раньше срока", Assert.Single(critical.Fired).Text);
        Assert.True(ok.IsEmpty);
    }

    [Fact]
    public void Retention_stalled_fires()
    {
        var c = Eval().Evaluate(Snap(Now, Array.Empty<InventoryHost>(), retention: false));
        Assert.Equal("retention", Assert.Single(c.Fired).Key);
    }

    [Fact]
    public void Reminder_after_repeat_hours_while_still_firing()
    {
        var e = Eval(c => c.RepeatHours = 24);
        var hosts = new[] { Host("pc-1", Now.AddDays(-5)) };

        e.Evaluate(Snap(Now, hosts));
        var before = e.Evaluate(Snap(Now.AddHours(23), hosts));
        var after = e.Evaluate(Snap(Now.AddHours(24), hosts));
        var next = e.Evaluate(Snap(Now.AddHours(25), hosts));

        Assert.True(before.IsEmpty);
        Assert.Single(after.Reminder);
        Assert.True(next.IsEmpty);
    }

    [Fact]
    public void Message_groups_hosts_and_truncates_long_lists()
    {
        var e = Eval(c => c.MaxListedHosts = 3);
        var hosts = Enumerable.Range(1, 7).Select(i => Host($"pc-{i}", Now.AddDays(-4))).ToList();

        var cycle = e.Evaluate(Snap(Now, hosts, freePct: 8));
        string text = e.Format(cycle);

        Assert.StartsWith("test — новых: 8", text);
        Assert.Contains("мало места на сервере", text.Split('\n')[1]);   // серверные — первыми
        Assert.Contains("pc-3 молчит", text);
        Assert.DoesNotContain("pc-4 молчит", text);
        Assert.Contains("… и ещё 4 хост(ов) с тем же", text);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body;
        public Uri? Uri;
        public long? ContentLength;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri;
            ContentLength = request.Content!.Headers.ContentLength;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Webhook_posts_text_and_structured_alerts()
    {
        var handler = new CaptureHandler();
        var ch = new WebhookAlertChannel(new HttpClient(handler), "https://chat.example/hooks/abc?chat_id=1", "prod");
        var e = Eval();
        var cycle = e.Evaluate(Snap(Now, new[] { Host("pc-1", Now.AddDays(-4)) }));

        await ch.SendAsync("subj", e.Format(cycle), cycle, CancellationToken.None);

        Assert.Equal("chat_id=1", handler.Uri!.Query.TrimStart('?'));
        // Content-Length обязателен: chunked-тело простой приёмник видел пустым.
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(handler.Body!), handler.ContentLength);
        var json = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Contains("pc-1 молчит", json.GetProperty("text").GetString());
        Assert.Equal("prod", json.GetProperty("server").GetString());
        var alert = json.GetProperty("alerts")[0];
        Assert.Equal("firing", alert.GetProperty("state").GetString());
        Assert.Equal("host-silent", alert.GetProperty("kind").GetString());
        Assert.Equal("pc-1", alert.GetProperty("host").GetString());
    }

    private sealed class FixedFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public FixedFactory(HttpMessageHandler h) => _h = h;
        public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
    }

    [Fact]
    public async Task Service_checks_real_db_and_counts_notifications()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"watcher-alerts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var db = new Db(Path.Combine(dir, "watcher.db"));
            db.UpsertHeartbeat("pc-old", "10.0.0.1", "0.9.0");
            using (var c = db.Open())
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "UPDATE hosts SET last_heartbeat_at = $t";
                cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.AddDays(-5).ToString("o"));
                cmd.ExecuteNonQuery();
            }
            var cfg = new ServerConfig { DataDir = dir, Alerts = new AlertConfig { WebhookUrl = "https://hook.example/x", DiskLowPercent = 0 } };
            var health = new ServerHealth();
            var handler = new CaptureHandler();
            var svc = new AlertService(db, cfg, health, NullLogger<AlertService>.Instance, new FixedFactory(handler));

            await svc.CheckOnceAsync(CancellationToken.None);

            Assert.Contains("pc-old молчит 5 дн.", JsonDocument.Parse(handler.Body!).RootElement.GetProperty("text").GetString());
            Assert.Equal(1, health.AlertsFiring);
            Assert.Equal(1, health.AlertNotifications[("webhook", "ok")]);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
