using Watcher.SessionService;
using Xunit;

namespace Watcher.SessionService.Tests;

// 0.15.0: решённое «чья сессия» переживает перезапуск службы и не зависит от
// того, остался ли 4688 в журнале к закрытию транскрипта.
public class TranscriptOriginsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"origins-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Survives_restart_and_skips_unknown()
    {
        var o = new TranscriptOrigins(_path);
        o.Set("a.txt", SessionOrigin.Ssh, "10.0.0.5");
        o.Set("b.txt", SessionOrigin.NotSsh, null);
        o.Set("c.txt", SessionOrigin.Unknown, null);
        o.Save();

        var again = new TranscriptOrigins(_path);
        Assert.Equal(new TranscriptOrigins.Entry(SessionOrigin.Ssh, "10.0.0.5"), again.Get("A.TXT"));
        Assert.Equal(SessionOrigin.NotSsh, again.Get("b.txt")!.Origin);
        Assert.Null(again.Get("c.txt"));
    }

    [Fact]
    public void Retain_drops_entries_of_gone_files()
    {
        var o = new TranscriptOrigins(_path);
        o.Set("a.txt", SessionOrigin.Ssh, null);
        o.Set("b.txt", SessionOrigin.NotSsh, null);
        o.Retain(new[] { "b.txt" });
        o.Save();
        var again = new TranscriptOrigins(_path);
        Assert.Null(again.Get("a.txt"));
        Assert.Equal(1, again.Count);
    }

    [Fact]
    public void Broken_file_starts_empty()
    {
        File.WriteAllText(_path, "{not json");
        Assert.Equal(0, new TranscriptOrigins(_path).Count);
    }
}
