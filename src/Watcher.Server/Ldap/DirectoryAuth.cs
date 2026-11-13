using System.Collections.Concurrent;

namespace Watcher.Server;

// H1: вход пользователей каталога и перепроверка их сессий. Локальные
// пользователи (в т.ч. аварийный admin) сюда не попадают — у них прежний вход
// по паролю из БД.
public sealed class DirectoryAuth
{
    private static readonly string[] RoleRank = { "viewer", "operator", "admin" };

    // 0.12: настройки можно поменять в веб-UI (Настройки → Вход через AD) без
    // перезапуска — конфиг и каталог меняются вместе, одним объектом.
    private sealed record Active(LdapConfig Cfg, IDirectory? Directory, string Source);
    private volatile Active _active;
    private readonly LdapConfig _fileCfg;
    private readonly Db _db;
    private readonly ServerHealth _health;
    private readonly ILogger<DirectoryAuth> _log;
    private readonly Func<DateTime> _clock;
    private readonly ConcurrentDictionary<long, DateTime> _lastCheck = new();

    // Ключ server_settings: конфиг из веб-UI главнее Server:Ldap из appsettings/.env.
    public const string SettingKey = "ldap";

    public DirectoryAuth(ServerConfig cfg, Db db, ServerHealth health, ILogger<DirectoryAuth> log)
    {
        _fileCfg = cfg.Ldap;
        _db = db;
        _health = health;
        _log = log;
        _clock = () => DateTime.UtcNow;
        _active = Load(FromSetting(db.GetSetting(SettingKey), log));
    }

    // Для тестов — свой каталог и часы.
    public DirectoryAuth(LdapConfig cfg, Db db, ServerHealth health, ILogger<DirectoryAuth> log, IDirectory? directory, Func<DateTime>? clock)
    {
        _fileCfg = cfg;
        _active = new Active(cfg, directory, "file");
        _db = db;
        _health = health;
        _log = log;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    private LdapConfig _cfg => _active.Cfg;
    private IDirectory? _directory => _active.Directory;

    // Откуда сейчас настройки: ui — веб-UI, file — appsettings/.env, none — не заданы.
    public string Source => _active.Source;
    public LdapConfig Config => _active.Cfg;
    public LdapConfig FileConfig => _fileCfg;

    // Новый конфиг из веб-UI (null — убрать, вернуться к appsettings/.env).
    public void Apply(LdapConfig? uiCfg)
    {
        _active = Load(uiCfg);
        _lastCheck.Clear();
    }

    private Active Load(LdapConfig? uiCfg)
    {
        var cfg = uiCfg ?? _fileCfg;
        string source = uiCfg is not null ? "ui" : _fileCfg.Enabled ? "file" : "none";
        return new Active(cfg, CreateDirectory(cfg, _log), source);
    }

    public static LdapConfig? FromSetting(string? json, ILogger log)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<LdapConfig>(json); }
        catch (System.Text.Json.JsonException e)
        {
            log.LogError(e, "LDAP settings in the database are not valid JSON, ignored");
            return null;
        }
    }

    // Неверный конфиг — LDAP выключен, но сервер стартует: войти может
    // локальный admin и поправить настройки.
    private static IDirectory? CreateDirectory(LdapConfig cfg, ILogger log)
    {
        if (!cfg.Enabled) return null;
        if (cfg.Validate() is { } error)
        {
            log.LogError("LDAP disabled: {Error}", error);
            return null;
        }
        log.LogInformation("LDAP sign-in enabled: {Url}, {Groups} group mappings", cfg.Url, cfg.GroupRoles.Count);
        if (cfg.Plaintext)
            log.LogWarning("LDAP without encryption (AllowPlaintext): user passwords go to {Url} in clear text", cfg.Url);
        return new LdapDirectory(cfg);
    }

    public bool Enabled => _directory is not null;

    public sealed record LoginResult(User? User, string? FailReason);

    public async Task<LoginResult> LoginAsync(string login, string password, CancellationToken ct)
    {
        DirectoryUser? du;
        try { du = await _directory!.AuthenticateAsync(login, password, ct); }
        catch (Exception e) when (e is DirectoryUnavailableException or InvalidOperationException)
        {
            _health.CountLdapError();
            _log.LogWarning("LDAP sign-in for {User} failed: {Error}", login, e.Message);
            return new(null, "ldap unavailable");
        }
        if (du is null) return new(null, "bad password or unknown user");
        if (du.Disabled) return new(null, "disabled in directory");
        if (MapRole(du.Groups, _cfg.GroupRoles) is not { } role) return new(null, "no watcher group in directory");
        if (_db.UpsertLdapUser(du.Login, du.Dn, role) is not { } user) return new(null, "name taken by local user");
        if (user.Disabled) return new(null, "disabled");
        user = SyncExport(user, du.Groups);
        _lastCheck[user.Id] = _clock();
        return new(user, null);
    }

    // true — сессия остаётся; false — закрыть (reason — для журнала). Каталог
    // недоступен — true: падение контроллера домена не должно выкидывать всех
    // (новые входы через LDAP при этом не проходят).
    public async Task<(bool Keep, string? Reason)> RecheckAsync(User user, CancellationToken ct)
    {
        if (!Enabled) return (false, "ldap sign-in turned off");
        DateTime now = _clock();
        if (_lastCheck.TryGetValue(user.Id, out var last) && now - last < TimeSpan.FromMinutes(Math.Max(1, _cfg.RecheckMinutes)))
            return (true, null);

        DirectoryUser? du;
        try { du = await _directory!.LookupAsync(user.LdapDn!, ct); }
        catch (Exception e) when (e is DirectoryUnavailableException or InvalidOperationException)
        {
            _health.CountLdapError();
            _log.LogWarning("LDAP recheck for {User} skipped, directory unavailable: {Error}", user.Username, e.Message);
            return (true, null);
        }
        _lastCheck[user.Id] = now;
        if (du is null) return (false, "removed from directory");
        if (du.Disabled) return (false, "disabled in directory");
        string? role = MapRole(du.Groups, _cfg.GroupRoles);
        if (role is null) return (false, "no watcher group in directory");
        // Роль в cookie (политики доступа смотрят на claim) — при смене роли
        // проще и надёжнее перелогинить, чем переписывать сессию.
        if (role != user.Role)
        {
            _db.UpdateUserRole(user.Id, role);
            return (false, $"role changed in directory: {user.Role} → {role}");
        }
        // Право выгрузки проверяется по БД на каждый запрос — сессию не закрываем.
        SyncExport(user, du.Groups);
        return (true, null);
    }

    private User SyncExport(User user, IEnumerable<string> groups)
    {
        bool want = InAnyGroup(groups, _cfg.ExportGroups);
        if (want == user.CanExport) return user;
        _db.SetUserCanExport(user.Id, want);
        _log.LogInformation("Export permission of {User} set from directory: {CanExport}", user.Username, want);
        return user with { CanExport = want };
    }

    public static bool InAnyGroup(IEnumerable<string> userGroups, IReadOnlyList<string> wanted) =>
        MapRole(userGroups, wanted.Select(g => new LdapGroupRole { Group = g, Role = "viewer" }).ToList()) is not null;

    // Старшая роль среди групп пользователя. Group в настройке — DN группы или
    // её имя (CN), без учёта регистра.
    public static string? MapRole(IEnumerable<string> userGroups, IReadOnlyList<LdapGroupRole> map)
    {
        var groups = userGroups.ToList();
        int best = -1;
        foreach (var m in map)
        {
            bool byDn = m.Group.Contains('=');
            bool hit = groups.Any(g => byDn
                ? string.Equals(g, m.Group, StringComparison.OrdinalIgnoreCase)
                : string.Equals(CommonName(g), m.Group, StringComparison.OrdinalIgnoreCase));
            if (hit) best = Math.Max(best, Array.IndexOf(RoleRank, m.Role));
        }
        return best >= 0 ? RoleRank[best] : null;
    }

    // CN=Watcher-Admins,OU=Groups,DC=corp → Watcher-Admins (с учётом «\,» в имени).
    public static string CommonName(string dn)
    {
        int eq = dn.IndexOf('=');
        if (eq < 0) return dn;
        int end = eq + 1;
        while (end < dn.Length && !(dn[end] == ',' && dn[end - 1] != '\\')) end++;
        return dn[(eq + 1)..end].Replace("\\,", ",");
    }
}
