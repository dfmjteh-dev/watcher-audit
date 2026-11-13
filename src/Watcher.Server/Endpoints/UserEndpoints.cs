using System.Text.Json;

namespace Watcher.Server;

// R14: управление пользователями (Этап C2) и журнал доступа (Этап C3) — только
// admin. Политика на группе: новый эндпоинт здесь не может её «забыть».
public static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("").RequireAuthorization("AdminOnly");

        // Этап C2 (roadmap-next.md): #/settings/users — создать, сменить роль,
        // отключить, сбросить пароль, назначить группы (viewer-scoping из Этапа C1,
        // до этого было доступно только через CLI --set-user-groups). Все — AdminOnly,
        // не Management: роли operator тоже не положено управлять учётками.
        var users = admin.MapGroup("/api/users");

        users.MapGet("", (Db db) => Results.Ok(
            db.ListUsers().Select(u => new { u.Id, u.Username, u.Role, u.Disabled, groupIds = db.UserGroupIds(u.Id), authSource = u.AuthSource, canExport = u.CanExport, canManagePhones = u.CanManagePhones })));

        users.MapPost("", (HttpContext ctx, CreateUserRequest req, Db db, AuditLog audit) =>
        {
            string username = (req.Username ?? "").Trim();
            if (string.IsNullOrEmpty(username)) return Results.BadRequest(new { error = "username missing" });
            if (!CliCommands.ValidRoles.Contains(req.Role))
                return Results.BadRequest(new { error = $"unknown role, expected one of: {string.Join(", ", CliCommands.ValidRoles)}" });
            if (db.UserByUsername(username) is not null)
                return Results.Conflict(new { error = "username already exists" });

            bool generated = string.IsNullOrEmpty(req.Password);
            string password = generated ? CliCommands.GenerateRandomPassword() : req.Password!;
            if (password.Length < CliCommands.MinPasswordLength)
                return Results.BadRequest(new { error = $"password must be at least {CliCommands.MinPasswordLength} characters" });

            long id = db.InsertUser(username, UserAuth.HashPassword(password), req.Role);
            audit.Write(ctx, "user.create", $"user:{username}", new { role = req.Role, generated });
            return Results.Ok(new { id, username, role = req.Role, generatedPassword = generated ? password : null });
        });

        users.MapPatch("/{id:long}", (HttpContext ctx, long id, UpdateUserRequest req, Db db, AuditLog audit) =>
        {
            var user = db.UserById(id);
            if (user is null) return Results.NotFound();
            // H1: роль пользователя каталога задают группы AD — ручная правка
            // перезаписалась бы при следующей проверке. Отключить можно.
            if (req.Role is not null && user.IsLdap && req.Role != user.Role)
                return Results.Conflict(new { error = "role of a directory user is set by directory groups" });
            // H7: право выгрузки пользователя каталога — группы Server:Ldap:ExportGroups.
            if (req.CanExport is not null && user.IsLdap && req.CanExport != user.CanExport)
                return Results.Conflict(new { error = "export permission of a directory user is set by directory groups" });
            if (req.Role is not null && !CliCommands.ValidRoles.Contains(req.Role))
                return Results.BadRequest(new { error = $"unknown role, expected one of: {string.Join(", ", CliCommands.ValidRoles)}" });

            // Lockout-guard: не дать снять роль admin или отключить последнего
            // включённого admin'а — иначе управлять пользователями/группами стало бы
            // физически некому (сброс только прямой правкой SQLite, как до Задачи 10b).
            bool losesAdmin = user.Role == "admin" && !user.Disabled &&
                ((req.Role is not null && req.Role != "admin") || req.Disabled == true);
            if (losesAdmin && db.CountEnabledAdmins() <= 1)
                return Results.BadRequest(new { error = "cannot remove the last enabled admin" });

            if (req.Role is not null) db.UpdateUserRole(id, req.Role);
            if (req.Disabled is not null) db.SetUserDisabled(id, req.Disabled.Value);
            if (req.CanExport is not null) db.SetUserCanExport(id, req.CanExport.Value);
            if (req.CanManagePhones is not null) db.SetUserCanManagePhones(id, req.CanManagePhones.Value);
            audit.Write(ctx, "user.update", $"user:{user.Username}", new
            {
                role = req.Role is null ? null : new { from = user.Role, to = req.Role },
                disabled = req.Disabled is null ? null : new { from = user.Disabled, to = req.Disabled.Value },
                canExport = req.CanExport is null ? null : new { from = user.CanExport, to = req.CanExport.Value },
                canManagePhones = req.CanManagePhones is null ? null : new { from = user.CanManagePhones, to = req.CanManagePhones.Value },
            });
            return Results.Ok();
        });

        users.MapPost("/{id:long}/reset-password", (HttpContext ctx, long id, ResetUserPasswordRequest req, Db db, AuditLog audit) =>
        {
            var user = db.UserById(id);
            if (user is null) return Results.NotFound();
            if (user.IsLdap) return Results.Conflict(new { error = "directory user has no local password" });
            bool generated = string.IsNullOrEmpty(req.Password);
            string password = generated ? CliCommands.GenerateRandomPassword() : req.Password!;
            if (password.Length < CliCommands.MinPasswordLength)
                return Results.BadRequest(new { error = $"password must be at least {CliCommands.MinPasswordLength} characters" });
            db.SetUserPasswordById(id, UserAuth.HashPassword(password));
            audit.Write(ctx, "user.reset-password", $"user:{user.Username}", new { generated });
            return Results.Ok(new { generatedPassword = generated ? password : null });
        });

        users.MapGet("/{id:long}/groups", (long id, Db db) =>
            db.UserById(id) is null ? Results.NotFound() : Results.Ok(db.UserGroupIds(id)));

        users.MapPut("/{id:long}/groups", (HttpContext ctx, long id, SetUserGroupsRequest req, Db db, AuditLog audit) =>
        {
            var user = db.UserById(id);
            if (user is null) return Results.NotFound();
            var groupIds = req.GroupIds ?? new List<long>();
            db.SetUserGroups(id, groupIds);
            audit.Write(ctx, "user.groups", $"user:{user.Username}", new { groupIds });
            return Results.Ok();
        });

        // Этап C3 (roadmap-next.md): #/audit — только admin. operator меняет конфиги и
        // сам попадает в журнал, но читать его (в т.ч. чужие просмотры записей) — нет.
        admin.MapGet("/api/audit", (Db db, string? username, string? action, DateTime? from, DateTime? to, int limit = 100, int offset = 0) =>
        {
            limit = Math.Clamp(limit, 1, 1000);
            offset = Math.Max(offset, 0);
            var rows = db.QueryAudit(username, action, from, to, limit, offset);
            return Results.Ok(new
            {
                total = db.CountAudit(username, action, from, to),
                limit,
                offset,
                items = rows.Select(e => new
                {
                    e.Id,
                    ts = e.Ts.ToString("o"),
                    e.Username,
                    e.Ip,
                    e.Action,
                    e.Target,
                    details = e.Details is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(e.Details),
                }),
            });
        });
    }
}
