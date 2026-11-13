using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;

namespace Watcher.Server;

// Заменяет собой общий Basic Auth (BasicAuth.cs, Фаза 0): вместо одного shared
// AdminUser/AdminPassword — cookie-сессия поверх таблицы users (см. Endpoints/AuthEndpoints.cs,
// /api/login). /api/upload, /api/heartbeat и /api/login остаются открытыми —
// первые два защищены отдельной Bearer-схемой (ApiToken, Фаза 3 ничего не
// меняет в этой схеме), третий обязан быть доступен до логина.
public static class AuthGate
{
    // login.css/login.js — Этап E1: вынесены из login.html под CSP без
    // 'unsafe-inline', а грузятся до входа. Больше ничего из wwwroot публичным
    // не становится (app.js/views/ — только после логина).
    // /healthz (H4) — без данных, только ok/fail; /metrics проверяет свой
    // Bearer MetricsToken сам (Prometheus cookie не носит).
    // i18n.js/i18n.en.js (0.14.4) — переключатель языка на странице входа; только строки интерфейса.
    private static readonly string[] PublicPaths = { "/login.html", "/login.css", "/login.js", "/i18n.js", "/i18n.en.js", "/i18n.srv.js", "/favicon.svg", "/api/login", "/healthz", "/metrics" };
    // 0.12, редизайн: шрифты нужны странице входа — только этот каталог статики.
    private static readonly string[] PublicPrefixes = { "/fonts/" };
    // /api/enroll (Задача G1) — агент ещё без секрета, проверяет токен регистрации сам.
    // /api/updates (H2) — скачивание пакета обновления агентом по своей учётке.
    // /api/events (Этап 3 Android) — поток событий телефона, Bearer учётки агента.
    private static readonly string[] MachinePathPrefixes = { "/api/upload", "/api/agent-diagnostics", "/api/heartbeat", "/api/enroll", "/api/updates" };

    // A2 (roadmap-next.md): мутирующие /api/* работают на одной cookie без
    // antiforgery-токенов — требуем этот заголовок, который обычная HTML-
    // форма/сторонний fetch без explicit JS не может выставить (а выставить
    // его через fetch с другого origin не даст CORS: сервер не отдаёт
    // Access-Control-Allow-Origin, значит preflight-запрос с кастомным
    // заголовком не пройдёт). /api/upload и /api/heartbeat сюда не попадают —
    // у них Bearer ApiToken, не cookie, CSRF на них не действует вообще.
    private static readonly HashSet<string> MutatingMethods =
        new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    public static void Use(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            string path = ctx.Request.Path.Value ?? "";

            if ((HttpMethods.IsPost(ctx.Request.Method) && path.Equals("/api/events", StringComparison.OrdinalIgnoreCase)) ||
                MachinePathPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
                PublicPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)) ||
                (PublicPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) && !path.Contains("..", StringComparison.Ordinal)))
            {
                await next();
                return;
            }

            if (ctx.User.Identity?.IsAuthenticated == true)
            {
                // Этап C2 (roadmap-next.md): раньше disabled проверялся только в
                // /api/login — уже выданная cookie продолжала работать до истечения
                // (7 дней, SlidingExpiration), "отключить" пользователя в #/settings/users
                // не было мгновенным. userId — claim NameIdentifier (добавлен в Этапе C1).
                // Запрос в БД на каждый cookie-запрос — приемлемо: это человеческий
                // веб-трафик (горстка админов), не агентский heartbeat на 300 хостов.
                string? userIdClaim = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (userIdClaim is not null)
                {
                    var db = ctx.RequestServices.GetRequiredService<Db>();
                    var sessionUser = db.UserById(long.Parse(userIdClaim));
                    string? revokeReason = null;
                    // H1: пользователь каталога — раз в RecheckMinutes проверяется в AD:
                    // отключён, удалён, выведен из групп — сессия закрывается сразу.
                    if (sessionUser is { IsLdap: true, Disabled: false })
                    {
                        var (keep, reason) = await ctx.RequestServices.GetRequiredService<DirectoryAuth>().RecheckAsync(sessionUser, ctx.RequestAborted);
                        if (!keep)
                        {
                            revokeReason = reason;
                            ctx.RequestServices.GetRequiredService<AuditLog>().Write(ctx, "ldap.session-revoked", $"user:{sessionUser.Username}", new { reason });
                        }
                    }
                    if (sessionUser is null || sessionUser.Disabled || revokeReason is not null)
                    {
                        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                        {
                            ctx.Response.StatusCode = 401;
                            await ctx.Response.WriteAsJsonAsync(new { error = "account disabled" });
                            return;
                        }
                        ctx.Response.Redirect("/login.html");
                        return;
                    }
                }

                if (MutatingMethods.Contains(ctx.Request.Method) &&
                    path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
                    ctx.Request.Headers["X-Requested-With"] != "XMLHttpRequest")
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsJsonAsync(new { error = "missing X-Requested-With" });
                    return;
                }

                await next();
                return;
            }

            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                return;
            }

            ctx.Response.Redirect("/login.html");
        });
    }
}
