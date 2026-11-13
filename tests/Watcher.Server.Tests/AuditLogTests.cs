using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Watcher.Server.Tests;

// Этап C3 (roadmap-next.md): журнал доступа — входы, просмотр видео (с дедупом
// Range-запросов одного плеера), консоль (список/поиск/.out), изменения
// пользователей/групп/конфигов; #/audit только для admin; свой retention.
public class AuditLogTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Db _db;
    private readonly HttpClient _admin;

    public AuditLogTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-audit-{Guid.NewGuid():N}");
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

    private List<AuditEntry> Entries(string action) => _db.QueryAudit(null, action, null, null, 1000, 0);

    [Fact]
    public async Task Login_success_and_failure_are_logged()
    {
        using var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var bad = await anon.PostAsJsonAsync("/api/login", new { username = "admin", password = "wrong-password-xx" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        using var unknown = await anon.PostAsJsonAsync("/api/login", new { username = "nobody", password = "wrong-password-xx" });

        Assert.Contains(Entries("login.ok"), e => e.Username == "admin");
        var failed = Entries("login.failed");
        Assert.Contains(failed, e => e.Username == "admin" && e.Details!.Contains("bad password"));
        Assert.Contains(failed, e => e.Username == "nobody" && e.Details!.Contains("unknown user"));
        // Префиксный фильтр "login" находит обе разновидности.
        Assert.True(_db.CountAudit(null, "login", null, null) >= 3);
    }

    [Fact]
    public async Task Video_view_is_logged_once_per_player_session()
    {
        string blobRel = "blobs/h/seg.mp4";
        Directory.CreateDirectory(Path.Combine(_dataDir, "blobs", "h"));
        await File.WriteAllBytesAsync(Path.Combine(_dataDir, blobRel), new byte[4096]);
        long segId = _db.Insert(new Segment(
            Id: 0, Host: "h", Filename: "seg.mp4",
            StartedAt: DateTime.UtcNow.AddMinutes(-5), EndedAt: DateTime.UtcNow,
            DurationSec: 300, Frames: 10, MotionEvents: 0, StartReason: "scheduled",
            SizeBytes: 4096, Sha256: "deadbeef", FilePath: blobRel, UploadedAt: DateTime.UtcNow));

        // Как <video>: несколько Range-запросов одного просмотра.
        for (int i = 0; i < 3; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/segments/{segId}/video");
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(i * 1000, i * 1000 + 999);
            using var resp = await _admin.SendAsync(req);
            Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        }

        var views = Entries("segment.video");
        var single = Assert.Single(views);
        Assert.Equal("admin", single.Username);
        Assert.Equal($"segment:{segId}", single.Target);
        Assert.Contains("\"host\":\"h\"", single.Details);
    }

    [Fact]
    public async Task Denied_video_access_by_restricted_viewer_is_logged()
    {
        long groupA = _db.CreateGroup("audit-a");
        long groupB = _db.CreateGroup("audit-b");
        _db.UpsertHeartbeat("host-b", "10.0.0.2", "1.0.0");
        _db.SetHostGroup(_db.ListFleetHosts(null, 100, 0).First(h => h.Name == "host-b").Id, groupB);
        long segId = _db.Insert(new Segment(
            Id: 0, Host: "host-b", Filename: "b.mp4",
            StartedAt: DateTime.UtcNow, EndedAt: DateTime.UtcNow, DurationSec: 1, Frames: 1, MotionEvents: 0,
            StartReason: "scheduled", SizeBytes: 1, Sha256: "x", FilePath: "blobs/b.mp4", UploadedAt: DateTime.UtcNow));

        using var viewer = await LoginAsAsync("v-audit", "viewer");
        _db.SetUserGroups(_db.UserByUsername("v-audit")!.Id, new[] { groupA });

        using var resp = await viewer.GetAsync($"/api/segments/{segId}/video");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains(Entries("segment.video"), e => e.Username == "v-audit" && e.Details!.Contains("\"denied\":true"));
    }

    [Fact]
    public async Task Terminal_search_logs_query_text_and_out_download()
    {
        string outRel = "blobs/terminal/h/s.out";
        Directory.CreateDirectory(Path.Combine(_dataDir, "blobs", "terminal", "h"));
        await File.WriteAllTextAsync(Path.Combine(_dataDir, outRel), "rm -rf /tmp/x");
        long termId = _db.InsertTerminalSession(new TerminalSession(
            Id: 0, Host: "h", SessionUser: "root", Kind: "terminal-output", RemoteAddr: null,
            StartedAt: DateTime.UtcNow, EndedAt: DateTime.UtcNow, DurationSec: 1, ExitCode: 0,
            Input: null, OutFilePath: outRel, TimingFilePath: null, SizeBytes: 10, Sha256: null,
            ExternalId: "h:out:s.out", UploadedAt: DateTime.UtcNow), "rm -rf /tmp/x");

        using var list = await _admin.GetAsync("/api/terminal-sessions?search=" + Uri.EscapeDataString("rm -rf"));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var outResp = await _admin.GetAsync($"/api/terminal-sessions/{termId}/out");
        Assert.Equal(HttpStatusCode.OK, outResp.StatusCode);

        var query = Assert.Single(Entries("terminal.query"));
        using (var doc = JsonDocument.Parse(query.Details!))
        {
            Assert.Equal("rm -rf", doc.RootElement.GetProperty("search").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("results").GetInt32());
        }
        Assert.Contains(Entries("terminal.out"), e => e.Target == $"terminal:{termId}" && e.Username == "admin");
    }

    [Fact]
    public async Task User_and_group_changes_are_logged()
    {
        using var create = await _admin.PostAsJsonAsync("/api/users", new { username = "op-audit", role = "operator", password = "operator-pass-123" });
        long id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        using var patch = await _admin.PatchAsJsonAsync($"/api/users/{id}", new { role = "viewer" });
        using var group = await _admin.PostAsJsonAsync("/api/groups", new { name = "audited-group" });
        long gid = (await group.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        using var cfg = await _admin.PutAsJsonAsync($"/api/groups/{gid}/config", new { config = new { outputFps = 2 } });

        Assert.Contains(Entries("user.create"), e => e.Target == "user:op-audit" && e.Details!.Contains("operator"));
        var update = Assert.Single(Entries("user.update"));
        Assert.Contains("\"from\":\"operator\",\"to\":\"viewer\"", update.Details);
        Assert.Contains(Entries("group.create"), e => e.Target == $"group:{gid}");
        Assert.Contains(Entries("group.config.set"), e => e.Target == $"group:{gid}" && e.Details!.Contains("\"outputFps\":2"));
        // Пароль (заданный явно) в журнал не попадает ни в каком виде.
        Assert.DoesNotContain(_db.QueryAudit(null, null, null, null, 1000, 0), e => (e.Details ?? "").Contains("operator-pass-123"));
    }

    [Fact]
    public async Task Audit_endpoint_is_admin_only_and_filters()
    {
        using var op = await LoginAsAsync("op-a", "operator");
        using var viewer = await LoginAsAsync("v-a", "viewer");
        using var opResp = await op.GetAsync("/api/audit");
        Assert.Equal(HttpStatusCode.Forbidden, opResp.StatusCode);
        using var vResp = await viewer.GetAsync("/api/audit");
        Assert.Equal(HttpStatusCode.Forbidden, vResp.StatusCode);

        using var resp = await _admin.GetAsync("/api/audit?username=v-a&action=login");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        var item = body.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("login.ok", item.GetProperty("action").GetString());
        Assert.Equal("viewer", item.GetProperty("details").GetProperty("role").GetString());
    }

    [Fact]
    public void Action_prefix_filter_does_not_match_partial_words()
    {
        _db.InsertAudit(new AuditEntry(0, DateTime.UtcNow, "x", null, "user.create", null, null));
        _db.InsertAudit(new AuditEntry(0, DateTime.UtcNow, "x", null, "usermod", null, null));
        Assert.Equal(1, _db.CountAudit("x", "user", null, null));
        Assert.Equal(1, _db.CountAudit("x", "usermod", null, null));
    }

    [Fact]
    public void Retention_deletes_audit_by_its_own_age_only()
    {
        _db.InsertAudit(new AuditEntry(0, DateTime.UtcNow.AddDays(-400), "old", null, "login.ok", null, null));
        _db.InsertAudit(new AuditEntry(0, DateTime.UtcNow.AddDays(-100), "recent", null, "login.ok", null, null));

        // RetentionDays=30 (записи) не должен задевать журнал — у него свой срок.
        var svc = new RetentionService(_db,
            new ServerConfig { DataDir = _dataDir, RetentionDays = 30, AuditRetentionDays = 365 },
            NullLogger<RetentionService>.Instance);
        svc.RunOnce();

        Assert.Equal(0, _db.CountAudit("old", null, null, null));
        Assert.Equal(1, _db.CountAudit("recent", null, null, null));

        var keepForever = new RetentionService(_db,
            new ServerConfig { DataDir = _dataDir, AuditRetentionDays = 0 },
            NullLogger<RetentionService>.Instance);
        keepForever.RunOnce();
        Assert.Equal(1, _db.CountAudit("recent", null, null, null));
    }
}
