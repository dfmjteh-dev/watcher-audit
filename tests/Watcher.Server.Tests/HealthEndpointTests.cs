using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Watcher.Server.Tests;

// H4 (roadmap-next.md): /healthz и /metrics.
public class HealthEndpointTests : IDisposable
{
    private const string MetricsToken = "metrics-token-123";
    private static readonly string[] EnvKeys =
    {
        "Server__DataDir", "Server__DbPath", "Server__AdminUser", "Server__AdminPassword",
        "Server__CookieSecure", "Server__AllowAnonymousAgents", "Server__MetricsToken", "Server__MinFreeDiskPercent",
    };

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-health-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program>? _factory;

    public HealthEndpointTests() => Directory.CreateDirectory(_dataDir);

    private HttpClient Start(string metricsToken = MetricsToken, string? minFreeDiskPercent = null)
    {
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        Environment.SetEnvironmentVariable("Server__AllowAnonymousAgents", "true");
        Environment.SetEnvironmentVariable("Server__MetricsToken", metricsToken);
        Environment.SetEnvironmentVariable("Server__MinFreeDiskPercent", minFreeDiskPercent);
        _factory = new WebApplicationFactory<Program>();
        return _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var key in EnvKeys) Environment.SetEnvironmentVariable(key, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private static async Task<string> Metrics(HttpClient client)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MetricsToken);
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/plain", resp.Content.Headers.ContentType!.MediaType);
        Assert.Contains(resp.Content.Headers.ContentType.Parameters, p => p.Name == "version" && p.Value == "0.0.4");
        return await resp.Content.ReadAsStringAsync();
    }

    private static MultipartFormDataContent UploadForm(string host, string filename)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent("fake mp4"u8.ToArray()), "video", filename);
        string meta = JsonSerializer.Serialize(new { host, startedAt = DateTime.UtcNow.ToString("o"), endedAt = DateTime.UtcNow.ToString("o") });
        content.Add(new StringContent(meta), "meta", filename + ".json");
        return content;
    }

    [Fact]
    public async Task Healthz_is_anonymous_and_reports_checks_without_numbers()
    {
        using var client = Start();

        using var resp = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
        var checks = body.GetProperty("checks");
        Assert.Equal("ok", checks.GetProperty("db").GetString());
        Assert.Equal("ok", checks.GetProperty("retention").GetString());
        Assert.Contains(checks.GetProperty("disk").GetString(), new[] { "ok", "unknown" });
        Assert.DoesNotContain(_dataDir, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Healthz_fails_with_503_when_free_disk_is_below_threshold()
    {
        // Свободно не может быть 100% — порог гарантированно не выполняется.
        using var client = Start(minFreeDiskPercent: "100");

        using var resp = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("fail", body.GetProperty("status").GetString());
        Assert.Equal("fail", body.GetProperty("checks").GetProperty("disk").GetString());
    }

    [Fact]
    public async Task Metrics_is_disabled_without_token_in_config()
    {
        using var client = Start(metricsToken: "");

        using var resp = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Metrics_requires_its_own_token_even_for_logged_in_admin()
    {
        using var client = Start();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        (await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123" })).Dispose();

        using var noToken = await client.GetAsync("/metrics");
        using var wrong = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
        using var wrongResp = await client.SendAsync(wrong);

        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongResp.StatusCode);
    }

    [Fact]
    public async Task Metrics_count_uploads_heartbeats_logins_and_hosts()
    {
        using var client = Start();
        using (var r = await client.PostAsync("/api/upload", UploadForm("pc-1", "pc-1_a.mp4"))) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await client.PostAsync("/api/upload", UploadForm("pc-1", "pc-1_a.mp4"))) Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        using (var r = await client.PostAsync("/api/upload", new StringContent("x"))) Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using (var r = await client.PostAsJsonAsync("/api/heartbeat", new { host = "pc-1", ip = "10.0.0.1", version = "0.1.0", droppedSegments = 3 }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        // «Устарел» — не дошёл до версии, назначенной группе (версии агента и сервера раздельные).
        {
            var db = new Db(Path.Combine(_dataDir, "watcher.db"));
            long g = db.CreateGroup("pilot");
            db.SetGroupTargetVersion(g, "0.2.0");
            db.SetHostGroup(db.ListFleetHosts(null, 100, 0).Single(h => h.Name == "pc-1").Id, g);
        }
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        (await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "wrong-password" })).Dispose();
        (await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123" })).Dispose();

        string m = await Metrics(client);

        Assert.Contains("watcher_build_info{version=\"", m);
        Assert.Contains("watcher_uploads_total{kind=\"video\",result=\"ok\"} 1\n", m);
        Assert.Contains("watcher_uploads_total{kind=\"video\",result=\"duplicate\"} 1\n", m);
        Assert.Contains("watcher_uploads_total{kind=\"video\",result=\"rejected\"} 1\n", m);
        Assert.Contains("watcher_heartbeats_total 1\n", m);
        Assert.Contains("watcher_logins_total{result=\"failed\"} 1\n", m);
        Assert.Contains("watcher_logins_total{result=\"ok\"} 1\n", m);
        Assert.Contains("watcher_hosts{state=\"online\"} 1\n", m);
        Assert.Contains("watcher_host_dropped_segments{host=\"pc-1\"} 3\n", m);
        Assert.Contains("watcher_host_last_heartbeat_timestamp_seconds{host=\"pc-1\"} ", m);
        Assert.Contains("watcher_hosts_outdated_agent 1\n", m);
        Assert.Contains("watcher_records{kind=\"video\"} 1\n", m);
        Assert.Contains("# TYPE watcher_uploads_total counter\n", m);
    }

    [Fact]
    public void Retention_alive_window()
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        var interval = TimeSpan.FromMinutes(60);

        Assert.True(new ServerHealth(now.AddMinutes(-5)).IsRetentionAlive(now, interval));   // ещё не было прохода, только стартовали
        Assert.False(new ServerHealth(now.AddHours(-5)).IsRetentionAlive(now, interval));    // ни одного прохода за 5 часов

        var health = new ServerHealth(now.AddDays(-1));
        health.RetentionSucceeded(now.AddMinutes(-90));
        Assert.True(health.IsRetentionAlive(now, interval));
        health.RetentionSucceeded(now.AddHours(-3));
        Assert.False(health.IsRetentionAlive(now, interval));
    }

    [Theory]
    [InlineData(200, "ok")]
    [InlineData(409, "duplicate")]
    [InlineData(422, "collision")]
    [InlineData(401, "unauthorized")]
    [InlineData(403, "unauthorized")]
    [InlineData(400, "rejected")]
    [InlineData(413, "rejected")]
    [InlineData(500, "error")]
    public void Upload_result_by_status_code(int code, string expected) =>
        Assert.Equal(expected, ServerHealth.UploadResult(code));

    [Fact]
    public void Label_values_are_escaped() =>
        Assert.Equal("a\\\"b\\\\c\\nd", HealthEndpoints.MetricsWriter.EscapeLabel("a\"b\\c\nd"));

    [Theory]
    [InlineData(null, "http://127.0.0.1:5050/healthz")]
    [InlineData("http://0.0.0.0:5050", "http://127.0.0.1:5050/healthz")]
    [InlineData("http://+:8080;https://+:8443", "http://127.0.0.1:8080/healthz")]
    [InlineData("http://127.0.0.1:5092", "http://127.0.0.1:5092/healthz")]
    public void Healthcheck_url_from_listen_url(string? listen, string expected) =>
        Assert.Equal(expected, CliCommands.HealthCheckUrl(listen));
}
