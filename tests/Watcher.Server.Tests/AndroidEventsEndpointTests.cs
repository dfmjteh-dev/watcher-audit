using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Watcher.Server.Tests;

public class AndroidEventsEndpointTests : IDisposable
{
    // Валидный P-256 SubjectPublicKeyInfo (base64) — для проверки передачи ключа в enroll.
    private const string TestPublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAECxz96OGMsHNLOeuMGLfVbOvZ5qZMkFEl6+FkutvadwtIYZwpc4ipE7b16yIn/B8JNnyvFOSJB13L1VFrM4myTg==";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"watcher-events-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;
    private readonly Db _db;

    public AndroidEventsEndpointTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("Server__DataDir", _dir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        Environment.SetEnvironmentVariable("Server__ApiToken", "legacy-token");
        // Этап 8: открытый ключ обновлений — проверяем, что enroll его отдаёт агенту.
        Environment.SetEnvironmentVariable("Server__UpdatePublicKey", TestPublicKey);
        _factory = new WebApplicationFactory<Program>();
        _db = _factory.Services.GetRequiredService<Db>();
        _admin = Login("admin");
    }

    private HttpClient Login(string user)
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        using var resp = c.PostAsJsonAsync("/api/login", new { username = user, password = "test-password-123" }).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return c;
    }

    private async Task<HttpClient> Agent(string host = "PHONE", string kind = "android")
    {
        using var t = await _admin.PostAsJsonAsync("/api/agents/enroll-tokens", new { name = "test-" + Guid.NewGuid().ToString("N"), ttlHours = 24 });
        using var token = JsonDocument.Parse(await t.Content.ReadAsStringAsync());
        var c = _factory.CreateClient();
        using var e = await c.PostAsJsonAsync("/api/enroll", new { token = token.RootElement.GetProperty("token").GetString(), host, kind });
        Assert.Equal(HttpStatusCode.OK, e.StatusCode);
        using var secret = JsonDocument.Parse(await e.Content.ReadAsStringAsync());
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret.RootElement.GetProperty("secret").GetString());
        return c;
    }

    private static object Event(string? id = null, string ts = "2026-10-22T12:00:00Z") =>
        new { eventId = id ?? Guid.NewGuid().ToString("D"), ts, type = "app_foreground", package = "com.example.app" };

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Retry_acknowledges_whole_batch_without_duplicate_rows_and_cannot_spoof_host()
    {
        using var agent = await Agent();
        object[] events = [Event(), Event()];
        for (int i = 0; i < 2; i++)
        {
            using var resp = await agent.PostAsJsonAsync("/api/events", new { host = "OTHER", events });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await Json(resp);
            Assert.Equal(2, body.GetProperty("accepted").GetInt32());
            Assert.Equal(i == 0 ? 2 : 0, body.GetProperty("inserted").GetInt32());
        }
        Assert.Equal(2, _db.CountAndroidEvents("PHONE"));
        Assert.Equal(0, _db.CountAndroidEvents("OTHER"));
        using var page = await _admin.GetAsync("/api/inventory/PHONE/events?limit=1&offset=1");
        var result = await Json(page);
        Assert.Equal(2, result.GetProperty("total").GetInt32());
        Assert.Single(result.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Only_android_credentials_can_write_and_cookie_cannot_replace_bearer()
    {
        using var anon = _factory.CreateClient();
        using (var r = await anon.PostAsJsonAsync("/api/events", new { events = new[] { Event() } }))
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        using (var r = await _admin.PostAsJsonAsync("/api/events", new { events = new[] { Event() } }))
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        anon.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "legacy-token");
        using (var r = await anon.PostAsJsonAsync("/api/events", new { events = new[] { Event() } }))
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        foreach (string kind in new[] { "video", "terminal" })
        {
            using var wrong = await Agent("HOST-" + kind, kind);
            using var r = await wrong.PostAsJsonAsync("/api/events", new { events = new[] { Event() } });
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }
    }

    [Fact]
    public async Task Invalid_event_rejects_entire_batch()
    {
        using var agent = await Agent();
        string id = Guid.NewGuid().ToString("D");
        string good = JsonSerializer.Serialize(Event());
        string[] bad = [
            "null",
            $$"""{"eventId":"{{id}}","ts":"2026-10-22T12:00:00Z","type":"unknown"}""",
            $$"""{"eventId":"{{id}}","ts":"2026-10-22T12:00:00","type":"device_state"}""",
            $$"""{"eventId":"{{id}}","ts":"today","type":"device_state"}""",
            """{"ts":"2026-10-22T12:00:00Z","type":"device_state"}""",
            $$"""{"eventId":"{{id}}","ts":"2026-10-22T12:00:00Z","type":"device_state","detail":42}""",
            $$"""{"eventId":"{{id}}","ts":"2026-10-22T12:00:00Z","type":"device_state","detail":[]}""",
            $$"""{"eventId":"{{id}}","ts":"2026-10-22T12:00:00Z","type":"app_foreground"}""",
            JsonSerializer.Serialize(new { eventId = id, ts = "2026-10-22T12:00:00Z", type = "device_state", package = new string('x', 256) }),
            JsonSerializer.Serialize(new { eventId = id, ts = "2026-10-22T12:00:00Z", type = "device_state", detail = new { value = new string('x', Db.MaxDetailLength) } }),
        ];
        foreach (string b in bad)
        {
            using var content = new StringContent("{\"events\":[" + good + "," + b + "]}", Encoding.UTF8, "application/json");
            using var resp = await agent.PostAsync("/api/events", content);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Equal(0, _db.CountAndroidEvents("PHONE"));
        }
    }

    [Fact]
    public async Task Batch_and_body_limits_are_enforced_and_empty_batch_is_acknowledged()
    {
        using var agent = await Agent();
        using (var r = await agent.PostAsJsonAsync("/api/events", new { events = Array.Empty<object>() }))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal(0, (await Json(r)).GetProperty("accepted").GetInt32());
        }
        using (var r = await agent.PostAsJsonAsync("/api/events", new { events = Enumerable.Range(0, 501).Select(_ => Event()).ToArray() }))
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using var content = new StringContent(new string(' ', EventsEndpoints.MaxBodyBytes + 1), Encoding.UTF8, "application/json");
        using (var r = await agent.PostAsync("/api/events", content))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Equal(0, _db.CountAndroidEvents("PHONE"));
    }

    [Fact]
    public async Task Read_requires_recordings_role_and_viewer_group_scope()
    {
        using var agent = await Agent();
        using (var r = await agent.PostAsJsonAsync("/api/events", new { events = new[] { Event() } }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var anon = _factory.CreateClient();
        using (var r = await anon.GetAsync("/api/inventory/PHONE/events"))
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        _db.InsertUser("operator", UserAuth.HashPassword("test-password-123"), "operator");
        using var op = Login("operator");
        using (var r = await op.GetAsync("/api/inventory/PHONE/events"))
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        _db.InsertUser("viewer", UserAuth.HashPassword("test-password-123"), "viewer");
        using var viewer = Login("viewer");
        using (var r = await viewer.GetAsync("/api/inventory/PHONE/events"))
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        long group = _db.CreateGroup("phones");
        _db.SetHostGroup(_db.Inventory().Single(h => h.Name == "PHONE").Id!.Value, group);
        _db.SetUserGroups(_db.UserByUsername("viewer")!.Id, [group]);
        using (var r = await viewer.GetAsync("/api/inventory/PHONE/events?limit=5000&offset=-1"))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var page = await Json(r);
            Assert.Equal(1000, page.GetProperty("limit").GetInt32());
            Assert.Equal(0, page.GetProperty("offset").GetInt32());
        }
    }

    [Fact]
    public async Task Inventory_and_traffic_detail_remain_opaque_and_offset_timestamp_is_normalized()
    {
        using var agent = await Agent();
        object[] events = [
            new { eventId = Guid.NewGuid().ToString("D"), ts = "2026-10-22T15:00:00+03:00", type = "app_inventory", detail = new { packages = new[] { new { package = "com.example.app", versionName = "1", versionCode = 1, installer = "com.android.vending", system = false } }, part = 0, parts = 1 } },
            new { eventId = Guid.NewGuid().ToString("D"), ts = "2026-10-22T12:00:00Z", type = "app_traffic", package = "com.example.app", detail = new { rxBytes = 12, txBytes = 3, intervalMs = 1000, networkType = "wifi" } }
        ];
        using var r = await agent.PostAsJsonAsync("/api/events", new { events });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var rows = _db.ListAndroidEvents("PHONE", 10, 0);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc), row.Ts));
        Assert.Contains("com.android.vending", rows.Single(row => row.Type == "app_inventory").Detail);
        Assert.Contains("wifi", rows.Single(row => row.Type == "app_traffic").Detail);
    }

    [Fact]
    public async Task Revoked_credential_cannot_write_events()
    {
        using var agent = await Agent();
        long id = _db.ListAgentCredentials().Single().Id;
        using (var r = await _admin.PostAsync($"/api/agents/credentials/{id}/revoke", null))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await agent.PostAsJsonAsync("/api/events", new { events = new[] { Event() } }))
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal(0, _db.CountAndroidEvents("PHONE"));
    }

    [Fact]
    public async Task Rename_keeps_history_and_retry_key_delete_cleans_events_and_blocks_agent()
    {
        using var agent = await Agent();
        object[] events = [Event()];
        using (var r = await agent.PostAsJsonAsync("/api/events", new { events }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        long id = _db.Inventory().Single(h => h.Name == "PHONE").Id!.Value;
        Assert.Equal(RenameOutcome.Renamed, _db.RenameHost(id, "RENAMED"));
        using (var r = await agent.PostAsJsonAsync("/api/events", new { host = "PHONE", events }))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal(0, (await Json(r)).GetProperty("inserted").GetInt32());
        }
        Assert.Equal(0, _db.CountAndroidEvents("PHONE"));
        Assert.Equal(1, _db.CountAndroidEvents("RENAMED"));
        using (var r = await _admin.DeleteAsync($"/api/fleet/{id}")) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(0, _db.CountAndroidEvents("RENAMED"));
        using (var r = await agent.PostAsJsonAsync("/api/events", new { events })) Assert.Equal(HttpStatusCode.Gone, r.StatusCode);
    }

    [Fact]
    public async Task Summary_aggregates_day_and_inventory_flags_blind_agent()
    {
        using var agent = await Agent();
        // Пульс — чтобы у хоста была платформа android (Db.SetHostUpdateInfo).
        using (var r = await agent.PostAsJsonAsync("/api/heartbeat", new { host = "PHONE", version = "1.0", platform = "android" }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        object Ev(string ts, string type, string? package = null, object? detail = null) =>
            new { eventId = Guid.NewGuid().ToString("D"), ts, type, package, detail };
        object[] events =
        [
            Ev("2026-10-21T23:00:00Z", "device_state", detail: new { osVersion = "13", usageAccess = true }),
            Ev("2026-10-22T10:00:00+03:00", "app_foreground", "com.game"),
            Ev("2026-10-22T07:30:00Z", "app_background", "com.game"),
            Ev("2026-10-22T08:00:00Z", "app_installed", "com.game", new { installer = "com.google.android.packageinstaller" }),
            Ev("2026-10-22T09:00:00Z", "app_traffic", "com.game", new { rxBytes = 1000, txBytes = 24, intervalMs = 60000, networkType = "wifi" }),
            Ev("2026-10-22T09:30:00Z", "app_inventory", detail: new { snapshotId = "s1", part = 0, parts = 1, packages = new[] { new { package = "com.game", versionCode = 1, system = false } } }),
            Ev("2026-10-22T11:00:00Z", "device_state", detail: new { osVersion = "13", usageAccess = false }),
            Ev("2026-10-23T01:00:00Z", "app_foreground", "com.next.day"),
        ];
        using (var r = await agent.PostAsJsonAsync("/api/events", new { events }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        using (var r = await _admin.GetAsync("/api/inventory/PHONE/android-summary?from=2026-10-22T00:00:00Z&to=2026-10-23T00:00:00Z"))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var s = await Json(r);
            var app = Assert.Single(s.GetProperty("apps").EnumerateArray());
            Assert.Equal("com.game", app.GetProperty("package").GetString());
            Assert.Equal(30 * 60_000, app.GetProperty("foregroundMs").GetInt64());
            Assert.Equal("com.google.android.packageinstaller", s.GetProperty("changes")[0].GetProperty("installer").GetString());
            Assert.Equal(1024, s.GetProperty("traffic")[0].GetProperty("wifiBytes").GetInt64());
            Assert.False(s.GetProperty("deviceState").GetProperty("usageAccess").GetBoolean());
            Assert.Equal(1, s.GetProperty("inventory").GetProperty("total").GetInt32());
        }
        // Состояние — последнее на конец периода, даже если оно было до его начала.
        using (var r = await _admin.GetAsync("/api/inventory/PHONE/android-summary?from=2026-10-22T00:00:00Z&to=2026-10-22T01:00:00Z"))
            Assert.True((await Json(r)).GetProperty("deviceState").GetProperty("usageAccess").GetBoolean());
        using (var r = await _admin.GetAsync("/api/inventory/PHONE/android-summary?from=2026-10-22T00:00:00Z&to=2026-10-30T00:00:00Z"))
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using (var r = await _admin.GetAsync("/api/inventory/PHONE/android-summary"))
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        _db.InsertUser("operator", UserAuth.HashPassword("test-password-123"), "operator");
        using (var op = Login("operator"))
        using (var r = await op.GetAsync("/api/inventory/PHONE/android-summary?from=2026-10-22T00:00:00Z&to=2026-10-23T00:00:00Z"))
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

        using (var r = await _admin.GetAsync("/api/inventory/PHONE"))
        {
            var h = (await Json(r)).GetProperty("host");
            Assert.Equal("android", h.GetProperty("platform").GetString());
            Assert.False(h.GetProperty("androidUsageAccess").GetBoolean());
            Assert.StartsWith("2026-10-23T01:00:00", h.GetProperty("androidLastEventAt").GetString());
        }
    }

    [Fact]
    public async Task Network_and_security_log_accepted_and_summarized()
    {
        using var agent = await Agent();
        object Ev(string type, object detail) =>
            new { eventId = Guid.NewGuid().ToString("D"), ts = "2026-10-22T10:00:00Z", type, detail };
        object[] events =
        [
            Ev("net_dns", new { domain = "a.example", package = "com.browser", count = 4 }),
            Ev("net_connect", new { ip = "1.2.3.4", port = 443, package = "com.browser", count = 2 }),
            Ev("security", new { tag = 210002, name = "adb_shell_command", data = "ls" }),
        ];
        using (var r = await agent.PostAsJsonAsync("/api/events", new { events }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        // net/security с непустым package отвергаются (пакет — в detail).
        using (var r = await agent.PostAsJsonAsync("/api/events",
            new { events = new[] { new { eventId = Guid.NewGuid().ToString("D"), ts = "2026-10-22T10:00:00Z", type = "net_dns", package = "com.x", detail = new { domain = "a" } } } }))
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);

        using var s = await _admin.GetAsync("/api/inventory/PHONE/android-summary?from=2026-10-22T00:00:00Z&to=2026-10-23T00:00:00Z");
        var j = await Json(s);
        var net = j.GetProperty("network");
        Assert.Equal(2, net.GetArrayLength());
        Assert.Contains(net.EnumerateArray(), n => n.GetProperty("target").GetString() == "a.example" && n.GetProperty("count").GetInt32() == 4);
        Assert.Contains(net.EnumerateArray(), n => n.GetProperty("target").GetString() == "1.2.3.4:443");
        var sec = j.GetProperty("security");
        Assert.Equal("adb_shell_command", sec[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Owner_role_reported_and_cleared_on_command()
    {
        using var agent = await Agent();
        async Task<JsonElement> Beat(bool owner)
        {
            using var r = await agent.PostAsJsonAsync("/api/heartbeat",
                new { host = "PHONE", version = "1.0", platform = "android", ownerActive = owner });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return await Json(r);
        }
        // Владелец сообщает роль — обычный ответ без команды снять.
        Assert.False((await Beat(true)).GetProperty("clearOwner").GetBoolean());
        long id = _db.Inventory().Single(h => h.Name == "PHONE").Id!.Value;
        using (var r = await _admin.GetAsync("/api/inventory/PHONE"))
            Assert.True((await Json(r)).GetProperty("host").GetProperty("androidOwnerActive").GetBoolean());

        // Админ просит снять роль → следующий heartbeat владельца получает команду.
        using (var r = await _admin.PostAsync($"/api/fleet/{id}/android/clear-owner", null))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await _admin.GetAsync("/api/inventory/PHONE"))
            Assert.True((await Json(r)).GetProperty("host").GetProperty("androidClearOwnerRequested").GetBoolean());
        Assert.True((await Beat(true)).GetProperty("clearOwner").GetBoolean());

        // Агент снял роль (ownerActive=false) → команда исполнена, флаги сброшены.
        Assert.False((await Beat(false)).GetProperty("clearOwner").GetBoolean());
        using (var r = await _admin.GetAsync("/api/inventory/PHONE"))
        {
            var h = (await Json(r)).GetProperty("host");
            Assert.False(h.GetProperty("androidOwnerActive").GetBoolean());
            Assert.False(h.GetProperty("androidClearOwnerRequested").GetBoolean());
        }

        // Снять роль с не-владельца нечего — 404.
        using (var r = await _admin.PostAsync($"/api/fleet/{id}/android/clear-owner", null))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Enroll_returns_update_public_key()
    {
        using var t = await _admin.PostAsJsonAsync("/api/agents/enroll-tokens", new { name = "pk-" + Guid.NewGuid().ToString("N"), ttlHours = 1 });
        using var token = JsonDocument.Parse(await t.Content.ReadAsStringAsync());
        using var c = _factory.CreateClient();
        using var e = await c.PostAsJsonAsync("/api/enroll", new { token = token.RootElement.GetProperty("token").GetString(), host = "PK-PHONE", kind = "android" });
        Assert.Equal(HttpStatusCode.OK, e.StatusCode);
        var j = await Json(e);
        Assert.Equal(TestPublicKey, j.GetProperty("updatePublicKey").GetString());
    }

    [Fact]
    public async Task Lock_command_flows_via_heartbeat()
    {
        using var agent = await Agent();
        async Task<JsonElement> Beat(bool locked)
        {
            using var r = await agent.PostAsJsonAsync("/api/heartbeat",
                new { host = "PHONE", version = "1.0", platform = "android", locked });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return await Json(r);
        }
        await Beat(false);   // стать android-хостом
        long id = _db.Inventory().Single(h => h.Name == "PHONE").Id!.Value;

        // Админ (PhoneManagement) просит заблокировать → heartbeat несёт lockDevice=true.
        using (var r = await _admin.PostAsJsonAsync($"/api/fleet/{id}/android/lock", new { locked = true }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await _admin.GetAsync("/api/inventory/PHONE"))
            Assert.True((await Json(r)).GetProperty("host").GetProperty("androidLockRequested").GetBoolean());
        Assert.True((await Beat(false)).GetProperty("lockDevice").GetBoolean());

        // Агент сообщил, что заблокировался.
        await Beat(true);
        using (var r = await _admin.GetAsync("/api/inventory/PHONE"))
            Assert.True((await Json(r)).GetProperty("host").GetProperty("androidLocked").GetBoolean());

        // Снятие.
        using (var r = await _admin.PostAsJsonAsync($"/api/fleet/{id}/android/lock", new { locked = false }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False((await Beat(true)).GetProperty("lockDevice").GetBoolean());
    }

    [Fact]
    public async Task Offline_admin_unlock_sends_local_admin_creds_and_clears_lock()
    {
        using var agent = await Agent();
        async Task<JsonElement> Beat(bool adminUnlocked = false)
        {
            using var r = await agent.PostAsJsonAsync("/api/heartbeat",
                new { host = "PHONE", version = "1.0", platform = "android", adminUnlocked });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return await Json(r);
        }

        // Android-хост получает в heartbeat логины/хэши локальных админов — для
        // офлайн-разблокировки по учётке без сети.
        var first = await Beat();
        var creds = first.GetProperty("adminCredentials").EnumerateArray().ToList();
        Assert.Contains(creds, c => c.GetProperty("u").GetString() == "admin");
        // Хэш — формат ASP.NET PasswordHasher (base64, начинается с маркера v3 'AQAAAA').
        Assert.All(creds, c => Assert.StartsWith("AQAAAA", c.GetProperty("h").GetString()));

        long id = _db.Inventory().Single(h => h.Name == "PHONE").Id!.Value;
        using (var r = await _admin.PostAsJsonAsync($"/api/fleet/{id}/android/lock", new { locked = true }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True((await Beat()).GetProperty("lockDevice").GetBoolean());

        // Агент сообщает adminUnlocked=true (админ снял локально) → сервер снимает
        // запрос блокировки, следующий heartbeat уже без lockDevice.
        Assert.False((await Beat(adminUnlocked: true)).GetProperty("lockDevice").GetBoolean());
        using (var r = await _admin.GetAsync("/api/inventory/PHONE"))
            Assert.False((await Json(r)).GetProperty("host").GetProperty("androidLockRequested").GetBoolean());
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string key in new[] { "DataDir", "DbPath", "AdminUser", "AdminPassword", "CookieSecure", "ApiToken", "UpdatePublicKey" })
            Environment.SetEnvironmentVariable("Server__" + key, null);
        try { Directory.Delete(_dir, true); } catch { }
    }
}
