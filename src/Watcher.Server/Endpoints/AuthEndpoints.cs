using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Watcher.Server;

// R14: вход/выход и «я» — любой роли (только аутентификация, где она нужна).
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Та же категория, что у app.Logger до R14 — имя приложения.
        var log = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Watcher.Server");

        // 0.13: версия сервера внизу страницы входа (до входа — публично, как и
        // сама страница). Только X.Y.Z, без хеша сборки.
        app.MapGet("/api/login", () => Results.Ok(new { version = AppVersion.Core(AppVersion.Current) }));

        app.MapPost("/api/login", async (HttpContext ctx, Db db, AuditLog audit, LoginThrottle throttle, ServerHealth health, DirectoryAuth directory, LoginRequest req) =>
        {
            // A3 (roadmap-next.md): per-username лимит — отдельно от IP-лимита политики
            // "login" (RequireRateLimiting ниже), см. LoginThrottle.
            if (throttle.TooManyAttempts(req.Username))
            {
                log.LogWarning("Слишком много попыток логина для {User} с {Ip}",
                    req.Username, ctx.Connection.RemoteIpAddress);
                audit.Write(ctx, "login.failed", username: req.Username, details: new { reason = "rate-limited" });
                health.CountLogin("rate_limited");
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            var user = db.UserByUsername(req.Username);
            bool passwordOk;
            string? ldapReason = null;
            // H1: локальная учётка (в т.ч. аварийный admin) — всегда по паролю из БД,
            // даже при включённом LDAP. Остальное — через каталог.
            if (directory.Enabled && user is not { IsLdap: false })
            {
                var r = await directory.LoginAsync(req.Username, req.Password, ctx.RequestAborted);
                user = r.User;
                passwordOk = user is not null;
                ldapReason = r.FailReason;
            }
            else
            {
                // VerifyPassword считается всегда, даже если user не найден (против DummyHash) — иначе
                // короткое замыкание на user is null делает ответ на неизвестный username быстрее, чем
                // на известный с неверным паролем, и это timing-сайдчаннел для перебора логинов.
                // У пользователя каталога хэш пустой — локальный пароль не проходит никогда.
                passwordOk = UserAuth.VerifyPassword(user?.PasswordHash ?? UserAuth.DummyHash, req.Password);
            }
            if (user is null || user.Disabled || !passwordOk)
            {
                // A3 (roadmap-next.md): неудачные входы с IP. Пароль не логируем, только
                // то, что попытка была. Этап C3: плюс в audit_log — причина видна только
                // admin'у в #/audit, клиенту по-прежнему одинаковый 401 на все три случая.
                log.LogWarning("Неудачный вход для {User} с {Ip}", req.Username, ctx.Connection.RemoteIpAddress);
                string reason = ldapReason is not null ? "ldap: " + ldapReason
                    : user is null ? "unknown user" : user.Disabled ? "disabled" : "bad password";
                audit.Write(ctx, "login.failed", username: req.Username, details: new { reason });
                health.CountLogin("failed");
                return Results.Unauthorized();
            }

            throttle.Reset(user.Username);
            health.CountLogin("ok");

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user.Username),
                new(ClaimTypes.Role, user.Role),
                // Этап C1 (roadmap-next.md): нужен для ViewerScope.AllowedGroupIds — без id
                // в claim пришлось бы на каждый запрос делать UserByUsername вместо
                // прямого db.UserGroupIds(id).
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7) });
            audit.Write(ctx, "login.ok", username: user.Username, details: new { role = user.Role, source = user.AuthSource });
            return Results.Ok(new { username = user.Username, role = user.Role });
        }).RequireRateLimiting("login");

        app.MapPost("/api/logout", async (HttpContext ctx, AuditLog audit) =>
        {
            audit.Write(ctx, "logout");
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });

        app.MapGet("/api/me", (HttpContext ctx, Db db) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
            // version — для UI (E2): «старая версия агента» = не совпадает с сервером.
            // authSource (H1) — у пользователя каталога нет «Сменить пароль».
            // canExport (H7) — показывать ли вкладку «Выгрузка».
            var me = db.UserByUsername(ctx.User.Identity.Name!);
            string authSource = me?.AuthSource ?? "local";
            // Этап 11: админ может редактировать политики всегда (политика
            // PhoneManagement), поэтому в UI отдаём эффективное право.
            bool canManagePhones = (me?.CanManagePhones ?? false) || ctx.User.IsInRole("admin");
            // timeZone (0.14.4) — зона сервера: в ней UI показывает время и режет сутки.
            return Results.Ok(new { username = ctx.User.Identity.Name, role = ctx.User.FindFirstValue(ClaimTypes.Role), version = AppVersion.Core(AppVersion.Current), authSource, canExport = me?.CanExport ?? false, canManagePhones, timeZone = ServerClock.ZoneId });
        });

        // Самообслуживание (Этап C2, roadmap-next.md) — доступно любой роли, не
        // RequireAuthorization("AdminOnly"): каждый меняет СВОЙ пароль, не чужой.
        app.MapPost("/api/me/password", (HttpContext ctx, Db db, AuditLog audit, ChangeOwnPasswordRequest req) =>
        {
            string username = ctx.User.Identity!.Name!;
            var user = db.UserByUsername(username);
            if (user is null) return Results.Unauthorized();
            if (user.IsLdap) return Results.Conflict(new { error = "password of a directory user is changed in the directory" });
            if (!UserAuth.VerifyPassword(user.PasswordHash, req.CurrentPassword))
                return Results.BadRequest(new { error = "current password is wrong" });
            if (string.IsNullOrEmpty(req.NewPassword) || req.NewPassword.Length < CliCommands.MinPasswordLength)
                return Results.BadRequest(new { error = $"password must be at least {CliCommands.MinPasswordLength} characters" });
            db.SetUserPasswordById(user.Id, UserAuth.HashPassword(req.NewPassword));
            audit.Write(ctx, "user.change-own-password", $"user:{user.Username}");
            return Results.Ok();
        });
    }
}

// A3 (roadmap-next.md): лимит по IP (политика "login" в Program.cs) не ловит
// распределённый/NAT-перебор ОДНОГО логина с разных адресов — этот, по
// имени пользователя, ловит именно такой случай. Отдельный от IP-лимита
// словарь, не замена ему. Singleton в DI, а не static: у каждого экземпляра
// приложения (WebApplicationFactory в тестах) свой счётчик.
public sealed class LoginThrottle
{
    private const int MaxAttemptsPerUser = 10;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _byUser = new();

    public bool TooManyAttempts(string username)
    {
        DateTime now = DateTime.UtcNow;
        var state = _byUser.AddOrUpdate(
            username,
            _ => (1, now),
            (_, old) => now - old.WindowStart > Window ? (1, now) : (old.Count + 1, old.WindowStart));
        return state.Count > MaxAttemptsPerUser;
    }

    public void Reset(string username) => _byUser.TryRemove(username, out _);
}
