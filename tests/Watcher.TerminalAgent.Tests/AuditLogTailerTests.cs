using Watcher.TerminalAgent;
using Xunit;

namespace Watcher.TerminalAgent.Tests;

public class AuditLogTailerTests
{
    private static (string log, string bookmark) NewPaths()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wta-tailer-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return (Path.Combine(dir, "audit.log"), Path.Combine(dir, "bookmark.txt"));
    }

    [Fact]
    public void Missing_log_file_returns_empty()
    {
        var (log, bookmark) = NewPaths();
        var tailer = new AuditLogTailer(log, bookmark);
        Assert.Empty(tailer.ReadNewLines());
    }

    [Fact]
    public void Reads_only_new_lines_since_bookmark_and_advances_it()
    {
        var (log, bookmark) = NewPaths();
        File.WriteAllText(log, "line1\nline2\n");

        var tailer = new AuditLogTailer(log, bookmark);
        var first = tailer.ReadNewLines();
        Assert.Equal(new[] { "line1", "line2" }, first);

        Assert.Empty(tailer.ReadNewLines()); // ничего не дописали — пусто

        File.AppendAllText(log, "line3\n");
        var second = tailer.ReadNewLines();
        Assert.Equal(new[] { "line3" }, second);
    }

    [Fact]
    public void Does_not_return_unterminated_trailing_line_until_it_is_completed()
    {
        var (log, bookmark) = NewPaths();
        File.WriteAllText(log, "complete\nincomplete-so-far");

        var tailer = new AuditLogTailer(log, bookmark);
        Assert.Equal(new[] { "complete" }, tailer.ReadNewLines());
        Assert.Empty(tailer.ReadNewLines()); // хвост без '\n' — ещё не отдаём

        File.AppendAllText(log, "-now-done\n");
        Assert.Equal(new[] { "incomplete-so-far-now-done" }, tailer.ReadNewLines());
    }

    [Fact]
    public void Rotation_shrinking_file_resets_to_start()
    {
        var (log, bookmark) = NewPaths();
        File.WriteAllText(log, "aaaaaaaaaa\n");
        var tailer = new AuditLogTailer(log, bookmark);
        tailer.ReadNewLines();

        File.WriteAllText(log, "new\n"); // "повёрнутый" файл короче bookmark'а
        Assert.Equal(new[] { "new" }, tailer.ReadNewLines());
    }
}
