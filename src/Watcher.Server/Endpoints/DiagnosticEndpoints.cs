using System.Text.Json;

namespace Watcher.Server;

public static class DiagnosticEndpoints
{
    public static void MapDiagnosticEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/agent-diagnostics", async (HttpContext ctx, Db db, ServerConfig cfg, UpdateRollout rollout) =>
        {
            if (AgentAccess.Authenticate(ctx, db, cfg) is not { } caller) return Results.Empty;
            if (caller.Identity is not { } identity) return Results.StatusCode(403);
            var bytes = new byte[65537]; int count = 0, read;
            while (count < bytes.Length && (read = await ctx.Request.Body.ReadAsync(bytes.AsMemory(count), ctx.RequestAborted)) > 0) count += read;
            if (count > 65536) return Results.StatusCode(413);
            DiagnosticBatch? batch;
            try { batch = JsonSerializer.Deserialize<DiagnosticBatch>(bytes.AsSpan(0, count), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid diagnostics" }); }
            if (batch is null || batch.Events?.Count > 32 || batch.Events?.Any(e => e is null || !DiagnosticContract.Valid(e)) == true)
                return Results.BadRequest(new { error = "invalid diagnostics" });
            if (batch.MaintenanceKeyId is { } key && (key.Length != 64 || !key.All(char.IsAsciiHexDigit))) return Results.BadRequest(new { error = "invalid diagnostics" });
            if (batch.MaintenanceResults is { } results && (results.Count > 16 || results.Any(r => r is null || !Guid.TryParseExact(r.GrantId, "N", out _) || r.Status is not ("accepted" or "completed" or "failed" or "rejected") || r.Code is not ("accepted" or "completed" or "operation-failed" or "invalid-or-used-grant" or "replay-store-unavailable" or "package-download-failed")))) return Results.BadRequest(new { error = "invalid diagnostics" });
            if (batch.UpdateProgress is { } progress && (progress.Count > 16 || progress.Any(p => p is null || !UpdateProgressContract.Valid(p)))) return Results.BadRequest(new { error = "invalid update progress" });
            if (batch.Snapshot is { } s && (!Guid.TryParseExact(s.RequestId, "N", out _) || s.Version is not { Length: > 0 and <= 64 }
                || s.Version.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-')) || s.Platform is not ("windows" or "linux")
                || s.QueueFiles is < 0 or > 10000000 || s.QueueBytes is < 0 or > 1000000000000000 || s.MemoryBytes is < 0 or > 1000000000000000
                || s.Processes is < 0 or > 100000 || s.FreeBytes is < 0 or > 1000000000000000))
                return Results.BadRequest(new { error = "invalid diagnostics" });
            var receipt = db.ReceiveDiagnostics(identity, batch, DateTime.UtcNow);
            if (receipt?.MaintenanceAccepted is { } confirmed && batch.MaintenanceResults is { } completed)
                foreach (var result in completed.Where(r => r.Status is "completed" or "failed" or "rejected" && confirmed.Contains(r.GrantId)))
                    if (db.IsGroupUpdate(identity.CredentialId, result.GrantId)) rollout.ReleaseProtected(identity.HostName, result.GrantId);
            return receipt is null ? Results.StatusCode(429) : Results.Ok(receipt);
        });
        app.MapGet("/api/hosts/{name}/diagnostics", (string name, long? cursor, HttpContext ctx, Db db, AuditLog audit) =>
        {
            if (!ViewerScope.CanSeeHost(ctx, db, name)) return Results.Forbid();
            var host = db.Inventory(onlyName: name).FirstOrDefault();
            if (host?.Id is not { } id) return Results.NotFound();
            var items = db.DiagnosticEvents(id, cursor); bool more = items.Count > 50;
            if (more) items.RemoveAt(items.Count - 1);
            audit.Write(ctx, "agent.diagnostics-view", "host:" + id, new { count = items.Count });
            return Results.Ok(new
            {
                items,
                nextCursor = more ? items[^1].Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                states = db.DiagnosticStates(id)
            });
        });
        app.MapGet("/api/hosts/{name}/diagnostics/state", (string name, HttpContext ctx, Db db) =>
        {
            if (!ViewerScope.CanSeeHost(ctx, db, name)) return Results.Forbid();
            var host = db.Inventory(onlyName: name).FirstOrDefault();
            return host?.Id is { } id ? Results.Ok(db.DiagnosticStates(id)) : Results.NotFound();
        });
        app.MapPost("/api/hosts/{name}/diagnostics/request", (string name, HttpContext ctx, Db db) =>
        {
            var host = db.Inventory(onlyName: name).FirstOrDefault();
            return host?.Id is { } id ? Results.Ok(new { requested = db.RequestDiagnostics(id, DateTime.UtcNow, ctx.User.Identity?.Name ?? "admin") }) : Results.NotFound();
        }).RequireAuthorization("AdminOnly");
    }
}
