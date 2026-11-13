using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Watcher.Server.Tests;

// 0.14.4: отчёты из заготовок (ReportFactsService + Db.ReportFacts.cs): тот же
// отчёт, что прямой расчёт по сырым данным; новые и удалённые записи
// досчитываются; смена типов программ — полный пересчёт; подсказки
// «Пользователь» не схлопываются от фильтра по пользователю.
public partial class ReportFactsTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Db _db;
    private readonly HttpClient _admin;
    private readonly ReportFactsService _service;
    // Вчерашний полдень по UTC — одни местные сутки сервера в любой зоне ±11 ч.
    private readonly DateTime _noon = DateTime.UtcNow.Date.AddDays(-1).AddHours(12);

    public ReportFactsTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-reportfacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        _factory = new WebApplicationFactory<Program>();
        _admin = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _admin.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        _admin.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123" }).GetAwaiter().GetResult().Dispose();
        _db = new Db(Path.Combine(_dataDir, "watcher.db"));
        _service = new ReportFactsService(_db, NullLogger<ReportFactsService>.Instance);
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

    private long Segment(string host, string user, double fromH, double toH, params ActivityEntry[] activity)
    {
        var s = _noon.AddHours(fromH);
        var e = _noon.AddHours(toH);
        long id = _db.Insert(new Segment(0, host, $"{host}-{Guid.NewGuid():N}.mp4", s, e, (e - s).TotalSeconds, 10, 1, "motion", 100, "x",
            "blobs/x.mp4", DateTime.UtcNow, user));
        if (activity.Length > 0) _db.InsertActivity(id, host, s, activity);
        return id;
    }

    private string Day => Db.LocalDayOf(_noon);

    private async Task<JsonElement> Report(string extra = "")
    {
        using var resp = await _admin.GetAsync($"/api/reports/activity?from={Day}&to={Day}{extra}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private ActivityReport Direct()
    {
        var offset = TimeSpan.FromMinutes(ServerClock.OffsetMinutes(_noon));
        var local = DateTime.ParseExact(Day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var from = DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        var segs = new List<ReportSegment>();
        var acts = new List<ReportActivity>();
        foreach (var host in new[] { "rds", "pc" })
        {
            var (s, a) = _db.ReportSourceForHostDay(host, from, from.AddDays(1));
            segs.AddRange(s);
            acts.AddRange(a);
        }
        return Reports.Build(segs, acts, new ReportOptions(from, from.AddDays(1), (int)offset.TotalMinutes, false), AppKinds.Load(_db));
    }

    [Fact]
    public async Task Report_from_facts_matches_direct_calculation()
    {
        Segment("rds", "ivan", 0, 2, new ActivityEntry(0, "chrome", "Почта", "mail.example.com"), new ActivityEntry(3600, "EXCEL", "План - Excel"));
        Segment("rds", "petr", 1, 3, new ActivityEntry(0, "chrome", "2 непрочитанных чата - Google Chrome"));
        Segment("pc", "ivan", 0.5, 1, new ActivityEntry(0, "1cv8c", "Отель"));
        _service.RunOnce();

        var r = await Report();
        var d = Direct();
        Assert.Equal(d.TotalActiveSec, r.GetProperty("totalActiveSec").GetDouble());
        Assert.Equal(3 * 3600 + 1800, d.TotalActiveSec);   // rds 0–3, pc 0,5–1
        Assert.Equal(d.Hosts.Select(h => (h.Host, h.ActiveSec)),
            r.GetProperty("hosts").EnumerateArray().Select(h => (h.GetProperty("host").GetString()!, h.GetProperty("activeSec").GetDouble())));
        Assert.Equal(d.Apps.Select(a => (a.Process, a.Seconds, a.Hosts, a.Users)),
            r.GetProperty("apps").EnumerateArray().Select(a => (a.GetProperty("process").GetString()!, a.GetProperty("seconds").GetDouble(), a.GetProperty("hosts").GetInt32(), a.GetProperty("users").GetInt32())));
        Assert.Equal(d.Users.Select(u => (u.User, u.ActiveSec, u.Segments)),
            r.GetProperty("users").EnumerateArray().Select(u => (u.GetProperty("user").GetString()!, u.GetProperty("activeSec").GetDouble(), u.GetProperty("segments").GetInt32())));
        Assert.Equal(d.Subjects.Select(s => s.Subject), r.GetProperty("subjects").EnumerateArray().Select(s => s.GetProperty("subject").GetString()));
        Assert.Equal(d.BrowserNoSiteSec, r.GetProperty("browserNoSiteSec").GetDouble());
        Assert.Equal(0, r.GetProperty("pending").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, r.GetProperty("computedAt").ValueKind);
    }

    [Fact]
    public async Task New_and_deleted_records_are_recomputed()
    {
        long first = Segment("pc", "ivan", 0, 1);
        _service.RunOnce();
        Assert.Equal(3600, (await Report()).GetProperty("totalActiveSec").GetDouble());

        // Новое и удалённое видно сразу: перед отчётом сутки периода досчитываются.
        Segment("pc", "ivan", 2, 3);
        Assert.Equal(7200, (await Report()).GetProperty("totalActiveSec").GetDouble());
        _db.DeleteRecords("pc", [first], []);
        Assert.Equal(1, _db.ReportDirtyCount());
        var r = await Report();
        Assert.Equal(3600, r.GetProperty("totalActiveSec").GetDouble());
        Assert.Equal(0, r.GetProperty("pending").GetInt32());
    }

    [Fact]
    public async Task User_filter_keeps_all_suggestions_and_filters_activity()
    {
        Segment("rds", "ivan", 0, 2);
        Segment("rds", "petr", 1, 3);
        _service.RunOnce();

        var r = await Report("&user=PETR");
        Assert.Equal(2 * 3600, r.GetProperty("totalActiveSec").GetDouble());   // только его записи на хосте
        Assert.Equal(new[] { "petr" }, r.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("user").GetString()));
        Assert.Equal(new[] { "ivan", "petr" }, r.GetProperty("knownUsers").EnumerateArray().Select(u => u.GetString()));
    }

    [Fact]
    public async Task Changing_app_kinds_rebuilds_facts()
    {
        Segment("pc", "ivan", 0, 1, new ActivityEntry(0, "chrome", "Почта", "mail.example.com"));
        _service.RunOnce();
        Assert.Contains((await Report()).GetProperty("subjects").EnumerateArray(), s => s.GetProperty("subject").GetString() == "mail.example.com");

        using (var put = await _admin.PutAsJsonAsync("/api/settings/app-kinds", new { overrides = new Dictionary<string, string> { ["chrome"] = "app" } }))
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        _service.RunOnce();
        Assert.Empty((await Report()).GetProperty("subjects").EnumerateArray());
    }
}
