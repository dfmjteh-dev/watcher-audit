using System.Security.Cryptography;

namespace Watcher.Server;

// H2 (roadmap-next.md): обновление агентов с сервера. Админ загружает пакет
// версии с подписью владельца и назначает группе целевую версию; агент в ответе
// heartbeat получает «для тебя есть версия X», скачивает её с /api/updates/{id},
// сам проверяет подпись и ставит. Откат — назначить группе прежнюю версию.
public static class UpdateEndpoints
{
    public static void MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("").RequireAuthorization("AdminOnly");

        admin.MapGet("/api/agent-packages", (Db db, ServerConfig cfg, UpdateRollout rollout) =>
        {
            var versions = AgentVersionPolicy.Load(db);
            var groupOf = db.Inventory(null).ToDictionary(h => h.Name, h => (h.GroupId, h.GroupName));
            return Results.Ok(new
            {
                // Версии раздельные: сервер — своя, агенты — по загруженным пакетам.
                serverVersion = AppVersion.Core(AppVersion.Current),
                latestAgentVersion = versions.LatestAgentVersion,
                publicKeyConfigured = UpdateSigning.LoadPublicKey(cfg.UpdatePublicKey) is not null,
                // 0.15.0: сколько хостов сейчас качают/ставят пакет и предел одновременных.
                rollout = new { active = rollout.Active, limit = rollout.MaxConcurrent },
                packages = db.ListAgentPackages().Select(p => new
                {
                    p.Id,
                    p.Platform,
                    p.Version,
                    p.Sha256,
                    p.Size,
                    p.UploadedBy,
                    uploadedAt = p.UploadedAt.ToString("o"),
                }),
                groups = db.GroupTargets().Select(g => new { id = g.Id, name = g.Name, targetVersion = g.TargetVersion }),
                hosts = db.HostUpdateStates().Select(h =>
                {
                    var (groupId, groupName) = groupOf.GetValueOrDefault(h.Name);
                    var st = versions.For(h.Name, groupId, h.Version);
                    return new
                    {
                        name = h.Name,
                        platform = h.Platform,
                        version = h.Version,
                        group = groupName,
                        targetVersion = st.Target,
                        latestVersion = st.Latest,
                        versionState = st.Status,
                        status = h.Status,
                        statusAt = h.StatusAt?.ToString("o"),
                    };
                }),
            });
        });

        admin.MapPost("/api/agent-packages", async (HttpContext ctx, Db db, ServerConfig cfg, AuditLog audit) =>
        {
            if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "expected multipart/form-data" });
            var form = await ctx.Request.ReadFormAsync();
            string platform = form["platform"].ToString(), version = form["version"].ToString().Trim(), signature = form["signature"].ToString().Trim();
            var file = form.Files["file"];
            if (file is null || file.Length == 0) return Results.BadRequest(new { error = "file missing" });

            AgentPackageStore.Result r;
            await using (var src = file.OpenReadStream())
                r = await AgentPackageStore.ImportAsync(db, cfg, platform, version, signature, src, ctx.User.Identity?.Name, ctx.RequestAborted);
            switch (r.Outcome)
            {
                case AgentPackageStore.Outcome.NoPublicKey:
                    return Results.Json(new { error = "Server:UpdatePublicKey не задан или не разобран — без него пакеты не принимаются (docs/operations.md, «Обновление агентов с сервера»)" }, statusCode: 409);
                case AgentPackageStore.Outcome.BadPlatform:
                    return Results.BadRequest(new { error = $"platform: one of {string.Join(", ", UpdateSigning.Platforms)}" });
                case AgentPackageStore.Outcome.BadVersion:
                    return Results.BadRequest(new { error = "version: X.Y.Z" });
                case AgentPackageStore.Outcome.Empty:
                    return Results.BadRequest(new { error = "file missing" });
                case AgentPackageStore.Outcome.Exists:
                    return Results.Conflict(new { error = "package for this platform and version already uploaded — delete it first" });
                case AgentPackageStore.Outcome.BadSignature:
                    audit.Write(ctx, "agent-package.upload", $"agent-package:{platform}/{version}", new { sha256 = r.Sha256, rejected = "bad signature" });
                    return Results.BadRequest(new { error = "подпись не сходится с файлом, платформой и версией (sign-update.sh с теми же --platform и --version?)" });
            }
            audit.Write(ctx, "agent-package.upload", $"agent-package:{platform}/{version}", new { id = r.Id, sha256 = r.Sha256, size = r.Size });
            return Results.Ok(new { id = r.Id, sha256 = r.Sha256 });
        });

        admin.MapDelete("/api/agent-packages/{id:long}", (HttpContext ctx, long id, Db db, ServerConfig cfg, AuditLog audit) =>
        {
            var p = db.AgentPackageById(id);
            if (p is null) return Results.NotFound();
            if (db.GroupTargets().FirstOrDefault(g => g.TargetVersion == p.Version) is { Name: not null } g)
                return Results.Conflict(new { error = $"версия {p.Version} — целевая у группы «{g.Name}»" });
            db.DeleteAgentPackage(id);
            try { File.Delete(Path.Combine(cfg.DataDir, p.FilePath)); } catch (IOException) { }
            audit.Write(ctx, "agent-package.delete", $"agent-package:{p.Platform}/{p.Version}", new { id });
            return Results.Ok();
        });

        admin.MapPut("/api/groups/{id:long}/agent-version", (HttpContext ctx, long id, GroupAgentVersionRequest req, Db db, AuditLog audit) =>
        {
            string? version = string.IsNullOrWhiteSpace(req.Version) ? null : req.Version.Trim();
            var before = db.GroupTargets().FirstOrDefault(g => g.Id == id);
            if (before.Name is null) return Results.NotFound();
            if (version is not null && !db.ListAgentPackages().Any(p => p.Version == version))
                return Results.BadRequest(new { error = $"нет загруженного пакета версии {version}" });
            db.SetGroupTargetVersion(id, version);
            audit.Write(ctx, "group.agent-version", $"group:{before.Name}", new { from = before.TargetVersion, to = version });
            return Results.Ok();
        });

        // Скачивание пакета агентом — его Bearer-учётка (AuthGate пропускает
        // /api/updates как машинный путь). Пакеты не секретны, но раздаём только
        // своим агентам.
        // Пакет для офлайн-обслуживания (GuardRepair submit … --package): админ скачивает
        // его вместе с файлом разрешения и передаёт на станцию без связи.
        admin.MapGet("/api/agent-packages/{id:long}/file", (HttpContext ctx, long id, Db db, ServerConfig cfg, AuditLog audit) =>
        {
            var p = db.AgentPackageById(id);
            if (p is null) return Results.NotFound();
            string full = Path.GetFullPath(Path.Combine(cfg.DataDir, p.FilePath));
            if (!File.Exists(full)) return Results.NotFound();
            audit.Write(ctx, "agent-package.download", $"agent-package:{p.Version}", new { id, platform = p.Platform });
            string extension = UpdateSigning.PackageExtension(p.Platform);
            return Results.File(full, "application/octet-stream", $"watcher-agent-{p.Version}{extension}");
        });

        app.MapGet("/api/updates/{id:long}", (HttpContext ctx, long id, Db db, ServerConfig cfg) =>
        {
            if (AgentAccess.Authenticate(ctx, db, cfg) is null) return Results.Empty;
            var p = db.AgentPackageById(id);
            if (p is null) return Results.NotFound();
            string full = Path.GetFullPath(Path.Combine(cfg.DataDir, p.FilePath));
            return File.Exists(full) ? Results.File(full, "application/octet-stream", Path.GetFileName(full)) : Results.NotFound();
        });
    }

    // Ответ heartbeat: пакет целевой версии для платформы агента, если версия
    // отличается от текущей (в любую сторону — откат разрешён, решение владельца).
    // 0.15.0: не больше rollout.MaxConcurrent хостов одновременно (UpdateRollout).
    public static object? OfferFor(Db db, string host, string? platform, string? agentVersion, UpdateRollout? rollout = null)
    {
        if (platform is null || !UpdateSigning.Platforms.Contains(platform)) return null;
        if (db.TargetVersionForHost(host) is not { } target) { rollout?.Release(host); return null; }
        if (agentVersion is not null && AppVersion.Core(agentVersion) == target) { rollout?.Release(host); return null; }
        // MSI до 0.16 запрещают понижение: незащищённый агент получал бы 1603 на каждом heartbeat.
        if (ProtectedGroupUpdates.DowngradeBlock(platform, agentVersion, target, protectedAgent: false) is { } reason)
        {
            db.SetHostUpdateInfo(host, platform, $"blocked {target}: {reason}");
            rollout?.Release(host);
            return null;
        }
        if (db.AgentPackageFor(platform, target) is not { } p) return null;
        if (rollout is not null && !rollout.TryAcquire(host, p.Version)) return null;
        return new { id = p.Id, version = p.Version, platform = p.Platform, sha256 = p.Sha256, size = p.Size, signature = p.Signature, url = $"/api/updates/{p.Id}" };
    }
}
