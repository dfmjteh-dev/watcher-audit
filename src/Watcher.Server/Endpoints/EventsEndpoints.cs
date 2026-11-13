using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Watcher.Server;

// Этап 3 Android: приём — только учётка android; чтение — как содержимое записей,
// а не здоровье парка (operator не видит, viewer ограничен группами).
public static class EventsEndpoints
{
    public const int MaxBodyBytes = 10 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapEventsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/events", async (HttpContext ctx, Db db, ServerConfig cfg) =>
        {
            if (AgentAccess.Authenticate(ctx, db, cfg, AgentAuth.KindAndroid) is not { } caller) return Results.Empty;
            // У нового протокола нет legacy-агентов: общий токен/dev-режим не
            // удостоверяют ни хост, ни вид агента.
            if (caller.Legacy) return Results.StatusCode(StatusCodes.Status403Forbidden);

            // Проверяем и Content-Length, и фактически прочитанные байты (chunked).
            // Не используем автопривязку DTO: она материализует JSON до лимитов.
            if (ctx.Request.ContentLength > MaxBodyBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            if (!ctx.Request.HasJsonContentType())
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            using var body = new MemoryStream();
            byte[] buffer = new byte[16 * 1024];
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(buffer.AsMemory(), ctx.RequestAborted)) > 0)
            {
                if (body.Length + read > MaxBodyBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                body.Write(buffer, 0, read);
            }
            body.Position = 0;
            AndroidEventsRequest? req;
            try { req = await JsonSerializer.DeserializeAsync<AndroidEventsRequest>(body, JsonOptions, ctx.RequestAborted); }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid JSON" }); }
            if (req?.Events is not { } events)
                return Results.BadRequest(new { error = "events array required" });
            if (events.Count > Db.MaxEventsPerRequest)
                return Results.BadRequest(new { error = $"too many events (max {Db.MaxEventsPerRequest})" });

            // host тела может быть старым после переименования. Источник истины —
            // учётка, как в upload/heartbeat; чужое имя не меняет владельца данных.
            var clean = new List<AndroidEventInput>(events.Count);
            foreach (var e in events)
            {
                if (e?.EventId is null || !Guid.TryParseExact(e.EventId, "D", out var eventId) || eventId == Guid.Empty)
                    return Results.BadRequest(new { error = "eventId must be a nonzero UUID" });
                if (e.Type is null || !Db.AndroidEventTypes.Contains(e.Type))
                    return Results.BadRequest(new { error = "unknown event type" });
                if (!TryParseTs(e.Ts, out string tsUtc))
                    return Results.BadRequest(new { error = "ts must be ISO 8601 with timezone" });
                bool packageless = Db.PackagelessTypes.Contains(e.Type);
                if (!packageless && string.IsNullOrWhiteSpace(e.Package))
                    return Results.BadRequest(new { error = "package required for app events" });
                // Этап 6: у journal-типов пакет — в detail; верхний package должен быть пуст.
                if (e.Type is "app_inventory" or "net_dns" or "net_connect" or "security" or "policy" && e.Package is not null)
                    return Results.BadRequest(new { error = $"{e.Type} package must be null" });
                if (e.Package is not null && (string.IsNullOrWhiteSpace(e.Package) ||
                    !UploadValidation.IsSafeMetaField(e.Package, Db.MaxPackageLength)))
                    return Results.BadRequest(new { error = "invalid package" });
                string? detail = null;
                if (e.Detail is { ValueKind: not JsonValueKind.Null } d)
                {
                    if (d.ValueKind != JsonValueKind.Object)
                        return Results.BadRequest(new { error = "detail must be an object" });
                    detail = JsonSerializer.Serialize(d);
                    if (Encoding.UTF8.GetByteCount(detail) > Db.MaxDetailLength)
                        return Results.BadRequest(new { error = "detail too large" });
                }
                clean.Add(new AndroidEventInput(tsUtc, e.Type, e.Package, detail, eventId.ToString("D")));
            }

            int inserted = db.InsertAndroidEvents(caller.Identity!.HostId, clean);
            if (inserted < 0) return Results.StatusCode(StatusCodes.Status410Gone);
            return Results.Ok(new { accepted = clean.Count, inserted });
        });

        app.MapGet("/api/inventory/{name}/events", (string name, HttpContext ctx, Db db, int limit = 200, int offset = 0) =>
        {
            if (!ViewerScope.CanSeeHost(ctx, db, name)) return Results.Forbid();
            limit = Math.Clamp(limit, 1, 1000);
            offset = Math.Max(0, offset);
            return Results.Ok(new { items = db.ListAndroidEvents(name, limit, offset), total = db.CountAndroidEvents(name), limit, offset });
        }).RequireAuthorization("Recordings");

        // Этап 4: сводка устройства за период (веб-UI показывает сутки в зоне
        // сервера — границы считает браузер, как у ленты видео). Политика та же,
        // что у ленты: это содержимое активности, не здоровье парка.
        app.MapGet("/api/inventory/{name}/android-summary", (string name, HttpContext ctx, Db db, DateTime? from, DateTime? to) =>
        {
            if (!ViewerScope.CanSeeHost(ctx, db, name)) return Results.Forbid();
            if (from is not { } f || to is not { } t)
                return Results.BadRequest(new { error = "from and to required" });
            f = f.ToUniversalTime();
            t = t.ToUniversalTime();
            if (t <= f || t - f > AndroidSummary.MaxRange)
                return Results.BadRequest(new { error = $"range must be positive and at most {AndroidSummary.MaxRange.TotalDays} days" });
            var (events, truncated) = db.ListAndroidEventsRange(name, f, t, AndroidSummary.MaxEvents);
            return Results.Ok(AndroidSummary.Build(f, t, DateTime.UtcNow, events, truncated,
                db.LatestAndroidEvent(name, "device_state", t), db.LatestAndroidInventory(name, t)));
        }).RequireAuthorization("Recordings");
    }

    private static bool TryParseTs(string? ts, out string utc)
    {
        utc = "";
        if (ts is null || ts.Length > 40 ||
            !Regex.IsMatch(ts, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,9})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant) ||
            !DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return false;
        utc = dt.UtcDateTime.ToString("o");
        return true;
    }
}
