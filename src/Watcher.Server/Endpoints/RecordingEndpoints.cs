using System.Text.Json;

namespace Watcher.Server;

// R14: просмотр записей — видео и консоль. Политика Recordings на группах;
// ограничение viewer'а группами хостов — внутри хендлеров (ViewerScope).
public static class RecordingEndpoints
{
    public static void MapRecordingEndpoints(this IEndpointRouteBuilder app)
    {
        var segments = app.MapGroup("/api/segments").RequireAuthorization("Recordings");

        segments.MapGet("", (HttpContext ctx, Db db, string? host, DateTime? from, DateTime? to, bool motionOnly = false, int limit = 200, string? sessionUser = null, int offset = 0) =>
        {
            var rows = db.Query(host, from?.ToUniversalTime(), to?.ToUniversalTime(), motionOnly, Math.Clamp(limit, 1, 1000), sessionUser, ViewerScope.AllowedGroupIds(ctx, db), Math.Max(offset, 0));
            return Results.Ok(rows.Select(s => new
            {
                s.Id,
                s.Host,
                s.Filename,
                startedAt = s.StartedAt.ToString("o"),
                endedAt = s.EndedAt.ToString("o"),
                s.DurationSec,
                s.Frames,
                s.MotionEvents,
                s.StartReason,
                s.SizeBytes,
                s.SessionUser,
                stationStartedAt = s.StationStartedAt?.ToString("o"),
                monitors = s.MonitorsJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(s.MonitorsJson),
                // Версия файла для URL видео (?v=): сервер пережимает записи
                // (Transcode.cs) и подменяет файл — Range-запросы одного
                // просмотра не должны попасть в другой файл.
                v = VideoVersion(s.Sha256),
            }));
        });

        segments.MapGet("/{id:long}/meta", (long id, HttpContext ctx, Db db) =>
        {
            var s = db.ById(id);
            if (s is null) return Results.NotFound();
            return ViewerScope.CanSeeHost(ctx, db, s.Host) ? Results.Ok(s) : Results.Forbid();
        });

        segments.MapGet("/{id:long}/video", (long id, HttpContext ctx, Db db, ServerConfig cfg, AuditLog audit, string? v) =>
        {
            var s = db.ById(id);
            if (s is null) return Results.NotFound();
            var details = new { host = s.Host, startedAt = s.StartedAt.ToString("o"), sessionUser = s.SessionUser };
            if (!ViewerScope.CanSeeHost(ctx, db, s.Host))
            {
                // Попытка открыть чужую группу — тоже в журнал (без дедупа: это не
                // Range-запросы одного просмотра, а отказ на каждом).
                audit.Write(ctx, "segment.video", $"segment:{id}", new { details.host, details.startedAt, details.sessionUser, denied = true });
                return Results.Forbid();
            }
            // Файл подменён (пережат), а плеер открыл старую версию: не отдаём
            // ему куски другого файла — 410, плеер переоткроет новую с той же секунды.
            if (!string.IsNullOrEmpty(v) && !string.Equals(v, VideoVersion(s.Sha256), StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status410Gone);
            string full = Path.GetFullPath(Path.Combine(cfg.DataDir, s.FilePath));
            if (!File.Exists(full)) return Results.NotFound();
            audit.WriteVideoView(ctx, id, details);
            return Results.File(full, "video/mp4", enableRangeProcessing: true);
        });

        // H9: смены активного окна в сегменте — подписи в плеере. Без отдельной
        // записи в журнал: плеер запрашивает их вместе с видео, а просмотр видео
        // уже пишется (segment.video).
        segments.MapGet("/{id:long}/activity", (long id, HttpContext ctx, Db db) =>
        {
            var s = db.ById(id);
            if (s is null) return Results.NotFound();
            if (!ViewerScope.CanSeeHost(ctx, db, s.Host)) return Results.Forbid();
            return Results.Ok(db.ActivityForSegment(id).Select(ActivityDto));
        });

        // H9: поиск «когда открывали X» по процессу и заголовку окна. Заголовки —
        // содержимое экрана (темы писем, имена файлов), поэтому каждый поиск — в
        // журнал, как список консоли (terminal.query).
        app.MapGet("/api/activity", (HttpContext ctx, Db db, AuditLog audit, string? host, DateTime? from, DateTime? to, string? q, string? sessionUser, int limit = 200, int offset = 0) =>
        {
            limit = Math.Clamp(limit, 1, 1000);
            offset = Math.Max(offset, 0);
            var rows = db.SearchActivity(host, from?.ToUniversalTime(), to?.ToUniversalTime(), q, sessionUser,
                ViewerScope.AllowedGroupIds(ctx, db), limit, offset);
            audit.Write(ctx, "activity.search", null, new
            {
                q = AuditLog.Clip(q),
                host,
                sessionUser,
                from = from?.ToUniversalTime().ToString("o"),
                to = to?.ToUniversalTime().ToString("o"),
                results = rows.Count,
            });
            return Results.Ok(rows.Select(ActivityDto));
        }).RequireAuthorization("Recordings");

        // 0.13 (проверка RC): удалить выбранные записи хоста (видео и консоль) —
        // только admin, в журнал доступа; под действующим legal hold — не удаляются.
        app.MapPost("/api/records/delete", (HttpContext ctx, DeleteRecordsRequest req, Db db, ServerConfig cfg, AuditLog audit) =>
        {
            var segs = (req.SegmentIds ?? []).Distinct().ToList();
            var terms = (req.TerminalIds ?? []).Distinct().ToList();
            if (string.IsNullOrWhiteSpace(req.Host) || segs.Count + terms.Count == 0)
                return Results.BadRequest(new { error = "host and segmentIds/terminalIds required" });
            if (segs.Count + terms.Count > 1000) return Results.BadRequest(new { error = "too many records" });
            var r = db.DeleteRecords(req.Host, segs, terms);
            int failed = FleetEndpoints.DeleteRecordingFiles(cfg.DataDir, r.Files);
            audit.Write(ctx, "records.delete", $"host:{req.Host}", new
            {
                segments = r.Segments,
                terminalSessions = r.TerminalSessions,
                held = r.Held,
                filesNotDeleted = failed,
                segmentIds = segs,
                terminalIds = terms,
            });
            return Results.Ok(new { r.Segments, r.TerminalSessions, r.Held, filesNotDeleted = failed });
        }).RequireAuthorization("AdminOnly");

        var terminal = app.MapGroup("/api/terminal-sessions").RequireAuthorization("Recordings");

        // sessions=true (0.12, список «Консоль»): одна строка на сессию — вывод,
        // связанный с вводом той же сессии, приходит внутри строки ввода (outputs).
        terminal.MapGet("", (HttpContext ctx, Db db, AuditLog audit, string? host, string? user, string? kind, DateTime? from, DateTime? to, string? search, long? groupId, string? os, string? service, string? type, string? origin, bool sessions = false, int limit = 200, bool page = false, string? cursor = null) =>
        {
            // 0.13: service=hide — без служебных подключений, only — только они, иначе все.
            bool? svc = service switch { "hide" => false, "only" => true, _ => null };
            PageCursor? before = null;
            if (cursor is not null && !PageCursor.TryParse(cursor, out before)) return Results.BadRequest(new { error = "invalid cursor" });
            int size = Math.Clamp(limit, 1, page ? 100 : 1000);
            var rows = db.QueryTerminal(host, user, kind, from?.ToUniversalTime(), to?.ToUniversalTime(), search, groupId, size + (page ? 1 : 0), ViewerScope.AllowedGroupIds(ctx, db), os, sessions, svc, type, before?.At, before?.Id ?? 0, compact: page, origin: origin);
            bool more = page && rows.Count > size;
            if (more) rows.RemoveAt(rows.Count - 1);
            var children = sessions
                ? db.TerminalChildren(rows.Where(r => r.Kind == "terminal-input").Select(r => r.Id).ToList(), compact: page).ToLookup(c => c.ParentId!.Value)
                : null;
            // Этап C3: пишется каждый запрос списка, не только с search — колонка input
            // (набранные в терминале команды) отдаётся прямо в этом ответе, то есть
            // список консоли сам по себе уже просмотр содержимого, а не только метаданных
            // как /api/segments. Фильтры — чтобы было видно, ЧЬЮ консоль смотрели.
            audit.Write(ctx, "terminal.query", null, new
            {
                search = AuditLog.Clip(search),
                host,
                user,
                kind,
                groupId,
                os,
                service,
                type,
                from = from?.ToUniversalTime().ToString("o"),
                to = to?.ToUniversalTime().ToString("o"),
                results = rows.Count,
            });
            var items = rows.Select(r => children is null ? TerminalRowDto(r, page) : SessionRowDto(r, children[r.Id].ToList(), page));
            if (!page) return Results.Ok(items);
            string? next = more && rows.Count > 0 ? new PageCursor(rows[^1].StartedAt.ToString("o"), rows[^1].Id).Encode() : null;
            return Results.Ok(new { items, nextCursor = next });
        });

        terminal.MapGet("/{id:long}/out", (long id, HttpContext ctx, Db db, ServerConfig cfg, AuditLog audit) =>
        {
            var s = db.TerminalById(id);
            if (s?.OutFilePath is null) return Results.NotFound();
            if (!ViewerScope.CanSeeHost(ctx, db, s.Host))
            {
                audit.Write(ctx, "terminal.out", $"terminal:{id}", new { host = s.Host, sessionUser = s.SessionUser, denied = true });
                return Results.Forbid();
            }
            string full = Path.GetFullPath(Path.Combine(cfg.DataDir, s.OutFilePath));
            if (!File.Exists(full)) return Results.NotFound();
            audit.Write(ctx, "terminal.out", $"terminal:{id}", new { host = s.Host, sessionUser = s.SessionUser });
            return Results.File(full, "text/plain", Path.GetFileName(full));
        });

        terminal.MapGet("/{id:long}/timing", (long id, HttpContext ctx, Db db, ServerConfig cfg, AuditLog audit) =>
        {
            var s = db.TerminalById(id);
            if (s?.TimingFilePath is null) return Results.NotFound();
            if (!ViewerScope.CanSeeHost(ctx, db, s.Host))
            {
                audit.Write(ctx, "terminal.timing", $"terminal:{id}", new { host = s.Host, sessionUser = s.SessionUser, denied = true });
                return Results.Forbid();
            }
            string full = Path.GetFullPath(Path.Combine(cfg.DataDir, s.TimingFilePath));
            if (!File.Exists(full)) return Results.NotFound();
            audit.Write(ctx, "terminal.timing", $"terminal:{id}", new { host = s.Host, sessionUser = s.SessionUser });
            return Results.File(full, "text/plain", Path.GetFileName(full));
        });

        // Задача E4 (roadmap-next.md): одна сессия для плеера консоли (глубокая ссылка
        // #/console?s=<id>) + записи другого kind той же консольной сессии, чтобы
        // показать ввод и вывод рядом. Общего id у них нет (см. Db.TerminalOverlapping)
        // — ищем по host+user и пересечению по времени. Отдаёт input (набранные
        // команды), то есть это просмотр содержимого — пишется в журнал, как и список.
        terminal.MapGet("/{id:long}", (long id, HttpContext ctx, Db db, AuditLog audit) =>
        {
            var s = db.TerminalById(id);
            if (s is null) return Results.NotFound();
            if (!ViewerScope.CanSeeHost(ctx, db, s.Host))
            {
                audit.Write(ctx, "terminal.session", $"terminal:{id}", new { host = s.Host, sessionUser = s.SessionUser, denied = true });
                return Results.Forbid();
            }
            // 0.12: связь по audit-сессии (parent_id); записи до 0.12 её не имеют —
            // для них прежний поиск по пересечению времени.
            List<TerminalSession> related;
            if (s.ParentId is { } pid && db.TerminalById(pid) is { } parent)
                related = [parent, .. db.TerminalChildren([pid]).Where(c => c.Id != s.Id)];
            else if (s.Kind == "terminal-input" && db.TerminalChildren([s.Id]) is { Count: > 0 } kids)
                related = kids;
            else
                related = db.TerminalOverlapping(s.Host, s.SessionUser, s.Kind == "terminal-input" ? "terminal-output" : "terminal-input",
                    s.StartedAt, s.EndedAt, TimeSpan.FromSeconds(5), 20);
            audit.Write(ctx, "terminal.session", $"terminal:{id}", new { host = s.Host, sessionUser = s.SessionUser, related = related.Count });
            return Results.Ok(new { session = TerminalRowDto(s), related = related.Select(x => TerminalRowDto(x)) });
        });

        // 0.13 (п.7): «Скрыть похожие» — правило служебного подключения по этой
        // сессии дописывается в конфиг группы хоста (scope=group) или самого хоста,
        // пометки консоли хостов пересчитываются сразу. Смотрит записи и правит
        // конфиг — нужны обе роли (на деле admin).
        terminal.MapPost("/{id:long}/service-rule", (long id, HttpContext ctx, ServiceRuleRequest req, Db db, AuditLog audit) =>
        {
            if (db.TerminalById(id) is not { } s) return Results.NotFound();
            if (db.Inventory(onlyName: s.Host).FirstOrDefault() is not { Id: { } hostId } h) return Results.NotFound();
            var rule = new System.Text.Json.Nodes.JsonObject();
            foreach (var (k, v) in new[] { ("user", req.User), ("from", req.From), ("command", req.Command), ("action", req.Action ?? "hide"), ("note", req.Note) })
                if (!string.IsNullOrWhiteSpace(v)) rule[k] = v.Trim();

            bool toGroup = req.Scope != "host";
            if (toGroup && h.GroupId is null)
                return Results.BadRequest(new { error = "host has no group", message = "Хост без группы — правило можно добавить только хосту." });
            string? ownJson = db.GetHostConfig(hostId)?.ConfigJson;
            if (toGroup && ServiceConnections.HasOwnRules(ownJson))
                return Results.Conflict(new { error = "host has own rules", message = "У хоста свой список служебных подключений, правила группы на него не действуют — добавьте правило хосту." });
            string? targetJson = toGroup ? db.GetGroupConfig(h.GroupId!.Value)?.ConfigJson : ownJson;
            var config = ServiceConnections.WithRule(targetJson, rule);
            if (AgentConfigValidation.Validate(JsonSerializer.Deserialize<JsonElement>(config)) is { } err)
                return Results.BadRequest(new { error = err });
            int? version = toGroup ? db.SetGroupConfig(h.GroupId!.Value, config) : db.SetHostConfig(hostId, config);
            if (version is null) return Results.NotFound();
            var configEl = JsonSerializer.Deserialize<JsonElement>(config);
            if (toGroup) audit.Write(ctx, "group.config.set", $"group:{h.GroupId}", new { configVersion = version, config = configEl, fromTerminal = id });
            else audit.Write(ctx, "host.config.set", $"host:{s.Host}", new { configVersion = version, config = configEl, fromTerminal = id });
            int marked = ServiceConnections.Reclassify(db, toGroup ? ServiceConnections.HostsOfGroup(db, h.GroupId!.Value) : [s.Host]);
            return Results.Ok(new { scope = toGroup ? "group" : "host", group = h.GroupName, configVersion = version, marked });
        }).RequireAuthorization("Management");
    }

    private static object ActivityDto(ActivityRow a) => new
    {
        a.SegmentId,
        a.Host,
        a.SessionUser,
        ts = a.Ts.ToString("o"),
        a.OffsetSec,
        a.Process,
        a.Title,
        a.Site,
    };

    // Строка консольной сессии для UI — общая для списка и /related (E4).
    private static object TerminalRowDto(TerminalSession s, bool compact = false) => new
    {
        s.Id,
        s.Host,
        s.SessionUser,
        s.Kind,
        s.RemoteAddr,
        startedAt = s.StartedAt.ToString("o"),
        endedAt = s.EndedAt.ToString("o"),
        s.DurationSec,
        s.ExitCode,
        input = compact && s.Input is { Length: > 768 } ? s.Input[..768] : s.Input,
        inputTruncated = compact && s.Input is { Length: > 768 },
        hasOutput = s.OutFilePath is not null,
        // Задача F2: вывод без .timing (транскрипт PowerShell) — текст, не проигрыватель.
        hasTiming = s.TimingFilePath is not null,
        s.Source,
        s.Shell,
        s.SizeBytes,
        stationStartedAt = s.StationStartedAt?.ToString("o"),
        s.Live,
        s.ActiveSec,
        lastActivityAt = s.LastActivityAt?.ToString("o"),
        s.ParentId,
        s.Service,
        s.Cols,
        s.Rows,
        s.Origin,
        s.Mode,
        inputDecoded = compact && s.InputDecoded is { Length: > 768 } ? s.InputDecoded[..768] : s.InputDecoded,
    };

    // Строка списка в режиме «одна строка на сессию»: запись ввода (или вывод
    // без ввода) + связанный вывод. Сессия идёт, пока идёт хоть одна её часть;
    // активное время и последняя активность — по выводу, если он есть.
    private static object SessionRowDto(TerminalSession s, List<TerminalSession> outputs, bool compact = false)
    {
        var all = outputs.Prepend(s).ToList();
        var activity = all.Where(x => x.LastActivityAt is not null).Select(x => x.LastActivityAt!.Value).DefaultIfEmpty().Max();
        double? active = all.Any(x => x.ActiveSec is not null) ? all.Sum(x => x.ActiveSec ?? 0) : null;
        return new
        {
            row = TerminalRowDto(s, compact),
            outputs = outputs.Select(o => TerminalRowDto(o, compact)),
            live = all.Any(x => x.Live),
            activeSec = active,
            lastActivityAt = activity == default ? null : activity.ToString("o"),
            endedAt = all.Max(x => x.EndedAt).ToString("o"),
        };
    }

    public static string VideoVersion(string sha256) => sha256.Length > 12 ? sha256[..12] : sha256;
}
