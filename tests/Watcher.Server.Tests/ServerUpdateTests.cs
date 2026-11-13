using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

// Обновление сервера из веб-UI (ServerUpdate.cs): что сервер кладёт помощнику
// на хосте и что отказывается класть. Сам помощник (server-update.sh) проверяет
// то же ещё раз — его здесь нет.
public class ServerUpdateTests : IDisposable
{
    private readonly string _dir;

    public ServerUpdateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"watcher-srvupd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no network in tests");
    }

    private ServerUpdateService Service(string current = "0.13.0") =>
        new(new ServerConfig { ServerUpdate = new ServerUpdateConfig { Dir = _dir } }, new NoHttp(), NullLogger<ServerUpdateService>.Instance)
        { CurrentVersion = current };

    // Архив как из make-server-bundle.sh: всё внутри watcher-server-X.Y.Z/.
    private static byte[] Archive(string top, string? version, bool installer = true, string? extraName = null)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, top + "/"));
            if (installer)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, top + "/install-server.sh") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("#!/bin/sh\n")) });
            if (version is not null)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, top + "/VERSION") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(version + "\n")) });
            if (extraName is not null)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, extraName) { DataStream = new MemoryStream([1]) });
        }
        return ms.ToArray();
    }

    private string Write(byte[] bytes)
    {
        string f = Path.Combine(_dir, $"a-{Guid.NewGuid():N}.tar.gz");
        File.WriteAllBytes(f, bytes);
        return f;
    }

    [Theory]
    [InlineData("0.14.0", "0.13.0", true)]
    [InlineData("0.13.0", "0.13.0", true)]    // переустановка той же
    [InlineData("0.12.2", "0.13.0", false)]   // откат — нет: схема БД
    [InlineData("1.0", "0.13.0", false)]
    public void Only_forward_or_same_version_is_allowed(string target, string current, bool ok) =>
        Assert.Equal(ok, ServerUpdateService.IsAllowedTarget(target, current) && ServerUpdateService.IsVersion(target));

    [Fact]
    public void Sha_is_taken_from_release_sums()
    {
        string sha = new string('a', 64);
        string sums = $"{new string('b', 64)}  watcher-agent-v0.14.0.zip\n{sha}  watcher-server-0.14.0.tar.gz\n";
        Assert.Equal(sha, ServerUpdateService.ShaFromSums(sums, "watcher-server-0.14.0.tar.gz"));
        Assert.Null(ServerUpdateService.ShaFromSums(sums, "watcher-server-0.15.0.tar.gz"));
    }

    [Fact]
    public void Release_json_from_github_is_parsed()
    {
        var json = JsonDocument.Parse("""
        { "tag_name": "v0.14.0", "published_at": "2026-10-20T10:00:00Z", "body": "notes", "html_url": "https://github.com/o/r/releases/tag/v0.14.0",
          "assets": [
            { "name": "SHA256SUMS", "size": 100, "browser_download_url": "https://x/SHA256SUMS" },
            { "name": "watcher-server-0.14.0.tar.gz", "size": 546752338, "browser_download_url": "https://x/watcher-server-0.14.0.tar.gz" } ] }
        """).RootElement;
        var r = ServerUpdateService.ParseRelease(json)!;
        Assert.Equal("0.14.0", r.Version);
        Assert.Equal(546752338, r.ArchiveSize);
        Assert.Equal("https://x/SHA256SUMS", r.SumsUrl);
    }

    [Fact]
    public void Archive_version_is_read_and_bad_layouts_rejected()
    {
        Assert.Equal("0.14.0", ServerUpdateService.ReadArchiveVersion(Write(Archive("watcher-server-0.14.0", "0.14.0"))));
        Assert.Null(ServerUpdateService.ReadArchiveVersion(Write(Archive("watcher-server-0.14.0", "0.15.0"))));       // VERSION ≠ папка
        Assert.Null(ServerUpdateService.ReadArchiveVersion(Write(Archive("watcher-server-0.14.0", "0.14.0", installer: false))));
        Assert.Null(ServerUpdateService.ReadArchiveVersion(Write(Archive("watcher-server-0.14.0", "0.14.0", extraName: "etc/cron.d/x"))));
        Assert.Null(ServerUpdateService.ReadArchiveVersion(Write(Archive("watcher-server-0.14.0", "0.14.0", extraName: "watcher-server-0.14.0/../../etc/x"))));
        Assert.Null(ServerUpdateService.ReadArchiveVersion(Write(Archive("other-0.14.0", "0.14.0"))));
    }

    [Fact]
    public async Task Upload_queues_request_for_host_helper()
    {
        var svc = Service();
        var r = await svc.InstallFromUploadAsync(new MemoryStream(Archive("watcher-server-0.14.0", "0.14.0")), "admin", CancellationToken.None);

        Assert.Equal(ServerUpdateService.Outcome.Ok, r.Outcome);
        Assert.Equal("0.14.0", r.Version);
        var req = Assert.Single(Directory.GetFiles(Path.Combine(_dir, "requests"), "*.json"));
        using var doc = JsonDocument.Parse(File.ReadAllText(req));
        string id = Path.GetFileNameWithoutExtension(req);
        Assert.Equal($"incoming/{id}.tar.gz", doc.RootElement.GetProperty("archive").GetString());
        Assert.Equal("0.13.0", doc.RootElement.GetProperty("currentVersion").GetString());
        Assert.True(File.Exists(Path.Combine(_dir, "incoming", id + ".tar.gz")));
        Assert.Empty(Directory.GetFiles(_dir, "*.part", SearchOption.AllDirectories));
        Assert.True(svc.RequestPending());

        // Пока заявка не забрана помощником — вторая не принимается.
        var again = await svc.InstallFromUploadAsync(new MemoryStream(Archive("watcher-server-0.14.0", "0.14.0")), "admin", CancellationToken.None);
        Assert.Equal(ServerUpdateService.Outcome.Pending, again.Outcome);
    }

    [Fact]
    public async Task Downgrade_and_foreign_archive_are_not_queued()
    {
        var svc = Service("0.14.0");
        var old = await svc.InstallFromUploadAsync(new MemoryStream(Archive("watcher-server-0.13.0", "0.13.0")), "admin", CancellationToken.None);
        var junk = await svc.InstallFromUploadAsync(new MemoryStream(Encoding.UTF8.GetBytes("not a tar")), "admin", CancellationToken.None);

        Assert.Equal(ServerUpdateService.Outcome.Downgrade, old.Outcome);
        Assert.NotEqual(ServerUpdateService.Outcome.Ok, junk.Outcome);
        Assert.False(svc.RequestPending());
        Assert.Empty(Directory.Exists(Path.Combine(_dir, "incoming")) ? Directory.GetFiles(Path.Combine(_dir, "incoming")) : []);
    }

    [Fact]
    public void Helper_status_and_log_are_read_from_dir()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "logs"));
        File.WriteAllText(Path.Combine(_dir, "status.json"), """{"id":"20261020abcd","state":"done","version":"0.14.0","message":"ok"}""");
        File.WriteAllText(Path.Combine(_dir, "logs", "20261020abcd.log"), "install log");
        var svc = Service();
        Assert.Equal("done", svc.HelperStatus()!.Value.GetProperty("state").GetString());
        Assert.Equal("install log", svc.HelperLogTail("20261020abcd"));
        Assert.Null(svc.HelperLogTail("../status"));
    }

    // ---- 0.14.3: загрузка по частям ----

    private static async Task<ServerUpdateService.ChunkOutcome> Put(ServerUpdateService svc, string id, long offset, byte[] data, int from, int len) =>
        (await svc.AppendChunkAsync(id, offset, new MemoryStream(data, from, len), CancellationToken.None)).Outcome;

    private async Task WaitQueued(ServerUpdateService svc)
    {
        for (int i = 0; i < 100 && !svc.RequestPending(); i++) await Task.Delay(50);
        Assert.True(svc.RequestPending());
    }

    [Fact]
    public async Task Chunked_upload_resumes_after_restart_and_queues_in_background()
    {
        byte[] arc = Archive("watcher-server-0.14.0", "0.14.0");
        var svc = Service();
        var (o, s, _) = svc.StartUpload("watcher-server-0.14.0.tar.gz", arc.Length, "admin");
        Assert.Equal(ServerUpdateService.Outcome.Ok, o);
        int third = arc.Length / 3;
        Assert.Equal(ServerUpdateService.ChunkOutcome.Ok, await Put(svc, s!.Id, 0, arc, 0, third));
        // Не то смещение (повтор той же части после обрыва) — сервер говорит, сколько у него.
        var (wrong, ws) = await svc.AppendChunkAsync(s.Id, 0, new MemoryStream(arc, 0, third), CancellationToken.None);
        Assert.Equal((ServerUpdateService.ChunkOutcome.WrongOffset, (long)third), (wrong, ws!.Received));

        // «Перезапуск сервера» — новый экземпляр на тех же файлах; тот же файл — та же загрузка.
        var svc2 = Service();
        var (o2, s2, _) = svc2.StartUpload("watcher-server-0.14.0.tar.gz", arc.Length, "admin");
        Assert.Equal((ServerUpdateService.Outcome.Ok, s.Id, (long)third), (o2, s2!.Id, s2.Received));
        Assert.Equal(ServerUpdateService.ChunkOutcome.Ok, await Put(svc2, s.Id, third, arc, third, third));
        Assert.Equal(ServerUpdateService.ChunkOutcome.Complete, await Put(svc2, s.Id, 2 * third, arc, 2 * third, arc.Length - 2 * third));
        Assert.Null(svc2.ActiveUpload());   // сразу: идёт проверка, не «незаконченная загрузка»

        await WaitQueued(svc2);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "requests", s.Id + ".json")));
        Assert.Equal("0.14.0", doc.RootElement.GetProperty("version").GetString());
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(arc)).ToLowerInvariant(), doc.RootElement.GetProperty("sha256").GetString());
        Assert.Null(svc2.ActiveUpload());
        Assert.Empty(Directory.GetFiles(_dir, "*.part", SearchOption.AllDirectories));
    }

    private sealed class BreaksAfter(byte[] data, int after) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= after) throw new IOException("connection reset");
            int n = Math.Min(count, Math.Min(after, data.Length) - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Chunk_broken_midway_keeps_what_arrived()
    {
        byte[] arc = Archive("watcher-server-0.14.0", "0.14.0");
        var svc = Service();
        var (_, s, _) = svc.StartUpload("watcher-server-0.14.0.tar.gz", arc.Length, "admin");
        await Assert.ThrowsAsync<IOException>(() => svc.AppendChunkAsync(s!.Id, 0, new BreaksAfter(arc, 100), CancellationToken.None));
        Assert.Equal(100, svc.ActiveUpload()!.Received);
        Assert.Equal(ServerUpdateService.ChunkOutcome.Complete, await Put(svc, s!.Id, 100, arc, 100, arc.Length - 100));
        await WaitQueued(svc);
    }

    [Fact]
    public async Task Other_file_while_unfinished_is_busy_cancel_frees_and_github_waits()
    {
        var svc = Service();
        var (_, s, _) = svc.StartUpload("watcher-server-0.14.0.tar.gz", 1000, "admin");
        var (busy, active, err) = svc.StartUpload("watcher-server-0.14.1.tar.gz", 2000, "admin");
        Assert.Equal((ServerUpdateService.Outcome.Busy, s!.Id), (busy, active!.Id));
        Assert.Contains("не закончена", err);
        Assert.Equal(ServerUpdateService.Outcome.Busy, (await svc.InstallFromGitHubAsync("admin", CancellationToken.None)).Outcome);

        Assert.True(svc.CancelUpload(s.Id));
        Assert.Null(svc.ActiveUpload());
        Assert.Empty(Directory.GetFiles(_dir, "*.part", SearchOption.AllDirectories));
        Assert.Equal(ServerUpdateService.Outcome.BadArchive, svc.StartUpload("evil.sh", 10, "admin").Outcome);
        Assert.Equal(ServerUpdateService.Outcome.BadArchive, svc.StartUpload("watcher-server-0.14.0.tar.gz", 0, "admin").Outcome);
    }

    [Fact]
    public async Task Bad_archive_after_last_chunk_fails_and_cleans_up()
    {
        byte[] notArchive = Encoding.UTF8.GetBytes("not a tar.gz at all");
        var svc = Service();
        var (_, s, _) = svc.StartUpload("watcher-server-0.14.0.tar.gz", notArchive.Length, "admin");
        Assert.Equal(ServerUpdateService.ChunkOutcome.Complete, await Put(svc, s!.Id, 0, notArchive, 0, notArchive.Length));
        for (int i = 0; i < 100 && JsonSerializer.Serialize(svc.PhaseSnapshot()).Contains("verifying"); i++) await Task.Delay(50);
        Assert.Contains("failed", JsonSerializer.Serialize(svc.PhaseSnapshot()));
        Assert.False(svc.RequestPending());
        Assert.Null(svc.ActiveUpload());
        Assert.Empty(Directory.GetFiles(_dir, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public void Abandoned_upload_is_dropped_after_a_day()
    {
        var svc = Service();
        var (_, s, _) = svc.StartUpload("watcher-server-0.14.0.tar.gz", 1000, "admin");
        string meta = Path.Combine(_dir, "uploads", s!.Id + ".json");
        var old = JsonSerializer.Deserialize<UploadSession>(File.ReadAllText(meta))! with { UpdatedAt = DateTime.UtcNow.AddDays(-2) };
        File.WriteAllText(meta, JsonSerializer.Serialize(old));
        Assert.Null(svc.ActiveUpload());
        Assert.False(File.Exists(meta));
        Assert.Empty(Directory.GetFiles(_dir, "*.part", SearchOption.AllDirectories));
    }
}

public class ServerUpdateEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;

    public ServerUpdateEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-srvupd-http-{Guid.NewGuid():N}");
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

    [Fact]
    public async Task Without_host_helper_update_is_refused_and_status_says_so()
    {
        var st = await _admin.GetFromJsonAsync<JsonElement>("/api/server-update");
        Assert.False(st.GetProperty("enabled").GetBoolean());

        using var body = new ByteArrayContent([1, 2, 3]);
        using var resp = await _admin.PostAsync("/api/server-update/upload", body);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }
}

// 0.14.3: загрузка по частям через HTTP — как её делает страница.
public class ServerUpdateChunkEndpointTests : IDisposable
{
    private readonly string _dataDir, _updDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;
    private static readonly string[] Env = ["Server__DataDir", "Server__DbPath", "Server__AdminUser", "Server__AdminPassword", "Server__CookieSecure", "Server__ServerUpdate__Dir"];

    public ServerUpdateChunkEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-srvupd-chunk-{Guid.NewGuid():N}");
        _updDir = Path.Combine(_dataDir, "updates");
        Directory.CreateDirectory(_updDir);
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        Environment.SetEnvironmentVariable("Server__ServerUpdate__Dir", _updDir);
        _factory = new WebApplicationFactory<Program>();
        _admin = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _admin.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        _admin.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123" }).GetAwaiter().GetResult().Dispose();
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var key in Env) Environment.SetEnvironmentVariable(key, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private static byte[] Archive(string version)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz))
        {
            string top = "watcher-server-" + version;
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, top + "/"));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, top + "/install-server.sh") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("#!/bin/sh\n")) });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, top + "/VERSION") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(version + "\n")) });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, top + "/payload.bin") { DataStream = new MemoryStream(System.Security.Cryptography.RandomNumberGenerator.GetBytes(50_000)) });
        }
        return ms.ToArray();
    }

    private async Task<HttpResponseMessage> Put(string id, long offset, byte[] data, int from, int len)
    {
        using var body = new ByteArrayContent(data, from, len);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        return await _admin.PutAsync($"/api/server-update/uploads/{id}?offset={offset}", body);
    }

    [Fact]
    public async Task Page_flow_chunks_resume_and_background_queue()
    {
        string version = "99.0.0";   // новее любой текущей
        byte[] arc = Archive(version);
        string name = $"watcher-server-{version}.tar.gz";
        var start = await (await _admin.PostAsJsonAsync("/api/server-update/uploads", new { name, size = arc.Length })).Content.ReadFromJsonAsync<JsonElement>();
        string id = start.GetProperty("id").GetString()!;
        int half = arc.Length / 2;
        using (var r1 = await Put(id, 0, arc, 0, half)) Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        // «Перезагрузка страницы»: статус показывает незаконченную загрузку, повтор той же части — 409 с принятым.
        var st = await _admin.GetFromJsonAsync<JsonElement>("/api/server-update");
        Assert.Equal(half, st.GetProperty("upload").GetProperty("received").GetInt64());
        using (var dup = await Put(id, 0, arc, 0, half))
        {
            Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
            Assert.Equal(half, (await dup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("received").GetInt64());
        }
        // Тот же файл заново — та же загрузка, с того же места.
        var again = await (await _admin.PostAsJsonAsync("/api/server-update/uploads", new { name, size = arc.Length })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((id, (long)half), (again.GetProperty("id").GetString(), again.GetProperty("received").GetInt64()));
        // Обновление с GitHub, пока загрузка не закончена, — нельзя.
        using (var gh = await _admin.PostAsync("/api/server-update/github", null)) Assert.Equal(HttpStatusCode.Conflict, gh.StatusCode);

        using (var last = await Put(id, half, arc, half, arc.Length - half)) Assert.Equal(HttpStatusCode.Accepted, last.StatusCode);
        string req = Path.Combine(_updDir, "requests", id + ".json");
        for (int i = 0; i < 100 && !File.Exists(req); i++) await Task.Delay(50);
        Assert.True(File.Exists(req));
        Assert.Equal(version, JsonDocument.Parse(File.ReadAllText(req)).RootElement.GetProperty("version").GetString());
        var after = await _admin.GetFromJsonAsync<JsonElement>("/api/server-update");
        Assert.Equal(JsonValueKind.Null, after.GetProperty("upload").ValueKind);
        Assert.True(after.GetProperty("pending").GetBoolean());
    }

    [Fact]
    public async Task Cancel_and_unknown_upload()
    {
        var start = await (await _admin.PostAsJsonAsync("/api/server-update/uploads", new { name = "watcher-server-99.0.0.tar.gz", size = 1000 })).Content.ReadFromJsonAsync<JsonElement>();
        string id = start.GetProperty("id").GetString()!;
        using (var del = await _admin.DeleteAsync($"/api/server-update/uploads/{id}")) Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        using (var gone = await Put(id, 0, [1, 2, 3], 0, 3)) Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using (var bad = await _admin.PostAsJsonAsync("/api/server-update/uploads", new { name = "x.exe", size = 10 })) Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}

