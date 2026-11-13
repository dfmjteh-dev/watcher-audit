using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Watcher.Server.Tests;

public class UploadEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly HttpClient _authed;

    public UploadEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-api-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        // ConfigureAppConfiguration via WithWebHostBuilder is too late: Program.cs's top-level
        // statements call builder.Configuration.GetSection("Server").Bind(cfg) before that hook
        // runs. Env vars ARE seen in time — WebApplication.CreateBuilder's own
        // AddEnvironmentVariables() picks them up before any app code executes.
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        // A2 (roadmap-next.md): Cookie.SecurePolicy=Always по умолчанию — тесты ходят по http (WebApplicationFactory), без этого cookie не возвращался бы клиентом.
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        // B2 (roadmap-next.md): пустой ApiToken без явного opt-in теперь останавливает сервер.
        Environment.SetEnvironmentVariable("Server__AllowAnonymousAgents", "true");

        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        // /api/segments — cookie-auth, в отличие от /api/upload (machine token), см. FleetEndpointTests.
        _authed = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        // A2 (roadmap-next.md): CSRF-гейт в AuthGate требует этот заголовок на мутирующих /api/*.
        _authed.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        _authed.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123" })
            .GetAwaiter().GetResult().Dispose();
    }

    public void Dispose()
    {
        _client.Dispose();
        _authed.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var key in new[] { "Server__DataDir", "Server__DbPath", "Server__AdminUser", "Server__AdminPassword", "Server__CookieSecure", "Server__AllowAnonymousAgents" })
            Environment.SetEnvironmentVariable(key, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private static MultipartFormDataContent MakeUploadForm(byte[] videoBytes, string filename, string? sha256 = null, string? metaJson = null)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(videoBytes), "video", filename);
        // filename обязателен у "meta" — без него ASP.NET Core кладёт часть в
        // form.Form, не в form.Files, и /api/upload (`form.Files["meta"]`) молча
        // её не находит (найдено при тесте Фазы 6 — существующие тесты этого не
        // замечали, т.к. не проверяли поля, зависящие от реального содержимого
        // meta). Реальный Watcher.Uploader передаёт filename (см. Uploader.cs:50).
        content.Add(new StringContent(metaJson ?? "{}"), "meta", filename + ".json");
        if (sha256 is not null) content.Add(new StringContent(sha256), "sha256");
        return content;
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task Upload_then_duplicate_filename_returns_409()
    {
        byte[] bytes = "fake mp4 bytes"u8.ToArray();
        string filename = "dup.mp4";

        using var first = await _client.PostAsync("/api/upload", MakeUploadForm(bytes, filename));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await _client.PostAsync("/api/upload", MakeUploadForm(bytes, filename));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Upload_same_filename_same_sha256_returns_409()
    {
        byte[] bytes = "same content"u8.ToArray();
        string sha = Sha256Hex(bytes);

        using var first = await _client.PostAsync("/api/upload", MakeUploadForm(bytes, "retry.mp4", sha256: sha));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await _client.PostAsync("/api/upload", MakeUploadForm(bytes, "retry.mp4", sha256: sha));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    // R2 (docs/code-review-2026-10.md): то же имя, другое содержимое — это другая
    // запись (одинаковое имя машины и т.п.), не повтор. 409 тут заставил бы агента
    // удалить её как "уже выгруженную".
    [Fact]
    public async Task Upload_same_filename_different_sha256_returns_422_and_keeps_original()
    {
        byte[] original = "host A segment"u8.ToArray();
        byte[] other = "host B segment, same name"u8.ToArray();

        using var first = await _client.PostAsync("/api/upload", MakeUploadForm(original, "clash.mp4", sha256: Sha256Hex(original)));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await _client.PostAsync("/api/upload", MakeUploadForm(other, "clash.mp4", sha256: Sha256Hex(other)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);

        var blob = Directory.GetFiles(Path.Combine(_dataDir, "blobs"), "clash.mp4", SearchOption.AllDirectories).Single();
        Assert.Equal(original, await File.ReadAllBytesAsync(blob));
    }

    [Fact]
    public async Task Upload_sha256_mismatch_returns_400_and_deletes_partial_blob()
    {
        byte[] bytes = "fake mp4 bytes"u8.ToArray();
        string filename = "mismatch.mp4";
        string wrongSha = new string('0', 64);

        using var resp = await _client.PostAsync("/api/upload", MakeUploadForm(bytes, filename, sha256: wrongSha));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        string blobsRoot = Path.Combine(_dataDir, "blobs");
        bool blobSurvived = Directory.Exists(blobsRoot)
            && Directory.EnumerateFiles(blobsRoot, filename, SearchOption.AllDirectories).Any();
        Assert.False(blobSurvived);
    }

    private void ExecSql(string sql)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_dataDir, "watcher.db")}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private string[] AllBlobFiles()
    {
        string root = Path.Combine(_dataDir, "blobs");
        return Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : [];
    }

    // R9 (docs/code-review-2026-10.md): успешная загрузка не оставляет .part.
    [Fact]
    public async Task Upload_success_leaves_only_final_blob()
    {
        byte[] bytes = "fake mp4 bytes"u8.ToArray();

        using var resp = await _client.PostAsync("/api/upload", MakeUploadForm(bytes, "ok.mp4", sha256: Sha256Hex(bytes)));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string blob = Assert.Single(AllBlobFiles());
        Assert.Equal("ok.mp4", Path.GetFileName(blob));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(blob));
    }

    // R9: Insert упал — на диске не остаётся ни итогового файла, ни .part.
    // Раньше файл без строки в БД оставался навсегда (retention его не видит).
    [Fact]
    public async Task Upload_insert_failure_leaves_no_files()
    {
        ExecSql("CREATE TRIGGER fail_insert BEFORE INSERT ON segments BEGIN SELECT RAISE(ABORT, 'boom'); END");
        byte[] bytes = "fake mp4 bytes"u8.ToArray();

        try
        {
            using var resp = await _client.PostAsync("/api/upload", MakeUploadForm(bytes, "orphan.mp4"));
            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            // TestServer пробрасывает необработанное исключение клиенту — это и есть 500.
        }

        Assert.Empty(AllBlobFiles());
    }

    // R9: гонка одинаковых загрузок (агент повторил запрос, не дождавшись
    // ответа) — все проходят проверку ByFilename, пока ни одна не вставила
    // строку. Ровно одна получает 200, остальные 409 как обычный повтор (не
    // 500 от UNIQUE), на диске один целый файл и ни одного .part.
    [Fact]
    public async Task Concurrent_identical_uploads_one_wins_rest_409_no_part_files()
    {
        byte[] bytes = new byte[8 * 1024 * 1024];
        new Random(42).NextBytes(bytes);
        string sha = Sha256Hex(bytes);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            _client.PostAsync("/api/upload", MakeUploadForm(bytes, "race.mp4", sha256: sha))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        string blob = Assert.Single(AllBlobFiles());
        Assert.Equal("race.mp4", Path.GetFileName(blob));
        Assert.Equal(sha, Sha256Hex(await File.ReadAllBytesAsync(blob)));
        foreach (var r in responses) r.Dispose();
    }

    [Fact]
    public async Task Upload_path_traversal_filename_returns_400()
    {
        byte[] bytes = "fake mp4 bytes"u8.ToArray();

        using var resp = await _client.PostAsync("/api/upload", MakeUploadForm(bytes, "../../evil.mp4"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Upload_valid_sha256_succeeds()
    {
        byte[] bytes = "fake mp4 bytes"u8.ToArray();
        string sha = Sha256Hex(bytes);

        using var resp = await _client.PostAsync("/api/upload", MakeUploadForm(bytes, "ok.mp4", sha256: sha));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // Фаза 6 (roadmap.md): sessionUser из meta попадает в БД и доступен через
    // /api/segments — и напрямую, и как фильтр.
    [Fact]
    public async Task Upload_with_sessionUser_in_meta_is_stored_and_filterable()
    {
        byte[] bytes = "fake mp4 bytes"u8.ToArray();
        string metaJson = """{"host":"rds-host","sessionUser":"alice"}""";

        using var resp = await _client.PostAsync("/api/upload", MakeUploadForm(bytes, "rds-host_alice_20260101_000000.mp4", metaJson: metaJson));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var all = await _authed.GetAsync("/api/segments?host=rds-host");
        var allJson = await all.Content.ReadAsStringAsync();
        Assert.Contains("\"sessionUser\":\"alice\"", allJson, StringComparison.OrdinalIgnoreCase);

        using var filtered = await _authed.GetAsync("/api/segments?host=rds-host&sessionUser=alice");
        var filteredJson = await filtered.Content.ReadAsStringAsync();
        Assert.Contains("rds-host_alice_20260101_000000.mp4", filteredJson);

        using var filteredOut = await _authed.GetAsync("/api/segments?host=rds-host&sessionUser=bob");
        var filteredOutJson = await filteredOut.Content.ReadAsStringAsync();
        Assert.Equal("[]", filteredOutJson);
    }

    [Fact]
    public async Task Upload_without_sessionUser_in_meta_stores_null()
    {
        byte[] bytes = "fake mp4 bytes"u8.ToArray();
        string metaJson = """{"host":"plain-host"}""";

        using var resp = await _client.PostAsync("/api/upload", MakeUploadForm(bytes, "plain.mp4", metaJson: metaJson));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var all = await _authed.GetAsync("/api/segments?host=plain-host");
        var allJson = await all.Content.ReadAsStringAsync();
        Assert.Contains("\"sessionUser\":null", allJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upload_without_multipart_returns_400()
    {
        using var resp = await _client.PostAsync("/api/upload", new StringContent("not multipart"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // A5 (roadmap-next.md): раньше DateTime.Parse на кривой строке валил
    // хендлер в 500 вместо 400 — TryParseUploadMeta ловит это сама.
    [Fact]
    public async Task Malformed_meta_returns_400_not_500()
    {
        byte[] bytes = "video"u8.ToArray();
        string metaJson = """{"host":"h","startedAt":"not-a-date"}""";

        using var resp = await _client.PostAsync("/api/upload", MakeUploadForm(bytes, "malformed.mp4", metaJson: metaJson));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Invalid_json_meta_returns_400_not_500()
    {
        byte[] bytes = "video"u8.ToArray();
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(bytes), "video", "invalid-meta.mp4");
        content.Add(new StringContent("{not json"), "meta", "invalid-meta.mp4.json");

        using var resp = await _client.PostAsync("/api/upload", content);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // A1 (roadmap-next.md): host/sessionUser/startReason идут в веб-UI без
    // контроля — сервер режет HTML-ломающие символы вторым слоем защиты.
    [Fact]
    public async Task Host_with_html_breaking_chars_returns_400()
    {
        byte[] bytes = "video"u8.ToArray();
        string metaJson = """{"host":"<img src=x onerror=alert(1)>"}""";

        using var resp = await _client.PostAsync("/api/upload", MakeUploadForm(bytes, "xss.mp4", metaJson: metaJson));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task StartReason_with_quote_returns_400()
    {
        byte[] bytes = "video"u8.ToArray();
        string metaJson = """{"host":"h","startReason":"motion\" onmouseover=\"alert(1)"}""";

        using var resp = await _client.PostAsync("/api/upload", MakeUploadForm(bytes, "xss2.mp4", metaJson: metaJson));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
