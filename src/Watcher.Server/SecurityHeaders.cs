namespace Watcher.Server;

// Этап E1 (roadmap-next.md): до него CSP ставил только Caddy (A4), и с
// 'unsafe-inline' — UI был инлайн-скриптом в index.html. Теперь UI — только
// внешние файлы, CSP строгий, и сервер отдаёт его сам: systemd-установка без
// Caddy (EXPOSE_HTTP=true) тоже защищена, а WebApplicationFactory-тесты видят
// заголовок. Caddy перезаписывает эти же заголовки своими — строки держать
// одинаковыми с Caddyfile/Caddyfile.systemd-example. HSTS — только в Caddy:
// Kestrel за ним видит http, слать HSTS с plain-HTTP бессмысленно.
public static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; media-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";

    public static void Use(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h.ContentSecurityPolicy = ContentSecurityPolicy;
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            await next();
        });
    }
}
