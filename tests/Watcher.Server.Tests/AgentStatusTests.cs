using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Watcher.Server.Tests;

// 0.13 (п.5 и п.12 обратной связи 2026-10-14): дошёл ли конфиг до агента и
// записи экрана; журнал потерь с «Принято».
public class AgentStatusTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;
    private readonly Db _db;

    public AgentStatusTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        _factory = new WebApplicationFactory<Program>();
        _db = _factory.Services.GetRequiredService<Db>();
        _admin = Login("admin", "test-password-123");
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

    private HttpClient Login(string username, string password)
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        c.PostAsJsonAsync("/api/login", new { username, password }).GetAwaiter().GetResult().Dispose();
        return c;
    }

    private HttpClient UserClient(string username, string role)
    {
        _db.InsertUser(username, UserAuth.HashPassword("password-123456"), role);
        return Login(username, "password-123456");
    }

    private async Task<HttpClient> AgentFor(string host)
    {
        using var t = await _admin.PostAsJsonAsync("/api/agents/enroll-tokens", new { name = "t", ttlHours = 24 });
        string token = JsonDocument.Parse(await t.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        using var anon = _factory.CreateClient();
        using var e = await anon.PostAsJsonAsync("/api/enroll", new { token, host, kind = "video" });
        string secret = JsonDocument.Parse(await e.Content.ReadAsStringAsync()).RootElement.GetProperty("secret").GetString()!;
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return c;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<JsonElement> Inv(string host) => (await Json(await _admin.GetAsync("/api/inventory/" + host))).GetProperty("host");

    // 0.13 (PR9б): ресурсы агента — последний отчёт; мусор и отсутствие отчёта прежний не стирают.
    [Fact]
    public async Task Agent_resources_from_heartbeat_are_shown_in_inventory()
    {
        using var agent = await AgentFor("PC-R");
        await Json(await agent.PostAsJsonAsync("/api/heartbeat", new { host = "PC-R" }));
        Assert.Equal(JsonValueKind.Null, (await Inv("PC-R")).GetProperty("resources").ValueKind);

        await Json(await agent.PostAsJsonAsync("/api/heartbeat", new
        {
            host = "PC-R",
            resources = new { cpuPercent = 3.14159, memBytes = 200_000_000L, processes = 4, queueBytes = 5_000_000L, queueFiles = 2 },
        }));
        var r = (await Inv("PC-R")).GetProperty("resources");
        Assert.Equal(3.1, r.GetProperty("cpuPercent").GetDouble());
        Assert.Equal(200_000_000L, r.GetProperty("memBytes").GetInt64());
        Assert.Equal(4, r.GetProperty("processes").GetInt32());
        Assert.Equal(2, r.GetProperty("queueFiles").GetInt32());

        await Json(await agent.PostAsJsonAsync("/api/heartbeat", new { host = "PC-R", resources = new { cpuPercent = 500, memBytes = -1L, processes = 1, queueBytes = 0L, queueFiles = 0 } }));
        await Json(await agent.PostAsJsonAsync("/api/heartbeat", new { host = "PC-R" }));
        Assert.Equal(4, (await Inv("PC-R")).GetProperty("resources").GetProperty("processes").GetInt32());
        // В списке хостов — то же.
        var list = await Json(await _admin.GetAsync("/api/inventory"));
        Assert.Equal(200_000_000L, list.EnumerateArray().Single(h => h.GetProperty("name").GetString() == "PC-R").GetProperty("resources").GetProperty("memBytes").GetInt64());
    }

    [Fact]
    public async Task Config_state_goes_unknown_pending_ok_and_back_to_pending_after_save()
    {
        using var agent = await AgentFor("PC-1");
        // Агент до 0.13 — хэш не сообщает.
        var hb = await Json(await agent.PostAsJsonAsync("/api/heartbeat", new { host = "PC-1" }));
        string hash = hb.GetProperty("configHash").GetString()!;
        Assert.Equal("unknown", (await Inv("PC-1")).GetProperty("configState").GetString());

        // Получил текущий, запись экрана работает со старым — ждём.
        await Json(await agent.PostAsJsonAsync("/api/heartbeat", new
        {
            host = "PC-1",
            configHash = hash,
            captures = new[] { new { user = "ivan", configHash = "000000000000", recording = true, at = DateTime.UtcNow } },
        }));
        Assert.Equal("pending", (await Inv("PC-1")).GetProperty("configState").GetString());

        await Json(await agent.PostAsJsonAsync("/api/heartbeat", new
        {
            host = "PC-1",
            configHash = hash,
            captures = new[] { new { user = "ivan", configHash = hash, recording = true, at = DateTime.UtcNow } },
        }));
        Assert.Equal("ok", (await Inv("PC-1")).GetProperty("configState").GetString());

        // Сохранили конфиг хоста — агент на старом, пока не придёт следующий heartbeat.
        long id = (await Inv("PC-1")).GetProperty("id").GetInt64();
        using (var put = await _admin.PutAsJsonAsync($"/api/fleet/{id}/config", new { config = new { outputFps = 3 } }))
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("pending", (await Inv("PC-1")).GetProperty("configState").GetString());
        var st = await Json(await _admin.GetAsync($"/api/fleet/{id}/config-status"));
        Assert.False(st.GetProperty("delivered").GetProperty("current").GetBoolean());
        Assert.Equal("ivan", st.GetProperty("captures")[0].GetProperty("user").GetString());
        Assert.NotEqual(hash, st.GetProperty("expected").GetProperty("hash").GetString());
    }

    [Fact]
    public async Task Losses_are_journaled_deduplicated_and_acknowledged()
    {
        using var agent = await AgentFor("PC-2");
        var loss = new { file = "PC-2_ivan_20261013_113107.mp4", recordedAt = "2026-10-13T08:31:07Z", sizeBytes = 1234, reason = "quota", at = DateTime.UtcNow };
        var hb = await Json(await agent.PostAsJsonAsync("/api/heartbeat", new
        {
            host = "PC-2",
            droppedSegments = 1,
            losses = new object[] { loss, loss, new { file = "x.mp4", reason = "unknown-reason" } },
        }));
        Assert.Equal(3, hb.GetProperty("lossesAccepted").GetInt32());   // обработаны все, сохранено одно
        var h = await Inv("PC-2");
        Assert.True(h.GetProperty("lossJournal").GetBoolean());
        Assert.Equal(1, h.GetProperty("unackedLosses").GetInt32());

        var list = await Json(await _admin.GetAsync("/api/inventory/PC-2/losses"));
        Assert.Single(list.EnumerateArray());
        Assert.Equal("quota", list[0].GetProperty("reason").GetString());
        Assert.Equal(1234, list[0].GetProperty("sizeBytes").GetInt64());

        using var viewer = UserClient("v-loss", "viewer");
        using (var denied = await viewer.PostAsync("/api/inventory/PC-2/losses/acknowledge", null))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var op = UserClient("op-loss", "operator");
        var ack = await Json(await op.PostAsync("/api/inventory/PC-2/losses/acknowledge", null));
        Assert.Equal(1, ack.GetProperty("acknowledged").GetInt32());
        Assert.Equal(0, (await Inv("PC-2")).GetProperty("unackedLosses").GetInt32());
        Assert.Empty((await Json(await _admin.GetAsync("/api/inventory/PC-2/losses"))).EnumerateArray());
        Assert.Single((await Json(await _admin.GetAsync("/api/inventory/PC-2/losses?all=true"))).EnumerateArray());
        Assert.Contains(_db.QueryAudit(null, "host.losses-acknowledged", null, null, 10, 0), e => e.Username == "op-loss");

        // Обзор: хост с журналом — по непринятым (их 0), не по счётчику агента (1).
        var ov = await Json(await _admin.GetAsync("/api/overview"));
        Assert.DoesNotContain(ov.GetProperty("withDrops").EnumerateArray(), x => x.GetProperty("name").GetString() == "PC-2");
    }

    [Fact]
    public async Task Loss_details_are_validated_and_summarized_beyond_the_list_limit()
    {
        using var agent = await AgentFor("PC-D");
        await Json(await agent.PostAsJsonAsync("/api/heartbeat", new { host = "PC-D" }));
        var events = Enumerable.Range(0, 601).Select(i => new LossReport("t" + i + ".txt", null, 10,
            "transcript-no-4688", DateTime.UtcNow, i < 600 ? "ancestry-incomplete" : "event-missing")).ToList();
        Assert.Equal(601, _db.InsertLosses("PC-D", events));
        var list = await Json(await _admin.GetAsync("/api/inventory/PC-D/losses"));
        Assert.Equal(500, list.GetArrayLength());
        Assert.All(list.EnumerateArray(), row => Assert.NotNull(row.GetProperty("detail").GetString()));
        var summary = await Json(await _admin.GetAsync("/api/inventory/PC-D/losses/summary"));
        Assert.Equal(2, summary.GetArrayLength());
        Assert.Equal(600, summary[0].GetProperty("count").GetInt64());
        Assert.Equal("ancestry-incomplete", summary[0].GetProperty("detail").GetString());
        var hb = await Json(await agent.PostAsJsonAsync("/api/heartbeat", new
        {
            host = "PC-D",
            losses = new object[] {
                new { file = "bad.txt", reason = "transcript-no-4688", detail = "secret or arbitrary text" },
                new { file = "quota.mp4", reason = "quota", detail = "event-missing" },
            },
        }));
        Assert.Equal(2, hb.GetProperty("lossesAccepted").GetInt32());
        Assert.All(_db.ListLosses("PC-D", true).Where(l => l.File is "bad.txt" or "quota.mp4"), l => Assert.Null(l.Detail));
        using var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/inventory/PC-D/losses/summary")).StatusCode);
        using var viewer = UserClient("restricted-details", "viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/inventory/PC-D/losses/summary")).StatusCode);
        await Json(await _admin.PostAsync("/api/inventory/PC-D/losses/acknowledge", null));
        Assert.Empty((await Json(await _admin.GetAsync("/api/inventory/PC-D/losses/summary"))).EnumerateArray());
        Assert.Equal(4, (await Json(await _admin.GetAsync("/api/inventory/PC-D/losses/summary?all=true"))).GetArrayLength());
    }

    [Fact]
    public void Legacy_loss_can_receive_detail_without_duplicating_the_event()
    {
        _db.InsertLosses("h", [new("a.txt", null, 1, "transcript-no-4688", DateTime.UtcNow)]);
        _db.InsertLosses("h", [new("a.txt", null, 1, "transcript-no-4688", DateTime.UtcNow, "event-missing")]);
        Assert.Equal("event-missing", Assert.Single(_db.ListLosses("h", true)).Detail);
    }

    // 0.14.2: агент узнаёт предел загрузки из heartbeat и удаляет файлы больше него
    // (по умолчанию 512 МБ) и удаляет файлы больше него с причиной too-large —
    // сервер такую потерю принимает в журнал.
    [Fact]
    public async Task Heartbeat_reports_upload_limit_and_accepts_too_large_loss()
    {
        using var agent = await AgentFor("PC-4");
        var hb = await Json(await agent.PostAsJsonAsync("/api/heartbeat", new
        {
            host = "PC-4",
            losses = new object[] { new { file = "PC-4_20261019_090000.mp4", sizeBytes = 8_000_000_000L, reason = "too-large", at = DateTime.UtcNow } },
        }));
        Assert.Equal(512L * 1024 * 1024, hb.GetProperty("maxUploadBytes").GetInt64());
        var list = await Json(await _admin.GetAsync("/api/inventory/PC-4/losses"));
        Assert.Equal("too-large", list[0].GetProperty("reason").GetString());
    }

    [Fact]
    public void Alerts_fire_on_new_journal_losses_not_on_counter_of_journal_hosts()
    {
        var eval = new AlertEvaluator(new AlertConfig());
        var now = DateTime.UtcNow;
        InventoryHost Host(int dropped, bool journal) => new(1, "PC-3", null, null, null, now, "0.13.0", dropped, true,
            0, 0, null, 0, 0, 0, 0, null, 0, LossJournal: journal);
        AlertSnapshot Snap(InventoryHost h, IReadOnlyList<(string, int)>? losses = null) => new(now, [h], null, 0, true, losses);

        eval.Evaluate(Snap(Host(0, true)));
        var c1 = eval.Evaluate(Snap(Host(5, true)));
        Assert.DoesNotContain(c1.Events, a => a.Kind == "dropped");
        var c2 = eval.Evaluate(Snap(Host(5, true), [("PC-3", 2)]));
        Assert.Contains(c2.Events, a => a.Kind == "dropped" && a.Host == "PC-3" && a.Text.Contains('2'));
    }
}
