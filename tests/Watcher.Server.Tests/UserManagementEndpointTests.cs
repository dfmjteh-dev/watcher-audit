using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Watcher.Server.Tests;

// Этап C2 (roadmap-next.md): #/settings/users — до этой задачи единственным
// способом завести operator/viewer был CLI --create-user (Этап C1). Эти тесты
// проверяют веб-эндпоинты: AdminOnly policy, lockout-guard на последнем
// включённом admin'е, самообслуживание (/api/me/password) и немедленную
// отмену сессии при disabled (AuthGate, не только /api/login).
public class UserManagementEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;

    public UserManagementEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-usermgmt-{Guid.NewGuid():N}");
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

    private HttpClient NonAdminClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<HttpClient> LoginAsAsync(string username, string role, string password = "password-123456")
    {
        using var createResp = await _admin.PostAsJsonAsync("/api/users", new { username, role, password });
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var client = NonAdminClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        using var loginResp = await client.PostAsJsonAsync("/api/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
        return client;
    }

    [Fact]
    public async Task Admin_can_create_list_and_update_user()
    {
        using var createResp = await _admin.PostAsJsonAsync("/api/users", new { username = "op1", role = "operator", password = "operator-pass-123" });
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        long id = created.GetProperty("id").GetInt64();

        using var listResp = await _admin.GetAsync("/api/users");
        var list = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(list.EnumerateArray(), u => u.GetProperty("username").GetString() == "op1" && u.GetProperty("role").GetString() == "operator");

        using var patchResp = await _admin.PatchAsJsonAsync($"/api/users/{id}", new { role = "viewer" });
        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);

        using var listResp2 = await _admin.GetAsync("/api/users");
        var list2 = await listResp2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(list2.EnumerateArray(), u => u.GetProperty("username").GetString() == "op1" && u.GetProperty("role").GetString() == "viewer");
    }

    [Fact]
    public async Task Create_user_with_unknown_role_is_rejected()
    {
        using var resp = await _admin.PostAsJsonAsync("/api/users", new { username = "x", role = "superadmin", password = "whatever-pass-123" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_user_without_password_generates_one()
    {
        using var resp = await _admin.PostAsJsonAsync("/api/users", new { username = "gen1", role = "viewer", password = (string?)null });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("generatedPassword").GetString()!.Length >= 12);
    }

    [Fact]
    public async Task Non_admin_cannot_manage_users()
    {
        using var op = await LoginAsAsync("op2", "operator");
        using var viewer = await LoginAsAsync("v1", "viewer");

        using var opResp = await op.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.Forbidden, opResp.StatusCode);

        using var viewerResp = await viewer.PostAsJsonAsync("/api/users", new { username = "sneaky", role = "admin", password = "sneaky-pass-123456" }); // gitleaks:allow — тестовый пароль
        Assert.Equal(HttpStatusCode.Forbidden, viewerResp.StatusCode);
    }

    [Fact]
    public async Task Cannot_demote_or_disable_the_last_enabled_admin()
    {
        using var listResp = await _admin.GetAsync("/api/users");
        var list = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        long adminId = list.EnumerateArray().First(u => u.GetProperty("role").GetString() == "admin").GetProperty("id").GetInt64();

        using var demoteResp = await _admin.PatchAsJsonAsync($"/api/users/{adminId}", new { role = "operator" });
        Assert.Equal(HttpStatusCode.BadRequest, demoteResp.StatusCode);

        using var disableResp = await _admin.PatchAsJsonAsync($"/api/users/{adminId}", new { disabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, disableResp.StatusCode);
    }

    [Fact]
    public async Task Can_demote_admin_when_another_enabled_admin_remains()
    {
        using var createResp = await _admin.PostAsJsonAsync("/api/users", new { username = "admin2", role = "admin", password = "admin2-pass-123456" });
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        long secondAdminId = created.GetProperty("id").GetInt64();

        using var demoteResp = await _admin.PatchAsJsonAsync($"/api/users/{secondAdminId}", new { role = "operator" });
        Assert.Equal(HttpStatusCode.OK, demoteResp.StatusCode);
    }

    [Fact]
    public async Task Reset_password_without_body_password_generates_one_and_old_password_stops_working()
    {
        using var viewer = await LoginAsAsync("v2", "viewer", "original-pass-123456");
        using var listResp = await _admin.GetAsync("/api/users");
        var list = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        long id = list.EnumerateArray().First(u => u.GetProperty("username").GetString() == "v2").GetProperty("id").GetInt64();

        using var resetResp = await _admin.PostAsJsonAsync($"/api/users/{id}/reset-password", new { password = (string?)null });
        Assert.Equal(HttpStatusCode.OK, resetResp.StatusCode);
        var body = await resetResp.Content.ReadFromJsonAsync<JsonElement>();
        string newPassword = body.GetProperty("generatedPassword").GetString()!;

        using var oldLogin = await NonAdminClient().PostAsJsonAsync("/api/login", new { username = "v2", password = "original-pass-123456" });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        using var newLogin = await NonAdminClient().PostAsJsonAsync("/api/login", new { username = "v2", password = newPassword });
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Fact]
    public async Task User_can_change_own_password_but_not_with_wrong_current_password()
    {
        using var viewer = await LoginAsAsync("v3", "viewer", "old-pass-123456");

        using var wrongResp = await viewer.PostAsJsonAsync("/api/me/password", new { currentPassword = "nope", newPassword = "new-pass-123456" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongResp.StatusCode);

        using var okResp = await viewer.PostAsJsonAsync("/api/me/password", new { currentPassword = "old-pass-123456", newPassword = "new-pass-123456" });
        Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);

        using var oldLogin = await NonAdminClient().PostAsJsonAsync("/api/login", new { username = "v3", password = "old-pass-123456" });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        using var newLogin = await NonAdminClient().PostAsJsonAsync("/api/login", new { username = "v3", password = "new-pass-123456" });
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Fact]
    public async Task Own_password_change_rejects_too_short_new_password()
    {
        using var viewer = await LoginAsAsync("v4", "viewer", "old-pass-123456");

        using var resp = await viewer.PostAsJsonAsync("/api/me/password", new { currentPassword = "old-pass-123456", newPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Disabling_a_user_immediately_revokes_their_existing_session()
    {
        using var viewer = await LoginAsAsync("v5", "viewer");
        using var beforeResp = await viewer.GetAsync("/api/segments");
        Assert.Equal(HttpStatusCode.OK, beforeResp.StatusCode);

        using var listResp = await _admin.GetAsync("/api/users");
        var list = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        long id = list.EnumerateArray().First(u => u.GetProperty("username").GetString() == "v5").GetProperty("id").GetInt64();
        using var disableResp = await _admin.PatchAsJsonAsync($"/api/users/{id}", new { disabled = true });
        Assert.Equal(HttpStatusCode.OK, disableResp.StatusCode);

        // Та же cookie, тот же HttpClient — сессия не истекла (7 дней), но
        // AuthGate теперь должен перепроверять disabled на каждый запрос.
        using var afterResp = await viewer.GetAsync("/api/segments");
        Assert.Equal(HttpStatusCode.Unauthorized, afterResp.StatusCode);
    }

    [Fact]
    public async Task Admin_can_assign_and_read_back_user_groups()
    {
        using var groupResp = await _admin.PostAsJsonAsync("/api/groups", new { name = "um-group" });
        var group = await groupResp.Content.ReadFromJsonAsync<JsonElement>();
        long groupId = group.GetProperty("id").GetInt64();

        using var createResp = await _admin.PostAsJsonAsync("/api/users", new { username = "v6", role = "viewer", password = "v6-pass-123456" });
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        long userId = created.GetProperty("id").GetInt64();

        using var putResp = await _admin.PutAsJsonAsync($"/api/users/{userId}/groups", new { groupIds = new[] { groupId } });
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        using var getResp = await _admin.GetAsync($"/api/users/{userId}/groups");
        var ids = await getResp.Content.ReadFromJsonAsync<long[]>();
        Assert.Equal(new[] { groupId }, ids);
    }
}
