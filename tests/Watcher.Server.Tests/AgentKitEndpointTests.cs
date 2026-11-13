using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Watcher.Server.Tests;

// Страница «Установка агентов»: манифест комплекта, скачивание только файлов
// из него и только admin, каждое скачивание — в журнал доступа.
public class AgentKitEndpointTests : IDisposable
{
    private static readonly string[] EnvKeys =
    {
        "Server__DataDir", "Server__DbPath", "Server__AdminUser", "Server__AdminPassword",
        "Server__CookieSecure", "Server__AgentKitDir",
    };

    private readonly string _dataDir;
    private readonly string _kitDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;
    private readonly Db _db;

    public AgentKitEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-kit-{Guid.NewGuid():N}");
        _kitDir = Path.Combine(_dataDir, "kit");
        Directory.CreateDirectory(Path.Combine(_kitDir, "windows"));
        Directory.CreateDirectory(Path.Combine(_kitDir, "updates"));
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        Environment.SetEnvironmentVariable("Server__AgentKitDir", _kitDir);
        _factory = new WebApplicationFactory<Program>();
        _admin = Login("admin", "test-password-123");
        _db = new Db(Path.Combine(_dataDir, "watcher.db"));
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var key in EnvKeys) Environment.SetEnvironmentVariable(key, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private HttpClient Login(string user, string password)
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        c.PostAsJsonAsync("/api/login", new { username = user, password }).GetAwaiter().GetResult().Dispose();
        return c;
    }

    private void WriteKit(string? tokenHash = null)
    {
        File.WriteAllText(Path.Combine(_kitDir, "windows", "watcher-agent.msi"), "msi");
        File.WriteAllText(Path.Combine(_kitDir, "updates", "packages.tsv"), "secret-ish");
        File.WriteAllText(Path.Combine(_kitDir, AgentKit.ManifestName), JsonSerializer.Serialize(new
        {
            version = "0.11.0",
            serverUrl = "https://watcher.test",
            builtAt = "2026-10-12T10:00:00Z",
            updateKey = "MFkwKEY",
            enrollTokenSha256 = tokenHash,
            files = new object[]
            {
                new { path = "windows/watcher-agent.msi", size = 3, sha256 = "abc" },
                new { path = "../watcher.db", size = 1, sha256 = "x" },
            },
        }));
    }

    [Fact]
    public async Task No_kit_yet_is_reported_not_an_error()
    {
        var r = await _admin.GetFromJsonAsync<JsonElement>("/api/agent-kit");
        Assert.True(r.GetProperty("configured").GetBoolean());
        Assert.False(r.GetProperty("available").GetBoolean());
        using var f = await _admin.GetAsync("/api/agent-kit/files/windows/watcher-agent.msi");
        Assert.Equal(HttpStatusCode.NotFound, f.StatusCode);
    }

    [Fact]
    public async Task Manifest_shows_token_state_and_download_is_audited()
    {
        string token = AgentAuth.NewEnrollToken();
        _db.InsertEnrollToken("install-kit", AgentAuth.Hash(token), null, DateTime.UtcNow.AddDays(30), null, "cli");
        WriteKit(AgentAuth.Hash(token));

        var r = await _admin.GetFromJsonAsync<JsonElement>("/api/agent-kit");
        Assert.True(r.GetProperty("available").GetBoolean());
        Assert.Equal("https://watcher.test", r.GetProperty("serverUrl").GetString());
        Assert.Equal("install-kit", r.GetProperty("token").GetProperty("name").GetString());
        Assert.False(r.GetProperty("token").GetProperty("revoked").GetBoolean());
        Assert.DoesNotContain(token, r.GetRawText());

        using var f = await _admin.GetAsync("/api/agent-kit/files/windows/watcher-agent.msi");
        Assert.Equal(HttpStatusCode.OK, f.StatusCode);
        Assert.Equal("msi", await f.Content.ReadAsStringAsync());
        Assert.Equal("watcher-agent.msi", f.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Contains(_db.QueryAudit("admin", "agent-kit.download", null, null, 10, 0),
            e => e.Target == "agent-kit:windows/watcher-agent.msi");
    }

    [Theory]
    [InlineData("updates/packages.tsv")]   // есть в каталоге, нет в манифесте
    [InlineData("kit.json")]
    [InlineData("../watcher.db")]          // в манифесте, но вне каталога
    [InlineData("..%2Fwatcher.db")]
    public async Task Only_manifest_files_inside_kit_are_served(string path)
    {
        WriteKit();
        using var f = await _admin.GetAsync("/api/agent-kit/files/" + path);
        Assert.Equal(HttpStatusCode.NotFound, f.StatusCode);
    }

    // 0.15.0: архив Android = инструменты из комплекта + последний APK из «Обновлений».
    [Fact]
    public async Task Android_zip_bundles_kit_tools_with_latest_apk()
    {
        WriteKit();
        Directory.CreateDirectory(Path.Combine(_kitDir, "android"));
        string tools = Path.Combine(_kitDir, AgentKitEndpoints.AndroidToolsPath);
        using (var z = System.IO.Compression.ZipFile.Open(tools, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(z.CreateEntry("watcher-android/install.cmd").Open());
            w.Write("@echo off");
        }
        var m = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(_kitDir, AgentKit.ManifestName)))!;
        var files = m["files"].Deserialize<List<object>>()!;
        files.Add(new { path = AgentKitEndpoints.AndroidToolsPath, size = 1, sha256 = "x" });
        File.WriteAllText(Path.Combine(_kitDir, AgentKit.ManifestName), JsonSerializer.Serialize(new
        {
            version = "0.11.0",
            serverUrl = "https://watcher.test",
            builtAt = "2026-10-12T10:00:00Z",
            files,
        }));

        using (var none = await _admin.GetAsync("/api/agent-kit/android.zip"))
            Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);   // APK ещё не загружен

        foreach (var (ver, body) in new[] { ("0.14.9", "old"), ("0.14.10", "new") })
        {
            string rel = $"updates/android-{ver}.apk";
            Directory.CreateDirectory(Path.Combine(_dataDir, "updates"));
            File.WriteAllText(Path.Combine(_dataDir, rel), body);
            _db.InsertAgentPackage(new AgentPackage(0, "android", ver, rel, "sha", body.Length, "sig", "admin", DateTime.UtcNow));
        }
        var info = await _admin.GetFromJsonAsync<JsonElement>("/api/agent-kit");
        Assert.True(info.GetProperty("android").GetProperty("tools").GetBoolean());
        Assert.Equal("0.14.10", info.GetProperty("android").GetProperty("apkVersion").GetString());

        using var f = await _admin.GetAsync("/api/agent-kit/android.zip");
        Assert.Equal(HttpStatusCode.OK, f.StatusCode);
        using var zip = new System.IO.Compression.ZipArchive(await f.Content.ReadAsStreamAsync());
        Assert.NotNull(zip.GetEntry("watcher-android/install.cmd"));
        using var apk = new StreamReader(zip.GetEntry("watcher-android/watcher-agent.apk")!.Open());
        Assert.Equal("new", apk.ReadToEnd());
        Assert.Contains(_db.QueryAudit("admin", "agent-kit.download", null, null, 10, 0), e => e.Target == "agent-kit:android");
    }

    [Fact]
    public async Task Viewer_cannot_see_or_download_kit()
    {
        WriteKit();
        _db.InsertUser("viewer1", UserAuth.HashPassword("viewer-password-1"), "viewer");
        using var viewer = Login("viewer1", "viewer-password-1");
        using var m = await viewer.GetAsync("/api/agent-kit");
        using var f = await viewer.GetAsync("/api/agent-kit/files/windows/watcher-agent.msi");
        Assert.Equal(HttpStatusCode.Forbidden, m.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, f.StatusCode);
    }
}
