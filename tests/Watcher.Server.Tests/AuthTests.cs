using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

public class AuthTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;

    public AuthTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        // Те же оговорки про тайминг env vars, что в UploadEndpointTests: биндинг
        // ServerConfig происходит в top-level statements Program.cs, раньше любого
        // хука WithWebHostBuilder — поэтому конфиг только через переменные окружения.
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        // A2 (roadmap-next.md): Cookie.SecurePolicy=Always по умолчанию — тесты ходят по http (WebApplicationFactory), без этого cookie не возвращался бы клиентом.
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        // B2 (roadmap-next.md): пустой ApiToken без явного opt-in теперь останавливает сервер.
        Environment.SetEnvironmentVariable("Server__AllowAnonymousAgents", "true");

        _factory = new WebApplicationFactory<Program>();
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var key in new[] { "Server__DataDir", "Server__DbPath", "Server__AdminUser", "Server__AdminPassword", "Server__CookieSecure", "Server__AllowAnonymousAgents" })
            Environment.SetEnvironmentVariable(key, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private HttpClient NoRedirectClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Api_without_cookie_returns_401()
    {
        using var client = NoRedirectClient();

        using var resp = await client.GetAsync("/api/segments");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Page_without_cookie_redirects_to_login()
    {
        using var client = NoRedirectClient();

        using var resp = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/login.html", resp.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Login_with_legacy_AdminUser_AdminPassword_succeeds_via_migration()
    {
        using var client = NoRedirectClient();

        using var resp = await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_401()
    {
        using var client = NoRedirectClient();

        using var resp = await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Login_with_unknown_username_returns_401()
    {
        using var client = NoRedirectClient();

        using var resp = await client.PostAsJsonAsync("/api/login", new { username = "nobody", password = "whatever" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task After_login_cookie_grants_access_to_api_and_logout_revokes_it()
    {
        // WebApplicationFactory client по умолчанию держит CookieContainer между
        // запросами одного HttpClient — именно поэтому один client используется
        // для login → api → logout → api.
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        // A2 (roadmap-next.md): CSRF-гейт требует этот заголовок на мутирующих
        // /api/* для залогиненной cookie-сессии — ниже дёргаем /api/logout.
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");

        using var login = await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var hosts = await client.GetAsync("/api/segments");
        Assert.Equal(HttpStatusCode.OK, hosts.StatusCode);

        using var me = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        // 0.14.4: зона сервера — в ней UI показывает время.
        var meJson = System.Text.Json.JsonDocument.Parse(await me.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(ServerClock.ZoneId, meJson.GetProperty("timeZone").GetString());
        Assert.False(string.IsNullOrEmpty(ServerClock.ZoneId));

        using var logout = await client.PostAsync("/api/logout", null);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        using var hostsAfterLogout = await client.GetAsync("/api/segments");
        Assert.Equal(HttpStatusCode.Unauthorized, hostsAfterLogout.StatusCode);
    }

    [Fact]
    public async Task Upload_endpoint_does_not_require_login_cookie()
    {
        using var client = NoRedirectClient();
        var content = new MultipartFormDataContent
        {
            { new ByteArrayContent("fake mp4 bytes"u8.ToArray()), "video", "noauth.mp4" },
            { new StringContent("{}"), "meta" },
        };

        using var resp = await client.PostAsync("/api/upload", content);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // A3 (roadmap-next.md): /api/login было можно перебирать без ограничений —
    // лимит по имени пользователя (LoginThrottle, Endpoints/AuthEndpoints.cs).
    [Fact]
    public async Task Repeated_failed_logins_for_same_user_get_rate_limited()
    {
        using var client = NoRedirectClient();
        HttpResponseMessage? last = null;
        for (int i = 0; i < 11; i++)
        {
            last?.Dispose();
            last = await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "wrong" });
        }

        Assert.Equal((HttpStatusCode)429, last!.StatusCode);
        last.Dispose();
    }

    // A2 (roadmap-next.md): мутирующий /api/* под cookie-сессией без
    // X-Requested-With — запрос, который сторонний сайт мог бы послать через
    // обычную форму (без JS, без кастомных заголовков).
    [Fact]
    public async Task Mutating_api_request_without_csrf_header_is_rejected()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var login = await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var resp = await client.PostAsJsonAsync("/api/groups", new { name = "no-csrf-header" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // A2 (roadmap-next.md): Secure/SameSite=Strict по умолчанию (Server__CookieSecure
    // не задан — отдельно от общего фикстурного "false" в конструкторе).
    [Fact]
    public async Task Login_cookie_has_secure_and_samesite_strict_by_default()
    {
        Environment.SetEnvironmentVariable("Server__CookieSecure", null);
        using var factory = new WebApplicationFactory<Program>();
        // HandleCookies=false — не нужен автоповтор cookie на следующий запрос,
        // проверяем только сырой заголовок логина.
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        using var login = await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // Kestrel пишет атрибуты cookie в нижнем регистре (secure; samesite=strict;
        // httonly) — не "Secure"/"SameSite" с большой буквы.
        string setCookie = string.Join(" ", login.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
        Assert.Contains("secure", setCookie);
        Assert.Contains("samesite=strict", setCookie);
        Assert.Contains("httponly", setCookie);

        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
    }
}
