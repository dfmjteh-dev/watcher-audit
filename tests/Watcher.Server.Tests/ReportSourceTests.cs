using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Watcher.Server.Tests;

public partial class ReportFactsTests
{
    [Fact]
    public async Task Source_intervals_explain_cached_app_seconds_and_preserve_account_host()
    {
        Segment("pc", "operator", 0, 1, new ActivityEntry(0, "excel", "sheet"), new ActivityEntry(600, "notepad", "notes"));
        Segment("rds", "operator", 0, 1, new ActivityEntry(0, "excel", "sheet"));
        var report = await Report("&process=excel");
        Assert.Equal(2, report.GetProperty("accounts").GetArrayLength());
        Assert.Equal(4200, report.GetProperty("apps")[0].GetProperty("seconds").GetDouble());
        using var response = await _admin.GetAsync($"/api/reports/source?from={Day}&to={Day}&kind=windows&process=excel&limit=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var first = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(600, first.GetProperty("items")[0].GetProperty("seconds").GetDouble());
        string cursor = first.GetProperty("nextCursor").GetString()!;
        var second = await _admin.GetFromJsonAsync<JsonElement>($"/api/reports/source?from={Day}&to={Day}&kind=windows&process=excel&limit=1&cursor={cursor}");
        Assert.Equal(3600, second.GetProperty("items")[0].GetProperty("seconds").GetDouble());
        Assert.NotEqual(first.GetProperty("items")[0].GetProperty("id").GetInt64(), second.GetProperty("items")[0].GetProperty("id").GetInt64());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        Assert.Equal("unknown", second.GetProperty("coverage").GetString());
    }
    [Fact]
    public async Task Overlap_is_kept_as_source_but_union_is_used_in_host_summary()
    {
        Segment("pc", "operator", 0, 1); Segment("pc", "operator", 0.5, 1.5);
        var report = await Report(); Assert.Equal(5400, report.GetProperty("totalActiveSec").GetDouble());
        var source = await _admin.GetFromJsonAsync<JsonElement>($"/api/reports/source?from={Day}&to={Day}&kind=records&host=pc");
        Assert.Equal(7200, source.GetProperty("items").EnumerateArray().Sum(x => x.GetProperty("seconds").GetDouble()));
        Assert.Equal(2, source.GetProperty("items").GetArrayLength());
    }
    [Theory]
    [InlineData("&cursor=invalid")]
    [InlineData("&cursor=9999999999999999999.1")]
    [InlineData("&limit=101")]
    [InlineData("&kind=secret")]
    public async Task Invalid_cursor_or_bounds_are_bad_request(string extra)
    {
        using var response = await _admin.GetAsync($"/api/reports/source?from={Day}&to={Day}{extra}"); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    [Fact]
    public void Source_scope_and_pending_count_do_not_include_other_groups_or_hosts()
    {
        long visible = Segment("pc", "operator", 0, 1), hidden = Segment("rds", "operator", 0, 1);
        using (var c = _db.Open()) using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO host_groups(id,name) VALUES(901,'allowed'),(902,'hidden'); INSERT INTO hosts(name,group_id) VALUES('pc',901),('rds',902); INSERT OR IGNORE INTO report_dirty(host,day) VALUES('pc',$day),('rds',$day)";
            cmd.Parameters.AddWithValue("$day", Day); cmd.ExecuteNonQuery();
        }
        DateTime start = DateTime.SpecifyKind(_noon.Date.AddDays(-1), DateTimeKind.Utc), end = _noon.Date.AddDays(2);
        var source = _db.ReportSource(start, end, false, [901], null, null, null, null, 50, null);
        Assert.Equal(visible, Assert.Single(source.Rows).SegmentId);
        Assert.Empty(_db.ReportSource(start, end, false, [], null, null, null, null, 50, null).Rows);
        Assert.Equal(1, _db.ReportDirtyCount(Day, Day, [901], null, null));
        Assert.Equal(0, _db.ReportDirtyCount(Day, Day, [], null, null));
        Assert.Equal(0, _db.ReportDirtyCount(Day, Day, [901], 902, null));
    }
    [Fact]
    public void Work_hours_clip_in_utc_preserves_period_boundaries_and_excludes_weekend()
    {
        var local = new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Unspecified);
        DateTime start = TimeZoneInfo.ConvertTimeToUtc(local), end = TimeZoneInfo.ConvertTimeToUtc(local.AddDays(1).AddHours(11));
        var clipped = ReportSourceEndpoints.Clip(start, end, start, end, true);
        Assert.Equal(2, clipped.Count); Assert.Equal(18 * 3600, clipped.Sum(x => (x.E - x.S).TotalSeconds));
        DateTime saturday = TimeZoneInfo.ConvertTimeToUtc(local.AddDays(5));
        Assert.Empty(ReportSourceEndpoints.Clip(saturday, saturday.AddHours(10), saturday, saturday.AddHours(10), true));
        Assert.Empty(ReportSourceEndpoints.Clip(start, end, end, end.AddDays(1), false));
    }
    [Fact]
    public async Task Ignored_applications_have_an_explicit_exclusion_reason()
    {
        Segment("pc", "operator", 0, 1, new ActivityEntry(0, "excel", "sheet"));
        using var changed = await _admin.PutAsJsonAsync("/api/settings/app-kinds", new { overrides = new Dictionary<string, string> { { "excel", "ignore" } } });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var source = await _admin.GetFromJsonAsync<JsonElement>($"/api/reports/source?from={Day}&to={Day}&kind=windows&process=excel");
        var row = Assert.Single(source.GetProperty("items").EnumerateArray());
        Assert.False(row.GetProperty("included").GetBoolean()); Assert.Equal("ignored-application", row.GetProperty("excludedReason").GetString());
    }
    [Fact]
    public void Source_page_is_bounded_with_one_hundred_thousand_activity_intervals()
    {
        using (var c = _db.Open()) using (var command = c.CreateCommand())
        {
            command.CommandText = @"WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<5000)
INSERT INTO segments(host,filename,started_at,ended_at,duration_sec,frames,motion_events,start_reason,size_bytes,sha256,file_path,uploaded_at,session_user)
SELECT 'load-'||(x%100),'load-'||x,strftime('%Y-%m-%dT%H:%M:%f',$base,'+'||x||' seconds')||'0000Z',strftime('%Y-%m-%dT%H:%M:%f',$base,'+'||(x+120)||' seconds')||'0000Z',120,120,1,'motion',100,'test','test.mp4',$base,'operator' FROM n;
WITH RECURSIVE offsets(x) AS (SELECT 0 UNION ALL SELECT x+1 FROM offsets WHERE x<19)
INSERT INTO segment_activity(segment_id,host,ts,offset_sec,process,title,search)
SELECT s.id,s.host,strftime('%Y-%m-%dT%H:%M:%f',s.started_at,'+'||(o.x*6)||' seconds')||'0000Z',o.x*6,'excel','','' FROM segments s CROSS JOIN offsets o WHERE s.filename LIKE 'load-%'";
            command.Parameters.AddWithValue("$base", _noon.ToString("o")); command.ExecuteNonQuery();
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var first = _db.ReportSource(_noon.AddHours(-1), _noon.AddHours(3), true, null, null, null, null, "excel", 50, null);
        var second = _db.ReportSource(_noon.AddHours(-1), _noon.AddHours(3), true, null, null, null, null, "excel", 50, first.NextCursor);
        Assert.Equal(50, first.Rows.Count); Assert.Equal(50, second.Rows.Count); Assert.NotNull(second.NextCursor);
        Assert.Empty(first.Rows.Select(x => x.Id).Intersect(second.Rows.Select(x => x.Id)));
        Assert.All(first.Rows, x => Assert.Equal(6, (x.EndUtc - x.StartUtc).TotalSeconds));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Indexed source pagination exceeded ten seconds on 100000 intervals");
    }

}
