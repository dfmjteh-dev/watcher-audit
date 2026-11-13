using Microsoft.AspNetCore.Http.Features;

namespace Watcher.Server;

// Обновление сервера из веб-UI (ServerUpdate.cs) — только admin, каждое
// действие в журнал доступа: заявка на обновление = запуск install-server.sh от
// root на хосте.
public static class ServerUpdateEndpoints
{
    public static void MapServerUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/server-update").RequireAuthorization("AdminOnly");

        admin.MapGet("", (ServerUpdateService svc) =>
        {
            var status = svc.HelperStatus();
            string? id = status is { } s && s.TryGetProperty("id", out var i) ? i.GetString() : null;
            return Results.Ok(new
            {
                enabled = svc.Enabled,
                currentVersion = svc.CurrentVersion,
                pending = svc.RequestPending(),
                upload = svc.ActiveUpload() is { } u ? Dto(u) : null,
                chunkBytes = ServerUpdateService.ChunkBytes,
                server = svc.PhaseSnapshot(),
                helper = status,
                log = svc.HelperLogTail(id),
            });
        });

        admin.MapGet("/latest", async (ServerUpdateService svc, CancellationToken ct) =>
        {
            try
            {
                var rel = await svc.LatestReleaseAsync(ct);
                if (rel is null) return Results.Ok(new { found = false });
                return Results.Ok(new
                {
                    found = true,
                    rel.Version,
                    rel.Tag,
                    publishedAt = rel.PublishedAt?.ToString("o"),
                    rel.Notes,
                    rel.ArchiveSize,
                    rel.HtmlUrl,
                    newer = Version.Parse(rel.Version) > Version.Parse(svc.CurrentVersion),
                    allowed = ServerUpdateService.IsAllowedTarget(rel.Version, svc.CurrentVersion),
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                return Results.Json(new { error = "GitHub недоступен: " + ex.Message }, statusCode: 502);
            }
        });

        // Скачивание — сотни МБ: фоном, ход — в GET (server.phase).
        admin.MapPost("/github", (HttpContext ctx, ServerUpdateService svc, AuditLog audit) =>
        {
            if (!svc.Enabled) return Disabled();
            if (svc.RequestPending()) return Results.Conflict(new { error = "обновление уже поставлено в очередь" });
            if (svc.ActiveUpload() is not null) return Results.Conflict(new { error = "не закончена загрузка архива — продолжите или отмените её" });
            string user = ctx.User.Identity?.Name ?? "?";
            audit.Write(ctx, "server.update", "server", new { source = "github", from = svc.CurrentVersion });
            _ = Task.Run(() => svc.InstallFromGitHubAsync(user, CancellationToken.None));
            return Results.Accepted();
        });

        // 0.14.3: загрузка по частям (ServerUpdate.cs) — переживает перезагрузку
        // страницы, обрыв связи и перезапуск сервера; проверка — фоном.
        admin.MapPost("/uploads", (HttpContext ctx, UploadStartRequest req, ServerUpdateService svc, AuditLog audit) =>
        {
            var (outcome, s, error) = svc.StartUpload(req.Name ?? "", req.Size, ctx.User.Identity?.Name ?? "?");
            if (outcome == ServerUpdateService.Outcome.Ok && s is not null && s.Received == 0)
                audit.Write(ctx, "server.update.upload", "server", new { s.Id, s.Name, s.Size, from = svc.CurrentVersion });
            return outcome switch
            {
                ServerUpdateService.Outcome.Ok => Results.Ok(Dto(s!)),
                ServerUpdateService.Outcome.Disabled => Disabled(),
                ServerUpdateService.Outcome.Pending => Results.Conflict(new { error = "обновление уже поставлено в очередь" }),
                ServerUpdateService.Outcome.Busy => Results.Conflict(new { error, upload = s is null ? null : Dto(s) }),
                _ => Results.BadRequest(new { error }),
            };
        });

        admin.MapPut("/uploads/{id}", async (HttpContext ctx, string id, long offset, ServerUpdateService svc, AuditLog audit) =>
        {
            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = ServerUpdateService.MaxChunkBytes;
            var (outcome, s) = await svc.AppendChunkAsync(id, offset, ctx.Request.Body, ctx.RequestAborted);
            if (outcome == ServerUpdateService.ChunkOutcome.Complete)
                audit.Write(ctx, "server.update", "server", new { source = "upload", s!.Name, s.Size, from = svc.CurrentVersion });
            return outcome switch
            {
                ServerUpdateService.ChunkOutcome.Ok => Results.Ok(new { s!.Received, s.Size, complete = false }),
                ServerUpdateService.ChunkOutcome.Complete => Results.Accepted(value: new { s!.Received, s.Size, complete = true }),
                ServerUpdateService.ChunkOutcome.WrongOffset => Results.Conflict(new { error = "offset", s!.Received, s.Size }),
                ServerUpdateService.ChunkOutcome.TooLarge => Results.BadRequest(new { error = "часть больше остатка архива или 16 МБ", s!.Received }),
                _ => Results.NotFound(new { error = "загрузка не найдена — начните заново" }),
            };
        });

        admin.MapDelete("/uploads/{id}", (HttpContext ctx, string id, ServerUpdateService svc, AuditLog audit) =>
        {
            if (!svc.CancelUpload(id)) return Results.NotFound();
            audit.Write(ctx, "server.update.upload-cancel", "server", new { id });
            return Results.Ok();
        });

        // Прежний приём одним запросом — для скриптов и старых страниц.
        // Тело запроса — сам архив (application/octet-stream), без multipart:
        // пишется на диск потоком, лимит — свой (MaxUploadMb), не MaxUploadSizeMb записей.
        admin.MapPost("/upload", async (HttpContext ctx, ServerUpdateService svc, ServerConfig cfg, AuditLog audit) =>
        {
            if (!svc.Enabled) return Disabled();
            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = cfg.ServerUpdate.MaxUploadMb * 1024L * 1024L;
            var r = await svc.InstallFromUploadAsync(ctx.Request.Body, ctx.User.Identity?.Name ?? "?", ctx.RequestAborted);
            audit.Write(ctx, "server.update", "server", new { source = "upload", from = svc.CurrentVersion, to = r.Version, outcome = r.Outcome.ToString() });
            return r.Outcome switch
            {
                ServerUpdateService.Outcome.Ok => Results.Ok(new { version = r.Version }),
                ServerUpdateService.Outcome.Busy => Results.Conflict(new { error = "уже идёт другая загрузка или скачивание" }),
                ServerUpdateService.Outcome.Pending => Results.Conflict(new { error = "обновление уже поставлено в очередь" }),
                ServerUpdateService.Outcome.BadArchive => Results.BadRequest(new { error = "это не архив сервера watcher (нужен watcher-server-X.Y.Z.tar.gz из релиза)" }),
                ServerUpdateService.Outcome.Downgrade => Results.BadRequest(new { error = $"версия {r.Version} старше текущей {svc.CurrentVersion} — откат через веб не поддерживается (схема БД)" }),
                _ => Results.Json(new { error = r.Error ?? "не удалось принять архив" }, statusCode: 500),
            };
        });
    }

    private static object Dto(UploadSession s) => new { s.Id, s.Name, s.Size, s.Received, s.CreatedBy, updatedAt = s.UpdatedAt.ToString("o") };

    private static IResult Disabled() =>
        Results.Json(new { error = "обновление из веб-интерфейса не настроено: нет помощника на хосте (Server:ServerUpdate:Dir), поставьте эту версию через install-server.sh" }, statusCode: 409);
}

public sealed record UploadStartRequest(string? Name, long Size);
