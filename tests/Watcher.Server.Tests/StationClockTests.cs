using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Watcher.Server.Tests;

// R7 (docs/code-review-2026-10.md): время записи по часам станции.
public class StationClockTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    private static TimeSpan? Measure(string? header)
    {
        var ctx = new DefaultHttpContext();
        if (header is not null) ctx.Request.Headers[StationClock.AgentTimeHeader] = header;
        return StationClock.MeasureSkew(ctx.Request, Now);
    }

    [Fact]
    public void Skew_is_server_minus_agent_and_null_without_header()
    {
        Assert.Null(Measure(null));
        Assert.Null(Measure("not a date"));
        Assert.Equal(TimeSpan.FromHours(1), Measure("2026-10-08T11:00:00.0000000Z"));
    }

    [Fact]
    public void Small_skew_leaves_times_as_is()
    {
        var started = Now.AddHours(-1);
        var (s, e, station) = StationClock.Correct(started, started.AddMinutes(5), TimeSpan.FromMinutes(3), Now);

        Assert.Equal(started, s);
        Assert.Equal(started.AddMinutes(5), e);
        Assert.Null(station);
    }

    [Fact]
    public void Large_skew_shifts_times_and_keeps_station_time()
    {
        // Часы станции в 2000 году, запись сделана за час до выгрузки.
        var skew = Now - new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var stationStarted = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(-1);

        var (s, e, station) = StationClock.Correct(stationStarted, stationStarted.AddMinutes(5), skew, Now);

        Assert.Equal(Now.AddHours(-1), s);
        Assert.Equal(Now.AddHours(-1).AddMinutes(5), e);
        Assert.Equal(stationStarted, station);
    }

    // Часы исправились до выгрузки (или агент до R7): запись из будущего —
    // ставим время выгрузки, длительность сохраняем.
    [Fact]
    public void Future_start_without_measurable_skew_becomes_upload_time()
    {
        var stationStarted = Now.AddDays(30);

        var (s, e, station) = StationClock.Correct(stationStarted, stationStarted.AddMinutes(5), null, Now);

        Assert.Equal(Now.AddMinutes(-5), s);
        Assert.Equal(Now, e);
        Assert.Equal(stationStarted, station);
    }

    // Станция неделю без сети, часы верные — старая запись это не ошибка часов.
    [Fact]
    public void Old_record_from_correct_clock_is_not_touched()
    {
        var started = Now.AddDays(-7);

        var (s, _, station) = StationClock.Correct(started, started.AddMinutes(5), TimeSpan.FromSeconds(2), Now);

        Assert.Equal(started, s);
        Assert.Null(station);
    }
}

public class StationClockEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly HttpClient _authed;

    public StationClockEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-clock-api-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        Environment.SetEnvironmentVariable("Server__AllowAnonymousAgents", "true");

        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _authed = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _authed.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        _authed.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123" })
            .GetAwaiter().GetResult().Dispose();
    }

    public void Dispose()
    {
        _client.Dispose();
        _authed.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var key in new[] { "Server__DataDir", "Server__DbPath", "Server__AdminUser", "Server__AdminPassword", "Server__CookieSecure", "Server__AllowAnonymousAgents" })
            Environment.SetEnvironmentVariable(key, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    // Часы станции ровно на 20 лет назад от настоящего момента.
    private static DateTime StationNow() => DateTime.UtcNow.AddYears(-20);

    private async Task<HttpResponseMessage> PostTerminalInput(DateTime stationNow, DateTime stationStarted)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent($$"""{"kind":"terminal-input","host":"skewed","sessionUser":"alice","startedAt":"{{stationStarted:o}}","endedAt":"{{stationStarted.AddSeconds(5):o}}","durationSec":5,"input":"echo r7","sessionId":3}"""), "meta", "meta.json");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/upload-terminal") { Content = content };
        req.Headers.Add(StationClock.AgentTimeHeader, stationNow.ToString("o"));
        return await _client.SendAsync(req);
    }

    [Fact]
    public async Task Terminal_input_from_skewed_station_gets_server_time_and_retry_is_still_409()
    {
        DateTime stationNow = StationNow();
        DateTime stationStarted = stationNow.AddMinutes(-30);

        using var r1 = await PostTerminalInput(stationNow, stationStarted);
        // Повтор той же записи позже — сдвиг меряется заново и чуть другой,
        // externalId от этого меняться не должен.
        using var r2 = await PostTerminalInput(stationNow.AddSeconds(7), stationStarted);

        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
        var row = (await _authed.GetFromJsonAsync<JsonElement>("/api/terminal-sessions?host=skewed"))[0];
        var started = row.GetProperty("startedAt").GetDateTime().ToUniversalTime();
        Assert.InRange(started, DateTime.UtcNow.AddMinutes(-35), DateTime.UtcNow.AddMinutes(-25));
        Assert.Equal(stationStarted, row.GetProperty("stationStartedAt").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task Video_from_skewed_station_gets_server_time()
    {
        DateTime stationNow = StationNow();
        DateTime stationStarted = stationNow.AddMinutes(-10);
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("fake mp4")), "video", "skewed_20060101.mp4");
        content.Add(new StringContent($$"""{"host":"skewed-v","startedAt":"{{stationStarted:o}}","endedAt":"{{stationStarted.AddMinutes(1):o}}","durationSec":60}"""), "meta", "meta.json");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/upload") { Content = content };
        req.Headers.Add(StationClock.AgentTimeHeader, stationNow.ToString("o"));

        using var resp = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var row = (await _authed.GetFromJsonAsync<JsonElement>("/api/segments?host=skewed-v"))[0];
        Assert.InRange(row.GetProperty("startedAt").GetDateTime().ToUniversalTime(), DateTime.UtcNow.AddMinutes(-15), DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(stationStarted, row.GetProperty("stationStartedAt").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task Heartbeat_with_wrong_clock_flags_host_in_inventory()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/heartbeat") { Content = JsonContent.Create(new { host = "skewed-hb", version = "0.8.0" }) };
        req.Headers.Add(StationClock.AgentTimeHeader, DateTime.UtcNow.AddHours(-3).ToString("o"));
        using var good = new HttpRequestMessage(HttpMethod.Post, "/api/heartbeat") { Content = JsonContent.Create(new { host = "fine-hb", version = "0.8.0" }) };
        good.Headers.Add(StationClock.AgentTimeHeader, DateTime.UtcNow.ToString("o"));

        (await _client.SendAsync(req)).Dispose();
        (await _client.SendAsync(good)).Dispose();

        var hosts = (await _authed.GetFromJsonAsync<JsonElement>("/api/inventory")).EnumerateArray().ToList();
        var skewed = hosts.Single(h => h.GetProperty("name").GetString() == "skewed-hb");
        var fine = hosts.Single(h => h.GetProperty("name").GetString() == "fine-hb");
        Assert.True(skewed.GetProperty("clockWrong").GetBoolean());
        Assert.InRange(skewed.GetProperty("clockSkewSec").GetDouble(), 3 * 3600 - 60, 3 * 3600 + 60);
        Assert.False(fine.GetProperty("clockWrong").GetBoolean());
    }
}
