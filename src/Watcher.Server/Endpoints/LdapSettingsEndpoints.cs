using System.Text.Json;

namespace Watcher.Server;

// 0.12 (п. 8 обратной связи): вход через AD настраивается в веб-UI —
// Настройки → Вход через AD, только admin. Сохранённое здесь главнее
// Server:Ldap из appsettings/.env и применяется без перезапуска; сброс —
// возврат к appsettings/.env. Пароль служебной учётки только на запись:
// наружу отдаётся лишь «задан/не задан», пустой при сохранении — прежний.
public static class LdapSettingsEndpoints
{
    public static void MapLdapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var ldap = app.MapGroup("/api/settings/ldap").RequireAuthorization("AdminOnly");

        ldap.MapGet("", (DirectoryAuth directory) => Results.Ok(new
        {
            source = directory.Source,
            enabled = directory.Enabled,
            plaintext = directory.Enabled && directory.Config.Plaintext,
            error = directory.Source == "none" ? null : directory.Config.Validate(),
            fileConfigured = directory.FileConfig.Enabled,
            config = Dto(directory.Config),
        }));

        ldap.MapPut("", (HttpContext ctx, LdapSettingsRequest req, Db db, DirectoryAuth directory, AuditLog audit) =>
        {
            var cfg = req.ToConfig(directory.Config);
            if (cfg.Validate() is { } error) return Results.BadRequest(new { error });
            db.SetSetting(DirectoryAuth.SettingKey, JsonSerializer.Serialize(cfg), ctx.User.Identity?.Name);
            directory.Apply(cfg);
            audit.Write(ctx, "ldap.settings.set", null, new
            {
                cfg.Url,
                cfg.StartTls,
                cfg.AllowPlaintext,
                cfg.BindDn,
                cfg.SearchBase,
                cfg.UserFilter,
                cfg.LoginAttribute,
                groupRoles = cfg.GroupRoles.Select(g => $"{g.Group} → {g.Role}"),
                cfg.ExportGroups,
                cfg.NestedGroups,
                passwordChanged = !string.IsNullOrEmpty(req.BindPassword),
            });
            return Results.Ok(new { source = directory.Source, enabled = directory.Enabled });
        });

        ldap.MapDelete("", (HttpContext ctx, Db db, DirectoryAuth directory, AuditLog audit) =>
        {
            db.DeleteSetting(DirectoryAuth.SettingKey);
            directory.Apply(null);
            audit.Write(ctx, "ldap.settings.reset", null);
            return Results.Ok(new { source = directory.Source, enabled = directory.Enabled });
        });

        // Проверка ещё не сохранённых настроек: соединение, TLS, bind служебной
        // учётки, база поиска; с логином и паролем — ещё и вход этого
        // пользователя, его группы и роль, которую он получит.
        ldap.MapPost("/test", async (HttpContext ctx, LdapSettingsRequest req, DirectoryAuth directory, AuditLog audit) =>
        {
            var cfg = req.ToConfig(directory.Config);
            if (cfg.Validate() is { } error) return Results.Ok(new { ok = false, message = error });
            var dir = new LdapDirectory(cfg);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds * 3));
            try
            {
                await dir.CheckAsync(cts.Token);
                if (string.IsNullOrWhiteSpace(req.TestLogin))
                    return Results.Ok(new { ok = true, message = "Соединение и служебная учётка — в порядке, база поиска найдена." });
                var du = await dir.AuthenticateAsync(req.TestLogin!, req.TestPassword ?? "", cts.Token);
                audit.Write(ctx, "ldap.settings.test-login", $"user:{req.TestLogin}", new { found = du is not null });
                if (du is null)
                    return Results.Ok(new { ok = false, message = $"Подключение в порядке, но «{req.TestLogin}» не найден или пароль неверен." });
                string? role = DirectoryAuth.MapRole(du.Groups, cfg.GroupRoles);
                return Results.Ok(new
                {
                    ok = role is not null && !du.Disabled,
                    message = du.Disabled ? "Учётка отключена в каталоге — войти не сможет."
                        : role is null ? "Вход прошёл, но ни одна группа пользователя не сопоставлена роли — войти не сможет."
                        : $"Вход прошёл: роль «{role}».",
                    user = new
                    {
                        du.Login,
                        du.Disabled,
                        role,
                        canExport = DirectoryAuth.InAnyGroup(du.Groups, cfg.ExportGroups),
                        groups = du.Groups.Select(DirectoryAuth.CommonName).Take(50),
                    },
                });
            }
            catch (DirectoryUnavailableException e) { return Results.Ok(new { ok = false, message = e.Message }); }
            catch (OperationCanceledException) { return Results.Ok(new { ok = false, message = $"LDAP: нет ответа за {cfg.TimeoutSeconds * 3} с" }); }
        });
    }

    private static object Dto(LdapConfig c) => new
    {
        c.Url,
        c.StartTls,
        c.AllowPlaintext,
        c.CaCertPem,
        c.CaCertPath,
        c.BindDn,
        hasBindPassword = !string.IsNullOrEmpty(c.BindPassword),
        c.SearchBase,
        c.UserFilter,
        c.LoginAttribute,
        groupRoles = c.GroupRoles.Select(g => new { g.Group, g.Role }),
        c.ExportGroups,
        c.NestedGroups,
        c.RecheckMinutes,
        c.TimeoutSeconds,
    };
}

public sealed record LdapGroupRoleDto(string? Group, string? Role);

public sealed record LdapSettingsRequest(
    string? Url, bool StartTls, string? CaCertPem, string? BindDn, string? BindPassword,
    string? SearchBase, string? UserFilter, string? LoginAttribute,
    List<LdapGroupRoleDto>? GroupRoles, List<string>? ExportGroups, bool NestedGroups = true,
    int RecheckMinutes = 15, int TimeoutSeconds = 5,
    string? TestLogin = null, string? TestPassword = null, bool AllowPlaintext = false)
{
    // Пустой пароль — прежний (из текущих настроек, откуда бы они ни были).
    public LdapConfig ToConfig(LdapConfig current) => new()
    {
        Url = (Url ?? "").Trim(),
        StartTls = StartTls,
        AllowPlaintext = AllowPlaintext,
        CaCertPem = (CaCertPem ?? "").Trim(),
        CaCertPath = string.IsNullOrWhiteSpace(CaCertPem) ? current.CaCertPath : "",
        BindDn = (BindDn ?? "").Trim(),
        BindPassword = string.IsNullOrEmpty(BindPassword) ? current.BindPassword : BindPassword,
        SearchBase = (SearchBase ?? "").Trim(),
        UserFilter = string.IsNullOrWhiteSpace(UserFilter) ? new LdapConfig().UserFilter : UserFilter.Trim(),
        LoginAttribute = string.IsNullOrWhiteSpace(LoginAttribute) ? "sAMAccountName" : LoginAttribute.Trim(),
        GroupRoles = (GroupRoles ?? new()).Where(g => !string.IsNullOrWhiteSpace(g.Group))
            .Select(g => new LdapGroupRole { Group = g.Group!.Trim(), Role = (g.Role ?? "").Trim() }).ToList(),
        ExportGroups = (ExportGroups ?? new()).Select(g => g.Trim()).Where(g => g.Length > 0).ToList(),
        NestedGroups = NestedGroups,
        RecheckMinutes = RecheckMinutes,
        TimeoutSeconds = TimeoutSeconds,
    };
}
