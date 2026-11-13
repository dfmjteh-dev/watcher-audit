namespace Watcher.Server;

// R14: загрузки от агентов — /api/upload (видео) и /api/upload-terminal (консоль).
// Здесь только HTTP: аутентификация агента и разбор multipart; приём записи —
// RecordingIngest. Без политики роли: агент несёт Bearer (учётка или общий токен), не cookie —
// см. AgentAccess и AuthGate.MachinePathPrefixes.
public static class UploadEndpoints
{
    public static void MapUploadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/upload", async (HttpContext ctx, Db db, ServerConfig cfg, RecordingIngest ingest) =>
        {
            // Видео шлют станции (учётка video) и Android-агент (запись экрана по
            // согласию, Этап 12+): обе привязывают запись к своему хосту. Чужой вид
            // (terminal) — 403. Общий токен/анонимный режим (Identity==null) — как раньше.
            if (AgentAccess.Authenticate(ctx, db, cfg) is not { } caller) return Results.Empty;
            if (caller.Identity is { } id && id.Kind != AgentAuth.KindVideo && id.Kind != AgentAuth.KindAndroid)
            {
                ctx.Response.StatusCode = 403;
                return Results.Empty;
            }
            // R7: до чтения тела — видео бывает большим, а сдвиг меряется на момент,
            // когда агент поставил заголовок.
            DateTime receivedAt = DateTime.UtcNow;
            TimeSpan? clockSkew = StationClock.MeasureSkew(ctx.Request, receivedAt);
            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "expected multipart/form-data" });
            var form = await ctx.Request.ReadFormAsync();
            var video = form.Files["video"];
            var meta = form.Files["meta"];
            if (video is null) return Results.BadRequest(new { error = "video missing" });
            string metaJson = "{}";
            if (meta is not null)
            {
                using var sr = new StreamReader(meta.OpenReadStream());
                metaJson = await sr.ReadToEndAsync();
            }
            var r = await ingest.VideoAsync(caller, video.FileName, metaJson, form["sha256"].ToString(),
                video.OpenReadStream, clockSkew, receivedAt, ctx.Request.Headers["X-Watcher-Video-Validation"] == "1", ctx.RequestAborted);
            return Results.Json(r.Body, statusCode: r.StatusCode);
        }).AddEndpointFilter(CountResult("video"));

        // Фаза 8 (roadmap.md) — Watcher.TerminalAgent сам себе Uploader (не через
        // Watcher.Uploader): он уже системный root-процесс с доступом к обоим
        // каталогам (queue/raw-output), а Uploader — per-user и не смог бы прочитать
        // чужие 600-файлы вывода. "/api/upload" префикс в AuthGate.MachinePathPrefixes
        // матчится по StartsWith, так что "/api/upload-terminal" уже покрыт той же
        // Bearer-схемой — отдельного решения по авторизации здесь принимать не надо.
        app.MapPost("/api/upload-terminal", async (HttpContext ctx, Db db, ServerConfig cfg, RecordingIngest ingest) =>
        {
            if (AgentAccess.Authenticate(ctx, db, cfg, AgentAuth.KindTerminal) is not { } caller) return Results.Empty;
            DateTime receivedAt = DateTime.UtcNow;
            TimeSpan? clockSkew = StationClock.MeasureSkew(ctx.Request, receivedAt);
            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "expected multipart/form-data" });
            var form = await ctx.Request.ReadFormAsync();
            var metaFile = form.Files["meta"];
            if (metaFile is null) return Results.BadRequest(new { error = "meta missing" });
            string metaJson;
            using (var sr = new StreamReader(metaFile.OpenReadStream())) metaJson = await sr.ReadToEndAsync();
            static UploadPart? Part(IFormFile? f) => f is null ? null : new UploadPart(f.OpenReadStream, f.Length);
            if (TerminalHost(caller, metaJson) is { } liveHost)
            {
                string? effective = db.GetEffectiveConfig(liveHost)?.ConfigJson;
                ctx.Response.Headers[RecordingIngest.LiveIntervalHeader] =
                    RecordingIngest.LiveIntervalFor(effective).ToString(System.Globalization.CultureInfo.InvariantCulture);
                ctx.Response.Headers[RecordingIngest.QueueMaxAgeHeader] =
                    RecordingIngest.QueueMaxAgeFor(effective).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            // 0.12: агент спрашивает, принимает ли сервер куски — ответ в заголовке выше.
            if (metaJson.Contains("\"probe\":true", StringComparison.Ordinal))
                return Results.Ok(new { probe = true });
            var r = await ingest.TerminalAsync(caller, metaJson, Part(form.Files["out"]), Part(form.Files["timing"]), clockSkew, receivedAt);
            return Results.Json(r.Body, statusCode: r.StatusCode);
        }).AddEndpointFilter(CountResult("terminal"));
    }

    // Хост консольной записи — как его определит RecordingIngest (учётка
    // агента или host из meta у общего токена); null — разбирать нечего.
    private static string? TerminalHost(AgentCaller caller, string metaJson)
    {
        string? claimed = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metaJson);
            if (doc.RootElement.TryGetProperty("host", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.String) claimed = h.GetString();
        }
        catch (System.Text.Json.JsonException) { }
        return AgentAuth.ResolveHost(caller, claimed);
    }

    // H4: счётчик загрузок по результату для /metrics — по коду ответа, не внутри
    // хендлеров. Results.Empty (отказ AgentAccess) код выставляет в Response.
    private static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> CountResult(string kind) =>
        async (ctx, next) =>
        {
            var health = ctx.HttpContext.RequestServices.GetRequiredService<ServerHealth>();
            try
            {
                var result = await next(ctx);
                int code = result is IStatusCodeHttpResult { StatusCode: int c } ? c : ctx.HttpContext.Response.StatusCode;
                health.CountUpload(kind, code);
                return result;
            }
            // 0.15.0: агент оборвал загрузку (сеть, сон, выключение) — его ошибка, не
            // сервера: 400 без стектрейса в логе (на проде ~2 в день); сегмент придёт повтором.
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException) when (!ctx.HttpContext.Response.HasStarted)
            {
                health.CountUpload(kind, 400);
                return Results.BadRequest(new { error = "request body incomplete" });
            }
            catch
            {
                health.CountUpload(kind, 500);
                throw;
            }
        };
}
