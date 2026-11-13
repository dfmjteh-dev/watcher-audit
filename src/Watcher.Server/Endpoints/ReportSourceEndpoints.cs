using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Watcher.Server;

public static class ReportSourceEndpoints
{
    public static void MapReportSourceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/reports/source", (HttpContext ctx, Db db, AuditLog audit, string from, string to, string kind = "records", long? groupId = null,
            string? host = null, string? user = null, string? process = null, bool workHours = false, int limit = 50, string? cursor = null) =>
        {
            if (!DateTime.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                || !DateTime.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
                || end < start || (end - start).TotalDays >= ReportEndpoints.MaxDays || start.Year < 1971 || end.Year > 9997
                || kind is not ("records" or "windows") || limit is < 1 or > 100 || cursor?.Length > 64 || host?.Length > 256 || user?.Length > 300 || process?.Length > 300)
                return Results.BadRequest(new { error = "invalid report source filters" });
            if (host is not null && !ViewerScope.CanSeeHost(ctx, db, host)) return Results.Forbid();
            DateTime fromUtc, toUtc;
            try { fromUtc = TimeZoneInfo.ConvertTimeToUtc(start, TimeZoneInfo.Local); toUtc = TimeZoneInfo.ConvertTimeToUtc(end.AddDays(1), TimeZoneInfo.Local); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid local date boundary" }); }
            ReportSourcePage page;
            try { page = db.ReportSource(fromUtc, toUtc, kind == "windows", ViewerScope.AllowedGroupIds(ctx, db), groupId, host, user, process, limit, cursor); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid report cursor" }); }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) { return Results.StatusCode(503); }
            var kinds = AppKinds.Load(db);
            var items = page.Rows.Select(row =>
            {
                var clipped = Clip(row.StartUtc, row.EndUtc, fromUtc, toUtc, workHours);
                bool included = kind != "windows" || kinds.KindOf(row.Process ?? "", false) != AppKind.Ignore;
                return new
                {
                    row.Id,
                    row.SegmentId,
                    row.Host,
                    row.User,
                    row.Process,
                    startUtc = row.StartUtc.ToString("o"),
                    endUtc = row.EndUtc.ToString("o"),
                    row.VideoOffset,
                    intervals = clipped.Select(i => new { startUtc = i.S.ToString("o"), endUtc = i.E.ToString("o"), seconds = (i.E - i.S).TotalSeconds }),
                    seconds = clipped.Sum(i => (i.E - i.S).TotalSeconds),
                    included = included && clipped.Count > 0,
                    excludedReason = !included ? "ignored-application" : clipped.Count == 0 ? "outside-selected-hours" : null
                };
            }).ToList();
            ctx.Response.Headers.CacheControl = "no-store"; audit.Write(ctx, "report.source", host is null ? null : "host:" + host, new { from, to, kind, groupId, user, process, workHours, count = items.Count });
            return Results.Ok(new
            {
                items,
                page.NextCursor,
                timeZone = ServerClock.ZoneId,
                readAt = DateTime.UtcNow.ToString("o"),
                kind,
                identityScope = "host-and-session-user",
                measurement = kind == "windows" ? "recorded-video-foreground-intervals" : "recorded-segments-union-per-host",
                coverage = "unknown",
                pending = db.ReportDirtyCount(from, to, ViewerScope.AllowedGroupIds(ctx, db), groupId, host)
            });
        }).RequireAuthorization("Recordings");
    }
    public static List<(DateTime S, DateTime E)> Clip(DateTime start, DateTime end, DateTime from, DateTime to, bool work)
    {
        start = start > from ? start : from; end = end < to ? end : to; var result = new List<(DateTime, DateTime)>(); if (end <= start) return result;
        if (!work) return [(start, end)];
        var zone = TimeZoneInfo.Local;
        DateTime localStart = TimeZoneInfo.ConvertTimeFromUtc(start, zone).Date, localEnd = TimeZoneInfo.ConvertTimeFromUtc(end, zone).Date;
        for (var day = localStart; day <= localEnd; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            DateTime a = TimeZoneInfo.ConvertTimeToUtc(day.AddHours(9), zone), b = TimeZoneInfo.ConvertTimeToUtc(day.AddHours(18), zone);
            a = a > start ? a : start; b = b < end ? b : end; if (b > a) result.Add((a, b));
        }
        return result;
    }
}
