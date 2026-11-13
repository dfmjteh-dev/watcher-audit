using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Watcher.Server.Tests;

// Этап E2 (roadmap-next.md): «Хосты» (/api/inventory) — одна таблица для всех
// ролей вместо прежних /api/hosts (Recordings, удалён в R13) + /api/fleet (Management); «Обзор»
// (/api/overview); массовое назначение группы; heartbeat-история (host_presence).
public class InventoryTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Db _db;
    private readonly HttpClient _admin;

    public InventoryTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-inventory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        Environment.SetEnvironmentVariable("Server__AllowAnonymousAgents", "true");

        _factory = new WebApplicationFactory<Program>();
        _admin = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _admin.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        _admin.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123" })
            .GetAwaiter().GetResult().Dispose();
        _db = new Db(Path.Combine(_dataDir, "watcher.db"));
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var key in new[] { "Server__DataDir", "Server__DbPath", "Server__AdminUser", "Server__AdminPassword", "Server__CookieSecure", "Server__AllowAnonymousAgents" })
            Environment.SetEnvironmentVariable(key, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private async Task<HttpClient> LoginAsAsync(string username, string role)
    {
        _db.InsertUser(username, UserAuth.HashPassword("password-123456"), role);
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        using var resp = await client.PostAsJsonAsync("/api/login", new { username, password = "password-123456" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return client;
    }

    private long Heartbeat(string name, string version = "0.8.0", int dropped = 0, long? groupId = null)
    {
        _db.UpsertHeartbeat(name, "10.0.0.1", version, dropped, isHttps: true);
        long id = _db.ListFleetHosts(null, 1000, 0).First(h => h.Name == name).Id;
        if (groupId is not null) _db.SetHostGroup(id, groupId);
        return id;
    }

    private void Segment(string host, DateTime startedAt, long size = 1000) => _db.Insert(new Segment(
        Id: 0, Host: host, Filename: $"{host}-{Guid.NewGuid():N}.mp4", StartedAt: startedAt, EndedAt: startedAt.AddMinutes(5),
        DurationSec: 300, Frames: 10, MotionEvents: 0, StartReason: "scheduled", SizeBytes: size, Sha256: "x",
        FilePath: "blobs/x.mp4", UploadedAt: DateTime.UtcNow));

    private static async Task<JsonElement> Json(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Inventory_merges_heartbeat_hosts_and_recording_only_hosts()
    {
        Heartbeat("pc-1");
        Segment("pc-1", DateTime.UtcNow.AddHours(-1), 500);
        Segment("pc-1", DateTime.UtcNow.AddDays(-3), 700);
        Segment("legacy-pc", DateTime.UtcNow.AddDays(-2));

        var list = (await Json(await _admin.GetAsync("/api/inventory"))).EnumerateArray().ToList();

        var pc1 = list.Single(h => h.GetProperty("name").GetString() == "pc-1");
        Assert.Equal(2, pc1.GetProperty("segments").GetInt32());
        Assert.Equal(1200, pc1.GetProperty("segmentBytes").GetInt64());
        Assert.Equal(1, pc1.GetProperty("segments24h").GetInt32());
        Assert.Equal("0.8.0", pc1.GetProperty("agentVersion").GetString());

        // Хост без heartbeat (только записи) не теряется, но без id — группу/конфиг не назначить.
        var legacy = list.Single(h => h.GetProperty("name").GetString() == "legacy-pc");
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("id").ValueKind);
        Assert.Equal(1, legacy.GetProperty("segments").GetInt32());
    }

    [Fact]
    public async Task Inventory_is_available_to_every_role_and_scoped_for_viewer()
    {
        long groupA = _db.CreateGroup("inv-a");
        long groupB = _db.CreateGroup("inv-b");
        Heartbeat("host-a", groupId: groupA);
        Heartbeat("host-b", groupId: groupB);

        using var op = await LoginAsAsync("op-inv", "operator");
        var opList = await Json(await op.GetAsync("/api/inventory"));
        Assert.Equal(2, opList.GetArrayLength());

        using var viewer = await LoginAsAsync("v-inv", "viewer");
        _db.SetUserGroups(_db.UserByUsername("v-inv")!.Id, new[] { groupA });
        var vList = await Json(await viewer.GetAsync("/api/inventory"));
        Assert.Equal("host-a", vList.EnumerateArray().Single().GetProperty("name").GetString());

        // Деталь чужого хоста — 403, и несуществующего тоже 403 (не 404): иначе
        // перебором можно узнать имена хостов чужих групп.
        using var other = await viewer.GetAsync("/api/inventory/host-b");
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        using var missing = await viewer.GetAsync("/api/inventory/no-such-host");
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        using var adminMissing = await _admin.GetAsync("/api/inventory/no-such-host");
        Assert.Equal(HttpStatusCode.NotFound, adminMissing.StatusCode);

        var overview = await Json(await viewer.GetAsync("/api/overview"));
        Assert.Equal(1, overview.GetProperty("hosts").GetProperty("total").GetInt32());
    }

    // 0.13 (PR9): загрузка сервера в обзоре и версия на странице входа (до входа).
    [Fact]
    public async Task Overview_has_server_load_block_and_login_page_gets_version()
    {
        var o = await Json(await _admin.GetAsync("/api/overview"));
        var server = o.GetProperty("server");
        Assert.Equal(Environment.ProcessorCount, server.GetProperty("cpuCount").GetInt32());
        Assert.Equal(15, server.GetProperty("intervalSec").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Array, server.GetProperty("history").ValueKind);

        using var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await Json(await anon.GetAsync("/api/login"));
        Assert.Equal(AppVersion.Core(AppVersion.Current), login.GetProperty("version").GetString());
        Assert.DoesNotContain("+", login.GetProperty("version").GetString());
    }

    // «Сегодня» на Обзоре и в «Хостах» — с полуночи смотрящего (since), не 24 часа.
    [Fact]
    public async Task Today_counters_start_at_since()
    {
        Heartbeat("pc-t");
        Segment("pc-t", DateTime.UtcNow.AddHours(-3));
        Segment("pc-t", DateTime.UtcNow.AddMinutes(-30));
        string since = Uri.EscapeDataString(DateTime.UtcNow.AddHours(-1).ToString("o"));

        var o = await Json(await _admin.GetAsync("/api/overview?since=" + since));
        Assert.Equal(1, o.GetProperty("last24h").GetProperty("segments").GetInt32());
        var inv = (await Json(await _admin.GetAsync("/api/inventory?since=" + since))).EnumerateArray().Single(h => h.GetProperty("name").GetString() == "pc-t");
        Assert.Equal(1, inv.GetProperty("segments24h").GetInt32());
        // Без since — последние 24 часа, как раньше.
        Assert.Equal(2, (await Json(await _admin.GetAsync("/api/overview"))).GetProperty("last24h").GetProperty("segments").GetInt32());
    }

    // 0.14.4: итоги по сегментам — из кэша (Db.HostTotals.cs): новые записи видны
    // сразу, удаление сбрасывает кэш, хост только с прошлыми записями не теряется.
    [Fact]
    public void Segment_totals_cache_sees_new_rows_and_deletes()
    {
        Heartbeat("pc-c");
        Segment("pc-c", DateTime.UtcNow.AddDays(-2), 100);
        Segment("old-only", DateTime.UtcNow.AddDays(-3), 50);
        Assert.Equal((1, 100L), Totals("pc-c"));

        Segment("pc-c", DateTime.UtcNow.AddMinutes(-1), 30);       // после кэширования
        Assert.Equal((2, 130L), Totals("pc-c"));
        var old = _db.Inventory().Single(h => h.Name == "old-only");
        Assert.Null(old.Id);
        Assert.Equal(1, old.Segments);
        Assert.Equal(new[] { "old-only", "pc-c" }, _db.Inventory().Select(h => h.Name).ToArray());
        Assert.Equal("old-only", _db.Inventory(onlyName: "old-only").Single().Name);

        long id = _db.Query("pc-c", null, null, false, 10).OrderBy(x => x.StartedAt).First().Id;
        _db.DeleteRecords("pc-c", [id], []);
        Assert.Equal((1, 30L), Totals("pc-c"));

        (int, long) Totals(string host) { var h = _db.Inventory().Single(x => x.Name == host); return (h.Segments, h.SegmentBytes); }
    }

    [Fact]
    public async Task Heartbeat_records_hourly_presence_once_per_hour()
    {
        string before = DateTime.UtcNow.ToString("yyyy-MM-ddTHH");
        Heartbeat("pc-p");
        Heartbeat("pc-p");
        Heartbeat("pc-p");
        string after = DateTime.UtcNow.ToString("yyyy-MM-ddTHH");

        var detail = await Json(await _admin.GetAsync("/api/inventory/pc-p"));
        var hours = detail.GetProperty("presenceHours").EnumerateArray().Select(h => h.GetString()).ToList();
        // Три heartbeat'а — одна строка (две, только если тест пересёк границу часа).
        Assert.Equal(new[] { before, after }.Distinct(), hours);
        Assert.Equal("pc-p", detail.GetProperty("host").GetProperty("name").GetString());
    }

    [Fact]
    public void Retention_drops_old_presence_rows()
    {
        Heartbeat("pc-r");
        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO host_presence (host, hour) VALUES ('pc-r', $h)";
            cmd.Parameters.AddWithValue("$h", DateTime.UtcNow.AddDays(-40).ToString("yyyy-MM-ddTHH"));
            cmd.ExecuteNonQuery();
        }
        new RetentionService(_db, new ServerConfig { DataDir = _dataDir }, NullLogger<RetentionService>.Instance).RunOnce();
        Assert.Single(_db.HostPresenceHours("pc-r", DateTime.UtcNow.AddDays(-60)));
    }

    [Fact]
    public async Task Overview_counts_states_problems_and_storage()
    {
        Heartbeat("fresh", version: AppVersion.Core(AppVersion.Current));
        Heartbeat("old-agent", version: "0.1.0", dropped: 3);
        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE hosts SET last_heartbeat_at = $t, last_heartbeat_https = 0 WHERE name = 'old-agent'";
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.AddHours(-2).ToString("o"));
            cmd.ExecuteNonQuery();
        }
        Segment("never-seen", DateTime.UtcNow.AddHours(-2), 7 * 1000);
        // Версии агента и сервера раздельные: «устарел» — агент не дошёл до версии,
        // назначенной группе, а не «старше сервера».
        long pilot = _db.CreateGroup("pilot");
        _db.SetGroupTargetVersion(pilot, "0.2.0");
        _db.SetHostGroup(_db.ListFleetHosts(null, 100, 0).Single(h => h.Name == "old-agent").Id, pilot);

        var o = await Json(await _admin.GetAsync("/api/overview"));
        var hosts = o.GetProperty("hosts");
        Assert.Equal(3, hosts.GetProperty("total").GetInt32());
        Assert.Equal(1, hosts.GetProperty("online").GetInt32());
        Assert.Equal(1, hosts.GetProperty("offline").GetInt32());
        Assert.Equal(1, hosts.GetProperty("neverSeen").GetInt32());
        Assert.Equal(1, o.GetProperty("last24h").GetProperty("segments").GetInt32());
        Assert.Equal("old-agent", o.GetProperty("withDrops").EnumerateArray().Single().GetProperty("name").GetString());
        var outdated = o.GetProperty("outdated").EnumerateArray().Single();
        Assert.Equal("old-agent", outdated.GetProperty("name").GetString());
        Assert.Equal("0.2.0", outdated.GetProperty("targetVersion").GetString());
        var inv = (await Json(await _admin.GetAsync("/api/inventory"))).EnumerateArray().ToList();
        Assert.False(inv.Single(h => h.GetProperty("name").GetString() == "fresh").GetProperty("agentOutdated").GetBoolean());
        Assert.True(inv.Single(h => h.GetProperty("name").GetString() == "old-agent").GetProperty("agentOutdated").GetBoolean());
        Assert.Equal("old-agent", o.GetProperty("plainHttp").EnumerateArray().Single().GetString());
        // Потери + старый агент + HTTP у одного хоста — один хост «требует внимания».
        Assert.Equal(1, o.GetProperty("problemHosts").GetInt32());

        var storage = o.GetProperty("storage");
        Assert.Equal(7000, storage.GetProperty("usedBytes").GetInt64());
        Assert.Equal(1000, storage.GetProperty("avgDailyBytes").GetInt64());
        Assert.Equal(30 * 1000, storage.GetProperty("forecastBytes").GetInt64());
        Assert.True(storage.GetProperty("diskFreeBytes").GetInt64() > 0);
    }

    [Fact]
    public async Task Bulk_assign_group_is_management_only_and_audited()
    {
        long group = _db.CreateGroup("bulk");
        long a = Heartbeat("bulk-a");
        long b = Heartbeat("bulk-b");

        using var viewer = await LoginAsAsync("v-bulk", "viewer");
        using var denied = await viewer.PostAsJsonAsync("/api/fleet/assign-group", new { hostIds = new[] { a, b }, groupId = group });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var op = await LoginAsAsync("op-bulk", "operator");
        var body = await Json(await op.PostAsJsonAsync("/api/fleet/assign-group", new { hostIds = new[] { a, b, 999999 }, groupId = group }));
        Assert.Equal(2, body.GetProperty("updated").GetInt32());
        Assert.All(_db.ListFleetHosts(null, 100, 0).Where(h => h.Name.StartsWith("bulk-")), h => Assert.Equal(group, h.GroupId));
        Assert.Equal(2, _db.CountAudit("op-bulk", "host.group", null, null));

        using var unknownGroup = await op.PostAsJsonAsync("/api/fleet/assign-group", new { hostIds = new[] { a }, groupId = 424242 });
        Assert.Equal(HttpStatusCode.BadRequest, unknownGroup.StatusCode);

        // groupId = null — снять группу.
        await Json(await op.PostAsJsonAsync("/api/fleet/assign-group", new { hostIds = new[] { a }, groupId = (long?)null }));
        Assert.Null(_db.ListFleetHosts(null, 100, 0).First(h => h.Id == a).GroupId);
    }
}
