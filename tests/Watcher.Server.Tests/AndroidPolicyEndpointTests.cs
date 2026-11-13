using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Watcher.Server.Tests;

public class AndroidPolicyEndpointTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"watcher-pol-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;
    private readonly Db _db;

    public AndroidPolicyEndpointTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("Server__DataDir", _dir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        _factory = new WebApplicationFactory<Program>();
        _db = _factory.Services.GetRequiredService<Db>();
        _admin = Login("admin");
    }

    private HttpClient Login(string user)
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        using var r = c.PostAsJsonAsync("/api/login", new { username = user, password = "test-password-123" }).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return c;
    }

    private long Host(string name)
    {
        _db.UpsertHeartbeat(name, null, "1.0");
        return _db.Inventory().Single(h => h.Name == name).Id!.Value;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return d.RootElement.Clone();
    }

    [Fact]
    public async Task SetGetPolicyMergesWithoutClobberingStationConfig()
    {
        long id = Host("PHONE");
        // Станционный конфиг хоста уже задан (как у обычного конфига).
        using (var r = await _admin.PutAsJsonAsync($"/api/fleet/{id}/config", new { config = new { outputFps = 4 } }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        // Ставим политику отдельным эндпоинтом.
        using (var r = await _admin.PutAsJsonAsync($"/api/fleet/{id}/android-policy",
            new { policy = new { suspendedPackages = new[] { "com.foo.game" }, restrictions = new[] { "no_factory_reset" } } }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        // android-policy GET вернул политику.
        using (var r = await _admin.GetAsync($"/api/fleet/{id}/android-policy"))
        {
            var pol = (await Json(r)).GetProperty("policy");
            Assert.Equal("com.foo.game", pol.GetProperty("suspendedPackages")[0].GetString());
        }
        // Станционный конфиг не затёрт — outputFps на месте рядом с androidPolicy.
        using (var r = await _admin.GetAsync($"/api/fleet/{id}/config"))
        {
            var cfg = (await Json(r)).GetProperty("config");
            Assert.Equal(4, cfg.GetProperty("outputFps").GetInt32());
            Assert.True(cfg.TryGetProperty("androidPolicy", out _));
        }
        // Пустая политика снимает ключ, станционный конфиг остаётся.
        using (var r = await _admin.PutAsJsonAsync($"/api/fleet/{id}/android-policy", new { policy = new { } }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await _admin.GetAsync($"/api/fleet/{id}/config"))
        {
            var cfg = (await Json(r)).GetProperty("config");
            Assert.False(cfg.TryGetProperty("androidPolicy", out _));
            Assert.Equal(4, cfg.GetProperty("outputFps").GetInt32());
        }
    }

    [Fact]
    public async Task ScreenRecordOnlyPolicyIsNotDroppedAsEmpty()
    {
        long id = Host("PHONE-SR");
        // Политика ТОЛЬКО с screenRecord (без списков-массивов): не должна считаться
        // пустой и выбрасываться из конфига (регресс: IsEmpty смотрел лишь на массивы).
        using (var r = await _admin.PutAsJsonAsync($"/api/fleet/{id}/android-policy",
            new { policy = new { screenRecord = new { mode = "device" } } }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await _admin.GetAsync($"/api/fleet/{id}/config"))
        {
            var cfg = (await Json(r)).GetProperty("config");
            Assert.True(cfg.TryGetProperty("androidPolicy", out var ap), "androidPolicy должен сохраниться");
            Assert.Equal("device", ap.GetProperty("screenRecord").GetProperty("mode").GetString());
        }
        // perApp со списком приложений — тоже сохраняется целиком.
        using (var r = await _admin.PutAsJsonAsync($"/api/fleet/{id}/android-policy",
            new { policy = new { screenRecord = new { mode = "perApp", apps = new[] { "com.foo.app" } } } }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await _admin.GetAsync($"/api/fleet/{id}/android-policy"))
        {
            var sr = (await Json(r)).GetProperty("policy").GetProperty("screenRecord");
            Assert.Equal("perApp", sr.GetProperty("mode").GetString());
            Assert.Equal("com.foo.app", sr.GetProperty("apps")[0].GetString());
        }
        // mode=off — всё та же «пустая» политика, androidPolicy снимается.
        using (var r = await _admin.PutAsJsonAsync($"/api/fleet/{id}/android-policy",
            new { policy = new { screenRecord = new { mode = "off" } } }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await _admin.GetAsync($"/api/fleet/{id}/config"))
            Assert.False((await Json(r)).GetProperty("config").TryGetProperty("androidPolicy", out _));
    }

    [Fact]
    public async Task RejectsProtectedPackage()
    {
        long id = Host("PHONE2");
        using var r = await _admin.PutAsJsonAsync($"/api/fleet/{id}/android-policy",
            new { policy = new { suspendedPackages = new[] { "com.android.settings" } } });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task RightGatesEditing()
    {
        long id = Host("PHONE3");
        _db.InsertUser("op", UserAuth.HashPassword("test-password-123"), "operator");
        using var op = Login("op");
        // operator без права — не может редактировать политику (PhoneManagement).
        using (var r = await op.PutAsJsonAsync($"/api/fleet/{id}/android-policy", new { policy = new { suspendedPackages = new[] { "com.foo" } } }))
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        // admin выдаёт право.
        long opId = _db.UserByUsername("op")!.Id;
        using (var r = await _admin.PatchAsJsonAsync($"/api/users/{opId}", new { canManagePhones = true }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await op.PutAsJsonAsync($"/api/fleet/{id}/android-policy", new { policy = new { suspendedPackages = new[] { "com.foo" } } }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var k in new[] { "DataDir", "DbPath", "AdminUser", "AdminPassword", "CookieSecure" })
            Environment.SetEnvironmentVariable("Server__" + k, null);
        try { Directory.Delete(_dir, true); } catch { }
    }
}
