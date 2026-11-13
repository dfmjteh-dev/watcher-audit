using Xunit;

namespace Watcher.Capture.Linux.Tests;

// 0.13: «Снять с записи» — pauseVideo/pauseConsole + pauseUntil (включительно).
public class RecordingPauseTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 15, 0, 0);

    [Fact]
    public void Pause_until_date_inclusive()
    {
        Assert.False(RecordingPause.IsActive(null, null, Now));
        Assert.False(RecordingPause.IsActive(false, "2030-01-01", Now));
        Assert.True(RecordingPause.IsActive(true, null, Now));
        Assert.True(RecordingPause.IsActive(true, "2026-10-14", Now));
        Assert.False(RecordingPause.IsActive(true, "2026-10-13", Now));
        Assert.True(RecordingPause.IsActive(true, "не дата", Now));   // битая дата — пауза держится
    }
}
