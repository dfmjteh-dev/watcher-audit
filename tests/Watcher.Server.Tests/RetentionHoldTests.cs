using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Watcher.Server.Tests;

// H6 (roadmap-next.md): срок хранения по группам и legal hold.
public class RetentionHoldTests : IDisposable
{
    private readonly string _dataDir;
    private readonly Db _db;
    private readonly DateTime _now = DateTime.UtcNow;

    public RetentionHoldTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-hold-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _db = new Db(Path.Combine(_dataDir, "watcher.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private long Segment(string host, int daysAgo, long size = 1000)
    {
        var at = _now.AddDays(-daysAgo);
        string name = $"{host}-{daysAgo}-{Guid.NewGuid():N}.mp4";
        return _db.Insert(new Segment(0, host, name, at, at.AddMinutes(1), 60, 10, 1, "motion", size, "x", name, at));
    }

    private long Terminal(string host, int daysAgo)
    {
        var at = _now.AddDays(-daysAgo);
        return _db.InsertTerminalSession(new TerminalSession(0, host, "root", "terminal-input", null, at, at.AddMinutes(1), 60, 0,
            "ls", null, null, 0, null, $"{host}:{Guid.NewGuid():N}", at), "ls");
    }

    private void InGroup(string host, long group)
    {
        _db.UpsertHeartbeat(host, "10.0.0.1", "1.0.0");
        _db.SetHostGroup(_db.ListFleetHosts(null, 100, 0).First(h => h.Name == host).Id, group);
    }

    private void Retention(int days, long maxBytes = 0, (long, long)? disk = null) =>
        new RetentionService(_db, new ServerConfig { DataDir = _dataDir, RetentionDays = days, RetentionMaxBytes = maxBytes, MinFreeDiskPercent = 0 },
            NullLogger<RetentionService>.Instance)
        { DiskSpace = () => disk }.RunOnce();

    private bool Exists(long segmentId) => _db.ById(segmentId) is not null;

    [Fact]
    public void Group_retention_overrides_server_default()
    {
        long longGroup = _db.CreateGroup("бухгалтерия"), keepGroup = _db.CreateGroup("архив");
        _db.SetGroupRetention(longGroup, 90);
        _db.SetGroupRetention(keepGroup, 0);
        InGroup("buh", longGroup);
        InGroup("arch", keepGroup);

        long plain60 = Segment("plain", 60), buh60 = Segment("buh", 60), buh100 = Segment("buh", 100), arch400 = Segment("arch", 400);
        long buhTerm100 = Terminal("buh", 100), plainTerm60 = Terminal("plain", 60);

        Retention(30);

        Assert.False(Exists(plain60));
        Assert.True(Exists(buh60));
        Assert.False(Exists(buh100));
        Assert.True(Exists(arch400));
        Assert.Null(_db.TerminalById(buhTerm100));
        Assert.Null(_db.TerminalById(plainTerm60));
    }

    [Fact]
    public void Group_retention_applies_even_when_server_keeps_forever()
    {
        long g = _db.CreateGroup("короткий");
        _db.SetGroupRetention(g, 7);
        InGroup("short", g);
        long s10 = Segment("short", 10), other10 = Segment("other", 10);

        Retention(0);

        Assert.False(Exists(s10));
        Assert.True(Exists(other10));
    }

    [Fact]
    public void Legal_hold_keeps_records_until_released()
    {
        long seg = Segment("pc1", 100), term = Terminal("pc1", 100), other = Segment("pc2", 100);
        long hold = _db.CreateLegalHold("pc1", null, null, "служебная проверка № 7", "admin");

        Retention(30);
        Assert.True(Exists(seg));
        Assert.NotNull(_db.TerminalById(term));
        Assert.False(Exists(other));

        Assert.True(_db.ReleaseLegalHold(hold, "admin", "проверка закрыта"));
        Assert.False(_db.ReleaseLegalHold(hold, "admin", null));   // повторно — нет
        Retention(30);
        Assert.False(Exists(seg));
        Assert.Null(_db.TerminalById(term));
        var h = _db.LegalHoldById(hold)!;
        Assert.False(h.Active);
        Assert.Equal("проверка закрыта", h.ReleaseReason);
    }

    [Fact]
    public void Legal_hold_for_a_period_keeps_only_that_period()
    {
        long inside = Segment("pc1", 100), before = Segment("pc1", 200);
        _db.CreateLegalHold("pc1", _now.AddDays(-120), _now.AddDays(-80), "период", "admin");

        Retention(30);

        Assert.True(Exists(inside));
        Assert.False(Exists(before));
    }

    [Fact]
    public void Space_pressure_does_not_delete_held_records()
    {
        long held = Segment("pc1", 5, size: 1000), free = Segment("pc2", 3, size: 1000);
        _db.CreateLegalHold("pc1", null, null, "суд", "admin");

        Retention(0, maxBytes: 500);

        Assert.True(Exists(held));
        Assert.False(Exists(free));
    }
}

// H6: API — только admin, всё в журнал.
public class RetentionHoldEndpointTests : IDisposable
{
    private static readonly string[] EnvKeys =
    {
        "Server__DataDir", "Server__DbPath", "Server__AdminUser", "Server__AdminPassword",
        "Server__CookieSecure", "Server__AllowAnonymousAgents", "Server__RetentionDays",
    };

    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;
    private readonly Db _db;

    public RetentionHoldEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-hold-api-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        Environment.SetEnvironmentVariable("Server__AllowAnonymousAgents", "true");
        Environment.SetEnvironmentVariable("Server__RetentionDays", "30");
        _factory = new WebApplicationFactory<Program>();
        _admin = Client();
        _admin.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123" }).GetAwaiter().GetResult().Dispose();
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

    private HttpClient Client()
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return c;
    }

    [Fact]
    public async Task Legal_hold_lifecycle_is_admin_only_and_audited()
    {
        _db.InsertUser("v1", UserAuth.HashPassword("password-123456"), "viewer");
        using var viewer = Client();
        (await viewer.PostAsJsonAsync("/api/login", new { username = "v1", password = "password-123456" })).Dispose();
        using (var r = await viewer.PostAsJsonAsync("/api/legal-holds", new { host = "pc1", reason = "x" })) Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using (var r = await viewer.GetAsync("/api/legal-holds")) Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

        using (var r = await _admin.PostAsJsonAsync("/api/legal-holds", new { host = "pc1", reason = "" })) Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using (var r = await _admin.PostAsJsonAsync("/api/legal-holds", new { host = "pc1", reason = "x", from = "2026-10-02T00:00:00Z", to = "2026-10-01T00:00:00Z" }))
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);

        long id;
        using (var r = await _admin.PostAsJsonAsync("/api/legal-holds", new { host = "pc1", reason = "запрос СБ № 12", from = "2026-10-01T00:00:00Z" }))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        }
        var active = await _admin.GetFromJsonAsync<JsonElement[]>("/api/legal-holds?active=true");
        Assert.Equal("запрос СБ № 12", Assert.Single(active!).GetProperty("reason").GetString());

        using (var r = await _admin.PostAsJsonAsync($"/api/legal-holds/{id}/release", new { reason = "закрыто" })) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await _admin.PostAsJsonAsync($"/api/legal-holds/{id}/release", new { })) Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Empty((await _admin.GetFromJsonAsync<JsonElement[]>("/api/legal-holds?active=true"))!);
        Assert.Single((await _admin.GetFromJsonAsync<JsonElement[]>("/api/legal-holds?host=pc1"))!);

        var audit = _db.QueryAudit(null, "legal-hold", null, null, 10, 0);
        Assert.Contains(audit, e => e.Action == "legal-hold.create" && e.Target == "host:pc1" && e.Details!.Contains("запрос СБ"));
        Assert.Contains(audit, e => e.Action == "legal-hold.release" && e.Details!.Contains("закрыто"));
    }

    [Fact]
    public async Task Group_retention_is_validated_and_audited()
    {
        long g = _db.CreateGroup("бухгалтерия");
        using (var r = await _admin.PutAsJsonAsync($"/api/groups/{g}/retention", new { retentionDays = -1 })) Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using (var r = await _admin.PutAsJsonAsync($"/api/groups/{g}/retention", new { retentionDays = 99999 })) Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using (var r = await _admin.PutAsJsonAsync("/api/groups/9999/retention", new { retentionDays = 10 })) Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        using (var r = await _admin.PutAsJsonAsync($"/api/groups/{g}/retention", new { retentionDays = 90 })) Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var ret = await _admin.GetFromJsonAsync<JsonElement>("/api/retention");
        Assert.Equal(30, ret.GetProperty("retentionDays").GetInt32());
        Assert.Equal(90, ret.GetProperty("groups")[0].GetProperty("retentionDays").GetInt32());

        using (var r = await _admin.PutAsJsonAsync($"/api/groups/{g}/retention", new { retentionDays = (int?)null })) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Null(_db.GroupRetentions().Single().RetentionDays);
        var audit = _db.QueryAudit(null, "group.retention", null, null, 10, 0);
        Assert.Equal(2, audit.Count);
        Assert.Contains(audit, e => e.Details == "{\"from\":null,\"to\":90}");
    }
}
