using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Watcher.Server.Tests;

public class TerminalUploadEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    // /api/upload-terminal — машинный путь (Bearer/без токена здесь, см. AuthGate
    // MachinePathPrefixes), не нужен cookie. /api/terminal-sessions* — обычный
    // веб-эндпоинт под cookie-авторизацией, как /api/segments — нужен _authed.
    private readonly HttpClient _client;
    private readonly HttpClient _authed;

    public TerminalUploadEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-term-api-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
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

    private static string InputMeta(string host, long sessionId, string input, string startedAt = "2026-10-06T10:00:00Z") =>
        $$"""{"kind":"terminal-input","host":"{{host}}","sessionUser":"alice","remoteAddr":"10.0.0.5","startedAt":"{{startedAt}}","endedAt":"2026-10-06T10:00:05Z","durationSec":5,"input":"{{input}}","sessionId":{{sessionId}}}""";

    private static string OutputMeta(string host, string outFile, string timingFile) =>
        $$"""{"kind":"terminal-output","host":"{{host}}","sessionUser":"alice","startedAt":"2026-10-06T10:00:00Z","endedAt":"2026-10-06T10:00:05Z","durationSec":5,"exitCode":0,"outFile":"{{outFile}}","timingFile":"{{timingFile}}"}""";

    [Fact]
    public async Task Terminal_input_upload_succeeds_and_is_queryable()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(InputMeta("hostA", 42, "echo hi")), "meta", "meta.json");

        using var resp = await _client.PostAsync("/api/upload-terminal", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var list = await _authed.GetAsync("/api/terminal-sessions?host=hostA");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        string body = await list.Content.ReadAsStringAsync();
        Assert.Contains("echo hi", body);
        Assert.Contains("terminal-input", body);
    }

    [Fact]
    public async Task Terminal_input_duplicate_sessionId_returns_409()
    {
        using var first = new MultipartFormDataContent();
        first.Add(new StringContent(InputMeta("hostB", 7, "a")), "meta", "meta.json");
        using var r1 = await _client.PostAsync("/api/upload-terminal", first);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        using var second = new MultipartFormDataContent();
        second.Add(new StringContent(InputMeta("hostB", 7, "a")), "meta", "meta.json");
        using var r2 = await _client.PostAsync("/api/upload-terminal", second);
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
    }

    // R1 (docs/code-review-2026-10.md): после перезагрузки хоста ядро снова
    // выдаёт ses=1,2,3… — та же ses с другим startedAt это новая сессия, не дубль.
    // R3: продолжение сессии после SweepAbandoned — тот же ses, позже startedAt.
    [Fact]
    public async Task Terminal_input_same_sessionId_different_startedAt_is_accepted()
    {
        using var first = new MultipartFormDataContent();
        first.Add(new StringContent(InputMeta("hostR", 1, "before-reboot", "2026-10-01T09:00:00Z")), "meta", "meta.json");
        using var r1 = await _client.PostAsync("/api/upload-terminal", first);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        using var second = new MultipartFormDataContent();
        second.Add(new StringContent(InputMeta("hostR", 1, "after-reboot", "2026-10-02T09:00:00Z")), "meta", "meta.json");
        using var r2 = await _client.PostAsync("/api/upload-terminal", second);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        using var list = await _authed.GetAsync("/api/terminal-sessions?host=hostR");
        string body = await list.Content.ReadAsStringAsync();
        Assert.Contains("before-reboot", body);
        Assert.Contains("after-reboot", body);
    }

    [Fact]
    public async Task Terminal_output_upload_stores_blobs_and_serves_them()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(OutputMeta("hostC", "alice-1-1.out", "alice-1-1.timing")), "meta", "meta.json");
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Script started...\nhello\n")), "out", "alice-1-1.out");
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("0.1 5\n0.2 6\n")), "timing", "alice-1-1.timing");

        using var resp = await _client.PostAsync("/api/upload-terminal", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        long id = json.RootElement.GetProperty("id").GetInt64();

        using var outResp = await _authed.GetAsync($"/api/terminal-sessions/{id}/out");
        Assert.Equal(HttpStatusCode.OK, outResp.StatusCode);
        Assert.Contains("hello", await outResp.Content.ReadAsStringAsync());

        using var timingResp = await _authed.GetAsync($"/api/terminal-sessions/{id}/timing");
        Assert.Equal(HttpStatusCode.OK, timingResp.StatusCode);
    }

    // 0.14.4: размер терминала из метаданных обёртки (старый script(1) его не
    // пишет) — в записи сессии; неправдоподобный — не сохраняется.
    [Theory]
    [InlineData(",\"cols\":206,\"rows\":57", 206, 57)]
    [InlineData(",\"cols\":null,\"rows\":null", null, null)]
    [InlineData(",\"cols\":0,\"rows\":99999", null, null)]
    public async Task Terminal_output_keeps_terminal_size_from_meta(string extra, int? cols, int? rows)
    {
        using var content = new MultipartFormDataContent();
        string meta = OutputMeta("hostS", "alice-2-2.out", "alice-2-2.timing");
        content.Add(new StringContent(meta[..^1] + extra + "}"), "meta", "meta.json");
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Script started on 2026-10-06\nhello\n")), "out", "alice-2-2.out");
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("0.1 6\n")), "timing", "alice-2-2.timing");
        using var resp = await _client.PostAsync("/api/upload-terminal", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        long id = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();

        var s = System.Text.Json.JsonDocument.Parse(await _authed.GetStringAsync($"/api/terminal-sessions/{id}")).RootElement.GetProperty("session");
        Assert.Equal(cols, s.GetProperty("cols").ValueKind == System.Text.Json.JsonValueKind.Null ? null : s.GetProperty("cols").GetInt32());
        Assert.Equal(rows, s.GetProperty("rows").ValueKind == System.Text.Json.JsonValueKind.Null ? null : s.GetProperty("rows").GetInt32());
    }

    private string[] AllBlobFiles()
    {
        string root = Path.Combine(_dataDir, "blobs");
        return Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : [];
    }

    private static MultipartFormDataContent OutputForm(string name)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(OutputMeta("hostC", $"{name}.out", $"{name}.timing")), "meta", "meta.json");
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("hello\n")), "out", $"{name}.out");
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("0.1 6\n")), "timing", $"{name}.timing");
        return content;
    }

    // R9 (docs/code-review-2026-10.md): успешная загрузка — только .out и
    // .timing на итоговых местах, без .part.
    [Fact]
    public async Task Terminal_output_success_leaves_no_part_files()
    {
        using var resp = await _client.PostAsync("/api/upload-terminal", OutputForm("alice-9-9"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(["alice-9-9.out", "alice-9-9.timing"], AllBlobFiles().Select(f => Path.GetFileName(f)!).Order().ToArray());
    }

    // R9: Insert упал — ни .out/.timing, ни .part на диске не остаётся.
    [Fact]
    public async Task Terminal_output_insert_failure_leaves_no_files()
    {
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_dataDir, "watcher.db")}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER fail_insert BEFORE INSERT ON terminal_sessions BEGIN SELECT RAISE(ABORT, 'boom'); END";
            cmd.ExecuteNonQuery();
        }

        try
        {
            using var resp = await _client.PostAsync("/api/upload-terminal", OutputForm("alice-8-8"));
            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            // TestServer пробрасывает необработанное исключение клиенту — это и есть 500.
        }

        Assert.Empty(AllBlobFiles());
    }

    [Fact]
    public async Task Terminal_output_path_traversal_filename_returns_400()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(OutputMeta("hostD", "../../evil.out", "evil.timing")), "meta", "meta.json");
        content.Add(new ByteArrayContent("x"u8.ToArray()), "out", "evil.out");
        content.Add(new ByteArrayContent("x"u8.ToArray()), "timing", "evil.timing");

        using var resp = await _client.PostAsync("/api/upload-terminal", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Terminal_output_missing_blob_returns_400()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(OutputMeta("hostE", "alice-2-2.out", "alice-2-2.timing")), "meta", "meta.json");

        using var resp = await _client.PostAsync("/api/upload-terminal", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Unknown_kind_returns_400()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("""{"kind":"bogus","host":"hostF"}"""), "meta", "meta.json");

        using var resp = await _client.PostAsync("/api/upload-terminal", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // A5 (roadmap-next.md): раньше DateTime.Parse на кривой строке валил
    // хендлер в 500 вместо 400 — TryParseTerminalMeta ловит это сама.
    [Fact]
    public async Task Malformed_meta_returns_400_not_500()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("""{"kind":"terminal-input","host":"hostG","startedAt":"not-a-date"}"""), "meta", "meta.json");

        using var resp = await _client.PostAsync("/api/upload-terminal", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Invalid_json_meta_returns_400_not_500()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("{not json"), "meta", "meta.json");

        using var resp = await _client.PostAsync("/api/upload-terminal", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // A1 (roadmap-next.md): host/sessionUser идут в веб-UI #/terminal без
    // контроля — сервер режет HTML-ломающие символы вторым слоем защиты.
    [Fact]
    public async Task Host_with_html_breaking_chars_returns_400()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("""{"kind":"terminal-input","host":"<script>alert(1)</script>"}"""), "meta", "meta.json");

        using var resp = await _client.PostAsync("/api/upload-terminal", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private async Task<long> UploadAsync(string meta, string? outFile = null)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(meta), "meta", "meta.json");
        if (outFile is not null)
        {
            content.Add(new ByteArrayContent("hello\n"u8.ToArray()), "out", outFile);
            content.Add(new ByteArrayContent("0.1 6\n"u8.ToArray()), "timing", outFile.Replace(".out", ".timing"));
        }
        using var resp = await _client.PostAsync("/api/upload-terminal", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();
    }

    private static string Meta(string kind, string user, string startedAt, string endedAt, string? outFile = null) => kind == "terminal-input"
        ? $$"""{"kind":"terminal-input","host":"hostR","sessionUser":"{{user}}","startedAt":"{{startedAt}}","endedAt":"{{endedAt}}","durationSec":1,"input":"ls","sessionId":{{Random.Shared.Next(1, 100000)}}}"""
        : $$"""{"kind":"terminal-output","host":"hostR","sessionUser":"{{user}}","startedAt":"{{startedAt}}","endedAt":"{{endedAt}}","durationSec":1,"exitCode":0,"outFile":"{{outFile}}","timingFile":"{{outFile!.Replace(".out", ".timing")}}"}""";

    // Задача E4: ввод (pam_tty_audit) охватывает всю SSH-сессию, вывод (script из
    // watcher-term-wrap.sh) — её внутреннюю часть; общего id нет, связь — по
    // host + user + пересечению по времени.
    [Fact]
    public async Task Session_endpoint_links_input_and_output_of_same_console_session()
    {
        long input = await UploadAsync(Meta("terminal-input", "alice", "2026-10-06T10:00:00Z", "2026-10-06T10:30:00Z"));
        long output = await UploadAsync(Meta("terminal-output", "alice", "2026-10-06T10:00:02Z", "2026-10-06T10:29:58Z", "alice-1-1.out"), "alice-1-1.out");
        // Не та же сессия: другой пользователь в то же время и тот же пользователь позже.
        await UploadAsync(Meta("terminal-output", "bob", "2026-10-06T10:00:02Z", "2026-10-06T10:29:58Z", "bob-1-1.out"), "bob-1-1.out");
        await UploadAsync(Meta("terminal-output", "alice", "2026-10-06T11:00:00Z", "2026-10-06T11:10:00Z", "alice-2-2.out"), "alice-2-2.out");

        using var fromOutput = await _authed.GetAsync($"/api/terminal-sessions/{output}");
        Assert.Equal(HttpStatusCode.OK, fromOutput.StatusCode);
        var root = System.Text.Json.JsonDocument.Parse(await fromOutput.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(output, root.GetProperty("session").GetProperty("id").GetInt64());
        Assert.True(root.GetProperty("session").GetProperty("hasOutput").GetBoolean());
        Assert.Equal(new[] { input }, root.GetProperty("related").EnumerateArray().Select(r => r.GetProperty("id").GetInt64()).ToArray());
        Assert.Equal("ls", root.GetProperty("related")[0].GetProperty("input").GetString());

        using var fromInput = await _authed.GetAsync($"/api/terminal-sessions/{input}");
        var related = System.Text.Json.JsonDocument.Parse(await fromInput.Content.ReadAsStringAsync()).RootElement.GetProperty("related");
        Assert.Equal(new[] { output }, related.EnumerateArray().Select(r => r.GetProperty("id").GetInt64()).ToArray());

        using var missing = await _authed.GetAsync("/api/terminal-sessions/999999");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // watcher-term-wrap.sh шлёт только startedAt/endedAt — длительность сервер
    // считает сам, иначе в UI у всех записей вывода 0:00 (живая проверка E4).
    [Fact]
    public async Task Output_without_durationSec_gets_duration_from_times()
    {
        string meta = """{"kind":"terminal-output","host":"hostD","sessionUser":"alice","startedAt":"2026-10-06T10:00:00Z","endedAt":"2026-10-06T10:05:00Z","exitCode":0,"outFile":"alice-9-9.out","timingFile":"alice-9-9.timing"}""";
        long id = await UploadAsync(meta, "alice-9-9.out");
        using var resp = await _authed.GetAsync($"/api/terminal-sessions/{id}");
        var session = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("session");
        Assert.Equal(300, session.GetProperty("durationSec").GetDouble());
    }

    // A5 (roadmap-next.md): .out не ограничен ничем своим кроме
    // MaxUploadSizeMb — search_text раньше читал его целиком в одну строку.
    [Fact]
    public async Task Huge_output_search_text_is_truncated()
    {
        string bigOut = new string('x', 3 * 1024 * 1024); // 3 MB > SearchTextMaxBytes (2 MB)
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(OutputMeta("hostH", "bob-3-3.out", "bob-3-3.timing")), "meta", "meta.json");
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(bigOut)), "out", "bob-3-3.out");
        content.Add(new ByteArrayContent("0.1 5\n"u8.ToArray()), "timing", "bob-3-3.timing");

        using var resp = await _client.PostAsync("/api/upload-terminal", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var list = await _authed.GetAsync("/api/terminal-sessions?host=hostH&search=" + Uri.EscapeDataString("...(усечено)"));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains("hostH", await list.Content.ReadAsStringAsync());
    }

    // Задача F2: транскрипт PowerShell (Windows) — вывод без .timing, с source/shell.
    private static string WinMeta(string kind, string host, string extra) =>
        $$"""{"kind":"{{kind}}","host":"{{host}}","sessionUser":"bob","remoteAddr":"10.0.0.9","startedAt":"2026-10-08T12:00:00Z","endedAt":"2026-10-08T12:00:30Z","durationSec":30,"source":"win-transcript","shell":"Windows PowerShell 5.1",{{extra}}}""";

    [Fact]
    public async Task Windows_transcript_output_without_timing_is_stored_as_text()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(WinMeta("terminal-output", "WIN-1", "\"outFile\":\"t1.txt\",\"timingFile\":\"\"")), "meta", "meta.json");
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("PS C:\\> whoami\nwin-1\\bob\n")), "out", "t1.txt");

        using var resp = await _client.PostAsync("/api/upload-terminal", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        long id = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();

        using var one = await _authed.GetAsync($"/api/terminal-sessions/{id}");
        var row = System.Text.Json.JsonDocument.Parse(await one.Content.ReadAsStringAsync()).RootElement.GetProperty("session");
        Assert.True(row.GetProperty("hasOutput").GetBoolean());
        Assert.False(row.GetProperty("hasTiming").GetBoolean());
        Assert.Equal("win-transcript", row.GetProperty("source").GetString());
        Assert.Equal("Windows PowerShell 5.1", row.GetProperty("shell").GetString());

        using var outResp = await _authed.GetAsync($"/api/terminal-sessions/{id}/out");
        Assert.Contains("whoami", await outResp.Content.ReadAsStringAsync());
        using var timingResp = await _authed.GetAsync($"/api/terminal-sessions/{id}/timing");
        Assert.Equal(HttpStatusCode.NotFound, timingResp.StatusCode);
        // Поиск по содержимому транскрипта работает так же, как по .out Linux.
        using var found = await _authed.GetAsync("/api/terminal-sessions?search=whoami&host=WIN-1");
        Assert.Contains($"\"id\":{id}", await found.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Linux_output_without_timing_is_still_rejected()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(OutputMeta("hostNT", "nt.out", "")), "meta", "meta.json");
        content.Add(new ByteArrayContent("x"u8.ToArray()), "out", "nt.out");

        using var resp = await _client.PostAsync("/api/upload-terminal", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Os_filter_separates_windows_and_linux_records()
    {
        using (var win = new MultipartFormDataContent())
        {
            win.Add(new StringContent(WinMeta("terminal-input", "WIN-2", "\"sessionId\":4242,\"input\":\"Get-Process\"")), "meta", "meta.json");
            using var r = await _client.PostAsync("/api/upload-terminal", win);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }
        using (var lin = new MultipartFormDataContent())
        {
            lin.Add(new StringContent(InputMeta("LNX-2", 7, "ls")), "meta", "meta.json");
            using var r = await _client.PostAsync("/api/upload-terminal", lin);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        string windows = await (await _authed.GetAsync("/api/terminal-sessions?os=windows")).Content.ReadAsStringAsync();
        string linux = await (await _authed.GetAsync("/api/terminal-sessions?os=linux")).Content.ReadAsStringAsync();
        Assert.Contains("WIN-2", windows);
        Assert.DoesNotContain("LNX-2", windows);
        Assert.Contains("LNX-2", linux);
        Assert.DoesNotContain("WIN-2", linux);
    }

    [Fact]
    public async Task Source_with_html_breaking_chars_returns_400()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("""{"kind":"terminal-input","host":"h","sessionUser":"u","startedAt":"2026-10-08T12:00:00Z","endedAt":"2026-10-08T12:00:01Z","sessionId":1,"input":"x","source":"<b>"}"""), "meta", "meta.json");
        using var resp = await _client.PostAsync("/api/upload-terminal", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
