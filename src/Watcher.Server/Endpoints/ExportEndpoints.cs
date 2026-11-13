using Microsoft.AspNetCore.Http.Features;

namespace Watcher.Server;

// H7: выгрузка записей хоста за период. Политика Export (роль с доступом к
// записям + право can_export); группы viewer'а — как у остальных записей
// (ViewerScope). Скачивание — обычный GET по ссылке: браузер сам сохраняет
// поток на диск, не держа гигабайты в памяти страницы. CSRF — cookie
// SameSite=Strict (с чужого сайта запрос уйдёт без неё).
public static class ExportEndpoints
{
    public static void MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        var export = app.MapGroup("/api/export").RequireAuthorization("Export");

        // Что войдёт в выгрузку — без файлов и без записи в журнал: те же
        // метаданные пользователь и так видит в списках видео/консоли.
        // 0.12: segmentIds/terminalIds (через запятую) — выгрузка отмеченных в
        // плеере записей вместо периода.
        export.MapGet("/preview", (HttpContext ctx, Db db, ServerConfig cfg, string? host, DateTime? from, DateTime? to, bool video = true, bool console = true, string? sessionUser = null, string? segmentIds = null, string? terminalIds = null) =>
        {
            if (Plan(db, cfg, host, from, to, video, console, sessionUser, segmentIds, terminalIds, false) is not { } p) return BadRequest();
            var (req, plan) = p;
            if (!ViewerScope.CanSeeHost(ctx, db, req.Host)) return Results.Forbid();
            return Results.Ok(new
            {
                segments = plan.Segments.Count,
                terminal = plan.Terminal.Count,
                bytes = plan.Bytes,
                tooMany = plan.TooMany,
                tooLarge = plan.Bytes > cfg.ExportMaxMb * 1024 * 1024,
                maxItems = cfg.ExportMaxItems,
                maxBytes = cfg.ExportMaxMb * 1024 * 1024,
            });
        });

        export.MapGet("", (HttpContext ctx, Db db, ServerConfig cfg, AuditLog audit, ILogger<RecordingExportLog> log, string? host, DateTime? from, DateTime? to, bool video = true, bool console = true, string? sessionUser = null, string? segmentIds = null, string? terminalIds = null) =>
        {
            if (Plan(db, cfg, host, from, to, video, console, sessionUser, segmentIds, terminalIds, true) is not { } p) return BadRequest();
            var (req, plan) = p;
            bool picked = !string.IsNullOrEmpty(segmentIds) || !string.IsNullOrEmpty(terminalIds);
            var details = new { from = req.From.ToString("o"), to = req.To.ToString("o"), video = req.Video, console = req.Console, sessionUser = req.SessionUser, picked };
            if (!ViewerScope.CanSeeHost(ctx, db, req.Host))
            {
                audit.Write(ctx, "export", $"host:{req.Host}", new { details.from, details.to, details.video, details.console, details.sessionUser, denied = true });
                return Results.Forbid();
            }
            if (plan.TooMany || plan.Bytes > cfg.ExportMaxMb * 1024 * 1024)
                return Results.Json(new { error = "export too large, narrow the period", maxItems = cfg.ExportMaxItems, maxMb = cfg.ExportMaxMb }, statusCode: 413);
            if (plan.Segments.Count + plan.Terminal.Count == 0)
                return Results.NotFound(new { error = "no recordings in this period" });

            // Запись до первого байта: нет записи в журнале — нет выгрузки
            // (fail-closed, как у AuditLog в целом). Номер выгрузки = id записи.
            long exportId = audit.Write(ctx, "export", $"host:{req.Host}", new
            {
                details.from,
                details.to,
                details.video,
                details.console,
                details.sessionUser,
                details.picked,
                segments = plan.Segments.Count,
                terminal = plan.Terminal.Count,
                bytes = plan.Bytes,
            });
            var meta = new RecordingExport.Meta(exportId, ctx.User.Identity?.Name ?? "", DateTime.UtcNow);
            string fileName = $"watcher-export-{exportId}-{UploadValidation.SanitizeHost(req.Host)}-{req.From:yyyyMMdd}.zip";

            return Results.Stream(body =>
            {
                // ZipArchive (.NET 8) пишет только синхронно. Это человеческий
                // запрос, по одному на выгрузку, — поток пула на время скачивания
                // приемлем.
                if (ctx.Features.Get<IHttpBodyControlFeature>() is { } bodyControl) bodyControl.AllowSynchronousIO = true;
                try
                {
                    var r = RecordingExport.Write(body, plan, req, meta, cfg.DataDir, ctx.RequestAborted);
                    body.Flush();
                    audit.Write(ctx, "export.done", $"export:{exportId}", new
                    {
                        host = req.Host,
                        manifestSha256 = r.ManifestSha256,
                        files = r.Files,
                        bytes = r.Bytes,
                        mismatched = r.Mismatched,
                        missing = r.Missing,
                    });
                }
                catch (Exception e) when (e is IOException or OperationCanceledException)
                {
                    // Скачивание оборвалось (закрыли вкладку, сеть) — получатель
                    // неполного zip манифеста не увидит; в журнале видно, что
                    // выгрузка не завершилась.
                    log.LogWarning("Export {ExportId} aborted: {Error}", exportId, e.Message);
                    audit.Write(ctx, "export.aborted", $"export:{exportId}", new { host = req.Host, error = e.Message });
                }
                return Task.CompletedTask;
            }, "application/zip", fileName);
        });
    }

    private static IResult BadRequest() =>
        Results.BadRequest(new { error = "host and either from < to with video and/or console, or segmentIds/terminalIds of existing records of this host" });

    private static (RecordingExport.Request, RecordingExport.Plan)? Plan(Db db, ServerConfig cfg, string? host, DateTime? from, DateTime? to,
        bool video, bool console, string? sessionUser, string? segmentIds, string? terminalIds, bool withActivity)
    {
        if (!string.IsNullOrEmpty(segmentIds) || !string.IsNullOrEmpty(terminalIds))
        {
            if (string.IsNullOrWhiteSpace(host) || Ids(segmentIds) is not { } segs || Ids(terminalIds) is not { } terms) return null;
            return RecordingExport.BuildFromIds(db, host, segs, terms, cfg.ExportMaxItems, withActivity);
        }
        if (Parse(host, from, to, video, console, sessionUser) is not { } req) return null;
        return (req, RecordingExport.Build(db, req, cfg.ExportMaxItems, withActivity));
    }

    // "1,2,3" → [1,2,3]; пусто — пустой список; мусор — null (400).
    private static List<long>? Ids(string? csv)
    {
        var list = new List<long>();
        foreach (var part in (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(part, out long id) || id <= 0) return null;
            list.Add(id);
        }
        return list;
    }

    private static RecordingExport.Request? Parse(string? host, DateTime? from, DateTime? to, bool video, bool console, string? sessionUser)
    {
        if (string.IsNullOrWhiteSpace(host) || from is null || to is null || (!video && !console)) return null;
        DateTime f = from.Value.ToUniversalTime(), t = to.Value.ToUniversalTime();
        if (f >= t) return null;
        return new(host, f, t, video, console, string.IsNullOrEmpty(sessionUser) ? null : sessionUser);
    }
}

// Категория логгера для выгрузок (static-класс категорией быть не может).
public sealed class RecordingExportLog;
