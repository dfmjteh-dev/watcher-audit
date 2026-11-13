using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Watcher.Server.Tests;

public class ConfigEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _anon;
    private readonly HttpClient _authed;

    public ConfigEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-config-api-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

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

    private async Task<long> CreateGroupAsync(string name)
    {
        using var resp = await _authed.PostAsJsonAsync("/api/groups", new { name });
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt64();
    }

    // 0.13 (п.7): «Скрыть похожие» в «Консоли» — правило в конфиг группы хоста.
    [Fact]
    public async Task Service_rule_from_a_session_goes_to_the_group_and_hides_similar_sessions()
    {
        var db = _factory.Services.GetRequiredService<Db>();
        long groupId = await CreateGroupAsync("net");
        db.EnsureHostExists("nm-1");
        long hostId = db.Inventory(onlyName: "nm-1").Single().Id!.Value;
        db.SetHostGroup(hostId, groupId);
        long Session(int n, string input) => db.InsertTerminalSession(new TerminalSession(
            0, "nm-1", "root", "terminal-input", "10.20.30.40", DateTime.UtcNow, DateTime.UtcNow, 1, null, input,
            null, null, 0, null, $"nm-1:in:{n}", DateTime.UtcNow, Source: "linux-exec"), input);
        long id = Session(1, "09:00:00Z /root$ lldpctl -f json\n");
        Session(2, "09:01:00Z /root$ lldpctl -f json\n");
        Session(3, "09:02:00Z /root$ cat /etc/shadow\n");

        using var resp = await _authed.PostAsJsonAsync($"/api/terminal-sessions/{id}/service-rule",
            new { scope = "group", user = "root", from = "10.20.30.40", command = "lldpctl", note = "LLDP" });
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("group", body.GetProperty("scope").GetString());
        Assert.Equal(2, body.GetProperty("marked").GetInt32());
        Assert.Contains("\"command\":\"lldpctl\"", db.GetGroupConfig(groupId)!.ConfigJson);

        using var list = await _authed.GetAsync("/api/terminal-sessions?host=nm-1&sessions=true&service=hide");
        var rows = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("shadow", Assert.Single(rows.EnumerateArray()).GetProperty("row").GetProperty("input").GetString());

        // Свой список у хоста перекрывает групповой — в группу добавлять бесполезно.
        db.SetHostConfig(hostId, "{\"serviceConnections\":[]}");
        using var conflict = await _authed.PostAsJsonAsync($"/api/terminal-sessions/{id}/service-rule", new { scope = "group", user = "root" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var bad = await _authed.PostAsJsonAsync($"/api/terminal-sessions/{id}/service-rule", new { scope = "host", command = "lldpctl" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Group_config_without_cookie_returns_401()
    {
        using var resp = await _anon.GetAsync("/api/groups/1/config");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Group_config_get_unset_returns_null_version()
    {
        long groupId = await CreateGroupAsync("lobby");

        using var resp = await _authed.GetAsync($"/api/groups/{groupId}/config");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("configVersion").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("config").ValueKind);
    }

    [Fact]
    public async Task Group_config_put_then_get_roundtrips()
    {
        long groupId = await CreateGroupAsync("lobby");

        using var put = await _authed.PutAsJsonAsync($"/api/groups/{groupId}/config",
            new { config = new { schemaVersion = 1, crf = 23 } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var get = await _authed.GetAsync($"/api/groups/{groupId}/config");
        var body = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("configVersion").GetInt32());
        Assert.Equal(23, body.GetProperty("config").GetProperty("crf").GetInt32());
    }

    [Fact]
    public async Task Group_config_put_non_object_returns_400()
    {
        long groupId = await CreateGroupAsync("lobby");

        using var resp = await _authed.PutAsJsonAsync($"/api/groups/{groupId}/config", new { config = "not-an-object" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // R4: preset склеивался в командную строку ffmpeg на всех станциях группы.
    [Fact]
    public async Task Group_config_put_with_injected_preset_returns_400_and_is_not_stored()
    {
        long groupId = await CreateGroupAsync("lobby");

        using var resp = await _authed.PutAsJsonAsync($"/api/groups/{groupId}/config",
            new { config = new { preset = "ultrafast -y C:\\Users\\Public\\x.mp4" } });
        using var get = await _authed.GetAsync($"/api/groups/{groupId}/config");
        var body = await get.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("config").ValueKind);
    }

    [Fact]
    public async Task Host_config_put_out_of_range_returns_400()
    {
        await _anon.PostAsJsonAsync("/api/heartbeat", new { host = "station-1", version = "0.1.0" });
        var fleet = await (await _authed.GetAsync("/api/fleet")).Content.ReadFromJsonAsync<JsonElement>();
        long hostId = fleet.GetProperty("items")[0].GetProperty("id").GetInt64();

        using var resp = await _authed.PutAsJsonAsync($"/api/fleet/{hostId}/config", new { config = new { captureIntervalMs = 0 } });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Group_config_put_unknown_group_returns_404()
    {
        using var resp = await _authed.PutAsJsonAsync("/api/groups/999/config", new { config = new { crf = 1 } });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Group_config_delete_clears_it()
    {
        long groupId = await CreateGroupAsync("lobby");
        await _authed.PutAsJsonAsync($"/api/groups/{groupId}/config", new { config = new { crf = 23 } });

        using var del = await _authed.DeleteAsync($"/api/groups/{groupId}/config");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        using var get = await _authed.GetAsync($"/api/groups/{groupId}/config");
        var body = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("config").ValueKind);
    }

    [Fact]
    public async Task Fleet_config_put_then_get_roundtrips()
    {
        await _anon.PostAsJsonAsync("/api/heartbeat", new { host = "station-1", version = "0.1.0" });
        var fleet = await (await _authed.GetAsync("/api/fleet")).Content.ReadFromJsonAsync<JsonElement>();
        long hostId = fleet.GetProperty("items")[0].GetProperty("id").GetInt64();

        using var put = await _authed.PutAsJsonAsync($"/api/fleet/{hostId}/config", new { config = new { crf = 18 } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var get = await _authed.GetAsync($"/api/fleet/{hostId}/config");
        var body = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(18, body.GetProperty("config").GetProperty("crf").GetInt32());
    }

    [Fact]
    public async Task Heartbeat_returns_empty_config_when_nothing_configured()
    {
        // {} а не null: агент перезаписывает кэш только объектом — иначе
        // очищенный конфиг группы так и оставался бы на станции.
        using var resp = await _anon.PostAsJsonAsync("/api/heartbeat", new { host = "station-x", version = "0.1.0" });
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, body.GetProperty("configVersion").GetInt32());
        Assert.Equal(JsonValueKind.Object, body.GetProperty("config").ValueKind);
        Assert.Empty(body.GetProperty("config").EnumerateObject());
    }

    [Fact]
    public async Task Heartbeat_returns_group_config_for_host_in_that_group()
    {
        long groupId = await CreateGroupAsync("lobby");
        await _authed.PutAsJsonAsync($"/api/groups/{groupId}/config", new { config = new { crf = 30 } });

        await _anon.PostAsJsonAsync("/api/heartbeat", new { host = "station-1", version = "0.1.0" });
        var fleet = await (await _authed.GetAsync("/api/fleet")).Content.ReadFromJsonAsync<JsonElement>();
        long hostId = fleet.GetProperty("items")[0].GetProperty("id").GetInt64();
        await _authed.PatchAsync($"/api/fleet/{hostId}", JsonContent.Create(new { groupId }));

        using var hb2 = await _anon.PostAsJsonAsync("/api/heartbeat", new { host = "station-1", version = "0.1.0" });
        var body = await hb2.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, body.GetProperty("configVersion").GetInt32());
        Assert.Equal(30, body.GetProperty("config").GetProperty("crf").GetInt32());
    }
}
