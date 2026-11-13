using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

public sealed class ListPagingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "watcher-pages-" + Guid.NewGuid().ToString("N"));
    private readonly Db _db;
    public ListPagingTests() { Directory.CreateDirectory(_dir); _db = new Db(Path.Combine(_dir, "db")); }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }
    [Fact]
    public void Cursor_pages_ties_without_duplicates_and_searches_full_text()
    {
        var at = DateTime.UtcNow;
        for (int i = 0; i < 105; i++) _db.InsertTerminalSession(new TerminalSession(0, "host", "user", "terminal-input", null, at, at, 0, null,
            new string('a', 2000) + "needle-at-end", null, null, 2000, null, "external-" + i, at), new string('a', 2000) + "needle-at-end");
        var all = new List<long>(); string? before = null; long beforeId = 0;
        while (true)
        {
            var page = _db.QueryTerminal(null, null, null, null, null, "needle-at-end", null, 50, sessions: true, beforeAt: before, beforeId: beforeId, compact: true);
            if (page.Count == 0) break;
            Assert.All(page, row => Assert.Equal(769, row.Input!.Length)); all.AddRange(page.Select(r => r.Id));
            var cursor = new PageCursor(page[^1].StartedAt.ToString("o"), page[^1].Id);
            Assert.True(PageCursor.TryParse(cursor.Encode(), out var parsed)); before = parsed!.At; beforeId = parsed.Id;
        }
        Assert.Equal(105, all.Count); Assert.Equal(105, all.Distinct().Count()); Assert.Equal(all.OrderDescending(), all);
        Assert.Equal(2013, _db.TerminalById(all[0])!.Input!.Length);
    }
    [Fact]
    public async Task Catalog_cursor_is_stable_case_insensitive_and_bound_to_query()
    {
        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO segment_activity(segment_id,host,ts,offset_sec,process,title,search) VALUES(1,'host',$ts,0,'Chrome','window','chrome'),(1,'host',$ts,1,'chrome','window','chrome'),(1,'host',$ts,2,'edge','window','edge')";
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o")); cmd.ExecuteNonQuery();
        }
        var cache = new AppCatalogCache(_db);
        var first = (await cache.GetAsync("", 1, null, CancellationToken.None))!;
        Assert.Equal(2, Assert.Single(first.Items).Count); Assert.NotNull(first.NextCursor);
        var second = (await cache.GetAsync("", 1, first.NextCursor, CancellationToken.None))!;
        Assert.Equal("edge", Assert.Single(second.Items).Process); Assert.Null(second.NextCursor);
        Assert.Equal(first.AsOf, second.AsOf);
        Assert.Null(await cache.GetAsync("other", 1, first.NextCursor, CancellationToken.None));
    }
    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("bnVsbA==")]
    public void Invalid_cursor_is_rejected(string cursor) => Assert.False(PageCursor.TryParse(cursor, out _));
}
