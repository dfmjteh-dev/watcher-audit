namespace Watcher.Server;

// R14: «Хосты» и «Обзор» (Этап E2) — для всех ролей, viewer — в своих группах.
public static class InventoryEndpoints
{
    // «Онлайн» на Обзоре (E2) — то же окно, что isOnline() в wwwroot/format.js.
    // Heartbeat по умолчанию раз в минуту (Watcher.Uploader, HeartbeatIntervalSeconds).
    internal static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(5);

    public static void MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        // Этап E2 (roadmap-next.md): раздел «Хосты» — одна таблица для всех ролей
        // (слияние «Видео» и «Парка»). Без RequireAuthorization по роли: operator видит
        // heartbeat и счётчики записей (это здоровье парка, не содержимое — смотреть
        // сами записи ему по-прежнему нельзя, это Recordings на /api/segments*),
        // viewer — то же в рамках своих групп (ViewerScope.AllowedGroupIds).
        app.MapGet("/api/inventory", (HttpContext ctx, Db db, DateTime? since) =>
        {
            var versions = AgentVersionPolicy.Load(db);
            return Results.Ok(db.Inventory(ViewerScope.AllowedGroupIds(ctx, db), since: Since(since)).Select(h => InventoryDto(h, versions)));
        });

        app.MapGet("/api/host-names", (HttpContext ctx, Db db, string? q, int limit = 50) =>
            Results.Ok(db.HostNameSuggestions(q, Math.Clamp(limit, 1, 100), ViewerScope.AllowedGroupIds(ctx, db))));

        app.MapGet("/api/inventory/{name}", (string name, HttpContext ctx, Db db) =>
        {
            // Сначала права, потом существование: иначе ограниченный viewer отличал бы
            // 404 от 403 и мог перебором узнать имена хостов чужих групп.
            if (!ViewerScope.CanSeeHost(ctx, db, name)) return Results.Forbid();
            var host = db.Inventory(null, name).FirstOrDefault();
            if (host is null) return Results.NotFound();
            // «heartbeat-история» вкладки «Инфо» — часы (UTC), когда хост был на связи.
            var presence = db.HostPresenceHours(name, DateTime.UtcNow.Date.AddDays(-6));
            return Results.Ok(new { host = InventoryDto(host, AgentVersionPolicy.Load(db)), presenceHours = presence });
        });

        // 0.13 (п.12): журнал потерь хоста — для всех ролей, viewer — в своих группах
        // (это здоровье парка, не содержимое записей).
        app.MapGet("/api/inventory/{name}/losses", (string name, HttpContext ctx, Db db, bool all = false) =>
        {
            if (!ViewerScope.CanSeeHost(ctx, db, name)) return Results.Forbid();
            return Results.Ok(db.ListLosses(name, unacknowledgedOnly: !all).Select(l => new
            {
                l.Id,
                l.File,
                recordedAt = l.RecordedAt?.ToString("o"),
                l.SizeBytes,
                l.Reason,
                l.Detail,
                reportedAt = l.ReportedAt.ToString("o"),
                acknowledgedAt = l.AcknowledgedAt?.ToString("o"),
                l.AcknowledgedBy,
            }));
        });

        app.MapGet("/api/inventory/{name}/losses/summary", (string name, HttpContext ctx, Db db, bool all = false) =>
        {
            if (!ViewerScope.CanSeeHost(ctx, db, name)) return Results.Forbid();
            return Results.Ok(db.LossGroups(name, unacknowledgedOnly: !all));
        });

        // 0.13 (п.4): кто на хосте попадал в запись и какие программы были на
        // переднем плане за 30 дней — «Инфо».
        app.MapGet("/api/inventory/{name}/people", (string name, HttpContext ctx, Db db) =>
        {
            if (!ViewerScope.CanSeeHost(ctx, db, name)) return Results.Forbid();
            return Results.Ok(new
            {
                users = db.HostUsers(name).Select(u => new
                {
                    u.User,
                    u.Segments,
                    lastVideo = u.LastVideo?.ToString("o"),
                    u.TerminalSessions,
                    lastConsole = u.LastConsole?.ToString("o"),
                }),
                apps = db.HostApps(name, DateTime.UtcNow.AddDays(-30)).Select(a => new { a.Process, seconds = Math.Round(a.Seconds), lastSeen = a.LastSeen.ToString("o") }),
            });
        });

        // «Принято» — admin/operator; красный значок гаснет до следующей потери.
        app.MapPost("/api/inventory/{name}/losses/acknowledge", (string name, HttpContext ctx, Db db, AuditLog audit) =>
        {
            int n = db.AcknowledgeLosses(name, ctx.User.Identity?.Name);
            audit.Write(ctx, "host.losses-acknowledged", $"host:{name}", new { count = n });
            return Results.Ok(new { acknowledged = n });
        }).RequireAuthorization("Management");

        // Обзор (E2) — для всех ролей; счётчики хостов в рамках групп viewer'а.
        // Хранилище — общее на сервер, не скрываем: объём/место — не содержимое записей.
        app.MapGet("/api/transcode/issues", (Db db, int limit = 50, long? cursor = null, string? status = null) =>
        {
            int size = Math.Clamp(limit, 1, 99);
            var items = db.TranscodeIssues(size + 1, cursor, status);
            bool more = items.Count > size;
            if (more) items.RemoveAt(items.Count - 1);
            return Results.Ok(new { items, nextCursor = more ? items[^1].Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null });
        }).RequireAuthorization("AdminOnly");
        app.MapGet("/api/transcode/quarantine/expired", (Db db) => Results.Ok(db.ExpiredTranscodeQuarantine(DateTime.UtcNow)))
            .RequireAuthorization("AdminOnly");
        app.MapPost("/api/transcode/quarantine/{id:long}/cleanup", (long id, QuarantineCleanupRequest req, HttpContext ctx, Db db, ServerConfig cfg) =>
            Results.Ok(db.CleanupTranscodeQuarantine(id, cfg.DataDir, DateTime.UtcNow, ctx.User.Identity?.Name ?? "admin", req.Apply)))
            .RequireAuthorization("AdminOnly");

        app.MapGet("/api/overview", (HttpContext ctx, Db db, ServerConfig cfg, ServerLoad load, TranscodeState transcode, DateTime? since) =>
        {
            var hosts = db.Inventory(ViewerScope.AllowedGroupIds(ctx, db), since: Since(since));
            var versions = AgentVersionPolicy.Load(db);
            DateTime now = DateTime.UtcNow;
            bool Online(InventoryHost h) => h.LastHeartbeatAt is { } t && now - t < OnlineWindow;

            // Нет статистики тома — показываем без неё.
            var drive = RetentionService.DriveSpace(cfg.DataDir);
            long? diskFree = drive?.Free, diskTotal = drive?.Total;
            // R8: та же проверка, что у RetentionService — UI предупреждает, что
            // записи удаляются раньше срока.
            bool diskLow = cfg.MinFreeDiskPercent > 0 && drive is { Total: > 0 } d
                && d.Free * 100.0 / d.Total < cfg.MinFreeDiskPercent;

            long used = hosts.Sum(h => h.SegmentBytes + h.TerminalBytes);
            long avgDaily = db.CachedAvgDailyBytes();
            // Установившийся объём при текущем приросте: столько хранится, пока retention
            // удаляет старое. Без RetentionDays по возрасту ничего не удаляется — прогноза нет.
            long? forecast = cfg.RetentionDays > 0 ? avgDaily * cfg.RetentionDays : null;
            if (forecast is not null && cfg.RetentionMaxBytes > 0) forecast = Math.Min(forecast.Value, cfg.RetentionMaxBytes);

            return Results.Ok(new
            {
                serverVersion = AppVersion.Core(AppVersion.Current),
                serverBuild = AppVersion.Current,
                hosts = new
                {
                    total = hosts.Count,
                    online = hosts.Count(Online),
                    offline = hosts.Count(h => h.LastHeartbeatAt is not null && !Online(h)),
                    neverSeen = hosts.Count(h => h.LastHeartbeatAt is null),
                },
                last24h = new
                {
                    segments = hosts.Sum(h => h.Segments24h),
                    segmentBytes = hosts.Sum(h => h.SegmentBytes24h),
                    terminalSessions = hosts.Sum(h => h.TerminalSessions24h),
                    activeHosts = hosts.Count(h => h.Segments24h + h.TerminalSessions24h > 0),
                },
                // 0.13: агент с журналом потерь — непринятые события, старый — счётчик.
                withDrops = hosts.Where(h => LossCount(h) > 0).Select(h => new { h.Name, DroppedSegments = LossCount(h) }),
                // Агент не дошёл до версии, назначенной его группе (AgentVersionPolicy).
                outdated = hosts.Select(h => (h, st: versions.For(h.Name, h.GroupId, h.AgentVersion)))
                    .Where(x => x.st.Status == "pending")
                    .Select(x => new { x.h.Name, x.h.AgentVersion, targetVersion = x.st.Target }),
                latestAgentVersion = versions.LatestAgentVersion,
                plainHttp = hosts.Where(h => h.LastHeartbeatHttps == false).Select(h => h.Name),
                // Этап 4 Android: агент без доступа к статистике — не видит приложений.
                blindAndroid = hosts.Where(h => h.AndroidUsageAccess == false).Select(h => h.Name),
                // «Требуют внимания» — число хостов (не проблем), тот же набор, что
                // фильтр «с проблемами» в «Хостах» (hostlist.js STATUS_FILTERS.problems).
                problemHosts = hosts.Count(h => HasProblem(h, versions)),
                storage = new
                {
                    usedBytes = used,
                    diskFreeBytes = diskFree,
                    diskTotalBytes = diskTotal,
                    avgDailyBytes = avgDaily,
                    forecastBytes = forecast,
                    retentionDays = cfg.RetentionDays,
                    retentionMaxBytes = cfg.RetentionMaxBytes,
                    minFreeDiskPercent = cfg.MinFreeDiskPercent,
                    diskLow,
                    auditRetentionDays = cfg.AuditRetentionDays,
                },
                // Пережатие записей (Transcode.cs): счётчики из БД + что служба делает сейчас.
                transcode = TranscodeDto(db.CachedTranscodeStats(), transcode, cfg),
                // 0.13: загрузка сервера — последний замер и история за час (ServerLoad).
                server = ServerLoadDto(load),
            });
        });
    }

    internal static object TranscodeDto(TranscodeStats st, TranscodeState state, ServerConfig cfg) => new
    {
        enabled = cfg.Transcode.Enabled,
        state = state.Snapshot(),
        done = st.Done,
        sourceBytes = st.SourceBytes,
        doneBytes = st.DoneBytes,
        savedBytes = st.SourceBytes - st.DoneBytes,
        kept = st.Kept,
        failed = st.Failed,
        retrying = st.Retrying,
        repaired = st.Repaired,
        quarantined = st.Quarantined,
        pending = st.Pending,
        pendingBytes = st.PendingBytes,
    };

    internal static object ServerLoadDto(ServerLoad load)
    {
        var samples = load.Samples();
        var last = samples.LastOrDefault();
        return new
        {
            inContainer = load.InContainer,
            cpuCount = Environment.ProcessorCount,
            intervalSec = (int)ServerLoad.Interval.TotalSeconds,
            at = last?.At.ToString("o"),
            cpuPercent = last?.CpuPercent,
            memTotalBytes = last?.MemTotalBytes,
            memAvailableBytes = last?.MemAvailableBytes,
            netRxBytesPerSec = last?.NetRxBytesPerSec,
            netTxBytesPerSec = last?.NetTxBytesPerSec,
            processCpuPercent = last?.ProcessCpuPercent,
            processMemBytes = last?.ProcessMemBytes,
            // [время, CPU %, занято памяти, приём B/s, отдача B/s] — для спарклайнов.
            history = samples.Select(x => new object?[]
            {
                x.At.ToString("o"), x.CpuPercent,
                x.MemTotalBytes is { } t && x.MemAvailableBytes is { } a ? t - a : null,
                x.NetRxBytesPerSec, x.NetTxBytesPerSec,
            }),
        };
    }

    // «Сегодня» на Обзоре и в «Хостах» — с полуночи смотрящего (браузер присылает
    // since); не дальше недели назад и не в будущем. Без него — последние 24 часа.
    internal static DateTime? Since(DateTime? since)
    {
        if (since is not { } s) return null;
        var utc = s.ToUniversalTime();
        var now = DateTime.UtcNow;
        return utc > now ? now : utc < now.AddDays(-7) ? now.AddDays(-7) : utc;
    }

    // Тот же список, что STATUS_FILTERS.problems в hostlist.js — держать одинаковыми.
    internal static bool HasProblem(InventoryHost h, AgentVersionPolicy versions) =>
        LossCount(h) > 0 || h.ConfigState == "pending" || h.LastHeartbeatHttps == false || h.LegacyAuth == true
        || h.DuplicateCredential || StationClock.IsSignificant(h.ClockSkewSec is { } k ? TimeSpan.FromSeconds(k) : null)
        || versions.IsOutdated(h.Name, h.GroupId, h.AgentVersion)
        // Этап 4: Android-агент без доступа к статистике — «ослеп», приложений не видит.
        || h.AndroidUsageAccess == false;

    internal static int LossCount(InventoryHost h) => h.LossJournal ? h.UnackedLosses : h.DroppedSegments;

    private static object InventoryDto(InventoryHost h, AgentVersionPolicy versions) => new
    {
        h.Id,
        h.Name,
        h.IpAddress,
        h.GroupId,
        h.GroupName,
        lastHeartbeatAt = h.LastHeartbeatAt?.ToString("o"),
        h.AgentVersion,
        // Версии агента и сервера раздельные — сравнение с назначенной группе
        // версией или последним пакетом (AgentVersionPolicy), не с сервером.
        agentOutdated = versions.IsOutdated(h.Name, h.GroupId, h.AgentVersion),
        agentUpdate = versions.For(h.Name, h.GroupId, h.AgentVersion),
        h.DroppedSegments,
        h.LastHeartbeatHttps,
        h.Segments,
        h.SegmentBytes,
        lastSegmentAt = h.LastSegmentAt?.ToString("o"),
        h.Segments24h,
        h.TerminalSessions,
        h.TerminalBytes,
        lastTerminalAt = h.LastTerminalAt?.ToString("o"),
        h.TerminalSessions24h,
        h.LegacyAuth,
        h.DuplicateCredential,
        h.ClockSkewSec,
        h.LastUser,
        h.UnackedLosses,
        h.LossJournal,
        h.ConfigState,
        h.CaptureState,
        resources = h.Resources is { } r ? new
        {
            r.CpuPercent,
            r.MemBytes,
            r.Processes,
            r.QueueBytes,
            r.QueueFiles,
            at = r.At.ToString("o"),
        } : null,
        h.Pause,
        // Этап 2: сотрудник, за которым закреплено устройство (веб-UI).
        h.Person,
        // Этап 4 (android-plan.md): платформа агента и состояние Android-агента.
        h.Platform,
        androidLastEventAt = h.AndroidLastEventAt?.ToString("o"),
        h.AndroidUsageAccess,
        // Этап 5: роль владельца устройства и ожидающая команда её снять.
        h.AndroidOwnerActive,
        h.AndroidClearOwnerRequested,
        // Этап 12: удалённая блокировка устройства.
        h.AndroidLockRequested,
        h.AndroidLocked,
        // Порог тот же, что у исправления времени записей (StationClock).
        clockWrong = StationClock.IsSignificant(h.ClockSkewSec is { } k ? TimeSpan.FromSeconds(k) : null),
    };
}

public sealed record QuarantineCleanupRequest(bool Apply = false);
