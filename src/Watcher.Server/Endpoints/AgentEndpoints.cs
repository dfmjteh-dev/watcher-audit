using System.Text.Json;

namespace Watcher.Server;

// R14: агентские heartbeat/регистрация и управление учётками агентов (Задача G1).
public static class AgentEndpoints
{
    // Больше за раз не принимаем — остальное агент пришлёт следующими heartbeat'ами.
    internal const int MaxLossesPerHeartbeat = 200;
    private const int MaxCaptures = 50;

    // Процессы записи — как прислал агент, но только известные поля и без
    // управляющих символов (это текст от агента, уйдёт в веб-UI).
    internal static string? CapturesJson(List<CaptureReport>? captures)
    {
        if (captures is null) return null;
        var clean = captures.Take(MaxCaptures)
            .Where(c => c.User is { Length: > 0 and <= 128 } u && UploadValidation.IsSafeMetaField(u))
            .Select(c => new
            {
                user = c.User,
                configHash = c.ConfigHash is { Length: > 0 and <= 64 } h && h.All(char.IsAsciiHexDigit) ? h : null,
                recording = c.Recording,
                at = c.At?.ToUniversalTime().ToString("o"),
            });
        return JsonSerializer.Serialize(clean);
    }

    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/heartbeat", (HttpContext ctx, Db db, ServerConfig cfg, ServerHealth health, AuditLog audit, HostRenameNotices notices, UpdateRollout rollout, MaintenanceSigner signer, HeartbeatRequest req) =>
        {
            if (AgentAccess.Authenticate(ctx, db, cfg) is not { } caller) return Results.Empty;
            if (caller.Legacy && string.IsNullOrWhiteSpace(req.Host)) return Results.BadRequest(new { error = "host missing" });
            // Машину переименовали — переименовываем хост (с историей), учётка та же.
            if (caller.Identity is { } id && AgentAuth.IsValidHostName(req.Host) && UploadValidation.IsSafeMetaField(req.Host) &&
                !string.Equals(req.Host, id.HostName, StringComparison.OrdinalIgnoreCase))
            {
                var outcome = db.RenameHost(id.HostId, req.Host!);
                if (outcome == RenameOutcome.Renamed)
                {
                    audit.Write(ctx, "agent.host-renamed", $"host:{req.Host}", new { from = id.HostName, kind = id.Kind }, username: "agent");
                    notices.Clear(id.HostId);
                    caller = caller with { Identity = id with { HostName = req.Host! } };
                }
                else if (outcome == RenameOutcome.Conflict && notices.FirstTime(id.HostId, req.Host!))
                {
                    // Раз на пару «хост → имя», не на каждый heartbeat.
                    audit.Write(ctx, "agent.host-rename-conflict", $"host:{id.HostName}", new { wants = req.Host, kind = id.Kind }, username: "agent");
                }
            }
            if (AgentAuth.ResolveHost(caller, req.Host) is not { } host)
                return Results.Json(new { error = "host does not match agent credential" }, statusCode: 403);
            // B1 (roadmap-next.md): Request.IsHttps (после UseForwardedHeaders в Program.cs) —
            // то, что реально увидел сервер на проводе, не самоотчёт агента о своём
            // ServerUrl (который мог сам быть неверно настроен или устареть).
            // R7: ошибка часов станции — для #/fleet (см. StationClock).
            TimeSpan? skew = StationClock.MeasureSkew(ctx.Request, DateTime.UtcNow);
            db.UpsertHeartbeat(host, req.Ip, req.Version, req.DroppedSegments ?? 0, ctx.Request.IsHttps, skew?.TotalSeconds);
            health.CountHeartbeat();
            // Задача G1: в #/fleet видно, какие хосты ещё на общем токене.
            db.SetHostLegacyAuth(host, caller.Legacy);
            // Конфиг группы, поверх него по полям — настройки хоста; известному хосту
            // всегда объект, хотя бы {} (см. Db.GetEffectiveConfig).
            var effective = db.GetEffectiveConfig(host);
            // H2: платформа и итог обновления — для «Обновлений»; статус длинный
            // или с управляющими символами не пишем (это текст от агента).
            string? platform = UpdateSigning.Platforms.Contains(req.Platform) ? req.Platform : null;
            string? updateStatus = req.UpdateStatus is { Length: > 0 and <= 300 } st && !st.Any(char.IsControl) ? st : null;
            // Старый обычный updater защищённого агента не выполняет установку,
            // его last-result может относиться к давнему обновлению. Итог Guard
            // приходит по диагностике и не должен затираться этим старым файлом.
            db.SetHostUpdateInfo(host, platform, ProtectedGroupUpdates.UsesMaintenanceChannel(db, caller.Identity) ? null : updateStatus);
            // Этап 5 (Android): роль владельца от агента; сервер отвечает командой
            // снять роль, пока агент владелец и админ её запросил.
            bool clearOwner = platform == AgentAuth.KindAndroid && req.OwnerActive is { } active
                && db.SyncAndroidOwner(host, active);
            // Этап 12: удалённая блокировка — сохраняем факт от агента, отдаём желаемое.
            bool lockDevice = platform == AgentAuth.KindAndroid && db.SyncAndroidLock(host, req.Locked);
            // Этап 12+: офлайн-админ разблокировал устройство локально — гасим команду.
            if (platform == AgentAuth.KindAndroid && req.AdminUnlocked == true) { db.ClearAndroidLockRequest(host); lockDevice = false; }
            // 0.13: что у агента — для «конфиг применён» и журнала потерь.
            string? reportedHash = req.ConfigHash is { Length: > 0 and <= 64 } ch && ch.All(char.IsAsciiHexDigit) ? ch : null;
            db.UpdateAgentStatus(host, reportedHash, CapturesJson(req.Captures), req.Losses is not null);
            // 0.13 (PR9б): ресурсы агента — агенты до 0.13 их не шлют, тогда прежний отчёт остаётся.
            if (Db.Sanitize(req.Resources, DateTime.UtcNow) is { } res) db.SetHostResources(host, res);
            int lossesAccepted = req.Losses is { Count: > 0 } losses ? db.InsertLosses(host, losses.Take(MaxLossesPerHeartbeat).ToList()) : 0;
            return Results.Ok(new
            {
                configVersion = effective?.ConfigVersion,
                configHash = effective is null ? null : Db.ConfigHash(effective.ConfigJson),
                lossesAccepted,
                // 0.14.2: предел тела запроса — агент не шлёт файл, который сервер
                // всё равно отвергнет 413 (иначе он повторял бы его бесконечно).
                maxUploadBytes = cfg.MaxUploadSizeMb * 1024L * 1024L,
                config = effective is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(effective.ConfigJson),
                update = ProtectedGroupUpdates.OfferFor(db, host, platform, req.Version, caller.Identity, signer, rollout),
                // 0.16: Linux-агент без защиты включает её сам по ключу из комплекта,
                // заверенному ключом обновлений (Windows получает ключ внутри MSI).
                maintenanceKey = platform is not null && platform.StartsWith("linux-", StringComparison.Ordinal) ? KitMaintenanceKey.Read(cfg.AgentKitDir) : null,
                // Этап 8 (Android): открытый ключ обновлений и в heartbeat — агент,
                // зарегистрированный до того, как ключ задали, подхватит его (fill-once).
                updatePublicKey = cfg.UpdatePublicKey,
                // Этап 5: пусто/false — обычный ответ; true — агенту снять роль владельца.
                clearOwner,
                // Этап 12: true — заблокировать устройство (kiosk-заглушка), false — снять.
                lockDevice,
                // Этап 12+: логины/хэши локальных админов — для офлайн-разблокировки на устройстве.
                adminCredentials = platform == AgentAuth.KindAndroid
                    ? db.LocalAdminCredentials().Select(a => new { u = a.Username, h = a.Hash }).ToArray()
                    : null,
            });
        });

        // Задача G1 (roadmap-next.md): регистрация агента. Токен регистрации вводится в
        // инсталлятор (многоразовый, со сроком и лимитом), агент меняет его на
        // собственный секрет — дальше ходит только с ним. Секрет показывается один раз,
        // на сервере только хэш. Повторная учётка того же вида у существующего хоста
        // работает, но помечается (#/fleet, Настройки → Агенты) и пишется в журнал.
        app.MapPost("/api/enroll", (HttpContext ctx, Db db, AuditLog audit, ServerConfig cfg, EnrollRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Token) || !AgentAuth.IsValidHostName(req.Host) ||
                req.Kind is null || !AgentAuth.Kinds.Contains(req.Kind))
                return Results.BadRequest(new { error = "token, host and kind (video|terminal|android) required" });

            string secret = AgentAuth.NewAgentSecret();
            string? ip = ctx.Connection.RemoteIpAddress?.ToString();
            var (outcome, cred, tokenName) = db.Enroll(AgentAuth.Hash(req.Token), req.Host!, req.Kind, AgentAuth.Hash(secret), ip, DateTime.UtcNow);
            if (outcome != EnrollOutcome.Ok || cred is null)
            {
                audit.Write(ctx, "agent.enroll-failed", $"host:{req.Host}", new { kind = req.Kind }, username: "agent");
                return Results.Json(new { error = "invalid, expired or exhausted enroll token" }, statusCode: 401);
            }
            audit.Write(ctx, "agent.enroll", $"host:{cred.HostName}",
                new { kind = cred.Kind, credentialId = cred.Id, enrollToken = tokenName, duplicate = cred.DuplicateKind }, username: "agent");
            // Этап 8 (Android): открытый ключ обновлений пиним при регистрации — им агент
            // проверяет подпись пакетов (как станция своим из конфига). Пусто — автообновление
            // выключено (нет ключа). Android вдобавок сам требует тот же сертификат подписи APK.
            return Results.Ok(new { secret, host = cred.HostName, kind = cred.Kind, credentialId = cred.Id, updatePublicKey = cfg.UpdatePublicKey });
        }).RequireRateLimiting("enroll");

        // Токены регистрации и учётки агентов — только admin.
        var agents = app.MapGroup("/api/agents").RequireAuthorization("AdminOnly");

        agents.MapGet("/enroll-tokens", (Db db) => Results.Ok(db.ListEnrollTokens().Select(t => new
        {
            t.Id,
            t.Name,
            t.GroupId,
            t.GroupName,
            createdAt = t.CreatedAt.ToString("o"),
            t.CreatedBy,
            expiresAt = t.ExpiresAt.ToString("o"),
            t.MaxUses,
            t.Uses,
            revokedAt = t.RevokedAt?.ToString("o"),
        })));

        agents.MapPost("/enroll-tokens", (HttpContext ctx, CreateEnrollTokenRequest req, Db db, AuditLog audit) =>
        {
            string name = (req.Name ?? "").Trim();
            if (name.Length == 0 || !UploadValidation.IsSafeMetaField(name))
                return Results.BadRequest(new { error = "name required" });
            if (req.TtlHours is < 1 or > 24 * 365)
                return Results.BadRequest(new { error = "ttlHours must be 1..8760" });
            if (req.MaxUses is < 1)
                return Results.BadRequest(new { error = "maxUses must be positive or empty" });
            if (req.GroupId is { } gid && !db.ListGroups().Any(g => g.Id == gid))
                return Results.BadRequest(new { error = "unknown group" });

            string token = AgentAuth.NewEnrollToken();
            var expiresAt = DateTime.UtcNow.AddHours(req.TtlHours);
            long id = db.InsertEnrollToken(name, AgentAuth.Hash(token), req.GroupId, expiresAt, req.MaxUses, ctx.User.Identity?.Name);
            audit.Write(ctx, "enroll-token.create", $"enroll-token:{name}",
                new { id, groupId = req.GroupId, ttlHours = req.TtlHours, maxUses = req.MaxUses });
            return Results.Ok(new { id, token, expiresAt = expiresAt.ToString("o") });
        });

        agents.MapPost("/enroll-tokens/{id:long}/revoke", (HttpContext ctx, long id, Db db, AuditLog audit) =>
        {
            var t = db.EnrollTokenById(id);
            if (t is null) return Results.NotFound();
            db.RevokeEnrollToken(id);
            audit.Write(ctx, "enroll-token.revoke", $"enroll-token:{t.Name}", new { id });
            return Results.Ok();
        });

        agents.MapGet("/credentials", (Db db, long? hostId) => Results.Ok(db.ListAgentCredentials(hostId).Select(a => new
        {
            a.Id,
            a.HostId,
            a.HostName,
            a.Kind,
            createdAt = a.CreatedAt.ToString("o"),
            a.EnrollTokenName,
            lastUsedAt = a.LastUsedAt?.ToString("o"),
            a.LastIp,
            revokedAt = a.RevokedAt?.ToString("o"),
            a.DuplicateKind,
        })));

        agents.MapPost("/credentials/{id:long}/revoke", (HttpContext ctx, long id, Db db, AuditLog audit) =>
        {
            var a = db.AgentCredentialById(id);
            if (a is null) return Results.NotFound();
            db.RevokeAgentCredential(id);
            audit.Write(ctx, "agent.revoke", $"host:{a.HostName}", new { credentialId = id, kind = a.Kind });
            return Results.Ok();
        });

        // 0.13 (п.10): отозванная учётка живого хоста — удалить из списка совсем.
        agents.MapDelete("/credentials/{id:long}", (HttpContext ctx, long id, Db db, AuditLog audit) =>
        {
            var a = db.AgentCredentialById(id);
            if (a is null) return Results.NotFound();
            if (!db.DeleteRevokedCredential(id)) return Results.Conflict(new { error = "credential is not revoked" });
            audit.Write(ctx, "agent.credential-delete", $"host:{a.HostName}", new { credentialId = id, kind = a.Kind });
            return Results.Ok();
        });

        agents.MapPost("/credentials/{id:long}/acknowledge", (HttpContext ctx, long id, Db db, AuditLog audit) =>
        {
            var a = db.AgentCredentialById(id);
            if (a is null) return Results.NotFound();
            db.AcknowledgeAgentCredential(id);
            audit.Write(ctx, "agent.acknowledge", $"host:{a.HostName}", new { credentialId = id, kind = a.Kind });
            return Results.Ok();
        });
    }
}

// Конфликт имени при переименовании хоста (Db.RenameHost) — в журнал один раз на
// пару «хост → желаемое имя», а не каждую минуту с heartbeat. После рестарта
// сервера запишется ещё раз — это не страшно.
public sealed class HostRenameNotices
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> _seen = new();

    public bool FirstTime(long hostId, string wanted) =>
        !(_seen.TryGetValue(hostId, out var prev) && prev == wanted) && (_seen[hostId] = wanted) is not null;

    public void Clear(long hostId) => _seen.TryRemove(hostId, out _);
}
