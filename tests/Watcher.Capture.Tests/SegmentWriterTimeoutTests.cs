using System.Diagnostics;
using System.Drawing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Capture;
using Xunit;

namespace Watcher.Capture.Tests;

// Verifies the FfmpegWriteTimeoutMs fix (roadmap "Фаза 0 — фундамент") without a real ffmpeg:
// SegmentWriter doesn't parse ffmpeg's output, it only writes BMP frames into its stdin, so a
// trivial process that never reads stdin reproduces a hung ffmpeg deterministically.
public class SegmentWriterTimeoutTests
{
    private static readonly ILogger NoopLogger = NullLogger.Instance;

    private static string BlockingProcessPath()
    {
        string name = "BlockingProcess" + (OperatingSystem.IsWindows() ? ".exe" : "");
        return Path.Combine(AppContext.BaseDirectory, name);
    }

    private static CaptureConfig MakeConfig(int timeoutMs) => new()
    {
        QueueDir = Path.Combine(Path.GetTempPath(), $"watcher-capture-tests-{Guid.NewGuid():N}"),
        FfmpegPath = BlockingProcessPath(),
        FfmpegWriteTimeoutMs = timeoutMs,
    };

    private static Bitmap BigFrame()
    {
        var bmp = new Bitmap(1920, 1080);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Red);
        return bmp;
    }

    [Fact]
    public void WriteFrame_times_out_instead_of_blocking_forever_when_ffmpeg_is_stuck()
    {
        var cfg = MakeConfig(timeoutMs: 1000);
        using var writer = new SegmentWriter(cfg, "test-host", null, NoopLogger);
        using var frame = BigFrame();

        writer.Open("test");
        var sw = Stopwatch.StartNew();
        writer.WriteFrame(frame, isMotion: true);
        sw.Stop();

        Assert.InRange(sw.ElapsedMilliseconds, 800, 5000);
        Assert.False(writer.IsOpen);
    }

    [Fact]
    public void Open_after_repeated_timeouts_does_not_leak_ffmpeg_process_handles()
    {
        var cfg = MakeConfig(timeoutMs: 300);
        using var writer = new SegmentWriter(cfg, "test-host", null, NoopLogger);
        using var frame = BigFrame();
        var self = Process.GetCurrentProcess();

        const int cycles = 6;
        var handleCounts = new List<int>();
        for (int i = 0; i < cycles; i++)
        {
            // Mirrors Program.cs's capture loop: Open() again on the next motion tick
            // without ever calling Close() on the previous (killed) segment.
            writer.Open($"cycle {i}");
            writer.WriteFrame(frame, isMotion: true);
            self.Refresh();
            handleCounts.Add(self.HandleCount);
        }

        int firstHalfMax = handleCounts.Take(cycles / 2).Max();
        int secondHalfMax = handleCounts.Skip(cycles / 2).Max();
        Assert.True(secondHalfMax - firstHalfMax <= 10,
            $"handle count grew from {firstHalfMax} to {secondHalfMax} across repeated Open()-after-Kill() cycles");
    }
}
