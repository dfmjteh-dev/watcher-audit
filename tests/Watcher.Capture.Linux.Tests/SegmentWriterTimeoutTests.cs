using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Capture.Linux;
using Xunit;

namespace Watcher.Capture.Linux.Tests;

// Проверяет FfmpegWriteTimeoutMs (тот же приём, что в Watcher.Capture (Windows)/
// SegmentWriterTimeoutTests.cs) без настоящего ffmpeg: SegmentWriter не парсит
// вывод ffmpeg, только пишет кадры в stdin, так что тривиальный процесс, никогда
// не читающий stdin, детерминированно воспроизводит зависший ffmpeg.
public class SegmentWriterTimeoutTests
{
    private static readonly ILogger NoopLogger = NullLogger.Instance;

    private static string BlockingProcessPath() => Path.Combine(AppContext.BaseDirectory, "BlockingProcess");

    private static CaptureConfig MakeConfig(int timeoutMs) => new()
    {
        QueueDir = Path.Combine(Path.GetTempPath(), $"watcher-capture-linux-tests-{Guid.NewGuid():N}"),
        FfmpegPath = BlockingProcessPath(),
        FfmpegWriteTimeoutMs = timeoutMs,
    };

    private static Frame BigFrame() => new(1920, 1080, new byte[1920 * 1080 * 3]);

    [Fact]
    public void WriteFrame_does_not_block_forever_when_ffmpeg_is_stuck()
    {
        // Два разных, одинаково допустимых пути здесь наблюдались на практике:
        // реальный таймаут (~timeoutMs, типично на настоящей Linux-машине — пайп
        // просто блокируется, как на Windows) или быстрый fault самой задачи записи
        // (наблюдалось в одной конкретной sandboxed-среде — ECONNRESET-подобная
        // ошибка раньше, чем успевает истечь таймаут). WriteFrame перехватывает оба
        // (см. комментарий у writeTask.Wait в SegmentWriter.cs) — тест проверяет
        // инвариант, который должен держаться в обоих случаях: не виснет вечно и
        // закрывает сегмент, а не точную раскладку по времени.
        var cfg = MakeConfig(timeoutMs: 1000);
        using var writer = new SegmentWriter(cfg, "test-host", "test-user", NoopLogger);
        var frame = BigFrame();

        writer.Open("test", frame.Width, frame.Height);
        var sw = Stopwatch.StartNew();
        writer.WriteFrame(frame, isMotion: true);
        sw.Stop();

        Assert.InRange(sw.ElapsedMilliseconds, 0, 5000);
        Assert.False(writer.IsOpen);
    }

    [Fact]
    public void Open_after_repeated_timeouts_does_not_leak_ffmpeg_process_handles()
    {
        var cfg = MakeConfig(timeoutMs: 300);
        using var writer = new SegmentWriter(cfg, "test-host", "test-user", NoopLogger);
        var frame = BigFrame();
        var self = Process.GetCurrentProcess();

        const int cycles = 6;
        var handleCounts = new List<int>();
        for (int i = 0; i < cycles; i++)
        {
            // Повторяет Program.cs: Open() на следующем motion-тике без Close()
            // на предыдущем (убитом по таймауту) сегменте.
            writer.Open($"cycle {i}", frame.Width, frame.Height);
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
