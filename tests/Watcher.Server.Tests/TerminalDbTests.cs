using System.Linq;
using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

// Фаза 8 (roadmap.md) — тесты на то, что HTTP-уровень (TerminalUploadEndpointTests)
// не покрывает прицельно: search_text LIKE и groupId JOIN в QueryTerminal,
// EnsureHostExists (не трогает существующий heartbeat).
public class TerminalDbTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;

    public TerminalDbTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"watcher-term-db-{Guid.NewGuid():N}.db");
        _db = new Db(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    private long InsertInput(string host, string user, string input, string searchText, DateTime? startedAt = null)
    {
        var s = startedAt ?? DateTime.UtcNow;
        return _db.InsertTerminalSession(new TerminalSession(
            Id: 0, Host: host, SessionUser: user, Kind: "terminal-input", RemoteAddr: null,
            StartedAt: s, EndedAt: s.AddSeconds(5), DurationSec: 5, ExitCode: null,
            Input: input, OutFilePath: null, TimingFilePath: null,
            SizeBytes: 0, Sha256: null, ExternalId: Guid.NewGuid().ToString(), UploadedAt: DateTime.UtcNow),
            searchText);
    }

    // 0.14.4: фильтр «Тип» — интерактивные (с терминалом) и команды без терминала.
    [Fact]
    public void QueryTerminal_type_separates_interactive_from_exec()
    {
        TerminalSession Row(string source) => new(
            Id: 0, Host: "h", SessionUser: "alice", Kind: "terminal-input", RemoteAddr: null,
            StartedAt: DateTime.UtcNow, EndedAt: DateTime.UtcNow, DurationSec: 1, ExitCode: null,
            Input: "ls", OutFilePath: null, TimingFilePath: null, SizeBytes: 0, Sha256: null,
            ExternalId: Guid.NewGuid().ToString(), UploadedAt: DateTime.UtcNow, Source: source);
        _db.InsertTerminalSession(Row("linux-tty-audit"), "ls");
        _db.InsertTerminalSession(Row("linux-exec"), "ls");
        _db.InsertTerminalSession(Row("win-transcript"), "ls");

        string[] Sources(string? type) => _db.QueryTerminal(null, null, null, null, null, null, null, 100, type: type)
            .Select(r => r.Source!).Order().ToArray();
        Assert.Equal(new[] { "linux-tty-audit", "win-transcript" }, Sources("interactive"));
        Assert.Equal(new[] { "linux-exec" }, Sources("exec"));
        Assert.Equal(3, Sources(null).Length);
    }

    // 0.15.0: ввод из auditd (linux-exec), но к сессии привязан вывод script(1) —
    // запись воспроизводится, значит сессия интерактивная, а не «без терминала».
    [Fact]
    public void QueryTerminal_exec_input_with_output_is_interactive()
    {
        var now = DateTime.UtcNow;
        long withOut = _db.InsertTerminalSession(new TerminalSession(
            Id: 0, Host: "h", SessionUser: "root", Kind: "terminal-input", RemoteAddr: null,
            StartedAt: now, EndedAt: now.AddMinutes(5), DurationSec: 300, ExitCode: null,
            Input: "vim /etc/hosts", OutFilePath: null, TimingFilePath: null, SizeBytes: 0, Sha256: null,
            ExternalId: Guid.NewGuid().ToString(), UploadedAt: now, Source: "linux-exec"), "vim");
        long bare = _db.InsertTerminalSession(new TerminalSession(
            Id: 0, Host: "h", SessionUser: "root", Kind: "terminal-input", RemoteAddr: null,
            StartedAt: now, EndedAt: now.AddSeconds(1), DurationSec: 1, ExitCode: null,
            Input: "uptime", OutFilePath: null, TimingFilePath: null, SizeBytes: 0, Sha256: null,
            ExternalId: Guid.NewGuid().ToString(), UploadedAt: now, Source: "linux-exec"), "uptime");
        long output = _db.InsertTerminalSession(new TerminalSession(
            Id: 0, Host: "h", SessionUser: "root", Kind: "terminal-output", RemoteAddr: null,
            StartedAt: now.AddSeconds(1), EndedAt: now.AddMinutes(4), DurationSec: 239, ExitCode: 0,
            Input: null, OutFilePath: "x.out", TimingFilePath: "x.timing", SizeBytes: 10, Sha256: null,
            ExternalId: Guid.NewGuid().ToString(), UploadedAt: now, Source: "linux-script"), "vim");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE terminal_sessions SET parent_id = $p WHERE id = $o";
            cmd.Parameters.AddWithValue("$p", withOut);
            cmd.Parameters.AddWithValue("$o", output);
            cmd.ExecuteNonQuery();
        }

        long[] Ids(string type) => _db.QueryTerminal(null, null, null, null, null, null, null, 100, sessions: true, type: type)
            .Select(r => r.Id).Order().ToArray();
        Assert.Equal(new[] { withOut }, Ids("interactive"));
        Assert.Equal(new[] { bare }, Ids("exec"));
    }

    // 0.14.4: пользователь — подстрока без учёта регистра (поиск по набранному).
    [Fact]
    public void QueryTerminal_user_is_case_insensitive_substring()
    {
        InsertInput("h", "a.butakov", "ls", "ls");
        InsertInput("h", "ivan", "ls", "ls");
        InsertInput("h", "x_y", "ls", "ls");
        Assert.Equal(new[] { "a.butakov" }, _db.QueryTerminal(null, "BUTA", null, null, null, null, null, 100).Select(r => r.SessionUser).ToArray());
        Assert.Equal(new[] { "x_y" }, _db.QueryTerminal(null, "_", null, null, null, null, null, 100).Select(r => r.SessionUser).ToArray());
        Assert.Equal(3, _db.QueryTerminal(null, " ", null, null, null, null, null, 100).Count);
    }

    [Fact]
    public void QueryTerminal_search_matches_search_text_not_just_input_column()
    {
        // search_text для terminal-output хранит содержимое .out, а не Input
        // (у output Input всегда null) — явная проверка, что фильтр реально
        // смотрит в search_text.
        _db.InsertTerminalSession(new TerminalSession(
            Id: 0, Host: "h", SessionUser: "alice", Kind: "terminal-output", RemoteAddr: null,
            StartedAt: DateTime.UtcNow, EndedAt: DateTime.UtcNow, DurationSec: 1, ExitCode: 0,
            Input: null, OutFilePath: "x.out", TimingFilePath: "x.timing",
            SizeBytes: 10, Sha256: "deadbeef", ExternalId: "ext-1", UploadedAt: DateTime.UtcNow),
            searchText: "drop table users");

        var hit = _db.QueryTerminal(null, null, null, null, null, "drop table", null, 100);
        var miss = _db.QueryTerminal(null, null, null, null, null, "nothing like this", null, 100);

        Assert.Single(hit);
        Assert.Empty(miss);
    }

    [Fact]
    public void QueryTerminal_search_is_case_sensitive_substring_like()
    {
        InsertInput("h", "alice", "echo hello world", "echo hello world");

        var rows = _db.QueryTerminal(null, null, null, null, null, "hello", null, 100);

        Assert.Single(rows);
    }

    [Fact]
    public void QueryTerminal_search_escapes_percent_and_underscore_as_literals()
    {
        InsertInput("h", "alice", "progress 50%_done", "progress 50%_done");

        // Без экранирования "%" и "_" в LIKE были бы wildcard'ами и совпали бы
        // с чем угодно — здесь ищем их буквально.
        var rows = _db.QueryTerminal(null, null, null, null, null, "50%_done", null, 100);

        Assert.Single(rows);
    }

    [Fact]
    public void QueryTerminal_groupId_filters_through_hosts_join()
    {
        _db.EnsureHostExists("host-in-group");
        _db.EnsureHostExists("host-no-group");
        long groupId = _db.CreateGroup("prod");
        long hostId = _db.ListFleetHosts(null, 100, 0).Single(h => h.Name == "host-in-group").Id;
        _db.SetHostGroup(hostId, groupId);

        InsertInput("host-in-group", "alice", "a", "a");
        InsertInput("host-no-group", "bob", "b", "b");

        var rows = _db.QueryTerminal(null, null, null, null, null, null, groupId, 100);

        Assert.Single(rows);
        Assert.Equal("host-in-group", rows[0].Host);
    }

    [Fact]
    public void EnsureHostExists_does_not_clobber_existing_heartbeat_data()
    {
        _db.UpsertHeartbeat("h", "10.0.0.5", "1.2.3", droppedSegments: 7);

        _db.EnsureHostExists("h"); // не должно перетереть ip/версию существующим ON CONFLICT DO NOTHING

        var host = _db.ListFleetHosts(null, 100, 0).Single(x => x.Name == "h");
        Assert.Equal("10.0.0.5", host.IpAddress);
        Assert.Equal("1.2.3", host.AgentVersion);
    }

    [Fact]
    public void EnsureHostExists_creates_host_row_visible_in_fleet_without_heartbeat()
    {
        _db.EnsureHostExists("terminal-only-host");

        var host = _db.ListFleetHosts(null, 100, 0).SingleOrDefault(x => x.Name == "terminal-only-host");
        Assert.NotNull(host);
        Assert.Null(host!.LastHeartbeatAt);
    }
}
