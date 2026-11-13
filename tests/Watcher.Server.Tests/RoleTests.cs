using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Watcher.Server.Tests;

// Этап C1 (roadmap-next.md): role пишется в claim с Фазы 1, но до этого эндпоинты
// её не проверяли — любой залогиненный был полным админом. Эти тесты закрывают
// ровно это: Management (группы/парк) и Recordings (видео/консоль) policy, плюс
// опциональное ограничение viewer'а списком групп через user_groups.
public class RoleTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Db _db;
    private readonly HttpClient _admin;

    public RoleTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-roles-{Guid.NewGuid():N}");
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

    [Fact]
    public async Task Operator_can_manage_groups_but_not_view_recordings()
    {
        using var op = await LoginAsAsync("op1", "operator");

        using var createResp = await op.PostAsJsonAsync("/api/groups", new { name = "op-group" });
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

        using var fleetResp = await op.GetAsync("/api/fleet");
        Assert.Equal(HttpStatusCode.OK, fleetResp.StatusCode);

        using var segmentsResp = await op.GetAsync("/api/segments");
        Assert.Equal(HttpStatusCode.Forbidden, segmentsResp.StatusCode);

        using var terminalResp = await op.GetAsync("/api/terminal-sessions");
        Assert.Equal(HttpStatusCode.Forbidden, terminalResp.StatusCode);
    }

    [Fact]
    public async Task Viewer_can_view_recordings_but_not_manage_groups()
    {
        using var viewer = await LoginAsAsync("v1", "viewer");

        using var segmentsResp = await viewer.GetAsync("/api/segments");
        Assert.Equal(HttpStatusCode.OK, segmentsResp.StatusCode);

        using var terminalResp = await viewer.GetAsync("/api/terminal-sessions");
        Assert.Equal(HttpStatusCode.OK, terminalResp.StatusCode);

        using var createGroupResp = await viewer.PostAsJsonAsync("/api/groups", new { name = "viewer-cant" });
        Assert.Equal(HttpStatusCode.Forbidden, createGroupResp.StatusCode);

        using var fleetResp = await viewer.GetAsync("/api/fleet");
        Assert.Equal(HttpStatusCode.Forbidden, fleetResp.StatusCode);
    }

    [Fact]
    public async Task Any_authenticated_role_can_list_group_names()
    {
        using var op = await LoginAsAsync("op2", "operator");
        using var viewer = await LoginAsAsync("v2", "viewer");

        using var opResp = await op.GetAsync("/api/groups");
        Assert.Equal(HttpStatusCode.OK, opResp.StatusCode);

        using var viewerResp = await viewer.GetAsync("/api/groups");
        Assert.Equal(HttpStatusCode.OK, viewerResp.StatusCode);
    }

    [Fact]
    public async Task Admin_is_unrestricted_by_groups()
    {
        long groupA = _db.CreateGroup("group-a");
        _db.SetHostGroup(CreateHost("host-a", groupA), groupA);
        InsertSegment("host-a");
        InsertSegment("host-b-no-group");

        using var resp = await _admin.GetAsync("/api/segments");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetArrayLength());
    }

    [Fact]
    // 0.14.4: без групп — ничего (раньше — всё: новый пользователь из AD получал
    // доступ ко всем записям).
    public async Task Viewer_without_assigned_groups_sees_nothing()
    {
        long g = _db.CreateGroup("nogroups-g");
        CreateHost("host-x", g);
        InsertSegment("host-x");
        InsertSegment("host-y");
        using var viewer = await LoginAsAsync("v3", "viewer");

        foreach (var url in new[] { "/api/segments", "/api/inventory", "/api/terminal-sessions" })
        {
            using var resp = await viewer.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(0, (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
        }
        long id = _db.Query("host-x", null, null, false, 10).Single().Id;
        using var video = await viewer.GetAsync($"/api/segments/{id}/meta");
        Assert.Equal(HttpStatusCode.Forbidden, video.StatusCode);
        var overview = await (await viewer.GetAsync("/api/overview")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, overview.GetProperty("hosts").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Viewer_restricted_to_group_only_sees_its_hosts()
    {
        long groupA = _db.CreateGroup("restricted-a");
        long groupB = _db.CreateGroup("restricted-b");
        CreateHost("restricted-host-a", groupA);
        CreateHost("restricted-host-b", groupB);
        InsertSegment("restricted-host-a");
        InsertSegment("restricted-host-b");

        using var viewer = await LoginAsAsync("v4", "viewer");
        var user = _db.UserByUsername("v4")!;
        _db.SetUserGroups(user.Id, new[] { groupA });

        using var resp = await viewer.GetAsync("/api/segments");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetArrayLength());
        Assert.Equal("restricted-host-a", body[0].GetProperty("host").GetString());
    }

    [Fact]
    public async Task Viewer_restricted_to_group_gets_403_on_direct_segment_id_of_other_group()
    {
        long groupA = _db.CreateGroup("direct-a");
        long groupB = _db.CreateGroup("direct-b");
        CreateHost("direct-host-a", groupA);
        CreateHost("direct-host-b", groupB);
        long segId = InsertSegment("direct-host-b");

        using var viewer = await LoginAsAsync("v5", "viewer");
        var user = _db.UserByUsername("v5")!;
        _db.SetUserGroups(user.Id, new[] { groupA });

        using var metaResp = await viewer.GetAsync($"/api/segments/{segId}/meta");
        Assert.Equal(HttpStatusCode.Forbidden, metaResp.StatusCode);
    }

    private long CreateHost(string name, long groupId)
    {
        _db.UpsertHeartbeat(name, "10.0.0.1", "1.0.0");
        _db.SetHostGroup(_db.ListFleetHosts(null, 100, 0).First(h => h.Name == name).Id, groupId);
        return groupId;
    }

    private long InsertSegment(string host) => _db.Insert(new Segment(
        Id: 0, Host: host, Filename: $"{host}-{Guid.NewGuid():N}.mp4",
        StartedAt: DateTime.UtcNow.AddMinutes(-5), EndedAt: DateTime.UtcNow,
        DurationSec: 300, Frames: 10, MotionEvents: 0, StartReason: "scheduled",
        SizeBytes: 1024, Sha256: "deadbeef", FilePath: "blobs/x.mp4",
        UploadedAt: DateTime.UtcNow, SessionUser: null));
}
