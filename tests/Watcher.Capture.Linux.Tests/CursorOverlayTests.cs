using Watcher.Capture.Linux;
using Xunit;

namespace Watcher.Capture.Linux.Tests;

// 0.14.3: курсор на кадре — смешивание premultiplied ARGB (как отдаёт XFixes).
public class CursorOverlayTests
{
    private static Frame Gray(int w, int h) => new(w, h, Enumerable.Repeat((byte)100, w * h * 3).ToArray());
    private static (byte R, byte G, byte B) At(Frame f, int x, int y) { int o = (y * f.Width + x) * 3; return (f.Pixels[o], f.Pixels[o + 1], f.Pixels[o + 2]); }

    [Fact]
    public void Opaque_replaces_transparent_keeps_half_mixes()
    {
        var f = Gray(4, 1);
        uint[] cur = [0xFFFF0000, 0x00000000, 0x80800000];   // красный; прозрачный; красный 50% (premultiplied)
        CursorOverlay.Blend(f, cur, 3, 1, 0, 0, 1);
        Assert.Equal((255, 0, 0), At(f, 0, 0));
        Assert.Equal((100, 100, 100), At(f, 1, 0));
        Assert.Equal((177, 49, 49), At(f, 2, 0));   // 128 + ⌊100·127/255⌋
        Assert.Equal((100, 100, 100), At(f, 3, 0));
    }

    [Fact]
    public void Clipped_at_frame_edges_and_scaled()
    {
        var f = Gray(3, 3);
        CursorOverlay.Blend(f, [0xFF00FF00], 1, 1, 2, 2, 2);   // 1×1 → 2×2 с угла (2,2): видна одна точка
        Assert.Equal((0, 255, 0), At(f, 2, 2));
        Assert.Equal((100, 100, 100), At(f, 1, 1));
        CursorOverlay.Blend(f, [0xFF0000FF], 1, 1, -1, -1, 2);  // заходит за левый верх
        Assert.Equal((0, 0, 255), At(f, 0, 0));
    }

    [Theory]
    [InlineData(32, 1.0, 1.0)]     // экран как есть — размер как есть
    [InlineData(32, 0.33, 0.5)]    // 1920 → 640: не мельче 16 px
    [InlineData(32, 2.0, 2.0)]
    public void Cursor_not_smaller_than_16px(int w, double scale, double expected) =>
        Assert.Equal(expected, CursorOverlay.SizeScale(w, scale), 3);
}
