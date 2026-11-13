using Xunit;

namespace Watcher.Server.Tests;

// 0.13 (проверка RC): удаление выбранных записей хоста (Db.RecordDelete.cs).
public class RecordDeleteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"watcher-del-{Guid.NewGuid():N}");
    private readonly Db _db;

    public RecordDeleteTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new Db(Path.Combine(_dir, "watcher.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static readonly DateTime T0 = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);

    private long Seg(string host, int minute)
    {
        var t = T0.AddMinutes(minute);
        long id = _db.Insert(new Segment(0, host, $"{host}_{minute}.mp4", t, t.AddSeconds(30), 30, 10, 1, "motion", 100, "x",
            $"blobs/{host}/{minute}.mp4", DateTime.UtcNow));
        _db.InsertActivity(id, host, t, [new ActivityEntry(0, "msedge", "Почта — Входящие")]);
        return id;
    }

    private long Term(string host, string kind, int minute, long? parent = null)
    {
        var t = T0.AddMinutes(minute);
        long id = _db.InsertTerminalSession(new TerminalSession(0, host, "root", kind, null, t, t.AddMinutes(1), 60, null,
            kind == "terminal-input" ? "ls" : null, kind == "terminal-output" ? $"blobs/t/{minute}.out" : null, null, 1, null,
            $"{host}:{kind}:{minute}", DateTime.UtcNow), null);
        if (parent is { } p)
        {
            using var c = _db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE terminal_sessions SET parent_id = $p WHERE id = $id";
            cmd.Parameters.AddWithValue("$p", p);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        return id;
    }

    [Fact]
    public void Deletes_only_records_of_this_host_with_files_activity_and_session_output()
    {
        long a = Seg("pc1", 0), b = Seg("pc1", 5), foreign = Seg("pc2", 0);
        long input = Term("pc1", "terminal-input", 0);
        Term("pc1", "terminal-output", 0, parent: input);

        var r = _db.DeleteRecords("pc1", [a, foreign], [input]);
        Assert.Equal(1, r.Segments);
        Assert.Equal(2, r.TerminalSessions);   // ввод + привязанный вывод
        Assert.Equal(0, r.Held);
        Assert.Equal(["blobs/pc1/0.mp4", "blobs/t/0.out"], r.Files.OrderBy(f => f));
        Assert.Equal([b], _db.Query("pc1", null, null, false, 100).Select(s => s.Id));
        Assert.Single(_db.Query("pc2", null, null, false, 100));
        Assert.Empty(_db.ActivityForSegment(a));
        Assert.Empty(_db.QueryTerminal("pc1", null, null, null, null, null, null, 100));
    }

    [Fact]
    public void Records_under_active_legal_hold_are_kept()
    {
        long inHold = Seg("pc1", 10), outside = Seg("pc1", 60);
        long hold = _db.CreateLegalHold("pc1", T0, T0.AddMinutes(30), "суд", "admin");
        var r = _db.DeleteRecords("pc1", [inHold, outside], []);
        Assert.Equal(1, r.Segments);
        Assert.Equal(1, r.Held);
        Assert.Equal([inHold], _db.Query("pc1", null, null, false, 100).Select(s => s.Id));

        _db.ReleaseLegalHold(hold, "admin", "закрыто");
        Assert.Equal(1, _db.DeleteRecords("pc1", [inHold], []).Segments);
    }

    [Fact]
    public void Known_titles_for_config_suggestions()
    {
        Seg("pc1", 0);
        var (_, processes, titles, _) = _db.KnownForHosts(["pc1"]);
        Assert.Contains("msedge", processes);
        Assert.Contains("Почта — Входящие", titles);
    }
}
