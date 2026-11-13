using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Watcher.Server.Tests;

// 0.12: настройки AD из веб-UI (server_settings) главнее Server:Ldap из
// appsettings/.env, применяются без перезапуска, пароль — только на запись.
public class LdapSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"watcher-ldapset-{Guid.NewGuid():N}");
    private readonly Db _db;

    public LdapSettingsTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new Db(Path.Combine(_dir, "watcher.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static LdapConfig Valid(string url = "ldaps://dc1.corp.local") => new()
    {
        Url = url,
        BindDn = "svc@corp.local",
        BindPassword = "file-secret",
        SearchBase = "DC=corp,DC=local",
        GroupRoles = { new LdapGroupRole { Group = "Watcher-Admins", Role = "admin" } },
    };

    private DirectoryAuth Auth(LdapConfig file) =>
        new(new ServerConfig { Ldap = file }, _db, new ServerHealth(), NullLogger<DirectoryAuth>.Instance);

    [Fact]
    public void Settings_from_ui_win_over_file_and_reset_returns_to_file()
    {
        Assert.Equal("none", Auth(new LdapConfig()).Source);
        Assert.Equal("file", Auth(Valid()).Source);

        _db.SetSetting(DirectoryAuth.SettingKey, JsonSerializer.Serialize(Valid("ldaps://dc2.corp.local")), "admin");
        var auth = Auth(Valid());
        Assert.Equal("ui", auth.Source);
        Assert.True(auth.Enabled);
        Assert.Equal("ldaps://dc2.corp.local", auth.Config.Url);

        _db.DeleteSetting(DirectoryAuth.SettingKey);
        auth.Apply(null);
        Assert.Equal("file", auth.Source);
        Assert.Equal("ldaps://dc1.corp.local", auth.Config.Url);
    }

    [Fact]
    public void Empty_password_keeps_the_current_one_and_invalid_config_is_rejected()
    {
        var req = new LdapSettingsRequest("ldaps://dc3.corp.local", false, null, "svc@corp.local", "",
            "DC=corp,DC=local", null, null, [new("Watcher-Viewers", "viewer")], null);
        var cfg = req.ToConfig(Valid());
        Assert.Equal("file-secret", cfg.BindPassword);
        Assert.Null(cfg.Validate());

        var plain = req with { Url = "ldap://dc3.corp.local" };
        Assert.Contains("открытым текстом", plain.ToConfig(Valid()).Validate());
        // 0.12.1: явное разрешение — ldap:// без StartTLS принимается и помечается.
        var allowed = (plain with { AllowPlaintext = true }).ToConfig(Valid());
        Assert.Null(allowed.Validate());
        Assert.True(allowed.Plaintext);
        Assert.False((req with { Url = "ldap://dc3.corp.local", StartTls = true }).ToConfig(Valid()).Plaintext);
        var badCa = req with { CaCertPem = "not a cert" };
        Assert.Contains("PEM", badCa.ToConfig(Valid()).Validate());
        var noGroups = req with { GroupRoles = [] };
        Assert.Contains("GroupRoles", noGroups.ToConfig(Valid()).Validate());
    }
}
