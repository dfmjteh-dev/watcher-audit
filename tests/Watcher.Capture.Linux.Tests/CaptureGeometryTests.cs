using Xunit;

namespace Watcher.Capture.Linux.Tests;

// 0.13 (п.2): несколько мониторов и разные разрешения — без растягивания.
public class CaptureGeometryTests
{
    private static MonitorRect M(int x, int y, int w, int h, bool primary = false) => new(x, y, w, h, primary);

    [Fact]
    public void Two_fullhd_side_by_side_keep_proportions()
    {
        var l = CaptureGeometry.Layout(0, 0, 3840, 1080, [M(0, 0, 1920, 1080, true), M(1920, 0, 1920, 1080)], 1920, 1080);
        Assert.Equal((3840, 1080), (l.Width, l.Height));   // раньше — 1920×1080, сплющено вдвое
        Assert.Equal([M(0, 0, 1920, 1080, true), M(1920, 0, 1920, 1080)], l.Monitors);
    }

    [Fact]
    public void Monitor_left_of_primary_with_negative_origin_and_different_sizes()
    {
        // Windows: второй монитор 1280×1024 слева от основного 1920×1080 — виртуальный экран с x = -1280.
        var l = CaptureGeometry.Layout(-1280, 0, 3200, 1080, [M(0, 0, 1920, 1080, true), M(-1280, 0, 1280, 1024)], 960, 540);
        Assert.Equal((1600, 540), (l.Width, l.Height));
        Assert.Equal(M(640, 0, 960, 540, true), l.Monitors[0]);   // основной — первым
        Assert.Equal(M(0, 0, 640, 512), l.Monitors[1]);
    }

    [Fact]
    public void Small_screen_is_not_upscaled_and_box_shrinks_big_one()
    {
        var sxga = CaptureGeometry.Layout(0, 0, 1280, 1024, [], 1920, 1080);
        Assert.Equal((1280, 1024), (sxga.Width, sxga.Height));   // раньше растягивался в 16:9
        var small = CaptureGeometry.Layout(0, 0, 1920, 1080, [M(0, 0, 1920, 1080, true)], 640, 480);
        Assert.Equal((640, 360), (small.Width, small.Height));
    }

    [Fact]
    public void Huge_desktop_is_capped_and_sizes_are_even()
    {
        var l = CaptureGeometry.Layout(0, 0, 11520, 2160, [M(0, 0, 3840, 2160, true), M(3840, 0, 3840, 2160), M(7680, 0, 3840, 2160)], 3840, 2160);
        Assert.True(l.Width <= CaptureGeometry.MaxWidth && l.Height <= CaptureGeometry.MaxHeight);
        Assert.Equal(0, l.Width % 2);
        Assert.Equal(0, l.Height % 2);
        var odd = CaptureGeometry.Layout(0, 0, 1366, 767, [], 1920, 1080);
        Assert.Equal((1366, 766), (odd.Width, odd.Height));
    }
}
