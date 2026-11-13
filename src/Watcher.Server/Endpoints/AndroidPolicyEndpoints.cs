using System.Text.Json;
using System.Text.Json.Nodes;

namespace Watcher.Server;

// Этап 11 Android (docs/android-plan.md): редактирование политики управления
// (androidPolicy) отдельным правом «управление телефонами» (PhoneManagement),
// а не общим Management на станционный конфиг. Политика — ключ androidPolicy в
// том же конфиг-блобе; эндпоинты вклеивают/убирают только его, не трогая
// станционные поля (экран/консоль), и наоборот.
public static class AndroidPolicyEndpoints
{
    public sealed record PolicyRequest(JsonElement Policy);
    public sealed record LockRequest(bool Locked);

    public static void MapAndroidPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("").RequireAuthorization("PhoneManagement");

        g.MapGet("/api/fleet/{id:long}/android-policy", (long id, Db db) =>
            db.HostNameById(id) is null ? Results.NotFound() : Results.Ok(Read(db.GetHostConfig(id))));

        // Этап 11/13: список установленных на устройстве приложений (имя + пакет) —
        // чтобы в редакторе политик выбирать приложения из списка, а не вписывать
        // пакеты руками. Из последнего снимка app_inventory агента.
        g.MapGet("/api/fleet/{id:long}/android-apps", (long id, Db db) =>
        {
            if (db.HostNameById(id) is not { } name) return Results.NotFound();
            return Results.Ok(InstalledApps(db, name));
        });

        g.MapGet("/api/groups/{id:long}/android-policy", (long id, Db db) =>
            Results.Ok(Read(db.GetGroupConfig(id))));

        g.MapPut("/api/fleet/{id:long}/android-policy", (HttpContext ctx, long id, PolicyRequest req, Db db, AuditLog audit) =>
        {
            if (Validate(req.Policy) is { } err) return Results.BadRequest(new { error = err });
            var cur = db.GetHostConfig(id);
            int? version = db.SetHostConfig(id, Merge(cur?.ConfigJson, req.Policy));
            if (version is null) return Results.NotFound();
            audit.Write(ctx, "host.android-policy.set", HostTarget(db, id), new { configVersion = version, policy = req.Policy });
            return Results.Ok(new { configVersion = version });
        });

        g.MapPut("/api/groups/{id:long}/android-policy", (HttpContext ctx, long id, PolicyRequest req, Db db, AuditLog audit) =>
        {
            if (Validate(req.Policy) is { } err) return Results.BadRequest(new { error = err });
            var cur = db.GetGroupConfig(id);
            int? version = db.SetGroupConfig(id, Merge(cur?.ConfigJson, req.Policy));
            if (version is null) return Results.NotFound();
            audit.Write(ctx, "group.android-policy.set", $"group:{id}", new { configVersion = version, policy = req.Policy });
            return Results.Ok(new { configVersion = version });
        });

        // Этап 12: удалённая блокировка устройства (kiosk-заглушка) при подозрении на
        // вмешательство — и её снятие. Переживает ребут; снимается только командой.
        g.MapPost("/api/fleet/{id:long}/android/lock", (HttpContext ctx, long id, LockRequest req, Db db, AuditLog audit) =>
        {
            if (!db.RequestAndroidLock(id, req.Locked)) return Results.NotFound();
            audit.Write(ctx, "host.android-lock", HostTarget(db, id), new { locked = req.Locked });
            return Results.Ok();
        });
    }

    // Список для пикера политики: ВСЕ приложения из последнего снимка app_inventory,
    // включая системные (Chrome, Настройки и т.п. — их тоже можно писать/блокировать;
    // раньше показывали только пользовательские, и системных не было в списке).
    // Пользовательские — сверху, затем системные; внутри — по имени.
    private static List<object> InstalledApps(Db db, string host)
    {
        var parts = db.LatestAndroidInventory(host, DateTime.UtcNow.AddHours(1));
        var apps = new Dictionary<string, (string? Label, string? Ver, bool System)>(StringComparer.Ordinal);
        var seen = new HashSet<int>();
        foreach (var part in parts)
        {
            if (part.Detail is null) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(part.Detail); } catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("part", out var pn) || pn.ValueKind != JsonValueKind.Number || !seen.Add(pn.GetInt32())) continue;
                if (!root.TryGetProperty("packages", out var pkgs) || pkgs.ValueKind != JsonValueKind.Array) continue;
                foreach (var a in pkgs.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object
                        || !a.TryGetProperty("package", out var p) || p.ValueKind != JsonValueKind.String) continue;
                    string name = p.GetString()!;
                    bool system = a.TryGetProperty("system", out var s) && s.ValueKind == JsonValueKind.True;
                    string? label = a.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                    string? ver = a.TryGetProperty("versionName", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    apps[name] = (label, ver, system);   // перекрытие частями безвредно
                }
            }
        }
        return apps
            .OrderBy(a => a.Value.System)                                   // пользовательские сверху
            .ThenBy(a => a.Value.Label ?? a.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => (object)new { package = a.Key, label = a.Value.Label, versionName = a.Value.Ver, system = a.Value.System })
            .ToList();
    }

    private static object Read(ConfigEntry? entry)
    {
        JsonElement? policy = null;
        if (entry is not null && JsonNode.Parse(entry.ConfigJson) is JsonObject o && o["androidPolicy"] is { } p)
            policy = JsonSerializer.Deserialize<JsonElement>(p.ToJsonString());
        return new { configVersion = entry?.ConfigVersion, policy };
    }

    // Проверяем политику через общую схему конфига (androidPolicy + защитный список).
    private static string? Validate(JsonElement policy)
    {
        using var doc = JsonDocument.Parse($"{{\"androidPolicy\":{policy.GetRawText()}}}");
        return AgentConfigValidation.Validate(doc.RootElement);
    }

    // Вклеить androidPolicy в существующий конфиг, не трогая остальные ключи.
    // Пустая политика (нет непустых списков/запретов) убирает ключ — это снятие.
    private static string Merge(string? existingJson, JsonElement policy)
    {
        var obj = (existingJson is not null ? JsonNode.Parse(existingJson) as JsonObject : null) ?? new JsonObject();
        obj.Remove("androidPolicy");
        if (!IsEmpty(policy)) obj["androidPolicy"] = JsonNode.Parse(policy.GetRawText());
        return obj.ToJsonString();
    }

    private static bool IsEmpty(JsonElement policy)
    {
        if (policy.ValueKind != JsonValueKind.Object) return true;
        foreach (var p in policy.EnumerateObject())
        {
            // Непустой список пакетов/запретов — политика не пустая.
            if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0) return false;
            // Этап 12+: включённая запись экрана — тоже содержимое (это объект, не
            // массив; без этой ветки androidPolicy только с screenRecord молча
            // отбрасывался, и запись по политике не доезжала до агента).
            if (p.Name == "screenRecord" && p.Value.ValueKind == JsonValueKind.Object
                && p.Value.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String
                && m.GetString() is { Length: > 0 } mode && mode != "off") return false;
        }
        return true;
    }

    private static string HostTarget(Db db, long id) =>
        db.HostNameById(id) is { } name ? $"host:{name}" : $"host:#{id}";
}
