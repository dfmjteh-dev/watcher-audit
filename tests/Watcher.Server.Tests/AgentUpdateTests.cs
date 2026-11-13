using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Watcher.Server.Tests;

// H2 (roadmap-next.md): обновление агентов с сервера — пакеты с подписью,
// целевая версия группы, предложение в heartbeat, скачивание агентом.
public class AgentUpdateTests : IDisposable
{
    private static readonly string[] EnvKeys =
    {
        "Server__DataDir", "Server__DbPath", "Server__AdminUser", "Server__AdminPassword",
        "Server__CookieSecure", "Server__AllowAnonymousAgents", "Server__UpdatePublicKey",
    };

    private readonly string _dataDir;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _agent = null!;
    private Db _db = null!;

    public AgentUpdateTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-upd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Start(Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()));
    }

    private void Start(string publicKey)
    {
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        Environment.SetEnvironmentVariable("Server__AllowAnonymousAgents", "true");
        Environment.SetEnvironmentVariable("Server__UpdatePublicKey", publicKey);
        _factory = new WebApplicationFactory<Program>();
        _admin = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _admin.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        _admin.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123" }).GetAwaiter().GetResult().Dispose();
        _agent = _factory.CreateClient();
        _db = new Db(Path.Combine(_dataDir, "watcher.db"));
    }

    // Сервер с другими настройками — те же данные, новые клиенты.
    private void Restart(string publicKey)
    {
        _admin.Dispose();
        _agent.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Start(publicKey);
    }

    public void Dispose()
    {
        _admin.Dispose();
        _agent.Dispose();
        _factory.Dispose();
        _key.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var key in EnvKeys) Environment.SetEnvironmentVariable(key, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private string Sign(string platform, string version, byte[] content, ECDsa? key = null)
    {
        string sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return Convert.ToBase64String((key ?? _key).SignData(UpdateSigning.Message(platform, version, sha), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    private async Task<HttpResponseMessage> Upload(string platform, string version, byte[] content, string? signature = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(platform), "platform" },
            { new StringContent(version), "version" },
            { new StringContent(signature ?? Sign(platform, version, content)), "signature" },
            { new ByteArrayContent(content), "file", "pkg.bin" },
        };
        return await _admin.PostAsync("/api/agent-packages", form);
    }

    private async Task<JsonElement> Heartbeat(string host, string version, string? platform, string? status = null)
    {
        using var r = await _agent.PostAsJsonAsync("/api/heartbeat", new { host, ip = "10.0.0.1", version, platform, updateStatus = status });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private long GroupWith(string host)
    {
        long g = _db.CreateGroup("пилот");
        _db.UpsertHeartbeat(host, "10.0.0.1", "0.9.0");
        _db.SetHostGroup(_db.ListFleetHosts(null, 100, 0).First(h => h.Name == host).Id, g);
        return g;
    }

    [Fact]
    public async Task Upload_checks_signature_platform_version_and_duplicates()
    {
        byte[] zip = "zip-0.10.0"u8.ToArray();
        using (var r = await Upload("macos-pkg", "0.10.0", zip)) Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using (var r = await Upload("windows-msi", "0.10", zip)) Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        // Подпись другой версии / другой платформы / чужим ключом — отказ, файл не остаётся.
        using (var r = await Upload("windows-msi", "0.10.0", zip, Sign("windows-msi", "0.10.1", zip))) Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using (var r = await Upload("windows-msi", "0.10.0", zip, Sign("linux-deb", "0.10.0", zip))) Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        using (var r = await Upload("windows-msi", "0.10.0", zip, Sign("windows-msi", "0.10.0", zip, other))) Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dataDir, "agent-packages"), "*", SearchOption.AllDirectories));

        using (var r = await Upload("windows-msi", "0.10.0", zip)) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await Upload("windows-msi", "0.10.0", zip)) Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);

        var audit = _db.QueryAudit(null, "agent-package.upload", null, null, 10, 0);
        Assert.Equal(4, audit.Count);
        Assert.Equal(3, audit.Count(e => e.Details!.Contains("bad signature")));
    }

    [Fact]
    public async Task Without_public_key_server_refuses_packages()
    {
        Restart("");
        using var r = await Upload("windows-msi", "0.10.0", "x"u8.ToArray());
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var state = await _admin.GetFromJsonAsync<JsonElement>("/api/agent-packages");
        Assert.False(state.GetProperty("publicKeyConfigured").GetBoolean());
    }

    // 0.13: Windows-агент из MSI обновляется MSI, агенты до 0.13 — zip той же версии.
    [Fact]
    public async Task Windows_zip_heartbeat_gets_no_offer_and_zip_packages_are_rejected()
    {
        // 0.16: windows-zip убран — пакет не принимается, станция с ним остаётся без платформы.
        using (var r = await Upload("windows-zip", "0.13.0", "zip"u8.ToArray())) Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        (await Upload("windows-msi", "0.13.0", "msi"u8.ToArray())).Dispose();
        long g = GroupWith("pc1");
        using (var r = await _admin.PutAsJsonAsync($"/api/groups/{g}/agent-version", new { version = "0.13.0" })) Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        Assert.Equal(JsonValueKind.Null, (await Heartbeat("pc1", "0.12.2", "windows-zip")).GetProperty("update").ValueKind);
        var msi = (await Heartbeat("pc1", "0.12.2", "windows-msi")).GetProperty("update");
        Assert.Equal("windows-msi", msi.GetProperty("platform").GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData("msi"u8.ToArray())).ToLowerInvariant(), msi.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Heartbeat_offers_target_version_for_platform_including_rollback()
    {
        byte[] win = "win-0.10.0"u8.ToArray(), deb = "deb-0.10.0"u8.ToArray();
        (await Upload("windows-msi", "0.10.0", win)).Dispose();
        (await Upload("linux-deb", "0.10.0", deb)).Dispose();
        long g = GroupWith("pc1");
        GroupWith2("srv1", g);

        // Цели нет — не предлагаем.
        Assert.Equal(JsonValueKind.Null, (await Heartbeat("pc1", "0.9.0+abc", "windows-msi")).GetProperty("update").ValueKind);

        using (var r = await _admin.PutAsJsonAsync($"/api/groups/{g}/agent-version", new { version = "9.9.9" })) Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using (var r = await _admin.PutAsJsonAsync($"/api/groups/{g}/agent-version", new { version = "0.10.0" })) Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var offer = (await Heartbeat("pc1", "0.9.0+abc", "windows-msi")).GetProperty("update");
        Assert.Equal("0.10.0", offer.GetProperty("version").GetString());
        Assert.Equal("windows-msi", offer.GetProperty("platform").GetString());
        string sha = Convert.ToHexString(SHA256.HashData(win)).ToLowerInvariant();
        Assert.Equal(sha, offer.GetProperty("sha256").GetString());
        Assert.True(UpdateSigning.Verify(_key, "windows-msi", "0.10.0", sha, offer.GetProperty("signature").GetString()!));

        // Платформа агента решает, какой пакет.
        Assert.Equal("linux-deb", (await Heartbeat("srv1", "0.9.0", "linux-deb")).GetProperty("update").GetProperty("platform").GetString());
        // Уже на целевой (с суффиксом сборки) / платформа неизвестна / пакета для платформы нет — нет предложения.
        Assert.Equal(JsonValueKind.Null, (await Heartbeat("pc1", "0.10.0+def", "windows-msi")).GetProperty("update").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await Heartbeat("pc1", "0.9.0", null)).GetProperty("update").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await Heartbeat("srv1", "0.9.0", "linux-rpm")).GetProperty("update").ValueKind);
        // Хост без группы — не обновляется.
        Assert.Equal(JsonValueKind.Null, (await Heartbeat("lonely", "0.9.0", "windows-msi")).GetProperty("update").ValueKind);

        // Откат: цель старше текущей версии — тоже предложение (deb: apt --allow-downgrades).
        (await Upload("windows-msi", "0.9.5", "win-0.9.5"u8.ToArray())).Dispose();
        (await Upload("linux-deb", "0.9.5", "deb-0.9.5"u8.ToArray())).Dispose();
        (await _admin.PutAsJsonAsync($"/api/groups/{g}/agent-version", new { version = "0.9.5" })).Dispose();
        Assert.Equal("0.9.5", (await Heartbeat("srv1", "0.10.0", "linux-deb")).GetProperty("update").GetProperty("version").GetString());
        // 0.16: MSI до 0.16 запрещают понижение — Windows-агенту не предлагаем, причина видна у хоста.
        Assert.Equal(JsonValueKind.Null, (await Heartbeat("pc1", "0.10.0", "windows-msi")).GetProperty("update").ValueKind);
        Assert.Contains("only to 0.16", _db.HostUpdateStates().Single(h => h.Name == "pc1").Status);

        var audit = _db.QueryAudit(null, "group.agent-version", null, null, 10, 0);
        Assert.Contains(audit, e => e.Details == "{\"from\":\"0.10.0\",\"to\":\"0.9.5\"}");
    }

    private void GroupWith2(string host, long g)
    {
        _db.UpsertHeartbeat(host, "10.0.0.2", "0.9.0");
        _db.SetHostGroup(_db.ListFleetHosts(null, 100, 0).First(h => h.Name == host).Id, g);
    }

    [Fact]
    public async Task Agent_downloads_package_and_status_is_visible()
    {
        byte[] win = "win-0.10.0-content"u8.ToArray();
        (await Upload("windows-msi", "0.10.0", win)).Dispose();
        long g = GroupWith("pc1");
        (await _admin.PutAsJsonAsync($"/api/groups/{g}/agent-version", new { version = "0.10.0" })).Dispose();
        var offer = (await Heartbeat("pc1", "0.9.0", "windows-msi")).GetProperty("update");

        Assert.Equal(win, await _agent.GetByteArrayAsync(offer.GetProperty("url").GetString()));
        // 0.16, п.7: админ скачивает тот же пакет для офлайн-обслуживания (submit --package).
        long packageId = offer.GetProperty("id").GetInt64();
        using (var file = await _admin.GetAsync($"/api/agent-packages/{packageId}/file"))
        {
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Equal(win, await file.Content.ReadAsByteArrayAsync());
            Assert.Equal("watcher-agent-0.10.0.msi", file.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        }
        using (var anonymous = await _agent.GetAsync($"/api/agent-packages/{packageId}/file")) Assert.NotEqual(HttpStatusCode.OK, anonymous.StatusCode);

        await Heartbeat("pc1", "0.9.0", "windows-msi", "failed 0.10.0: signature mismatch");
        var state = await _admin.GetFromJsonAsync<JsonElement>("/api/agent-packages");
        var host = state.GetProperty("hosts").EnumerateArray().Single(h => h.GetProperty("name").GetString() == "pc1");
        Assert.Equal("windows-msi", host.GetProperty("platform").GetString());
        Assert.Equal("failed 0.10.0: signature mismatch", host.GetProperty("status").GetString());
        Assert.True(state.GetProperty("publicKeyConfigured").GetBoolean());

        // Целевую версию удалить нельзя, после снятия цели — можно.
        long id = offer.GetProperty("id").GetInt64();
        using (var r = await _admin.DeleteAsync($"/api/agent-packages/{id}")) Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        (await _admin.PutAsJsonAsync($"/api/groups/{g}/agent-version", new { version = (string?)null })).Dispose();
        using (var r = await _admin.DeleteAsync($"/api/agent-packages/{id}")) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await _agent.GetAsync($"/api/updates/{id}")) Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Download_requires_agent_credential()
    {
        // Общий токен задан — анонимный агент не проходит.
        Environment.SetEnvironmentVariable("Server__ApiToken", "shared-token-xyz");
        try
        {
            Restart(Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()));
            using var r = await _agent.GetAsync("/api/updates/1");
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            _agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "shared-token-xyz");
            using var r2 = await _agent.GetAsync("/api/updates/1");
            Assert.Equal(HttpStatusCode.NotFound, r2.StatusCode);
        }
        finally { Environment.SetEnvironmentVariable("Server__ApiToken", null); }
    }

}
