using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Watcher.Server.Tests;

public class FleetEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _anon;
    private readonly HttpClient _authed;

    public FleetEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-fleet-api-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        // Та же оговорка про тайминг env vars, что в UploadEndpointTests/AuthTests.
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        // A2 (roadmap-next.md): Cookie.SecurePolicy=Always по умолчанию — тесты ходят по http (WebApplicationFactory), без этого cookie не возвращался бы клиентом.
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        // B2 (roadmap-next.md): пустой ApiToken без явного opt-in теперь останавливает сервер.
        Environment.SetEnvironmentVariable("Server__AllowAnonymousAgents", "true");

        _factory = new WebApplicationFactory<Program>();
        _anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _authed = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        // A2 (roadmap-next.md): CSRF-гейт в AuthGate требует этот заголовок на мутирующих /api/*.
        _authed.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        _authed.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123" })
            .GetAwaiter().GetResult().Dispose();
    }

    public void Dispose()
    {
        _anon.Dispose();
        _authed.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var key in new[] { "Server__DataDir", "Server__DbPath", "Server__AdminUser", "Server__AdminPassword", "Server__CookieSecure", "Server__AllowAnonymousAgents" })
            Environment.SetEnvironmentVariable(key, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Heartbeat_does_not_require_login_cookie()
    {
        using var resp = await _anon.PostAsJsonAsync("/api/heartbeat", new { host = "station-1", ip = "10.0.0.5", version = "0.1.0" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_missing_host_returns_400()
    {
        using var resp = await _anon.PostAsJsonAsync("/api/heartbeat", new { host = "", version = "0.1.0" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_then_fleet_list_shows_the_host()
    {
        await _anon.PostAsJsonAsync("/api/heartbeat", new { host = "station-1", ip = "10.0.0.5", version = "0.1.0" });

        using var resp = await _authed.GetAsync("/api/fleet");
        var body = await resp.Content.ReadFromJsonAsync<FleetListResponse>();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, body!.Total);
        Assert.Equal("station-1", body.Items[0].Name);
    }

    [Fact]
    public async Task Heartbeat_with_dropped_segments_shows_up_in_fleet_list()
    {
        await _anon.PostAsJsonAsync("/api/heartbeat", new { host = "station-1", version = "0.1.0", droppedSegments = 4 });

        var body = await (await _authed.GetAsync("/api/fleet")).Content.ReadFromJsonAsync<FleetListResponse>();

        Assert.Equal(4, body!.Items[0].DroppedSegments);
    }

    // B1 (roadmap-next.md): #/fleet должен видеть, что агент ходит по голому
    // HTTP — не по самоотчёту агента, а по тому, что сервер реально увидел на
    // запросе (Request.IsHttps после ForwardedHeaders). WebApplicationFactory
    // всегда бьёт по http без доверенного X-Forwarded-Proto (сервер доверяет
    // только диапазону Docker-бриджей, тестовый хост в него не попадает) —
    // здесь закрыт именно этот путь, не ветка "агент реально по HTTPS".
    [Fact]
    public async Task Heartbeat_over_plain_http_is_flagged_in_fleet_list()
    {
        await _anon.PostAsJsonAsync("/api/heartbeat", new { host = "station-1", version = "0.1.0" });

        var body = await (await _authed.GetAsync("/api/fleet")).Content.ReadFromJsonAsync<FleetListResponse>();

        Assert.Equal(false, body!.Items[0].LastHeartbeatHttps);
    }

    [Fact]
    public async Task Heartbeat_without_dropped_segments_defaults_to_zero()
    {
        await _anon.PostAsJsonAsync("/api/heartbeat", new { host = "station-1", version = "0.1.0" });

        var body = await (await _authed.GetAsync("/api/fleet")).Content.ReadFromJsonAsync<FleetListResponse>();

        Assert.Equal(0, body!.Items[0].DroppedSegments);
    }

    [Fact]
    public async Task Fleet_list_without_cookie_returns_401()
    {
        using var resp = await _anon.GetAsync("/api/fleet");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Groups_crud_roundtrip()
    {
        using var create = await _authed.PostAsJsonAsync("/api/groups", new { name = "lobby" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<GroupDto>();

        using var list = await _authed.GetAsync("/api/groups");
        var groups = await list.Content.ReadFromJsonAsync<List<GroupDto>>();
        Assert.Contains(groups!, g => g.Name == "lobby");

        using var rename = await _authed.PutAsJsonAsync($"/api/groups/{created!.Id}", new { name = "lobby-renamed" });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);

        using var delete = await _authed.DeleteAsync($"/api/groups/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        using var listAfter = await _authed.GetAsync("/api/groups");
        var groupsAfter = await listAfter.Content.ReadFromJsonAsync<List<GroupDto>>();
        Assert.DoesNotContain(groupsAfter!, g => g.Id == created.Id);
    }

    [Fact]
    public async Task Groups_create_duplicate_name_returns_409()
    {
        await _authed.PostAsJsonAsync("/api/groups", new { name = "lobby" });

        using var resp = await _authed.PostAsJsonAsync("/api/groups", new { name = "lobby" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_patch_assigns_group()
    {
        await _anon.PostAsJsonAsync("/api/heartbeat", new { host = "station-1", version = "0.1.0" });
        using var createGroup = await _authed.PostAsJsonAsync("/api/groups", new { name = "lobby" });
        var group = await createGroup.Content.ReadFromJsonAsync<GroupDto>();
        var fleet = await (await _authed.GetAsync("/api/fleet")).Content.ReadFromJsonAsync<FleetListResponse>();
        long hostId = fleet!.Items[0].Id;

        using var patch = await _authed.PatchAsync($"/api/fleet/{hostId}",
            JsonContent.Create(new { groupId = group!.Id }));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var after = await (await _authed.GetAsync("/api/fleet")).Content.ReadFromJsonAsync<FleetListResponse>();
        Assert.Equal(group.Id, after!.Items[0].GroupId);
    }

    // Этап 2 (docs/android-plan.md): привязка «устройство ↔ сотрудник» — поле
    // person в /api/fleet, ставится и снимается тем же PATCH, что группа.
    [Fact]
    public async Task Fleet_patch_sets_and_clears_person()
    {
        await _anon.PostAsJsonAsync("/api/heartbeat", new { host = "Pixel-7", version = "0.14.4", platform = "android" });
        long hostId = (await (await _authed.GetAsync("/api/fleet")).Content.ReadFromJsonAsync<FleetListResponse>())!.Items[0].Id;

        using (var set = await _authed.PatchAsync($"/api/fleet/{hostId}", JsonContent.Create(new { person = "Иван Петров" })))
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("Иван Петров",
            (await (await _authed.GetAsync("/api/fleet")).Content.ReadFromJsonAsync<FleetListResponse>())!.Items[0].Person);

        using (var clear = await _authed.PatchAsync($"/api/fleet/{hostId}", JsonContent.Create(new { person = (string?)null })))
            Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Null((await (await _authed.GetAsync("/api/fleet")).Content.ReadFromJsonAsync<FleetListResponse>())!.Items[0].Person);
    }

    // Патч только person не трогает группу, и наоборот (частичное обновление).
    [Fact]
    public async Task Fleet_patch_person_only_keeps_group_and_validates()
    {
        await _anon.PostAsJsonAsync("/api/heartbeat", new { host = "Pixel-7", version = "0.14.4" });
        using var createGroup = await _authed.PostAsJsonAsync("/api/groups", new { name = "phones" });
        long groupId = (await createGroup.Content.ReadFromJsonAsync<GroupDto>())!.Id;
        long hostId = (await (await _authed.GetAsync("/api/fleet")).Content.ReadFromJsonAsync<FleetListResponse>())!.Items[0].Id;

        await _authed.PatchAsync($"/api/fleet/{hostId}", JsonContent.Create(new { groupId }));
        await _authed.PatchAsync($"/api/fleet/{hostId}", JsonContent.Create(new { person = "Иван" }));

        var item = (await (await _authed.GetAsync("/api/fleet")).Content.ReadFromJsonAsync<FleetListResponse>())!.Items[0];
        Assert.Equal(groupId, item.GroupId);
        Assert.Equal("Иван", item.Person);

        // Недопустимые символы в имени сотрудника — 400.
        using var bad = await _authed.PatchAsync($"/api/fleet/{hostId}", JsonContent.Create(new { person = "<script>" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        // Пустой патч — 400.
        using var empty = await _authed.PatchAsync($"/api/fleet/{hostId}", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task Groups_without_cookie_returns_401()
    {
        using var resp = await _anon.GetAsync("/api/groups");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    private sealed record GroupDto(long Id, string Name, int HostCount);
    private sealed record FleetItemDto(long Id, string Name, string? IpAddress, long? GroupId, string? GroupName, string? LastHeartbeatAt, string? AgentVersion, int DroppedSegments, bool? LastHeartbeatHttps, string? Person);
    private sealed record FleetListResponse(int Total, int Limit, int Offset, List<FleetItemDto> Items);
}
