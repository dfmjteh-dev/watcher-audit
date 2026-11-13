using System.Text.Json;

namespace Watcher.Server;

// 0.14.3: «Настройки → Программы» — тип программы по имени процесса (AppKinds):
// что из её окна брать в отчёт «Сайты и документы» и в подсказки правил по
// заголовку. Видно стандартную таблицу, правки админа и процессы, которые
// реально встречались. Только admin; изменения — в журнал доступа.
public static class AppKindsEndpoints
{
    public const int SeenDays = 30, SeenLimit = 300, MaxOverrides = 1000;

    public static void MapAppKindsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/settings/app-kinds").RequireAuthorization("AdminOnly");

        g.MapGet("", (Db db, bool seen = true) =>
        {
            var map = AppKinds.Load(db);
            return Results.Ok(new
            {
                defaults = AppKinds.Defaults.ToDictionary(kv => kv.Key, kv => AppKinds.Names[kv.Value]),
                overrides = map.Overrides.ToDictionary(kv => kv.Key, kv => AppKinds.Names[kv.Value]),
                // hasSite — агент присылал домен вкладки: браузер, даже если его нет в таблице.
                seen = (seen ? db.TopProcesses(SeenDays, SeenLimit) : []).Select(p => new
                {
                    process = p.Process,
                    count = p.Count,
                    kind = AppKinds.Names[map.KindOf(p.Process, p.HasSite)],
                    p.HasSite,
                }),
                seenDays = SeenDays,
            });
        });

        g.MapGet("/seen", async (AppCatalogCache cache, HttpContext ctx, string? q, string? cursor, int limit = 50) =>
        {
            if (q?.Length > 200 || cursor?.Length > 100) return Results.BadRequest(new { error = "invalid cursor" });
            var result = await cache.GetAsync(q, Math.Clamp(limit, 1, 100), cursor, ctx.RequestAborted);
            return result is null ? Results.Conflict(new { error = "list expired, refresh the page" }) : Results.Ok(result);
        });

        g.MapPut("", (HttpContext ctx, AppKindsRequest req, Db db, AuditLog audit, ReportFactsService facts) =>
        {
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (p, k) in req.Overrides ?? new Dictionary<string, string>())
            {
                if (!AppKinds.IsValidProcess(p)) return Results.BadRequest(new { error = $"неверное имя процесса «{p}»" });
                if (AppKinds.Parse(k) is null) return Results.BadRequest(new { error = $"неизвестный тип «{k}» у «{p}»" });
                // Совпадает со стандартной таблицей — не правка, не храним. Процесс вне
                // таблицы храним как прислали: «программа» у него — тоже правка, если
                // агент присылает домен (иначе он считался бы браузером, AppKinds.KindOf).
                if (AppKinds.Defaults.TryGetValue(p.Trim(), out var d) && AppKinds.Names[d] == k) continue;
                overrides[p.Trim()] = k;
            }
            if (overrides.Count > MaxOverrides) return Results.BadRequest(new { error = $"не больше {MaxOverrides} правок" });
            if (overrides.Count == 0) db.DeleteSetting(AppKinds.SettingKey);
            else db.SetSetting(AppKinds.SettingKey, JsonSerializer.Serialize(overrides), ctx.User.Identity?.Name);
            audit.Write(ctx, "app-kinds.set", null, new { overrides });
            // Заготовки отчётов зависят от типов программ — пересчитать (в фоне).
            facts.Wake();
            return Results.Ok(new { overrides });
        });
    }
}

public sealed record AppKindsRequest(Dictionary<string, string>? Overrides);
