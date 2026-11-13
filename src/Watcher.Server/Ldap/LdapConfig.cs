namespace Watcher.Server;

// H1 (docs/roadmap-next.md): вход через Active Directory / LDAP. Секция
// Server:Ldap; выключено, пока не задан Url. Описание — docs/configuration.md.
public sealed class LdapConfig
{
    // ldaps://dc1.corp.local:636 или ldap://dc1.corp.local:389 (+ StartTls).
    // Имя — как в сертификате контроллера: по нему проверяется TLS.
    public string Url { get; set; } = "";
    public bool StartTls { get; set; } = false;
    // 0.12.1: явное разрешение ldap:// без StartTLS — пароли пользователей и
    // служебной учётки идут к контроллеру открытым текстом. Для домена, где на
    // контроллерах нет сертификата для LDAPS, по решению администратора.
    public bool AllowPlaintext { get; set; } = false;
    public bool Plaintext => Uri.TryCreate(Url, UriKind.Absolute, out var u) && u.Scheme == "ldap" && !StartTls;
    // Корневой CA контроллера, если он не в системном хранилище: путь к файлу
    // (appsettings/.env) или сам сертификат PEM-текстом (0.12, веб-UI).
    public string CaCertPath { get; set; } = "";
    public string CaCertPem { get; set; } = "";

    // Служебная учётка только для поиска (не администратор домена).
    // AD принимает и UPN: svc-watcher@corp.local.
    public string BindDn { get; set; } = "";
    public string BindPassword { get; set; } = "";

    public string SearchBase { get; set; } = "";
    // {0} — введённый логин (экранируется по RFC 4515). По умолчанию — AD:
    // sAMAccountName или UPN. OpenLDAP: (uid={0}).
    public string UserFilter { get; set; } = "(&(objectCategory=person)(objectClass=user)(|(sAMAccountName={0})(userPrincipalName={0})))";
    // Атрибут, значение которого становится именем пользователя в Watcher.
    public string LoginAttribute { get; set; } = "sAMAccountName";

    // Группа → роль. Group — DN группы или просто её имя (CN). Несколько
    // подходящих групп — берётся старшая роль (admin > operator > viewer).
    // Списком, а не словарём: DN с «=» и «,» не годится в имя переменной окружения.
    public List<LdapGroupRole> GroupRoles { get; set; } = new();
    // H7: группы каталога (DN или CN), дающие право выгрузки записей. Пусто —
    // у пользователей каталога этого права нет.
    public List<string> ExportGroups { get; set; } = new();
    // AD: учитывать вложенные группы (LDAP_MATCHING_RULE_IN_CHAIN). Не AD — false.
    public bool NestedGroups { get; set; } = true;

    // Как часто перепроверять в каталоге уже вошедшего пользователя: отключён,
    // удалён, выведен из групп — сессия закрывается, не дожидаясь конца cookie.
    public int RecheckMinutes { get; set; } = 15;
    public int TimeoutSeconds { get; set; } = 5;

    public bool Enabled => !string.IsNullOrWhiteSpace(Url);

    // null — конфигурация корректна.
    public string? Validate()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var u) || u.Scheme is not ("ldap" or "ldaps"))
            return "Server:Ldap:Url — ldaps://хост:636 или ldap://хост:389";
        if (u.Scheme == "ldap" && !StartTls && !AllowPlaintext)
            return "Server:Ldap:Url — ldap:// без StartTls передаёт пароли открытым текстом; используйте ldaps:// или StartTls=true, либо явно разрешите AllowPlaintext";
        if (string.IsNullOrWhiteSpace(SearchBase)) return "Server:Ldap:SearchBase не задан";
        if (!UserFilter.Contains("{0}", StringComparison.Ordinal)) return "Server:Ldap:UserFilter должен содержать {0}";
        if (GroupRoles.Count == 0) return "Server:Ldap:GroupRoles пуст — никто не сможет войти";
        foreach (var g in GroupRoles)
            if (string.IsNullOrWhiteSpace(g.Group) || !CliCommands.ValidRoles.Contains(g.Role))
                return $"Server:Ldap:GroupRoles — группа «{g.Group}» с ролью «{g.Role}»: роль одна из {string.Join(", ", CliCommands.ValidRoles)}";
        if (!string.IsNullOrEmpty(CaCertPath) && !File.Exists(CaCertPath)) return $"Server:Ldap:CaCertPath — файл не найден: {CaCertPath}";
        if (!string.IsNullOrWhiteSpace(CaCertPem))
        {
            try { using var _ = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(CaCertPem); }
            catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or ArgumentException)
            { return "CA контроллера — не сертификат PEM (-----BEGIN CERTIFICATE-----)"; }
        }
        if (TimeoutSeconds is < 1 or > 60) return "Server:Ldap:TimeoutSeconds — от 1 до 60";
        if (RecheckMinutes is < 1 or > 1440) return "Server:Ldap:RecheckMinutes — от 1 до 1440";
        return null;
    }
}

public sealed class LdapGroupRole
{
    public string Group { get; set; } = "";
    public string Role { get; set; } = "";
}
