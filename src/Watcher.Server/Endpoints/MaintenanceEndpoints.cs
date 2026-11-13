using System.Security.Cryptography;

namespace Watcher.Server;

public sealed record MaintenanceRequest(long CredentialId, string Action, int Minutes = 15, long? PackageId = null, bool Offline = false);
public static class MaintenanceEndpoints
{
    public static void MapMaintenanceEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/hosts/{name}/maintenance").RequireAuthorization("AdminOnly");
        routes.MapGet("", (string name, Db db) =>
        {
            var host = db.Inventory(onlyName: name).FirstOrDefault(); return host?.Id is { } id ? Results.Ok(db.MaintenanceHistory(id)) : Results.NotFound();
        });
        routes.MapPost("", (string name, MaintenanceRequest request, HttpContext ctx, Db db, MaintenanceSigner signer) =>
        {
            if (!MaintenanceSigning.Actions.Contains(request.Action) || request.Minutes is < 1 or > 60) return Results.BadRequest(new { error = "invalid maintenance action or duration" });
            var credential = db.AgentCredentialById(request.CredentialId);
            if (credential is null || credential.RevokedAt is not null || credential.HostName != name) return Results.NotFound();
            if (credential.Kind is not ("video" or "terminal")) return Results.BadRequest(new { error = "maintenance is supported only for video and terminal agents" });
            string? binding = db.CredentialBinding(credential.Id); if (binding is null) return Results.NotFound();
            var state = db.DiagnosticStates(credential.HostId).FirstOrDefault(s => s.CredentialId == credential.Id);
            if (state?.MaintenanceKeyId is null && !request.Offline) return Results.Conflict(new { error = "agent protection is not configured; configure the agent or export an offline grant" });
            using var key = signer.Key(state?.MaintenanceKeyId);
            if (key is null) return Results.Conflict(new { error = "maintenance signing key unavailable" });
            AgentPackage? package = request.Action == "update" && request.PackageId is { } id ? db.AgentPackageById(id) : null;
            if (request.Action == "update" && (package is null || package.Platform is not ("windows-msi" or "linux-deb" or "linux-rpm"))) return Results.BadRequest(new { error = "select a signed MSI, DEB or RPM package" });
            string? newKey = null;
            if (request.Action == "rotate-key")
            {
                using var active = signer.Key(); if (active is null) return Results.Conflict(new { error = "maintenance signing key unavailable" }); newKey = Convert.ToBase64String(active.ExportSubjectPublicKeyInfo());
            }
            var now = DateTime.UtcNow;
            var grant = new MaintenanceGrant(Guid.NewGuid().ToString("N"), binding, credential.Kind, request.Action, now, now.AddMinutes(request.Minutes), package?.Version, package?.Sha256, package?.Platform, package?.Signature, newKey, package?.Id, package?.Size);
            var signed = MaintenanceSigning.Sign(grant, key); db.IssueMaintenance(credential, signed, ctx.User.Identity?.Name ?? "admin", request.Offline); return Results.Ok(signed);
        });
    }
}
