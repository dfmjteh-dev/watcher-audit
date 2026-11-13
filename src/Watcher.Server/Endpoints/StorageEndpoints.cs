namespace Watcher.Server;

// H6 (roadmap-next.md): сроки хранения по группам и legal hold. Только admin:
// сокращение срока удаляет записи, а снятие hold позволяет retention удалить
// то, что держали для разбирательства. Всё — в журнал доступа.
public static class StorageEndpoints
{
    public const int MaxRetentionDays = 3650;
    public const int MaxReasonLength = 500;

    public static void MapStorageEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("").RequireAuthorization("AdminOnly");

        admin.MapGet("/api/retention", (Db db, ServerConfig cfg) => Results.Ok(new
        {
            retentionDays = cfg.RetentionDays,
            groups = db.GroupRetentions().Select(g => new { g.GroupId, g.Name, g.RetentionDays }),
        }));

        admin.MapPut("/api/groups/{id:long}/retention", (HttpContext ctx, long id, GroupRetentionRequest req, Db db, AuditLog audit) =>
        {
            if (req.RetentionDays is < 0 or > MaxRetentionDays)
                return Results.BadRequest(new { error = $"retentionDays: null (как у сервера) или 0..{MaxRetentionDays} (0 — не удалять по возрасту)" });
            var before = db.GroupRetentions().FirstOrDefault(g => g.GroupId == id);
            if (before is null) return Results.NotFound();
            db.SetGroupRetention(id, req.RetentionDays);
            audit.Write(ctx, "group.retention", $"group:{before.Name}", new { from = before.RetentionDays, to = req.RetentionDays });
            return Results.Ok();
        });

        var holds = admin.MapGroup("/api/legal-holds");

        holds.MapGet("", (Db db, string? host, bool active = false) =>
            Results.Ok(db.ListLegalHolds(string.IsNullOrWhiteSpace(host) ? null : host, active).Select(HoldDto)));

        holds.MapPost("", (HttpContext ctx, LegalHoldRequest req, Db db, AuditLog audit) =>
        {
            string host = (req.Host ?? "").Trim();
            string reason = (req.Reason ?? "").Trim();
            if (host.Length == 0 || !UploadValidation.IsSafeMetaField(host))
                return Results.BadRequest(new { error = "host missing or invalid" });
            if (reason.Length == 0 || reason.Length > MaxReasonLength)
                return Results.BadRequest(new { error = $"reason required, up to {MaxReasonLength} characters" });
            DateTime? from = req.From?.ToUniversalTime(), to = req.To?.ToUniversalTime();
            if (from is not null && to is not null && from >= to)
                return Results.BadRequest(new { error = "from must be before to" });
            long id = db.CreateLegalHold(host, from, to, reason, ctx.User.Identity?.Name);
            audit.Write(ctx, "legal-hold.create", $"host:{host}", new
            {
                id,
                from = from?.ToString("o"),
                to = to?.ToString("o"),
                reason = AuditLog.Clip(reason),
            });
            return Results.Ok(new { id });
        });

        holds.MapPost("/{id:long}/release", (HttpContext ctx, long id, ReleaseLegalHoldRequest req, Db db, AuditLog audit) =>
        {
            var hold = db.LegalHoldById(id);
            if (hold is null) return Results.NotFound();
            if (!hold.Active) return Results.Conflict(new { error = "already released" });
            string? reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim();
            if (reason is { Length: > MaxReasonLength }) return Results.BadRequest(new { error = $"reason up to {MaxReasonLength} characters" });
            db.ReleaseLegalHold(id, ctx.User.Identity?.Name, reason);
            audit.Write(ctx, "legal-hold.release", $"host:{hold.Host}", new { id, reason = AuditLog.Clip(reason) });
            return Results.Ok();
        });
    }

    public static object HoldDto(LegalHold h) => new
    {
        h.Id,
        h.Host,
        from = h.From?.ToString("o"),
        to = h.To?.ToString("o"),
        h.Reason,
        h.CreatedBy,
        createdAt = h.CreatedAt.ToString("o"),
        h.ReleasedBy,
        releasedAt = h.ReleasedAt?.ToString("o"),
        h.ReleaseReason,
        h.Active,
    };
}
