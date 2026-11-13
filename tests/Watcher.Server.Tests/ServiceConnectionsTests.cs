using System.Text.Json;
using Xunit;

namespace Watcher.Server.Tests;

// 0.13 (п.7): служебные подключения в «Консоли» (ServiceConnections.cs).
public class ServiceConnectionsTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-svc-{Guid.NewGuid():N}");
    private readonly Db _db;
    private readonly RecordingIngest _ingest;
    private static readonly AgentCaller SharedToken = new(null);
    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private const string Lldp = "09:01:00Z /root$ lldpctl -f json\n09:01:01Z /root$ lldpctl -f keyvalue\n";

    public ServiceConnectionsTests()
    {
        Directory.CreateDirectory(_dataDir);
        _db = new Db(Path.Combine(_dataDir, "watcher.db"));
        _ingest = new RecordingIngest(_db, new ServerConfig { DataDir = _dataDir });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private static List<ServiceRule> R(string rulesJson) => ServiceConnections.Rules($"{{\"serviceConnections\":{rulesJson}}}");

    [Theory]
    [InlineData("root", "10.20.30.40", Lldp, true)]
    [InlineData("ROOT", "10.20.30.40", Lldp, true)]
    [InlineData("root", "10.20.30.41", Lldp, false)]
    [InlineData("root", null, Lldp, false)]
    [InlineData("alice", "10.20.30.40", Lldp, false)]
    // Чужая команда в той же сессии — правило не прячет то, что в нём не описано.
    [InlineData("root", "10.20.30.40", Lldp + "09:01:02Z /root$ cat /etc/shadow\n", false)]
    [InlineData("root", "10.20.30.40", "", false)]
    public void Every_command_must_match(string user, string? addr, string input, bool expected) =>
        Assert.Equal(expected, ServiceConnections.Match(R("[{\"user\":\"root\",\"from\":\"10.20.30.40\",\"command\":\"lldpctl\"}]"), user, addr, input) is not null);

    [Theory]
    [InlineData("10.20.30.0/24", "10.20.30.77", true)]
    [InlineData("10.20.30.0/24", "10.20.31.1", false)]
    [InlineData("10.20.30.77", "::ffff:10.20.30.77", true)]
    [InlineData("10.0.0.0/8", "?", false)]
    public void From_is_an_address_or_a_subnet(string from, string addr, bool expected) =>
        Assert.Equal(expected, ServiceConnections.AddrMatches(from, addr));

    [Fact]
    public void Rule_without_command_matches_any_commands_and_first_rule_wins()
    {
        var rules = R("[{\"user\":\"backup-bot\",\"from\":\"10.20.30.77\",\"action\":\"drop\"},{\"user\":\"backup-bot\"}]");
        Assert.True(ServiceConnections.Match(rules, "backup-bot", "10.20.30.77", "git-upload-pack x")!.Drop);
        Assert.False(ServiceConnections.Match(rules, "backup-bot", "10.0.0.1", "anything")!.Drop);
    }

    // Команда по ssh — через оболочку: «bash -c 'cmd'» и «cmd» — одна и та же команда.
    [Theory]
    [InlineData("bash -c uptime", "uptime")]
    [InlineData("/bin/sh -c 'git-upload-pack '\\''/srv/repo'\\'''", "git-upload-pack '/srv/repo'")]
    [InlineData("bash -c 'cat /etc/hosts' extra", "cat /etc/hosts")]
    [InlineData("bash --login", "bash --login")]
    [InlineData("lldpctl -f json", "lldpctl -f json")]
    public void Shell_wrapper_is_unwrapped(string cmd, string expected) =>
        Assert.Equal(expected, ServiceConnections.UnwrapShell(cmd));

    [Fact]
    public void Ssh_exec_session_matches_by_the_command_itself() =>
        Assert.NotNull(ServiceConnections.Match(R("[{\"user\":\"wtest\",\"command\":\"uptime\"}]"), "wtest", "10.20.30.40",
            "09:35:26Z /home/wtest$ bash -c uptime\n09:35:26Z /home/wtest$ uptime\n"));

    [Fact]
    public void Signature_drops_time_and_cwd() =>
        Assert.Equal("lldpctl -f json\nlldpctl -f keyvalue", ServiceConnections.Signature(Lldp));

    [Theory]
    [InlineData("[{\"user\":\"root\"}]", null)]
    [InlineData("[{\"from\":\"10.0.0.0/8\",\"action\":\"drop\"}]", null)]
    [InlineData("[{\"command\":\"lldpctl\"}]", "user or from")]
    [InlineData("[{\"user\":\"root\",\"from\":\"10.0.0\"}]", "IP address")]
    [InlineData("[{\"user\":\"root\",\"action\":\"delete\"}]", "hide or drop")]
    [InlineData("[{\"user\":\"root\",\"x\":1}]", "unknown key")]
    [InlineData("{\"user\":\"root\"}", "must be an array")]
    public void Config_is_validated(string rules, string? error)
    {
        string? err = AgentConfigValidation.Validate(JsonDocument.Parse($"{{\"serviceConnections\":{rules}}}").RootElement);
        if (error is null) Assert.Null(err);
        else Assert.Contains(error, err);
    }

    private long HostWithRules(string rulesJson)
    {
        _db.EnsureHostExists("srv-1");
        long id = _db.Inventory(onlyName: "srv-1").Single().Id!.Value;
        _db.SetHostConfig(id, $"{{\"serviceConnections\":{rulesJson}}}");
        return id;
    }

    private Task<IngestResult> Exec(int ses, string input, bool? live = null, long offset = 0, string user = "root", string addr = "10.20.30.40") =>
        _ingest.TerminalAsync(SharedToken, JsonSerializer.Serialize(new
        {
            kind = "terminal-input",
            host = "srv-1",
            sessionUser = user,
            sessionId = ses,
            remoteAddr = addr,
            startedAt = "2026-10-15T09:01:00Z",
            endedAt = "2026-10-15T09:01:02Z",
            input,
            inputOffset = offset,
            live,
            source = "linux-exec",
        }), null, null, null, Now);

    private List<TerminalSession> List(bool? service) =>
        _db.QueryTerminal("srv-1", null, null, null, null, null, null, 100, sessions: true, service: service);

    [Fact]
    public async Task Hide_rule_keeps_the_record_out_of_the_default_list()
    {
        HostWithRules("[{\"user\":\"root\",\"from\":\"10.20.30.40\",\"command\":\"lldpctl\"}]");
        Assert.Equal(200, (await Exec(1, Lldp)).StatusCode);
        Assert.Equal(200, (await Exec(2, "09:02:00Z /root$ rm -rf /tmp/x\n")).StatusCode);

        Assert.Equal(["09:02:00Z /root$ rm -rf /tmp/x\n"], List(false).Select(s => s.Input));
        Assert.Equal([Lldp], List(true).Select(s => s.Input));
        Assert.Equal(2, List(null).Count);
    }

    [Fact]
    public async Task Drop_rule_does_not_store_the_session()
    {
        HostWithRules("[{\"user\":\"root\",\"from\":\"10.20.30.40\",\"command\":\"lldpctl\",\"action\":\"drop\"}]");
        var r = await Exec(1, Lldp);
        Assert.Equal(200, r.StatusCode);
        Assert.True(JsonSerializer.SerializeToElement(r.Body).GetProperty("skipped").GetBoolean());
        Assert.Empty(List(null));
    }

    [Fact]
    public async Task Live_session_is_dropped_only_when_closed_and_a_foreign_command_unhides_it()
    {
        HostWithRules("[{\"user\":\"root\",\"command\":\"lldpctl\",\"action\":\"drop\"}]");
        // Пока идёт — только скрыта: следующая команда может оказаться чужой.
        const string first = "09:01:00Z /root$ lldpctl -f json\n";
        Assert.Equal(200, (await Exec(1, first, live: true)).StatusCode);
        Assert.Single(List(true));
        Assert.Equal(200, (await Exec(1, "09:01:01Z /root$ lldpctl -f keyvalue\n", live: false, offset: first.Length)).StatusCode);
        Assert.Empty(List(null));

        Assert.Equal(200, (await Exec(2, first, live: true)).StatusCode);
        Assert.Equal(200, (await Exec(2, "09:01:01Z /root$ passwd\n", live: false, offset: first.Length)).StatusCode);
        var kept = Assert.Single(List(false));
        Assert.Contains("passwd", kept.Input);
    }

    [Fact]
    public async Task Changing_rules_reclassifies_stored_sessions()
    {
        long hostId = HostWithRules("[]");
        await Exec(1, Lldp);
        await Exec(2, "09:05:00Z /home/bob$ vim notes\n", user: "bob");
        Assert.Empty(List(true));

        _db.SetHostConfig(hostId, "{\"serviceConnections\":[{\"user\":\"root\",\"command\":\"lldpctl\",\"action\":\"drop\"}]}");
        // drop задним числом не удаляет — уже сохранённое только скрывается.
        Assert.Equal(1, ServiceConnections.Reclassify(_db, ["srv-1"]));
        Assert.Single(List(true));
        Assert.Equal(2, List(null).Count);

        _db.ClearHostConfig(hostId);
        Assert.Equal(1, ServiceConnections.Reclassify(_db, ["srv-1"]));
        Assert.Empty(List(true));
    }

    [Fact]
    public void Rule_is_appended_to_existing_config()
    {
        string cfg = ServiceConnections.WithRule("{\"schemaVersion\":1,\"crf\":30}", new System.Text.Json.Nodes.JsonObject { ["user"] = "root" });
        Assert.Equal("{\"schemaVersion\":1,\"crf\":30,\"serviceConnections\":[{\"user\":\"root\"}]}", cfg);
        Assert.True(ServiceConnections.HasOwnRules(cfg));
        Assert.False(ServiceConnections.HasOwnRules("{\"crf\":30}"));
    }
}
