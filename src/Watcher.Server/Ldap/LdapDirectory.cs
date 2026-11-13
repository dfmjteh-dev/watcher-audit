using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Novell.Directory.Ldap;

namespace Watcher.Server;

// Пользователь каталога: DN, имя для Watcher, DN всех групп (с вложенными), отключён ли.
public sealed record DirectoryUser(string Dn, string Login, IReadOnlyList<string> Groups, bool Disabled);

public interface IDirectory
{
    // Логин + пароль. null — нет такого пользователя или пароль неверен.
    // Исключение — каталог недоступен или настроен неверно.
    Task<DirectoryUser?> AuthenticateAsync(string login, string password, CancellationToken ct);

    // Перепроверка уже вошедшего по DN, без пароля. null — пользователя больше нет.
    Task<DirectoryUser?> LookupAsync(string dn, CancellationToken ct);
}

public sealed class DirectoryUnavailableException : Exception
{
    public DirectoryUnavailableException(string message, Exception inner) : base(message, inner) { }
}

public sealed class LdapDirectory : IDirectory
{
    // AD: userAccountControl, бит ACCOUNTDISABLE.
    private const int UacAccountDisable = 0x2;
    // AD: поиск групп с учётом вложенности.
    private const string MatchingRuleInChain = "1.2.840.113556.1.4.1941";

    private readonly LdapConfig _cfg;
    private readonly X509Certificate2? _ca;
    private readonly string _host;
    private readonly int _port;
    private readonly bool _ssl;

    public LdapDirectory(LdapConfig cfg)
    {
        _cfg = cfg;
        var u = new Uri(cfg.Url);
        _host = u.Host;
        _ssl = u.Scheme == "ldaps";
        _port = u.IsDefaultPort || u.Port <= 0 ? (_ssl ? 636 : 389) : u.Port;
        if (!string.IsNullOrWhiteSpace(cfg.CaCertPem)) _ca = X509Certificate2.CreateFromPem(cfg.CaCertPem);
        else if (!string.IsNullOrEmpty(cfg.CaCertPath)) _ca = new X509Certificate2(cfg.CaCertPath);
    }

    // 0.12, «проверить подключение» в веб-UI: соединение, TLS, bind служебной
    // учётки и что база поиска существует. Ошибка — DirectoryUnavailableException.
    public async Task CheckAsync(CancellationToken ct)
    {
        using var conn = await ServiceConnectionAsync(ct);
        LdapEntry? found;
        try { found = await FindOneAsync(conn, _cfg.SearchBase, LdapConnection.ScopeBase, "(objectClass=*)", ct); }
        catch (LdapException e) { throw new DirectoryUnavailableException($"LDAP: база поиска «{_cfg.SearchBase}»: {e.ResultCodeToString()}", e); }
        if (found is null)
            throw new DirectoryUnavailableException($"LDAP: база поиска «{_cfg.SearchBase}» не найдена", new InvalidOperationException());
    }

    public async Task<DirectoryUser?> AuthenticateAsync(string login, string password, CancellationToken ct)
    {
        // Пустой пароль в LDAP — «анонимный bind», который проходит. Отсекаем здесь.
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(login)) return null;
        // DOMAIN\user — привычный ввод на Windows; в фильтр идёт только user.
        int slash = login.IndexOf('\\');
        if (slash >= 0) login = login[(slash + 1)..];

        using var conn = await ServiceConnectionAsync(ct);
        var entry = await FindOneAsync(conn, _cfg.SearchBase, LdapConnection.ScopeSub,
            string.Format(System.Globalization.CultureInfo.InvariantCulture, _cfg.UserFilter, EscapeFilter(login)), ct);
        if (entry is null) return null;

        // Проверка пароля — bind под найденным DN отдельным соединением. AD
        // отказывает в bind отключённой/заблокированной/просроченной учётке.
        using (var userConn = await ConnectAsync(ct))
        {
            try { await userConn.BindAsync(entry.Dn, password, ct); }
            catch (LdapException e) when (e.ResultCode == LdapException.InvalidCredentials) { return null; }
        }
        return await ToUserAsync(conn, entry, ct);
    }

    public async Task<DirectoryUser?> LookupAsync(string dn, CancellationToken ct)
    {
        using var conn = await ServiceConnectionAsync(ct);
        LdapEntry? entry;
        try { entry = await FindOneAsync(conn, dn, LdapConnection.ScopeBase, "(objectClass=*)", ct); }
        catch (LdapException e) when (e.ResultCode == LdapException.NoSuchObject) { return null; }
        return entry is null ? null : await ToUserAsync(conn, entry, ct);
    }

    private async Task<DirectoryUser> ToUserAsync(LdapConnection conn, LdapEntry entry, CancellationToken ct)
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (entry.GetOrDefault("memberOf", null) is { } memberOf)
            foreach (var g in memberOf.StringValueArray) groups.Add(g);
        if (_cfg.NestedGroups)
        {
            var results = await conn.SearchAsync(_cfg.SearchBase, LdapConnection.ScopeSub,
                $"(member:{MatchingRuleInChain}:={EscapeFilter(entry.Dn)})", new[] { "distinguishedName" }, false, SearchConstraints(), ct);
            await foreach (var g in Entries(results, ct)) groups.Add(g.Dn);
        }
        bool disabled = int.TryParse(entry.GetStringValueOrDefault("userAccountControl", "0"), out int uac) && (uac & UacAccountDisable) != 0;
        string loginValue = entry.GetStringValueOrDefault(_cfg.LoginAttribute, "") is { Length: > 0 } l ? l : entry.Dn;
        return new DirectoryUser(entry.Dn, loginValue, groups.ToList(), disabled);
    }

    private async Task<LdapEntry?> FindOneAsync(LdapConnection conn, string searchBase, int scope, string filter, CancellationToken ct)
    {
        var results = await conn.SearchAsync(searchBase, scope, filter,
            new[] { "memberOf", "userAccountControl", _cfg.LoginAttribute }, false, SearchConstraints(), ct);
        LdapEntry? found = null;
        await foreach (var e in Entries(results, ct))
        {
            // Больше одного совпадения — фильтр неоднозначен: не угадываем.
            if (found is not null) throw new InvalidOperationException($"LDAP: фильтр нашёл больше одного пользователя ({found.Dn}, {e.Dn}) — уточните Server:Ldap:UserFilter");
            found = e;
        }
        return found;
    }

    // AD при поиске от корня домена отвечает и отсылками (зоны DNS,
    // конфигурация) — Novell бросает на них LdapReferralException посреди
    // перебора. Это не ошибка поиска: пропускаем.
    private static async IAsyncEnumerable<LdapEntry> Entries(ILdapSearchResults results, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (await results.HasMoreAsync(ct))
        {
            LdapEntry e;
            try { e = await results.NextAsync(ct); }
            catch (LdapReferralException) { continue; }
            yield return e;
        }
    }

    private LdapSearchConstraints SearchConstraints() => new()
    {
        ReferralFollowing = false,
        TimeLimit = _cfg.TimeoutSeconds * 1000,
        ServerTimeLimit = _cfg.TimeoutSeconds,
        MaxResults = 1000,
    };

    private async Task<LdapConnection> ServiceConnectionAsync(CancellationToken ct)
    {
        var conn = await ConnectAsync(ct);
        try
        {
            await conn.BindAsync(_cfg.BindDn, _cfg.BindPassword, ct);
            return conn;
        }
        catch (LdapException e)
        {
            conn.Dispose();
            throw new DirectoryUnavailableException(
                e.ResultCode == LdapException.InvalidCredentials
                    ? "LDAP: служебная учётка Server:Ldap:BindDn/BindPassword не принята"
                    : $"LDAP: bind служебной учётки не прошёл: {e.ResultCodeToString()} {e.LdapErrorMessage}", e);
        }
    }

    private async Task<LdapConnection> ConnectAsync(CancellationToken ct)
    {
        var options = new LdapConnectionOptions();
        if (_ssl) options.UseSsl();
        if (_ca is not null) options.ConfigureRemoteCertificateValidationCallback(ValidateWithCustomCa);
        var conn = new LdapConnection(options) { ConnectionTimeout = _cfg.TimeoutSeconds * 1000 };
        try
        {
            await conn.ConnectAsync(_host, _port, ct);
            if (!_ssl && _cfg.StartTls) await conn.StartTlsAsync(ct);
            return conn;
        }
        catch (Exception e) when (e is LdapException or IOException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException)
        {
            conn.Dispose();
            throw new DirectoryUnavailableException($"LDAP: нет соединения с {_host}:{_port}: {e.Message}", e);
        }
    }

    // Свой CA вместо системного хранилища — с полной проверкой имени и цепочки
    // (тот же подход, что у экспорта в SIEM, H4).
    private bool ValidateWithCustomCa(object sender, X509Certificate? cert, X509Chain? _, SslPolicyErrors errors)
    {
        if (cert is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_ca!);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(new X509Certificate2(cert));
    }

    // RFC 4515: \ * ( ) NUL в значениях фильтра.
    public static string EscapeFilter(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
            sb.Append(c switch
            {
                '\\' => "\\5c",
                '*' => "\\2a",
                '(' => "\\28",
                ')' => "\\29",
                '\0' => "\\00",
                _ => c.ToString(),
            });
        return sb.ToString();
    }
}
