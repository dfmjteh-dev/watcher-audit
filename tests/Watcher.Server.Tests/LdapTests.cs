using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Watcher.Server.Tests;

// H1 (roadmap-next.md): вход через AD/LDAP.
public class LdapTests : IDisposable
{
    private const string Admins = "CN=Watcher-Admins,OU=Groups,DC=corp,DC=local";
    private const string Viewers = "CN=Watcher-Viewers,OU=Groups,DC=corp,DC=local";
    private const string Other = "CN=Accounting,OU=Groups,DC=corp,DC=local";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"watcher-ldap-{Guid.NewGuid():N}");
    private readonly Db _db;
    private readonly FakeDirectory _directory = new();
    private readonly ServerHealth _health = new();
    private DateTime _now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    public LdapTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new Db(Path.Combine(_dir, "watcher.db"));
    }

    public void Dispose()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var key in new[] { "Server__DataDir", "Server__DbPath", "Server__AdminUser", "Server__AdminPassword", "Server__CookieSecure" })
            Environment.SetEnvironmentVariable(key, null);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static LdapConfig Cfg() => new()
    {
        Url = "ldaps://dc.corp.local",
        SearchBase = "DC=corp,DC=local",
        GroupRoles = { new() { Group = "Watcher-Admins", Role = "admin" }, new() { Group = Viewers, Role = "viewer" } },
        RecheckMinutes = 15,
    };

    private DirectoryAuth Auth(LdapConfig? cfg = null, bool enabled = true) =>
        new(cfg ?? Cfg(), _db, _health, NullLogger<DirectoryAuth>.Instance, enabled ? _directory : null, () => _now);

    private sealed class FakeDirectory : IDirectory
    {
        public readonly Dictionary<string, (string Password, DirectoryUser User)> Users = new(StringComparer.OrdinalIgnoreCase);
        public bool Down;
        public int Lookups;

        public void Add(string login, string password, bool disabled = false, params string[] groups) =>
            Users[login] = (password, new DirectoryUser($"CN={login},OU=Users,DC=corp,DC=local", login, groups, disabled));

        public void Set(string login, Func<DirectoryUser, DirectoryUser> change) =>
            Users[login] = (Users[login].Password, change(Users[login].User));

        public Task<DirectoryUser?> AuthenticateAsync(string login, string password, CancellationToken ct)
        {
            if (Down) throw new DirectoryUnavailableException("down", new IOException());
            return Task.FromResult(Users.TryGetValue(login, out var u) && u.Password == password && password != "" ? u.User : null);
        }

        public Task<DirectoryUser?> LookupAsync(string dn, CancellationToken ct)
        {
            Lookups++;
            if (Down) throw new DirectoryUnavailableException("down", new IOException());
            return Task.FromResult<DirectoryUser?>(Users.Values.Select(v => v.User).FirstOrDefault(u => u.Dn == dn));
        }
    }

    // --- роли ---

    [Fact]
    public void MapRole_by_dn_or_cn_highest_wins()
    {
        var map = Cfg().GroupRoles;
        Assert.Equal("admin", DirectoryAuth.MapRole(new[] { Viewers, Admins.ToUpperInvariant() }, map));
        Assert.Equal("viewer", DirectoryAuth.MapRole(new[] { Viewers }, map));
        Assert.Null(DirectoryAuth.MapRole(new[] { Other }, map));
        Assert.Null(DirectoryAuth.MapRole(Array.Empty<string>(), map));
    }

    [Fact]
    public void CommonName_handles_escaped_comma() =>
        Assert.Equal("Smith, John", DirectoryAuth.CommonName("CN=Smith\\, John,OU=Users,DC=corp"));

    [Fact]
    public void Filter_values_are_escaped() =>
        Assert.Equal("a\\2a\\28b\\29\\5cc\\00", LdapDirectory.EscapeFilter("a*(b)\\c\0"));

    [Theory]
    [InlineData("ldap://dc", false, "открытым текстом")]
    [InlineData("http://dc", false, "ldaps://")]
    [InlineData("ldaps://dc", false, null)]
    [InlineData("ldap://dc", true, null)]
    public void Config_validation(string url, bool startTls, string? error)
    {
        var c = Cfg();
        c.Url = url;
        c.StartTls = startTls;
        if (error is null) Assert.Null(c.Validate()); else Assert.Contains(error, c.Validate());
    }

    [Fact]
    public void Config_without_group_roles_is_invalid()
    {
        var c = Cfg();
        c.GroupRoles.Clear();
        Assert.Contains("GroupRoles", c.Validate());
    }

    // --- вход ---

    [Fact]
    public async Task Login_creates_directory_user_with_role_from_groups()
    {
        _directory.Add("alice", "pw", false, Admins);

        var r = await Auth().LoginAsync("alice", "pw", default);

        Assert.Null(r.FailReason);
        Assert.Equal("admin", r.User!.Role);
        Assert.True(r.User.IsLdap);
        Assert.Equal("", r.User.PasswordHash);
        Assert.False(UserAuth.VerifyPassword(r.User.PasswordHash, ""));   // локального пароля нет
    }

    [Theory]
    [InlineData("alice", "wrong", "bad password or unknown user")]
    [InlineData("nobody", "pw", "bad password or unknown user")]
    [InlineData("carol", "pw", "no watcher group in directory")]
    [InlineData("dave", "pw", "disabled in directory")]
    public async Task Login_failures(string login, string password, string reason)
    {
        _directory.Add("alice", "pw", false, Admins);
        _directory.Add("carol", "pw", false, Other);
        _directory.Add("dave", "pw", true, Viewers);

        var r = await Auth().LoginAsync(login, password, default);

        Assert.Null(r.User);
        Assert.Equal(reason, r.FailReason);
    }

    [Fact]
    public async Task Local_user_with_same_name_wins()
    {
        _db.InsertUser("alice", UserAuth.HashPassword("local-password-1"), "viewer");
        _directory.Add("alice", "pw", false, Admins);

        var r = await Auth().LoginAsync("alice", "pw", default);

        Assert.Equal("name taken by local user", r.FailReason);
        Assert.False(_db.UserByUsername("alice")!.IsLdap);
    }

    [Fact]
    public async Task Directory_down_fails_login_and_counts_error()
    {
        _directory.Down = true;

        var r = await Auth().LoginAsync("alice", "pw", default);

        Assert.Equal("ldap unavailable", r.FailReason);
        Assert.Equal(1, _health.LdapErrors);
    }

    [Fact]
    public async Task Locally_disabled_directory_user_cannot_login()
    {
        _directory.Add("alice", "pw", false, Admins);
        var u = (await Auth().LoginAsync("alice", "pw", default)).User!;
        _db.SetUserDisabled(u.Id, true);

        Assert.Equal("disabled", (await Auth().LoginAsync("alice", "pw", default)).FailReason);
    }

    // --- перепроверка сессии ---

    [Fact]
    public async Task Recheck_respects_interval_and_revokes_on_directory_changes()
    {
        _directory.Add("bob", "pw", false, Viewers);
        var auth = Auth();
        var user = (await auth.LoginAsync("bob", "pw", default)).User!;

        _now += TimeSpan.FromMinutes(5);
        Assert.True((await auth.RecheckAsync(user, default)).Keep);
        Assert.Equal(0, _directory.Lookups);                       // интервал не прошёл

        _now += TimeSpan.FromMinutes(15);
        Assert.True((await auth.RecheckAsync(user, default)).Keep);
        Assert.Equal(1, _directory.Lookups);

        _directory.Set("bob", u => u with { Groups = new[] { Other } });
        _now += TimeSpan.FromMinutes(16);
        var r = await auth.RecheckAsync(user, default);
        Assert.False(r.Keep);
        Assert.Equal("no watcher group in directory", r.Reason);
    }

    [Theory]
    [InlineData("disabled", "disabled in directory")]
    [InlineData("removed", "removed from directory")]
    public async Task Recheck_revokes_disabled_or_removed_user(string change, string reason)
    {
        _directory.Add("bob", "pw", false, Viewers);
        var auth = Auth();
        var user = (await auth.LoginAsync("bob", "pw", default)).User!;
        if (change == "disabled") _directory.Set("bob", u => u with { Disabled = true });
        else _directory.Users.Remove("bob");
        _now += TimeSpan.FromMinutes(16);

        var r = await auth.RecheckAsync(user, default);

        Assert.False(r.Keep);
        Assert.Equal(reason, r.Reason);
    }

    [Fact]
    public async Task Recheck_role_change_updates_db_and_forces_relogin()
    {
        _directory.Add("bob", "pw", false, Viewers);
        var auth = Auth();
        var user = (await auth.LoginAsync("bob", "pw", default)).User!;
        _directory.Set("bob", u => u with { Groups = new[] { Admins } });
        _now += TimeSpan.FromMinutes(16);

        var r = await auth.RecheckAsync(user, default);

        Assert.False(r.Keep);
        Assert.Contains("viewer → admin", r.Reason);
        Assert.Equal("admin", _db.UserById(user.Id)!.Role);
    }

    // H7: право выгрузки у пользователя каталога — из Server:Ldap:ExportGroups,
    // при входе и при перепроверке; сессия при этом не закрывается.
    [Fact]
    public async Task Export_permission_follows_export_groups()
    {
        var cfg = Cfg();
        cfg.ExportGroups.Add("Watcher-Exporters");
        _directory.Add("bob", "pw", false, Viewers, "CN=Watcher-Exporters,OU=Groups,DC=corp,DC=local");
        var auth = Auth(cfg);

        var user = (await auth.LoginAsync("bob", "pw", default)).User!;
        Assert.True(user.CanExport);
        Assert.True(_db.UserById(user.Id)!.CanExport);

        _directory.Set("bob", u => u with { Groups = new[] { Viewers } });
        _now += TimeSpan.FromMinutes(16);
        var r = await auth.RecheckAsync(_db.UserById(user.Id)!, default);
        Assert.True(r.Keep);
        Assert.False(_db.UserById(user.Id)!.CanExport);

        // Без ExportGroups в конфиге права нет ни у кого из каталога.
        _directory.Add("eve", "pw", false, Admins, "CN=Watcher-Exporters,OU=Groups,DC=corp,DC=local");
        Assert.False((await Auth().LoginAsync("eve", "pw", default)).User!.CanExport);
    }

    [Fact]
    public async Task Recheck_keeps_session_when_directory_is_down()
    {
        _directory.Add("bob", "pw", false, Viewers);
        var auth = Auth();
        var user = (await auth.LoginAsync("bob", "pw", default)).User!;
        _directory.Down = true;
        _now += TimeSpan.FromMinutes(16);

        Assert.True((await auth.RecheckAsync(user, default)).Keep);
        Assert.Equal(1, _health.LdapErrors);
    }

    [Fact]
    public async Task Recheck_revokes_when_ldap_turned_off()
    {
        _directory.Add("bob", "pw", false, Viewers);
        var user = (await Auth().LoginAsync("bob", "pw", default)).User!;

        var r = await Auth(enabled: false).RecheckAsync(user, default);

        Assert.False(r.Keep);
    }

    // --- через HTTP ---

    private WebApplicationFactory<Program>? _factory;

    private HttpClient StartServer()
    {
        Environment.SetEnvironmentVariable("Server__DataDir", _dir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddSingleton(sp => new DirectoryAuth(Cfg(), sp.GetRequiredService<Db>(), sp.GetRequiredService<ServerHealth>(),
                NullLogger<DirectoryAuth>.Instance, _directory, () => _now))));
        return NewClient();
    }

    private HttpClient NewClient()
    {
        var c = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return c;
    }

    private static Task<HttpResponseMessage> Login(HttpClient c, string user, string pw) =>
        c.PostAsJsonAsync("/api/login", new { username = user, password = pw });

    [Fact]
    public async Task Http_local_admin_still_works_with_ldap_enabled()
    {
        using var c = StartServer();
        using var r = await Login(c, "admin", "test-password-123");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Http_directory_user_login_and_restrictions()
    {
        _directory.Add("bob", "bob-dir-pw", false, Viewers);
        using var admin = StartServer();
        (await Login(admin, "admin", "test-password-123")).Dispose();
        using var bob = NewClient();

        using (var r = await Login(bob, "bob", "bob-dir-pw")) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var me = await bob.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal("viewer", me.GetProperty("role").GetString());
        Assert.Equal("ldap", me.GetProperty("authSource").GetString());
        using (var r = await bob.PostAsJsonAsync("/api/me/password", new { currentPassword = "x", newPassword = "new-password-123" }))
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);

        var users = await admin.GetFromJsonAsync<JsonElement>("/api/users");
        var bobRow = users.EnumerateArray().Single(u => u.GetProperty("username").GetString() == "bob");
        Assert.Equal("ldap", bobRow.GetProperty("authSource").GetString());
        long id = bobRow.GetProperty("id").GetInt64();
        using (var r = await admin.PostAsJsonAsync($"/api/users/{id}/reset-password", new { }))
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        using (var r = await admin.PatchAsJsonAsync($"/api/users/{id}", new { role = "admin" }))
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Http_session_revoked_after_disable_in_directory()
    {
        _directory.Add("bob", "bob-dir-pw", false, Viewers);
        using var bob = StartServer();
        (await Login(bob, "bob", "bob-dir-pw")).Dispose();
        using (var r = await bob.GetAsync("/api/inventory")) Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        _directory.Set("bob", u => u with { Disabled = true });
        _now += TimeSpan.FromMinutes(16);

        using (var r = await bob.GetAsync("/api/inventory")) Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        using (var r = await bob.GetAsync("/api/inventory")) Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);   // cookie снята
    }

    [Fact]
    public void Migration_2_adds_ldap_columns()
    {
        Assert.Equal("users-ldap", Db.Migrations[1].Name);
        long id = _db.InsertUser("local", "h", "viewer");
        Assert.Equal("local", _db.UserById(id)!.AuthSource);
    }
}
