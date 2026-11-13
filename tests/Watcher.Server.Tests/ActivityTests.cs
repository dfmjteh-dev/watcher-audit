using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Watcher.Server.Tests;

// H9 (roadmap-next.md): активное окно рядом с видео — приём из sidecar,
// подписи сегмента, поиск, группы viewer'а, журнал, удаление вместе с сегментом.
public class ActivityTests : IDisposable
{
    private static readonly string[] EnvKeys =
    {
        "Server__DataDir", "Server__DbPath", "Server__AdminUser", "Server__AdminPassword",
        "Server__CookieSecure", "Server__AllowAnonymousAgents",
    };

    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _agent;
    private readonly HttpClient _admin;
    private readonly Db _db;
    private readonly DateTime _t0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    public ActivityTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-activity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        Environment.SetEnvironmentVariable("Server__AllowAnonymousAgents", "true");

        _factory = new WebApplicationFactory<Program>();
        _agent = _factory.CreateClient();
        _admin = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _admin.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        _admin.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123" }).GetAwaiter().GetResult().Dispose();
        _db = new Db(Path.Combine(_dataDir, "watcher.db"));
    }

    public void Dispose()
    {
        _agent.Dispose();
        _admin.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var key in EnvKeys) Environment.SetEnvironmentVariable(key, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private async Task<long> Upload(string host, string filename, int minute, object[] activity)
    {
        var meta = new
        {
            host,
            startedAt = _t0.AddMinutes(minute).ToString("o"),
            endedAt = _t0.AddMinutes(minute + 1).ToString("o"),
            durationSec = 60,
            frames = 120,
            motionEvents = 3,
            startReason = "motion",
            activity,
        };
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes(filename)), "video", filename);
        form.Add(new StringContent(JsonSerializer.Serialize(meta)), "meta", filename + ".json");
        using var r = await _agent.PostAsync("/api/upload", form);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private async Task<HttpClient> LoginAs(string username, string role)
    {
        _db.InsertUser(username, UserAuth.HashPassword("password-123456"), role);
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        using var r = await c.PostAsJsonAsync("/api/login", new { username, password = "password-123456" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return c;
    }

    [Fact]
    public async Task Sidecar_activity_is_stored_cleaned_and_mapped_to_time()
    {
        long id = await Upload("pc1", "pc1-a.mp4", 0, new object[]
        {
            new { t = 0.0, process = "OUTLOOK", title = "Входящие — Почта" },
            new { t = 12.5, process = "chrome", title = "Отчёт\u0007 за\nквартал" + new string('x', 400) },
            new { t = -1.0, process = "bad", title = "отрицательное смещение" },
            new { t = 99999.0, process = "bad", title = "за пределами сегмента" },
            new { t = "5", process = "bad" },
            new { t = 20.0 },
            new { t = 30.0, process = "EXCEL" },
        });

        var rows = await _admin.GetFromJsonAsync<JsonElement[]>($"/api/segments/{id}/activity");
        Assert.Equal(3, rows!.Length);
        Assert.Equal("OUTLOOK", rows[0].GetProperty("process").GetString());
        Assert.Equal(_t0.ToString("o"), DateTime.Parse(rows[0].GetProperty("ts").GetString()!).ToUniversalTime().ToString("o"));
        string title = rows[1].GetProperty("title").GetString()!;
        Assert.StartsWith("Отчёт  за квартал", title);
        Assert.Equal(UploadValidation.MaxActivityTextLength, title.Length);
        Assert.Equal(_t0.AddSeconds(12.5), DateTime.Parse(rows[1].GetProperty("ts").GetString()!).ToUniversalTime());
        Assert.Equal(JsonValueKind.Null, rows[2].GetProperty("title").ValueKind);
    }

    [Fact]
    public async Task Search_is_case_insensitive_for_cyrillic_and_audited()
    {
        await Upload("pc1", "pc1-a.mp4", 0, new object[] { new { t = 0.0, process = "WINWORD", title = "Договор поставки.docx" } });
        await Upload("pc1", "pc1-b.mp4", 10, new object[] { new { t = 3.0, process = "chrome", title = "Новости" } });
        await Upload("pc2", "pc2-a.mp4", 5, new object[] { new { t = 1.0, process = "chrome", title = "ДОГОВОР аренды" } });

        var all = await _admin.GetFromJsonAsync<JsonElement[]>("/api/activity?q=договор");
        Assert.Equal(new[] { "pc2", "pc1" }, all!.Select(r => r.GetProperty("host").GetString()));

        var pc1 = await _admin.GetFromJsonAsync<JsonElement[]>("/api/activity?host=pc1&q=CHROME");
        Assert.Single(pc1!);
        Assert.Equal("Новости", pc1![0].GetProperty("title").GetString());

        var period = await _admin.GetFromJsonAsync<JsonElement[]>($"/api/activity?from={_t0.AddMinutes(4):o}&to={_t0.AddMinutes(6):o}");
        Assert.Equal("pc2", Assert.Single(period!).GetProperty("host").GetString());

        // LIKE-символы в запросе — буквально, не шаблон.
        Assert.Empty((await _admin.GetFromJsonAsync<JsonElement[]>("/api/activity?q=%25"))!);

        var audit = _db.QueryAudit(null, "activity.search", null, null, 10, 0);
        Assert.Equal(4, audit.Count);
        Assert.Contains(audit, e => e.Details!.Contains("\"q\":\"договор\""));
    }

    [Fact]
    public async Task Viewer_sees_only_hosts_of_own_groups()
    {
        long groupA = _db.CreateGroup("group-a");
        _db.UpsertHeartbeat("pc-a", "10.0.0.1", "1.0.0");
        _db.SetHostGroup(_db.ListFleetHosts(null, 100, 0).First(h => h.Name == "pc-a").Id, groupA);
        await Upload("pc-a", "pc-a.mp4", 0, new object[] { new { t = 0.0, process = "notepad", title = "секрет А" } });
        long other = await Upload("pc-b", "pc-b.mp4", 0, new object[] { new { t = 0.0, process = "notepad", title = "секрет Б" } });
        using var viewer = await LoginAs("v1", "viewer");
        _db.SetUserGroups(_db.UserByUsername("v1")!.Id, new List<long> { groupA });

        var found = await viewer.GetFromJsonAsync<JsonElement[]>("/api/activity?q=секрет");
        Assert.Equal("pc-a", Assert.Single(found!).GetProperty("host").GetString());
        using var r = await viewer.GetAsync($"/api/segments/{other}/activity");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

        using var op = await LoginAs("op1", "operator");
        using var r2 = await op.GetAsync("/api/activity?q=секрет");
        Assert.Equal(HttpStatusCode.Forbidden, r2.StatusCode);
    }

    [Fact]
    public async Task Activity_goes_away_with_the_segment()
    {
        long id = await Upload("pc1", "pc1-a.mp4", 0, new object[] { new { t = 0.0, process = "notepad", title = "x" } });
        Assert.Single(_db.ActivityForSegment(id));
        _db.DeleteSegmentRow(id);
        Assert.Empty(_db.ActivityForSegment(id));
        Assert.Empty(_db.SearchActivity(null, null, null, "notepad", null, null, 10, 0));
    }

    [Fact]
    public async Task Agent_without_activity_uploads_as_before()
    {
        long id = await Upload("pc1", "pc1-old.mp4", 0, Array.Empty<object>());
        Assert.Empty(_db.ActivityForSegment(id));
    }

    [Theory]
    [InlineData("\"full\"", true)]
    [InlineData("\"process\"", true)]
    [InlineData("\"off\"", true)]
    [InlineData("\"titles\"", false)]
    [InlineData("true", false)]
    public void Config_activity_mode_is_validated(string value, bool ok)
    {
        var config = JsonDocument.Parse($"{{\"activity\": {value}}}").RootElement;
        Assert.Equal(ok, AgentConfigValidation.Validate(config) is null);
    }
}
