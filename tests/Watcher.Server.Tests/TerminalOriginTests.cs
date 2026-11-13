using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Watcher.Server.Tests;

// 0.16: транскрипты PowerShell — метки происхождения/режима, расшифровка
// -EncodedCommand, «не сохранять» по настройке, фильтры «Консоли», миграция 33.
public class TerminalOriginTests : IDisposable
{
    private static readonly string[] EnvKeys =
    {
        "Server__DataDir", "Server__DbPath", "Server__AdminUser", "Server__AdminPassword",
        "Server__CookieSecure", "Server__AllowAnonymousAgents",
    };

    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly HttpClient _authed;

    // «$OutputEncoding = [System.Text.Encoding]::UTF8» — как в записи SSH-команды автоматизации.
    private static readonly string Script = "$OutputEncoding = [System.Text.Encoding]::UTF8";
    private static readonly string Encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));

    public TerminalOriginTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-term-origin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        Environment.SetEnvironmentVariable("Server__AllowAnonymousAgents", "true");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _authed = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _authed.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        _authed.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123" }).GetAwaiter().GetResult().Dispose();
    }

    public void Dispose()
    {
        _client.Dispose();
        _authed.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var key in EnvKeys) Environment.SetEnvironmentVariable(key, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    private async Task<HttpResponseMessage> Input(string host, long pid, string input, string? origin, string? mode, string start = "2026-11-02T07:47:39Z")
    {
        var meta = new Dictionary<string, object?>
        {
            ["kind"] = "terminal-input",
            ["host"] = host,
            ["sessionUser"] = "claude-rw",
            ["remoteAddr"] = "10.0.0.30",
            ["startedAt"] = start,
            ["endedAt"] = "2026-11-02T07:47:41Z",
            ["durationSec"] = 2,
            ["source"] = "win-transcript",
            ["shell"] = "Windows PowerShell 5.1",
            ["input"] = input,
            ["sessionId"] = pid,
            ["origin"] = origin,
            ["mode"] = mode,
        };
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(JsonSerializer.Serialize(meta)), "meta", "meta.json");
        return await _client.PostAsync("/api/upload-terminal", content);
    }

    private async Task<JsonElement[]> List(string query)
    {
        using var resp = await _authed.GetAsync("/api/terminal-sessions?" + query);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var root = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        return (root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("items")).EnumerateArray().ToArray();
    }

    [Fact]
    public void Decode_replaces_encoded_command_with_script()
    {
        string decoded = PowerShellCommands.Decode($"powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand {Encoded}")!;
        Assert.Contains(Script, decoded);
        Assert.StartsWith("powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand ⟦", decoded);
        Assert.Contains(Script, PowerShellCommands.Decode($"powershell -enc {Encoded}")!);
        Assert.Null(PowerShellCommands.Decode("powershell -ex Bypass -File C:\\scripts\\run.ps1"));
        Assert.Null(PowerShellCommands.Decode("Get-Date"));
        // Слово из base64-алфавита, не являющееся UTF-16-текстом, не подменяется.
        Assert.Null(PowerShellCommands.Decode("cmd -e AAAAAAAAAAAA"));
    }

    [Fact]
    public async Task Origin_mode_and_decoded_input_are_stored_and_shown()
    {
        using (var r = await Input("WIN-SRV", 10, $"powershell -NoProfile -EncodedCommand {Encoded}", "ssh", "exec")) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var row = (await List("host=WIN-SRV")).Single();
        Assert.Equal("ssh", row.GetProperty("origin").GetString());
        Assert.Equal("exec", row.GetProperty("mode").GetString());
        Assert.Contains(Script, row.GetProperty("inputDecoded").GetString());
        // Поиск находит и по расшифрованному тексту.
        Assert.Single(await List("host=WIN-SRV&search=OutputEncoding"));
    }

    [Fact]
    public async Task Unknown_label_values_are_not_stored()
    {
        using (var r = await Input("WIN-X", 11, "whoami", "evil<script>", "weird")) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var row = (await List("host=WIN-X")).Single();
        Assert.Equal(JsonValueKind.Null, row.GetProperty("origin").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("mode").ValueKind);
    }

    [Fact]
    public async Task Filters_type_exec_and_default_without_automation()
    {
        (await Input("WIN-F", 1, "Get-Process", "ssh", "interactive", "2026-11-02T08:00:00Z")).Dispose();
        (await Input("WIN-F", 2, $"powershell -EncodedCommand {Encoded}", "ssh", "exec", "2026-11-02T08:01:00Z")).Dispose();
        (await Input("WIN-F", 3, "Backup-Thing", "automation", "exec", "2026-11-02T08:02:00Z")).Dispose();
        (await Input("WIN-F", 4, "Invoke-Sql", "sql-agent", "exec", "2026-11-02T08:03:00Z")).Dispose();
        (await Input("WIN-F", 5, "dir", "unknown", "interactive", "2026-11-02T08:04:00Z")).Dispose();

        Assert.Equal(5, (await List("host=WIN-F")).Length);
        Assert.Equal(3, (await List("host=WIN-F&origin=no-automation")).Length);
        Assert.Single(await List("host=WIN-F&origin=automation"));
        Assert.Equal(2, (await List("host=WIN-F&type=interactive")).Length);
        Assert.Equal(3, (await List("host=WIN-F&type=exec")).Length);
    }

    [Fact]
    public async Task Skipped_origins_from_config_are_not_saved()
    {
        (await Input("WIN-S", 1, "first", "ssh", "interactive", "2026-11-02T08:00:00Z")).Dispose();
        var db = new Db(Path.Combine(_dataDir, "watcher.db"));
        long hostId = db.Inventory().Single(h => h.Name == "WIN-S").Id!.Value;
        db.SetHostConfig(hostId, """{"schemaVersion":1,"skipTranscriptOrigins":["automation"]}""");

        using (var r = await Input("WIN-S", 2, "Backup-Thing", "automation", "exec", "2026-11-02T08:01:00Z"))
            Assert.Contains("origin excluded", await r.Content.ReadAsStringAsync());
        (await Input("WIN-S", 3, "Get-Date", "ssh", "interactive", "2026-11-02T08:02:00Z")).Dispose();
        var rows = await List("host=WIN-S");
        Assert.Equal(2, rows.Length);
        Assert.DoesNotContain(rows, x => x.GetProperty("origin").GetString() == "automation");
    }

    [Fact]
    public void Migration_acknowledges_transcript_losses_and_decodes_old_input()
    {
        string path = Path.Combine(_dataDir, "migr.db");
        var db = new Db(path);
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
INSERT INTO host_losses (host, file, recorded_at, size_bytes, reason, reported_at) VALUES ('H', 'PowerShell_transcript.H.a.txt', '2026-11-01T00:00:00Z', 10, 'transcript-no-4688', '2026-11-01T00:00:00Z');
INSERT INTO host_losses (host, file, recorded_at, size_bytes, reason, reported_at) VALUES ('H', 'seg.mp4', '2026-11-01T00:00:00Z', 10, 'age', '2026-11-01T00:00:00Z');
INSERT INTO terminal_sessions (host, session_user, kind, started_at, ended_at, duration_sec, input, size_bytes, external_id, uploaded_at, source)
VALUES ('H', 'u', 'terminal-input', '2026-11-01T00:00:00Z', '2026-11-01T00:00:01Z', 1, $in, 0, 'H:in:1', '2026-11-01T00:00:01Z', 'win-transcript');
UPDATE terminal_sessions SET input_decoded = NULL;
UPDATE host_losses SET acknowledged_at = NULL;
DELETE FROM schema_migrations WHERE version = 33;
ALTER TABLE terminal_sessions DROP COLUMN origin; ALTER TABLE terminal_sessions DROP COLUMN mode; ALTER TABLE terminal_sessions DROP COLUMN input_decoded;";
            cmd.Parameters.AddWithValue("$in", $"powershell -EncodedCommand {Encoded}");
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        _ = new Db(path);
        using var check = new SqliteConnection($"Data Source={path};Pooling=False");
        check.Open();
        using var q = check.CreateCommand();
        q.CommandText = "SELECT (SELECT acknowledged_at IS NOT NULL FROM host_losses WHERE reason = 'transcript-no-4688'), (SELECT acknowledged_at IS NULL FROM host_losses WHERE reason = 'age'), (SELECT input_decoded FROM terminal_sessions WHERE external_id = 'H:in:1')";
        using var r = q.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(1, r.GetInt64(0));
        Assert.Equal(1, r.GetInt64(1));
        Assert.Contains(Script, r.GetString(2));
        _ = db;
    }
}
