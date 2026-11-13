using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Watcher.Server;

public static class DiagnosticArchiveEndpoints
{
    public static void MapDiagnosticArchiveEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/agent-diagnostics/archive", async (HttpContext ctx, Db db, ServerConfig cfg) =>
        {
            if (AgentAccess.Authenticate(ctx, db, cfg) is not { } caller) return Results.Empty;
            if (caller.Identity is not { } identity) return Results.StatusCode(403);
            byte[] bytes = new byte[DiagnosticArchiveContract.MaxBytes + 1]; int count = 0, read;
            while (count < bytes.Length && (read = await ctx.Request.Body.ReadAsync(bytes.AsMemory(count), ctx.RequestAborted)) > 0) count += read;
            if (count > DiagnosticArchiveContract.MaxBytes) return Results.StatusCode(413);
            DiagnosticArchiveReport? report;
            try { report = JsonSerializer.Deserialize<DiagnosticArchiveReport>(bytes.AsSpan(0, count), new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }); }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid diagnostic archive" }); }
            if (report is null || !DiagnosticArchiveContract.Valid(report)) return Results.BadRequest(new { error = "invalid diagnostic archive" });
            return db.ReceiveDiagnosticArchive(identity, report, DateTime.UtcNow) ? Results.Ok(new { accepted = report.RequestId }) : Results.NotFound();
        });
        var group = app.MapGroup("/api/hosts/{name}/diagnostics/archives").RequireAuthorization("AdminOnly");
        group.MapPost("", (string name, HttpContext ctx, Db db) =>
        {
            var host = db.Inventory(onlyName: name).FirstOrDefault();
            return host?.Id is { } id ? Results.Ok(new { requested = db.RequestDiagnosticArchives(id, DateTime.UtcNow, ctx.User.Identity?.Name ?? "admin") }) : Results.NotFound();
        });
        group.MapGet("", (string name, HttpContext ctx, Db db) =>
        {
            var host = db.Inventory(onlyName: name).FirstOrDefault();
            ctx.Response.Headers.CacheControl = "no-store";
            return host?.Id is { } id ? Results.Ok(db.DiagnosticArchives(id, DateTime.UtcNow)) : Results.NotFound();
        });
        group.MapGet("/{requestId}", (string name, string requestId, HttpContext ctx, Db db, AuditLog audit) =>
        {
            if (!Guid.TryParseExact(requestId, "N", out _)) return Results.NotFound();
            var host = db.Inventory(onlyName: name).FirstOrDefault();
            string? json = host?.Id is { } id ? db.DiagnosticArchiveJson(id, requestId, DateTime.UtcNow) : null;
            if (json is null) return Results.NotFound();
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("diagnostics.json").Open(), new UTF8Encoding(false))) writer.Write(json);
                using (var writer = new StreamWriter(zip.CreateEntry("README.txt").Open(), new UTF8Encoding(false)))
                    writer.Write("Очищенная диагностика Watcher. Только фиксированные коды событий, этапы обновления, счётчики логов и exit codes MSI. Исходные сообщения, stack traces, конфиги, credentials, содержимое пользовательских сессий не включены. Logs содержит последние ограниченные фрагменты не более двух файлов каждого источника; Truncated означает неполноту. Отсутствие событий не подтверждает исправность. CollectedAt — время приёма сервером. Архив доступен 24 часа после приёма.");
            }
            ctx.Response.Headers.CacheControl = "no-store"; audit.Write(ctx, "agent.archive-download", "host:" + host!.Id, new { requestId });
            return Results.File(buffer.ToArray(), "application/zip", "watcher-diagnostics-" + requestId + ".zip");
        });
    }
}
