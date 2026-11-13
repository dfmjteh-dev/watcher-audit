using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Watcher.Server.Tests;

// Задача G1 (roadmap-next.md): регистрация агентов, host из учётки, общий
// ApiToken как переходный путь.
public partial class AgentEnrollmentTests : IDisposable
{
    // Общий токен парка (до 1.0) — теперь просто неизвестный токен.
    private const string SharedToken = "legacy-shared-token";
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;

    public AgentEnrollmentTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-enroll-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");

        _factory = new WebApplicationFactory<Program>();
        _admin = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _admin.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        _admin.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123" })
            .GetAwaiter().GetResult().Dispose();
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var key in new[] { "Server__DataDir", "Server__DbPath", "Server__AdminUser", "Server__AdminPassword", "Server__CookieSecure" })
            Environment.SetEnvironmentVariable(key, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private HttpClient Agent(string? bearer)
    {
        var c = _factory.CreateClient();
        if (bearer is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return c;
    }

    private async Task<string> CreateEnrollToken(object body)
    {
        using var resp = await _admin.PostAsJsonAsync("/api/agents/enroll-tokens", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("token").GetString()!;
    }

    private async Task<HttpResponseMessage> Enroll(string token, string host, string kind = "video")
    {
        using var anon = Agent(null);
        return await anon.PostAsJsonAsync("/api/enroll", new { token, host, kind });
    }

    private async Task<string> EnrollSecret(string token, string host, string kind = "video")
    {
        using var resp = await Enroll(token, host, kind);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("secret").GetString()!;
    }

    private static MultipartFormDataContent UploadForm(string filename, string metaJson)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent("video"u8.ToArray()), "video", filename);
        content.Add(new StringContent(metaJson), "meta", filename + ".json");
        return content;
    }

    private async Task<JsonElement> FleetHost(string name)
    {
        using var resp = await _admin.GetAsync("/api/fleet");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(h => h.GetProperty("name").GetString() == name).Clone();
    }

    [Fact]
    public async Task Enrolled_agent_uploads_and_heartbeats_under_its_own_host()
    {
        string token = await CreateEnrollToken(new { name = "rollout", ttlHours = 24 });
        string secret = await EnrollSecret(token, "PC-1");
        Assert.StartsWith("wagt_", secret);

        using var agent = Agent(secret);
        using var hb = await agent.PostAsJsonAsync("/api/heartbeat", new { host = "PC-1", version = "1.0" });
        Assert.Equal(HttpStatusCode.OK, hb.StatusCode);
        // meta без host — агенту с учёткой не обязателен.
        using var up = await agent.PostAsync("/api/upload", UploadForm("PC-1_20261001_100000.mp4", "{}"));
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);

        using var segs = await _admin.GetAsync("/api/segments?host=PC-1");
        Assert.Contains("PC-1_20261001_100000.mp4", await segs.Content.ReadAsStringAsync());
        Assert.False((await FleetHost("PC-1")).GetProperty("legacyAuth").GetBoolean());
    }

    // Чужое имя в записи не даёт писать в чужой хост: запись ложится в хост
    // учётки. Чужое имя в heartbeat — попытка переименования; имя занято —
    // ничего не меняется, конфликт в журнале один раз, не на каждый heartbeat.
    [Fact]
    public async Task Agent_cannot_write_into_another_host()
    {
        string token = await CreateEnrollToken(new { name = "rollout", ttlHours = 24 });
        string secret1 = await EnrollSecret(token, "PC-1");
        await EnrollSecret(token, "PC-2");
        using var agent = Agent(secret1);

        using (var up = await agent.PostAsync("/api/upload", UploadForm("PC-1_20261001_100000.mp4", """{"host":"PC-2"}""")))
            Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        Assert.Contains("PC-1_20261001_100000.mp4", await (await _admin.GetAsync("/api/segments?host=PC-1")).Content.ReadAsStringAsync());
        Assert.DoesNotContain("PC-1_20261001_100000.mp4", await (await _admin.GetAsync("/api/segments?host=PC-2")).Content.ReadAsStringAsync());

        for (int i = 0; i < 2; i++)
        {
            using var hb = await agent.PostAsJsonAsync("/api/heartbeat", new { host = "PC-2" });
            Assert.Equal(HttpStatusCode.OK, hb.StatusCode);
        }
        _ = await FleetHost("PC-1");
        using var audit = await _admin.GetAsync("/api/audit?action=agent.host-rename-conflict");
        using var doc = JsonDocument.Parse(await audit.Content.ReadAsStringAsync());
        Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
    }

    // watcher-debug 2026-10-13: машину переименовали — агент продолжает выгружать
    // без ручных действий, хост переименован вместе с историей, дубля нет; записи
    // из очереди, сделанные до переименования (в meta старое имя), тоже уходят.
    [Fact]
    public async Task Renamed_machine_keeps_uploading_and_history()
    {
        string token = await CreateEnrollToken(new { name = "rollout", ttlHours = 24 });
        string secret = await EnrollSecret(token, "OLD-NAME");
        using var agent = Agent(secret);
        using (var up = await agent.PostAsync("/api/upload", UploadForm("OLD-NAME_20261001_100000.mp4", """{"host":"OLD-NAME"}""")))
            Assert.Equal(HttpStatusCode.OK, up.StatusCode);

        using (var hb = await agent.PostAsJsonAsync("/api/heartbeat", new { host = "NEW-NAME" }))
            Assert.Equal(HttpStatusCode.OK, hb.StatusCode);
        using (var up = await agent.PostAsync("/api/upload", UploadForm("OLD-NAME_20261001_110000.mp4", """{"host":"OLD-NAME"}""")))
            Assert.Equal(HttpStatusCode.OK, up.StatusCode);

        string segs = await (await _admin.GetAsync("/api/segments?host=NEW-NAME")).Content.ReadAsStringAsync();
        Assert.Contains("OLD-NAME_20261001_100000.mp4", segs);
        Assert.Contains("OLD-NAME_20261001_110000.mp4", segs);
        using var inv = JsonDocument.Parse(await (await _admin.GetAsync("/api/inventory")).Content.ReadAsStringAsync());
        var names = inv.RootElement.EnumerateArray().Select(h => h.GetProperty("name").GetString()).ToList();
        Assert.Contains("NEW-NAME", names);
        Assert.DoesNotContain("OLD-NAME", names);
        Assert.Contains("\"from\":\"OLD-NAME\"", await (await _admin.GetAsync("/api/audit?action=agent.host-renamed")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Video_credential_cannot_upload_terminal_sessions()
    {
        string token = await CreateEnrollToken(new { name = "rollout", ttlHours = 24 });
        string secret = await EnrollSecret(token, "PC-1", "video");
        using var agent = Agent(secret);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("""{"kind":"terminal-input","host":"PC-1","sessionUser":"a","startedAt":"2026-10-01T10:00:00Z","endedAt":"2026-10-01T10:00:01Z","input":"x","sessionId":1}"""), "meta", "m.json");

        using var resp = await agent.PostAsync("/api/upload-terminal", content);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Revoked_credential_is_rejected()
    {
        string token = await CreateEnrollToken(new { name = "rollout", ttlHours = 24 });
        string secret = await EnrollSecret(token, "PC-1");
        using var list = await _admin.GetAsync("/api/agents/credentials");
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        long id = doc.RootElement[0].GetProperty("id").GetInt64();

        using var revoke = await _admin.PostAsync($"/api/agents/credentials/{id}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        using var agent = Agent(secret);
        using var hb = await agent.PostAsJsonAsync("/api/heartbeat", new { host = "PC-1" });
        Assert.Equal(HttpStatusCode.Unauthorized, hb.StatusCode);
    }

    [Fact]
    public async Task Enroll_token_limits_expiry_and_revoke()
    {
        string limited = await CreateEnrollToken(new { name = "one", ttlHours = 24, maxUses = 1 });
        using (var first = await Enroll(limited, "PC-1")) Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using (var second = await Enroll(limited, "PC-2")) Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);

        string revoked = await CreateEnrollToken(new { name = "two", ttlHours = 24 });
        using var tokens = await _admin.GetAsync("/api/agents/enroll-tokens");
        using var doc = JsonDocument.Parse(await tokens.Content.ReadAsStringAsync());
        long id = doc.RootElement.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "two").GetProperty("id").GetInt64();
        // Значение токена в списке не отдаётся — только при создании.
        Assert.DoesNotContain(revoked, doc.RootElement.GetRawText());
        using (var r = await _admin.PostAsync($"/api/agents/enroll-tokens/{id}/revoke", null)) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var after = await Enroll(revoked, "PC-3")) Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);

        using (var bogus = await Enroll("wenr_bogus", "PC-4")) Assert.Equal(HttpStatusCode.Unauthorized, bogus.StatusCode);
    }

    [Fact]
    public async Task Enroll_validates_host_and_kind()
    {
        string token = await CreateEnrollToken(new { name = "rollout", ttlHours = 24 });
        using (var r = await Enroll(token, "<script>")) Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using (var r = await Enroll(token, "PC-1", "admin")) Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Enroll_token_group_is_assigned_to_new_host()
    {
        using var g = await _admin.PostAsJsonAsync("/api/groups", new { name = "Кассы" });
        using var gdoc = JsonDocument.Parse(await g.Content.ReadAsStringAsync());
        long groupId = gdoc.RootElement.GetProperty("id").GetInt64();
        string token = await CreateEnrollToken(new { name = "кассы", ttlHours = 24, groupId });

        await EnrollSecret(token, "POS-1");

        Assert.Equal(groupId, (await FleetHost("POS-1")).GetProperty("groupId").GetInt64());
    }

    // Решение по G1: повтор вида у существующего хоста не блокируется, а
    // помечается; второй агент другого вида (Linux: экран + консоль) — норма.
    [Fact]
    public async Task Second_credential_of_same_kind_is_flagged_other_kind_is_not()
    {
        string token = await CreateEnrollToken(new { name = "rollout", ttlHours = 24 });
        await EnrollSecret(token, "LNX-1", "video");
        await EnrollSecret(token, "LNX-1", "terminal");
        Assert.False((await FleetHost("LNX-1")).GetProperty("duplicateCredential").GetBoolean());

        string second = await EnrollSecret(token, "LNX-1", "video");
        Assert.True((await FleetHost("LNX-1")).GetProperty("duplicateCredential").GetBoolean());
        // Помеченная учётка работает.
        using var agent = Agent(second);
        using var hb = await agent.PostAsJsonAsync("/api/heartbeat", new { host = "LNX-1" });
        Assert.Equal(HttpStatusCode.OK, hb.StatusCode);

        using var list = await _admin.GetAsync("/api/agents/credentials");
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        long dupId = doc.RootElement.EnumerateArray().Single(a => a.GetProperty("duplicateKind").GetBoolean()).GetProperty("id").GetInt64();
        using (var ack = await _admin.PostAsync($"/api/agents/credentials/{dupId}/acknowledge", null)) Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        Assert.False((await FleetHost("LNX-1")).GetProperty("duplicateCredential").GetBoolean());

        using var audit = await _admin.GetAsync("/api/audit?action=agent.enroll");
        Assert.Contains("\"duplicate\":true", await audit.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Shared_token_is_not_accepted()
    {
        // Server__ApiToken из старого .env игнорируется: агент без учётки не проходит.
        Environment.SetEnvironmentVariable("Server__ApiToken", SharedToken);
        try
        {
            using var agent = Agent(SharedToken);
            using var hb = await agent.PostAsJsonAsync("/api/heartbeat", new { host = "OLD-1" });
            Assert.Equal(HttpStatusCode.Unauthorized, hb.StatusCode);
        }
        finally { Environment.SetEnvironmentVariable("Server__ApiToken", null); }

        using var none = Agent(null);
        using var anon = await none.PostAsJsonAsync("/api/heartbeat", new { host = "OLD-1" });
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        using var wrong = Agent("wagt_not-a-real-secret");
        using var w = await wrong.PostAsJsonAsync("/api/heartbeat", new { host = "OLD-1" });
        Assert.Equal(HttpStatusCode.Unauthorized, w.StatusCode);
    }

    // Этап 2 (docs/android-plan.md): kind=android принимается при регистрации,
    // heartbeat с платформой android возвращает действующий конфиг группы —
    // «конфиг из веб-UI доходит до устройства».
    [Fact]
    public async Task Android_agent_enrolls_heartbeats_and_receives_group_config()
    {
        using var g = await _admin.PostAsJsonAsync("/api/groups", new { name = "Телефоны" });
        long groupId = JsonDocument.Parse(await g.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();
        using (var cfg = await _admin.PutAsJsonAsync($"/api/groups/{groupId}/config",
            new { config = new { schemaVersion = 1, outputFps = 4 } }))
            Assert.Equal(HttpStatusCode.OK, cfg.StatusCode);

        string token = await CreateEnrollToken(new { name = "phones", ttlHours = 24, groupId });
        string secret = await EnrollSecret(token, "Pixel-7", "android");
        Assert.StartsWith("wagt_", secret);

        using var agent = Agent(secret);
        using var hb = await agent.PostAsJsonAsync("/api/heartbeat",
            new { host = "Pixel-7", version = "0.14.4", platform = "android" });
        Assert.Equal(HttpStatusCode.OK, hb.StatusCode);
        using var doc = JsonDocument.Parse(await hb.Content.ReadAsStringAsync());
        Assert.NotEqual(JsonValueKind.Null, doc.RootElement.GetProperty("configHash").ValueKind);
        Assert.Equal(4, doc.RootElement.GetProperty("config").GetProperty("outputFps").GetInt32());
    }

    // Учётка android не шлёт консоль (/api/upload-terminal) — как video/terminal
    // не шлют данные друг друга. Видео (/api/upload) — шлёт: запись экрана по
    // согласию (Этап 12+, #190).
    [Fact]
    public async Task Android_credential_uploads_video_but_not_terminal()
    {
        string token = await CreateEnrollToken(new { name = "phones", ttlHours = 24 });
        string secret = await EnrollSecret(token, "Pixel-7", "android");
        using var agent = Agent(secret);

        using (var up = await agent.PostAsync("/api/upload", UploadForm("Pixel-7_20261001_100000.mp4", "{}")))
            Assert.NotEqual(HttpStatusCode.Forbidden, up.StatusCode);

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("""{"kind":"terminal-input","host":"Pixel-7","sessionUser":"a","startedAt":"2026-10-01T10:00:00Z","endedAt":"2026-10-01T10:00:01Z","input":"x","sessionId":1}"""), "meta", "m.json");
        using (var term = await agent.PostAsync("/api/upload-terminal", content))
            Assert.Equal(HttpStatusCode.Forbidden, term.StatusCode);
    }

    [Fact]
    public async Task Agent_management_is_admin_only()
    {
        using var anon = Agent(null);
        using var r = await anon.GetAsync("/api/agents/credentials");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        // Секрет агента не даёт доступа к веб-API.
        string token = await CreateEnrollToken(new { name = "rollout", ttlHours = 24 });
        using var agent = Agent(await EnrollSecret(token, "PC-1"));
        using var r2 = await agent.GetAsync("/api/agents/enroll-tokens");
        Assert.Equal(HttpStatusCode.Unauthorized, r2.StatusCode);
    }
}
