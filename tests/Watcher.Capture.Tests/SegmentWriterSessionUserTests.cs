using Watcher.Capture;
using Xunit;

namespace Watcher.Capture.Tests;

// Фаза 6 (roadmap.md): sessionUser меняет имя файла сегмента и попадает в sidecar
// метаданные. Полный Open()/Close() через ffmpeg не нужен для этой логики — она
// живёт в чистых статических хелперах (см. SegmentWriter.BuildBaseName/
// SanitizeForFilename), тот же приём тестируемости без реального ffmpeg, что уже
// применён для таймаутов (SegmentWriterTimeoutTests, BlockingProcess).
public class SegmentWriterSessionUserTests
{
    [Fact]
    public void BuildBaseName_without_sessionUser_is_unchanged()
    {
        Assert.Equal("host1", SegmentWriter.BuildBaseName("host1", null));
        Assert.Equal("host1", SegmentWriter.BuildBaseName("host1", ""));
        Assert.Equal("host1", SegmentWriter.BuildBaseName("host1", "   "));
    }

    [Fact]
    public void BuildBaseName_with_sessionUser_appends_it()
    {
        Assert.Equal("host1_alice", SegmentWriter.BuildBaseName("host1", "alice"));
    }

    [Fact]
    public void SanitizeForFilename_strips_unsafe_characters()
    {
        Assert.Equal("alice", SegmentWriter.SanitizeForFilename("alice"));
        Assert.Equal("alice-2", SegmentWriter.SanitizeForFilename("al ice-2"));
        Assert.Equal("useradmin", SegmentWriter.SanitizeForFilename("user\\admin"));
        Assert.Equal("etc", SegmentWriter.SanitizeForFilename("../../etc"));
        Assert.Equal("unknown", SegmentWriter.SanitizeForFilename("///"));
    }

    [Fact]
    public void BuildBaseName_sanitizes_sessionUser_before_appending()
    {
        Assert.Equal("host1_DOMAINuser", SegmentWriter.BuildBaseName("host1", "DOMAIN\\user"));
    }
}
