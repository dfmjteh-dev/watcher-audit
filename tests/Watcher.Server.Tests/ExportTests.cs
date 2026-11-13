using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Watcher.Server.Tests;

// H7 (roadmap-next.md): выгрузка записей с проверяемой целостностью.
public class ExportTests : IDisposable
{
    private static readonly string[] EnvKeys =
    {
        "Server__DataDir", "Server__DbPath", "Server__AdminUser", "Server__AdminPassword",
        "Server__CookieSecure", "Server__AllowAnonymousAgents", "Server__ExportMaxItems",
    };

    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Db _db;
    private readonly HttpClient _admin;
    private readonly DateTime _t0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    public ExportTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        Environment.SetEnvironmentVariable("Server__AllowAnonymousAgents", "true");
        Environment.SetEnvironmentVariable("Server__ExportMaxItems", "10");

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

    private async Task<HttpClient> LoginAsAsync(string username, string role, bool canExport)
    {
        long id = _db.InsertUser(username, UserAuth.HashPassword("password-123456"), role);
        if (canExport) _db.SetUserCanExport(id, true);
        // 0.14.4: viewer без групп не видит ничего — даём ему группу с pc1.
        if (role == "viewer")
        {
            long g = _db.CreateGroup("export-" + username);
            _db.UpsertHeartbeat("pc1", "10.0.0.1", "0.14.3", 0, isHttps: true);
            _db.SetHostGroup(_db.ListFleetHosts(null, 1000, 0).First(h => h.Name == "pc1").Id, g);
            _db.SetUserGroups(id, new[] { g });
        }
        var client = Client();
        using var resp = await client.PostAsJsonAsync("/api/login", new { username, password = "password-123456" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return client;
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private string WriteBlob(string rel, byte[] content)
    {
        string full = Path.Combine(_dataDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return rel;
    }

    private long AddSegment(string host, int minute, byte[] content, string? storedSha = null)
    {
        string name = $"{host}-{minute:D2}.mp4";
        string rel = WriteBlob($"blobs/{host}/{name}", content);
        return _db.Insert(new Segment(0, host, name, _t0.AddMinutes(minute), _t0.AddMinutes(minute + 5), 300, 10, 0, "scheduled",
            content.Length, storedSha ?? Sha(content), rel, _t0.AddMinutes(minute + 6)));
    }

    private void AddTerminal(string host, int minute)
    {
        byte[] outBytes = Encoding.UTF8.GetBytes("$ ls\nfile\n");
        string outRel = WriteBlob($"blobs/terminal/{host}/s{minute}.out", outBytes);
        string timingRel = WriteBlob($"blobs/terminal/{host}/s{minute}.timing", Encoding.UTF8.GetBytes("0.1 5\n"));
        _db.InsertTerminalSession(new TerminalSession(0, host, "alice", "terminal-output", "10.0.0.9", _t0.AddMinutes(minute), _t0.AddMinutes(minute + 1),
            60, 0, null, outRel, timingRel, outBytes.Length, Sha(outBytes), $"{host}:out:{minute}", _t0), "ls");
        _db.InsertTerminalSession(new TerminalSession(0, host, "alice", "terminal-input", "10.0.0.9", _t0.AddMinutes(minute), _t0.AddMinutes(minute + 1),
            60, 0, "ls\nwhoami\n", null, null, 0, null, $"{host}:in:{minute}", _t0), "ls");
    }

    private string Query(string host, int fromMin = 0, int toMin = 120) =>
        $"host={host}&from={_t0.AddMinutes(fromMin):o}&to={_t0.AddMinutes(toMin):o}";

    private static async Task<ZipArchive> ReadZip(HttpResponseMessage resp)
    {
        var ms = new MemoryStream(await resp.Content.ReadAsByteArrayAsync());
        return new ZipArchive(ms, ZipArchiveMode.Read);
    }

    private static byte[] Entry(ZipArchive zip, string name)
    {
        using var s = zip.GetEntry(name)!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Export_needs_separate_permission_even_for_admin()
    {
        AddSegment("pc1", 0, new byte[] { 1, 2, 3 });
        using var viewer = await LoginAsAsync("v1", "viewer", canExport: false);
        using var op = await LoginAsAsync("op1", "operator", canExport: true);

        foreach (var c in new[] { _admin, viewer, op })
        {
            using var r = await c.GetAsync("/api/export?" + Query("pc1"));
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            using var p = await c.GetAsync("/api/export/preview?" + Query("pc1"));
            Assert.Equal(HttpStatusCode.Forbidden, p.StatusCode);
        }
        Assert.False(_db.UserByUsername("admin")!.CanExport);
    }

    // 0.12: выгрузка отмеченных в плеере записей; чужой хост отбрасывается,
    // к записи ввода добавляется связанный вывод.
    [Fact]
    public async Task Export_of_picked_records_only()
    {
        byte[] v1 = Encoding.ASCII.GetBytes("video-one");
        long s1 = AddSegment("pc1", 0, v1);
        AddSegment("pc1", 10, Encoding.ASCII.GetBytes("video-two"));
        long other = AddSegment("pc2", 5, new byte[] { 8 });
        long outId = _db.InsertTerminalSession(new TerminalSession(0, "pc1", "alice", "terminal-output", null, _t0.AddMinutes(20), _t0.AddMinutes(21),
            60, 0, null, WriteBlob("blobs/terminal/pc1/x.out", Encoding.UTF8.GetBytes("out")), null, 3, Sha(Encoding.UTF8.GetBytes("out")), "pc1:out:x", _t0, AuditSession: 4), "out");
        long inId = _db.InsertTerminalSession(new TerminalSession(0, "pc1", "alice", "terminal-input", null, _t0.AddMinutes(20), _t0.AddMinutes(21),
            60, 0, "ls\n", null, null, 0, null, "pc1:in:x", _t0, AuditSession: 4), "ls");
        _db.LinkTerminalSession(_db.TerminalById(inId)!);
        using var viewer = await LoginAsAsync("v1", "viewer", canExport: true);

        string q = $"host=pc1&segmentIds={s1},{other}&terminalIds={inId}";
        using (var p = await viewer.GetAsync("/api/export/preview?" + q))
        {
            var pv = await p.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, pv.GetProperty("segments").GetInt32());
            Assert.Equal(2, pv.GetProperty("terminal").GetInt32());
        }
        using var resp = await viewer.GetAsync("/api/export?" + q);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var zip = await ReadZip(resp);
        Assert.Equal(v1, Entry(zip, "video/pc1-00.mp4"));
        Assert.Null(zip.GetEntry("video/pc1-10.mp4"));
        Assert.Contains(zip.Entries, e => e.FullName.EndsWith("_x.out", StringComparison.Ordinal));
        Assert.Contains(zip.Entries, e => e.FullName.EndsWith($"_{inId}_input.txt", StringComparison.Ordinal));

        using (var bad = await viewer.GetAsync("/api/export?host=pc1&segmentIds=1,abc")) Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using (var none = await viewer.GetAsync($"/api/export?host=pc1&segmentIds={other}")) Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        _ = outId;
    }

    [Fact]
    public async Task Export_zip_has_files_manifest_and_sums_that_verify()
    {
        byte[] v1 = Encoding.ASCII.GetBytes("video-one"), v2 = Encoding.ASCII.GetBytes("video-two");
        AddSegment("pc1", 0, v1);
        AddSegment("pc1", 10, v2);
        AddSegment("pc1", 500, new byte[] { 9 });   // вне периода
        AddSegment("pc2", 5, new byte[] { 8 });     // другой хост
        AddTerminal("pc1", 20);
        using var viewer = await LoginAsAsync("v1", "viewer", canExport: true);

        using (var p = await viewer.GetAsync("/api/export/preview?" + Query("pc1")))
        {
            var pv = await p.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2, pv.GetProperty("segments").GetInt32());
            Assert.Equal(2, pv.GetProperty("terminal").GetInt32());
            Assert.False(pv.GetProperty("tooMany").GetBoolean());
        }

        using var resp = await viewer.GetAsync("/api/export?" + Query("pc1"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/zip", resp.Content.Headers.ContentType!.MediaType);
        using var zip = await ReadZip(resp);

        Assert.Equal(v1, Entry(zip, "video/pc1-00.mp4"));
        Assert.Equal(v2, Entry(zip, "video/pc1-10.mp4"));
        Assert.Null(zip.GetEntry("video/pc1-500.mp4"));
        Assert.Null(zip.GetEntry("video/pc2-05.mp4"));
        var terminalEntries = zip.Entries.Where(e => e.FullName.StartsWith("terminal/")).Select(e => e.FullName).ToList();
        Assert.Contains(terminalEntries, n => n.EndsWith("_input.txt"));
        Assert.Contains(terminalEntries, n => n.EndsWith("_s20.out"));
        Assert.Contains(terminalEntries, n => n.EndsWith("_s20.timing"));
        Assert.Equal("ls\nwhoami\n", Encoding.UTF8.GetString(Entry(zip, terminalEntries.Single(n => n.EndsWith("_input.txt")))));

        // SHA256SUMS проверяется как sha256sum -c: каждая строка — хеш файла в архиве.
        var sums = Encoding.UTF8.GetString(Entry(zip, "SHA256SUMS")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, sums.Length);
        foreach (var line in sums)
        {
            var (hash, path) = (line[..64], line[66..]);
            Assert.Equal(hash, Sha(Entry(zip, path)));
        }
        Assert.Contains(sums, l => l == $"{Sha(v1)}  video/pc1-00.mp4");

        byte[] manifestBytes = Entry(zip, "manifest.json");
        var manifest = JsonDocument.Parse(manifestBytes).RootElement;
        Assert.Equal("watcher-export/1", manifest.GetProperty("format").GetString());
        Assert.Equal("v1", manifest.GetProperty("exportedBy").GetString());
        Assert.Equal("pc1", manifest.GetProperty("host").GetString());
        Assert.Equal(0, manifest.GetProperty("mismatched").GetArrayLength());
        var files = manifest.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(5, files.Count);
        var video = files.First(f => f.GetProperty("path").GetString() == "video/pc1-00.mp4");
        Assert.Equal("upload", video.GetProperty("hashSource").GetString());
        Assert.True(video.GetProperty("matchesUpload").GetBoolean());
        Assert.Equal("segment", video.GetProperty("item").GetProperty("type").GetString());
        var input = files.First(f => f.GetProperty("path").GetString()!.EndsWith("_input.txt"));
        Assert.Equal("export", input.GetProperty("hashSource").GetString());
        Assert.NotNull(zip.GetEntry("README.txt"));

        // Журнал: запись до выгрузки (номер выгрузки = её id) и итог с хешем манифеста.
        long exportId = manifest.GetProperty("exportId").GetInt64();
        var audit = _db.QueryAudit(null, null, null, null, 100, 0);
        var start = audit.Single(e => e.Action == "export");
        Assert.Equal(exportId, start.Id);
        Assert.Equal("v1", start.Username);
        Assert.Equal("host:pc1", start.Target);
        var done = audit.Single(e => e.Action == "export.done");
        Assert.Equal($"export:{exportId}", done.Target);
        var doneDetails = JsonDocument.Parse(done.Details!).RootElement;
        Assert.Equal(Sha(manifestBytes), doneDetails.GetProperty("manifestSha256").GetString());
        Assert.Equal(5, doneDetails.GetProperty("files").GetInt32());
    }

    [Fact]
    public async Task Changed_and_missing_files_are_flagged_not_hidden()
    {
        byte[] original = Encoding.ASCII.GetBytes("original");
        AddSegment("pc1", 0, Encoding.ASCII.GetBytes("tampered"), storedSha: Sha(original));
        long gone = AddSegment("pc1", 10, new byte[] { 1 });
        File.Delete(Path.Combine(_dataDir, _db.ById(gone)!.FilePath));
        using var viewer = await LoginAsAsync("v1", "viewer", canExport: true);

        using var resp = await viewer.GetAsync("/api/export?" + Query("pc1") + "&console=false");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var zip = await ReadZip(resp);

        // В SHA256SUMS — хеш при загрузке: проверка у получателя падает.
        var sums = Encoding.UTF8.GetString(Entry(zip, "SHA256SUMS"));
        Assert.Contains($"{Sha(original)}  video/pc1-00.mp4", sums);
        Assert.DoesNotContain("pc1-10", sums);
        var manifest = JsonDocument.Parse(Entry(zip, "manifest.json")).RootElement;
        Assert.Equal("video/pc1-00.mp4", manifest.GetProperty("mismatched")[0].GetString());
        Assert.Equal("video/pc1-10.mp4", manifest.GetProperty("missing")[0].GetString());

        var done = _db.QueryAudit(null, "export.done", null, null, 10, 0).Single();
        Assert.Contains("pc1-00.mp4", done.Details);
    }

    [Fact]
    public async Task Viewer_limited_to_groups_cannot_export_other_hosts()
    {
        long groupA = _db.CreateGroup("group-a");
        _db.UpsertHeartbeat("pc-a", "10.0.0.1", "1.0.0");
        _db.SetHostGroup(_db.ListFleetHosts(null, 100, 0).First(h => h.Name == "pc-a").Id, groupA);
        AddSegment("pc-a", 0, new byte[] { 1 });
        AddSegment("pc-b", 0, new byte[] { 2 });
        using var viewer = await LoginAsAsync("v1", "viewer", canExport: true);
        _db.SetUserGroups(_db.UserByUsername("v1")!.Id, new List<long> { groupA });

        using (var ok = await viewer.GetAsync("/api/export?" + Query("pc-a"))) Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using (var no = await viewer.GetAsync("/api/export?" + Query("pc-b"))) Assert.Equal(HttpStatusCode.Forbidden, no.StatusCode);
        using (var pv = await viewer.GetAsync("/api/export/preview?" + Query("pc-b"))) Assert.Equal(HttpStatusCode.Forbidden, pv.StatusCode);
        var denied = _db.QueryAudit(null, "export", null, null, 10, 0).Single(e => e.Target == "host:pc-b");
        Assert.Contains("\"denied\":true", denied.Details);
    }

    [Fact]
    public async Task Too_large_export_is_refused_before_audit()
    {
        for (int i = 0; i < 11; i++) AddSegment("pc1", i, new byte[] { (byte)i });
        using var viewer = await LoginAsAsync("v1", "viewer", canExport: true);

        using var resp = await viewer.GetAsync("/api/export?" + Query("pc1"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        using (var bad = await viewer.GetAsync("/api/export?host=pc1")) Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        // Хост вне групп viewer'а — 403 (права раньше существования), у admin — 404.
        using (var hidden = await viewer.GetAsync("/api/export?" + Query("nohost"))) Assert.Equal(HttpStatusCode.Forbidden, hidden.StatusCode);
        // В журнале — только попытка к чужому хосту (denied), не отказ по размеру.
        Assert.All(_db.QueryAudit(null, "export", null, null, 10, 0), a => Assert.Contains("denied", a.Details ?? ""));
    }

    [Fact]
    public async Task Admin_grants_export_and_it_applies_without_relogin()
    {
        AddSegment("pc1", 0, new byte[] { 1 });
        using var viewer = await LoginAsAsync("v1", "viewer", canExport: false);
        long id = _db.UserByUsername("v1")!.Id;

        using (var r = await viewer.GetAsync("/api/export?" + Query("pc1"))) Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using (var p = await _admin.PatchAsJsonAsync($"/api/users/{id}", new { canExport = true })) Assert.Equal(HttpStatusCode.OK, p.StatusCode);
        using (var r = await viewer.GetAsync("/api/export?" + Query("pc1"))) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var me = await viewer.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.True(me.GetProperty("canExport").GetBoolean());

        using (var p = await _admin.PatchAsJsonAsync($"/api/users/{id}", new { canExport = false })) Assert.Equal(HttpStatusCode.OK, p.StatusCode);
        using (var r = await viewer.GetAsync("/api/export?" + Query("pc1"))) Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

        var updates = _db.QueryAudit(null, "user.update", null, null, 10, 0);
        Assert.Contains(updates, e => e.Details!.Contains("\"canExport\":{\"from\":false,\"to\":true}"));
    }

    // Обрыв скачивания: Kestrel молча выбрасывает запись в закрытое соединение,
    // поэтому Write смотрит на токен запроса — иначе в журнале был бы export.done.
    [Fact]
    public void Write_stops_on_cancelled_request()
    {
        AddSegment("pc1", 0, new byte[] { 1, 2, 3 });
        var req = new RecordingExport.Request("pc1", _t0, _t0.AddHours(1), true, false, null);
        var plan = RecordingExport.Build(_db, req, 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            RecordingExport.Write(new MemoryStream(), plan, req, new RecordingExport.Meta(1, "t", _t0), _dataDir, cts.Token));
    }
}
