using System.Text.Json;

namespace Watcher.Server;

// R14: группы, парк и их конфиги — политика Management на группе маршрутов.
public static class FleetEndpoints
{
    public static void MapFleetEndpoints(this IEndpointRouteBuilder app)
    {
        var groups = app.MapGroup("/api/groups");
        // GET без RequireAuthorization — только аутентификация (AuthGate), без
        // проверки роли: нужен и Management-странице "Парк", и Recordings-странице
        // "Консоль" (дропдаун фильтра по группе, #/terminal), см. wwwroot/views/. Имя и
        // число хостов группы — не то, что скрывается от viewer'а.
        groups.MapGet("", (Db db) => Results.Ok(
            db.ListGroups().Select(g => new { g.Id, g.Name, hostCount = g.HostCount })));

        var groupsManage = groups.MapGroup("").RequireAuthorization("Management");

        groupsManage.MapPost("", (HttpContext ctx, GroupRequest req, Db db, AuditLog audit) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name missing" });
            try
            {
                long id = db.CreateGroup(req.Name.Trim());
                audit.Write(ctx, "group.create", $"group:{id}", new { name = req.Name.Trim() });
                return Results.Ok(new { id, name = req.Name.Trim() });
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return Results.Conflict(new { error = "group name already exists" });
            }
        });

        groupsManage.MapPut("/{id:long}", (HttpContext ctx, long id, GroupRequest req, Db db, AuditLog audit) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name missing" });
            try
            {
                if (!db.RenameGroup(id, req.Name.Trim())) return Results.NotFound();
                audit.Write(ctx, "group.rename", $"group:{id}", new { name = req.Name.Trim() });
                return Results.Ok();
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return Results.Conflict(new { error = "group name already exists" });
            }
        });

        groupsManage.MapDelete("/{id:long}", (HttpContext ctx, long id, Db db, AuditLog audit) =>
        {
            var members = ServiceConnections.HostsOfGroup(db, id).ToList();
            if (!db.DeleteGroup(id)) return Results.NotFound();
            audit.Write(ctx, "group.delete", $"group:{id}");
            ServiceConnections.Reclassify(db, members);
            return Results.Ok();
        });

        groupsManage.MapGet("/{id:long}/config", (long id, Db db) =>
        {
            var entry = db.GetGroupConfig(id);
            return Results.Ok(new
            {
                configVersion = entry?.ConfigVersion,
                config = entry is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(entry.ConfigJson),
            });
        });

        // 0.13 (п.4): что встречалось на хостах группы — подсказки в её конфиге.
        groupsManage.MapGet("/{id:long}/known", (long id, Db db) =>
        {
            var hosts = db.Inventory().Where(h => h.GroupId == id).Select(h => h.Name).ToList();
            var (users, processes, titles, sites) = db.KnownForHosts(hosts);
            return Results.Ok(new { users, processes, titles, sites });
        });

        // 0.13 (п.5): на скольких хостах группы текущий конфиг уже действует.
        groupsManage.MapGet("/{id:long}/config-status", (long id, Db db) =>
        {
            var hosts = db.Inventory().Where(h => h.GroupId == id).ToList();
            return Results.Ok(new
            {
                total = hosts.Count,
                ok = hosts.Count(h => h.ConfigState == "ok"),
                pending = hosts.Where(h => h.ConfigState == "pending").Select(h => h.Name),
                unknown = hosts.Where(h => h.ConfigState == "unknown").Select(h => h.Name),
            });
        });

        groupsManage.MapPut("/{id:long}/config", (HttpContext ctx, long id, ConfigRequest req, Db db, AuditLog audit) =>
        {
            if (AgentConfigValidation.Validate(req.Config) is { } configError)
                return Results.BadRequest(new { error = configError });
            int? version = db.SetGroupConfig(id, JsonSerializer.Serialize(req.Config));
            if (version is null) return Results.NotFound();
            audit.Write(ctx, "group.config.set", $"group:{id}", new { configVersion = version, config = req.Config });
            ServiceConnections.Reclassify(db, ServiceConnections.HostsOfGroup(db, id));
            return Results.Ok(new { configVersion = version });
        });

        groupsManage.MapDelete("/{id:long}/config", (HttpContext ctx, long id, Db db, AuditLog audit) =>
        {
            if (!db.ClearGroupConfig(id)) return Results.NotFound();
            audit.Write(ctx, "group.config.clear", $"group:{id}");
            ServiceConnections.Reclassify(db, ServiceConnections.HostsOfGroup(db, id));
            return Results.Ok();
        });

        var fleet = app.MapGroup("/api/fleet").RequireAuthorization("Management");

        fleet.MapGet("", (Db db, long? groupId, int limit = 50, int offset = 0) =>
        {
            limit = Math.Clamp(limit, 1, 1000);
            offset = Math.Max(offset, 0);
            var rows = db.ListFleetHosts(groupId, limit, offset);
            return Results.Ok(new
            {
                total = db.CountFleetHosts(groupId),
                limit,
                offset,
                items = rows.Select(h => new
                {
                    h.Id,
                    h.Name,
                    h.IpAddress,
                    h.GroupId,
                    h.GroupName,
                    lastHeartbeatAt = h.LastHeartbeatAt?.ToString("o"),
                    h.AgentVersion,
                    h.DroppedSegments,
                    h.LastHeartbeatHttps,
                    h.LegacyAuth,
                    h.DuplicateCredential,
                    h.ClockSkewSec,
                    h.Person,
                }),
            });
        });

        // PATCH принимает частичное обновление: присутствующие поля меняются,
        // отсутствующие — нет. groupId (как раньше) и/или person (Этап 2,
        // привязка «устройство ↔ сотрудник»). Тело разбираем как JsonElement,
        // чтобы отличить «поле не прислали» от «поле = null» (запись не умеет).
        fleet.MapPatch("/{id:long}", (HttpContext ctx, long id, JsonElement body, Db db, AuditLog audit) =>
        {
            if (db.HostNameById(id) is null) return Results.NotFound();
            bool changed = false;

            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("groupId", out var g))
            {
                if (g.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null))
                    return Results.BadRequest(new { error = "groupId must be a number or null" });
                long? groupId = g.ValueKind == JsonValueKind.Null ? null : g.GetInt64();
                db.SetHostGroup(id, groupId);
                audit.Write(ctx, "host.group", HostTarget(db, id), new { groupId });
                if (db.HostNameById(id) is { } moved) ServiceConnections.Reclassify(db, [moved]);
                changed = true;
            }

            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("person", out var p))
            {
                if (p.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    return Results.BadRequest(new { error = "person must be a string or null" });
                string? person = p.ValueKind == JsonValueKind.Null ? null : p.GetString()?.Trim();
                if (person is { Length: 0 }) person = null;
                if (person is not null && !UploadValidation.IsSafeMetaField(person))
                    return Results.BadRequest(new { error = "person has invalid characters or is too long" });
                db.SetHostPerson(id, person);
                audit.Write(ctx, "host.person", HostTarget(db, id), new { person });
                changed = true;
            }

            if (!changed) return Results.BadRequest(new { error = "nothing to update (expected groupId and/or person)" });
            return Results.Ok();
        });

        // Этап 5 (Android, docs/android-plan.md): снять роль владельца устройства
        // с агента (clearDeviceOwnerApp). Команда держится до следующего heartbeat
        // владельца и гаснет, когда роль ушла — после этого агент удаляется штатно.
        // 404 — хост не Android-владелец (снимать нечего).
        fleet.MapPost("/{id:long}/android/clear-owner", (HttpContext ctx, long id, Db db, AuditLog audit) =>
        {
            if (!db.RequestAndroidClearOwner(id)) return Results.NotFound();
            audit.Write(ctx, "host.android-clear-owner", HostTarget(db, id));
            return Results.Ok();
        });

        // 0.12: и конфиг группы хоста — форма показывает, что хост наследует.
        fleet.MapGet("/{id:long}/config", (long id, Db db) =>
        {
            var entry = db.GetHostConfig(id);
            var group = db.HostGroup(id);
            var groupEntry = group is { } g ? db.GetGroupConfig(g.Id) : null;
            return Results.Ok(new
            {
                configVersion = entry?.ConfigVersion,
                config = entry is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(entry.ConfigJson),
                group = group is { } gr ? new
                {
                    id = gr.Id,
                    name = gr.Name,
                    config = groupEntry is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(groupEntry.ConfigJson),
                } : null,
            });
        });

        // 0.13 (п.10, п.14): удалить хост вместе с записями — только admin. Агент
        // на станции удалит себя: его учётка помечена, ответ ему — 410 (AgentAccess).
        app.MapDelete("/api/fleet/{id:long}", (HttpContext ctx, long id, Db db, ServerConfig cfg, AuditLog audit, bool forgetProtected = false) =>
        {
            var r = db.DeleteHost(id, forgetProtected);
            if (r.Outcome == HostDeleteOutcome.MaintenanceRequired) return Results.Conflict(new { error = "Сначала разрешите удаление защищённого агента в карточке хоста и дождитесь результата." });
            if (r.Outcome == HostDeleteOutcome.NotFound) return Results.NotFound();
            if (r.Outcome == HostDeleteOutcome.LegalHold)
                return Results.Conflict(new { error = "legal hold", message = "На записях хоста действует legal hold — снимите его (Настройки → Хранилище), потом удаляйте." });
            int failed = DeleteRecordingFiles(cfg.DataDir, r.Files ?? []);
            audit.Write(ctx, "host.delete", $"host:{r.Name}", new { segments = r.Segments, terminalSessions = r.TerminalSessions, credentials = r.Credentials, filesNotDeleted = failed, forgetProtected });
            return Results.Ok(new { r.Name, r.Segments, r.TerminalSessions, r.Credentials, filesNotDeleted = failed });
        }).RequireAuthorization("AdminOnly");

        // 0.13 (п.4): что встречалось на хосте — подсказки в конфиге вместо ручного ввода.
        fleet.MapGet("/{id:long}/known", (long id, Db db) =>
        {
            if (db.HostNameById(id) is not { } name) return Results.NotFound();
            var (users, processes, titles, sites) = db.KnownForHosts([name]);
            return Results.Ok(new { users, processes, titles, sites });
        });

        // 0.13 (п.5): дошёл ли конфиг — что действует сейчас (группа + хост), что
        // получил агент и что применили процессы записи экрана.
        fleet.MapGet("/{id:long}/config-status", (long id, Db db) =>
        {
            if (db.HostNameById(id) is not { } name) return Results.NotFound();
            return Results.Ok(ConfigStatus(db, id, name, DateTime.UtcNow));
        });

        fleet.MapPut("/{id:long}/config", (HttpContext ctx, long id, ConfigRequest req, Db db, AuditLog audit) =>
        {
            if (AgentConfigValidation.Validate(req.Config) is { } configError)
                return Results.BadRequest(new { error = configError });
            int? version = db.SetHostConfig(id, JsonSerializer.Serialize(req.Config));
            if (version is null) return Results.NotFound();
            audit.Write(ctx, "host.config.set", HostTarget(db, id), new { configVersion = version, config = req.Config });
            if (db.HostNameById(id) is { } name) ServiceConnections.Reclassify(db, [name]);
            return Results.Ok(new { configVersion = version });
        });

        fleet.MapDelete("/{id:long}/config", (HttpContext ctx, long id, Db db, AuditLog audit) =>
        {
            if (!db.ClearHostConfig(id)) return Results.NotFound();
            audit.Write(ctx, "host.config.clear", HostTarget(db, id));
            if (db.HostNameById(id) is { } name) ServiceConnections.Reclassify(db, [name]);
            return Results.Ok();
        });

        // Массовое назначение группы из раздела «Хосты» (E2) — один запрос вместо N
        // PATCH /api/fleet/{id}. groupId = null снимает группу. Каждый хост — отдельная
        // строка в журнале (как и одиночный PATCH), чтобы фильтр по хосту в #/audit
        // находил и массовые изменения.
        fleet.MapPost("/assign-group", (HttpContext ctx, BulkAssignGroupRequest req, Db db, AuditLog audit) =>
        {
            if (req.HostIds is not { Count: > 0 }) return Results.BadRequest(new { error = "hostIds missing" });
            if (req.HostIds.Count > 1000) return Results.BadRequest(new { error = "too many hosts" });
            if (req.GroupId is not null && db.ListGroups().All(g => g.Id != req.GroupId))
                return Results.BadRequest(new { error = "unknown group" });
            int updated = 0;
            var moved = new List<string>();
            foreach (long id in req.HostIds.Distinct())
            {
                if (!db.SetHostGroup(id, req.GroupId)) continue;
                updated++;
                audit.Write(ctx, "host.group", HostTarget(db, id), new { groupId = req.GroupId, bulk = true });
                if (db.HostNameById(id) is { } name) moved.Add(name);
            }
            ServiceConnections.Reclassify(db, moved);
            return Results.Ok(new { updated });
        });
    }

    // Журнал хранит имя хоста, не только id из hosts: по имени его потом ищут
    // (и оно же видно во всех остальных таблицах), а id без строки hosts ни о чём.
    private static string HostTarget(Db db, long hostId) =>
        db.HostNameById(hostId) is { } name ? $"host:{name}" : $"host:#{hostId}";

    internal static object ConfigStatus(Db db, long hostId, string name, DateTime nowUtc)
    {
        var effective = db.GetEffectiveConfig(name);
        string? expected = effective is null ? null : Db.ConfigHash(effective.ConfigJson);
        var group = db.HostGroup(hostId);
        var status = db.AgentStatusOf(name);
        return new
        {
            expected = new
            {
                hash = expected,
                groupName = group?.Name,
                groupVersion = group is { } g ? db.GetGroupConfig(g.Id)?.ConfigVersion : null,
                hostVersion = db.GetHostConfig(hostId)?.ConfigVersion,
            },
            delivered = status?.DeliveredHash is { } dh ? new { hash = dh, at = status.DeliveredAt?.ToString("o"), current = dh == expected } : null,
            captures = Db.ParseCaptures(status?.CapturesJson).Select(c => new
            {
                c.User,
                c.ConfigHash,
                c.Recording,
                at = c.At?.ToString("o"),
                alive = c.At is { } at && nowUtc - at < Db.CaptureAliveWindow,
                current = c.ConfigHash == expected,
            }),
            state = expected is null ? "unknown" : Db.ConfigStateOf(expected, status?.DeliveredHash, status?.CapturesJson, nowUtc),
        };
    }

    // Файлы записей удалённого хоста — только внутри DataDir (пути из БД
    // относительные; «..» и абсолютные не трогаем). Пустые каталоги — тоже.
    internal static int DeleteRecordingFiles(string dataDir, IEnumerable<string> relPaths)
    {
        string root = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        int failed = 0;
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        // Оба варианта файла записи (исходный и пережатый): после падения посреди
        // пережатия на диске может лежать «ничейный» второй (TranscodeService).
        foreach (var rel in relPaths.SelectMany(TranscodeService.FileVariants))
        {
            string full = Path.GetFullPath(Path.Combine(dataDir, rel));
            if (!full.StartsWith(root, StringComparison.Ordinal)) { failed++; continue; }
            try
            {
                if (File.Exists(full)) File.Delete(full);
                if (Path.GetDirectoryName(full) is { } d) dirs.Add(d);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
        }
        foreach (var d in dirs.OrderByDescending(x => x.Length))
        {
            for (var dir = d; dir is not null && dir.Length > root.Length && dir.StartsWith(root, StringComparison.Ordinal); dir = Path.GetDirectoryName(dir))
            {
                try
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                    else break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { break; }
            }
        }
        return failed;
    }
}
