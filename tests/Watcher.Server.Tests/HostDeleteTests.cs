using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Watcher.Server.Tests;

// 0.13 (п.10, п.14, п.15): удаление хоста с записями и самоудаление агента;
// удаление отозванных учёток; «Снять с записи».
public class HostDeleteTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;
    private readonly Db _db;

    public HostDeleteTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-hdel-{Guid.NewGuid():N}");
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

    private async Task<string> Secret(string host, string kind = "video")
    {
        using var t = await _admin.PostAsJsonAsync("/api/agents/enroll-tokens", new { name = "t-" + Guid.NewGuid().ToString("N")[..6], ttlHours = 24 });
        string token = JsonDocument.Parse(await t.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        using var anon = _factory.CreateClient();
        using var e = await anon.PostAsJsonAsync("/api/enroll", new { token, host, kind });
        Assert.Equal(HttpStatusCode.OK, e.StatusCode);
        return JsonDocument.Parse(await e.Content.ReadAsStringAsync()).RootElement.GetProperty("secret").GetString()!;
    }

    private HttpClient Agent(string secret)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return c;
    }

    private static MultipartFormDataContent Video(string file)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent("video"u8.ToArray()), "video", file);
        content.Add(new StringContent("{}"), "meta", file + ".json");
        return content;
    }

    private long HostId(string name) => _db.Inventory().Single(h => h.Name == name).Id!.Value;

    [Fact]
    public async Task Admin_deletes_host_with_recordings_and_agent_is_told_to_uninstall()
    {
        string secret = await Secret("GONE-1");
        using var agent = Agent(secret);
        using (var hb = await agent.PostAsJsonAsync("/api/heartbeat", new { host = "GONE-1" })) Assert.Equal(HttpStatusCode.OK, hb.StatusCode);
        using (var up = await agent.PostAsync("/api/upload", Video("GONE-1_20261013_100000.mp4"))) Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        string blob = Path.Combine(_dataDir, _db.Query("GONE-1", null, null, false, 10).Single().FilePath);
        Assert.True(File.Exists(blob));
        long id = HostId("GONE-1");

        _db.InsertUser("op-del", UserAuth.HashPassword("password-123456"), "operator");
        using var op = Login("op-del", "password-123456");
        using (var denied = await op.DeleteAsync($"/api/fleet/{id}")) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using (var del = await _admin.DeleteAsync($"/api/fleet/{id}")) Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.DoesNotContain(_db.Inventory(), h => h.Name == "GONE-1");
        Assert.False(File.Exists(blob));
        Assert.Single(_db.QueryAudit(null, "host.delete", null, null, 10, 0));

        // Агент на связи (или вернувшийся) — 410 «деинсталлируйся», хост не возрождается.
        for (int i = 0; i < 2; i++)
        {
            using var hb = await agent.PostAsJsonAsync("/api/heartbeat", new { host = "GONE-1" });
            Assert.Equal(HttpStatusCode.Gone, hb.StatusCode);
            Assert.True(hb.Headers.Contains(AgentAccess.UninstallHeader));
        }
        using (var up = await agent.PostAsync("/api/upload", Video("GONE-1_20261013_110000.mp4")))
            Assert.Equal(HttpStatusCode.Gone, up.StatusCode);
        Assert.DoesNotContain(_db.Inventory(), h => h.Name == "GONE-1");
        Assert.Single(_db.QueryAudit(null, "agent.uninstall-sent", null, null, 10, 0));

        // Переустановка на той же машине — новая учётка, всё работает.
        using var fresh = Agent(await Secret("GONE-1"));
        using (var hb = await fresh.PostAsJsonAsync("/api/heartbeat", new { host = "GONE-1" })) Assert.Equal(HttpStatusCode.OK, hb.StatusCode);
    }

    [Fact]
    public async Task Legal_hold_blocks_host_deletion()
    {
        using var agent = Agent(await Secret("HOLD-1"));
        (await agent.PostAsJsonAsync("/api/heartbeat", new { host = "HOLD-1" })).Dispose();
        _db.CreateLegalHold("HOLD-1", null, null, "расследование", "admin");
        using var del = await _admin.DeleteAsync($"/api/fleet/{HostId("HOLD-1")}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
        Assert.Contains(_db.Inventory(), h => h.Name == "HOLD-1");
    }

    [Fact]
    public async Task Only_revoked_credentials_can_be_deleted()
    {
        await Secret("CRED-1");
        long credId = _db.ListAgentCredentials().Single(a => a.HostName == "CRED-1").Id;
        using (var r = await _admin.DeleteAsync($"/api/agents/credentials/{credId}")) Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        (await _admin.PostAsync($"/api/agents/credentials/{credId}/revoke", null)).Dispose();
        using (var r = await _admin.DeleteAsync($"/api/agents/credentials/{credId}")) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.DoesNotContain(_db.ListAgentCredentials(), a => a.Id == credId);
    }

    [Fact]
    public async Task Paused_console_is_not_stored_and_pause_is_visible()
    {
        using var agent = Agent(await Secret("PAUSE-1", "terminal"));
        long id = HostId("PAUSE-1");
        using (var put = await _admin.PutAsJsonAsync($"/api/fleet/{id}/config",
            new { config = new { pauseVideo = true, pauseConsole = true, pauseReason = "отпуск" } }))
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var pause = _db.Inventory().Single(h => h.Name == "PAUSE-1").Pause;
        Assert.NotNull(pause);
        Assert.True(pause!.Video && pause.Console);
        Assert.Equal("отпуск", pause.Reason);

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("""{"kind":"terminal-input","host":"PAUSE-1","sessionUser":"root","startedAt":"2026-10-14T10:00:00Z","endedAt":"2026-10-14T10:00:05Z","input":"ls\n","sessionId":7}"""), "meta", "m.json");
        using var up = await agent.PostAsync("/api/upload-terminal", content);
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        Assert.Contains("paused", await up.Content.ReadAsStringAsync());
        Assert.Equal(0, _db.Inventory().Single(h => h.Name == "PAUSE-1").TerminalSessions);

        // Срок паузы прошёл — пишется снова.
        (await _admin.PutAsJsonAsync($"/api/fleet/{id}/config", new { config = new { pauseConsole = true, pauseUntil = "2020-01-01" } })).Dispose();
        Assert.Null(_db.Inventory().Single(h => h.Name == "PAUSE-1").Pause);

        using (var bad = await _admin.PutAsJsonAsync($"/api/fleet/{id}/config", new { config = new { pauseUntil = "завтра" } }))
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
