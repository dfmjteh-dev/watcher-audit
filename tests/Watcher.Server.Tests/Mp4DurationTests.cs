using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

// Длительность из moov/mvhd вместо ffprobe (пережатие, Transcode.cs). Сверено с
// ffprobe на 120 сегментах прода и их AV1-версиях — до миллисекунды.
public class Mp4DurationTests
{
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string self = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", ".."));

    [Fact]
    public void Reads_duration_of_real_mp4()
    {
        double? d = Mp4Duration.Read(Path.Combine(Repo(), "tests", "e2e", "media", "clip-3s.mp4"));
        Assert.NotNull(d);
        Assert.InRange(d!.Value, 2.9, 3.1);
    }

    [Fact]
    public void Not_mp4_or_truncated_gives_null_for_ffprobe_fallback()
    {
        Assert.Null(Mp4Duration.Read(new MemoryStream(new byte[100])));
        Assert.Null(Mp4Duration.Read(new MemoryStream([0, 0, 0, 8, (byte)'f', (byte)'t', (byte)'y', (byte)'p'])));
        var clip = File.ReadAllBytes(Path.Combine(Repo(), "tests", "e2e", "media", "clip-3s.mp4"));
        Assert.Null(Mp4Duration.Read(new MemoryStream(clip[..40])));
    }
}
