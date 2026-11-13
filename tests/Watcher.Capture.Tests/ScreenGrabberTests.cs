using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using Watcher.Capture;
using Xunit;

namespace Watcher.Capture.Tests;

// Задача D1 (roadmap-next.md): пустой кадр (все нули) при разблокировке /
// переподключении RDP — «экран недоступен», а не движение. Сам Grab() (BitBlt)
// в тесте не вызвать — проверяется только критерий пустого кадра.
[SupportedOSPlatform("windows")]
public class ScreenGrabberTests
{
    private static Bitmap Filled(int w, int h, Color c)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(c);
        return bmp;
    }

    [Fact]
    public void All_zero_frame_is_blank()
    {
        // 31 px — ширина строки 93 байта, stride с выравниванием 96: проверяем,
        // что хвост выравнивания не читается как данные.
        using var bmp = Filled(31, 7, Color.Black);
        Assert.True(ScreenGrabber.IsAllBlack(bmp));
    }

    [Fact]
    public void Single_non_black_pixel_in_last_row_is_not_blank()
    {
        using var bmp = Filled(31, 7, Color.Black);
        bmp.SetPixel(30, 6, Color.FromArgb(1, 0, 0));
        Assert.False(ScreenGrabber.IsAllBlack(bmp));
    }

    [Fact]
    public void Dark_but_not_zero_frame_is_not_blank()
    {
        using var bmp = Filled(64, 64, Color.FromArgb(2, 2, 2));
        Assert.False(ScreenGrabber.IsAllBlack(bmp));
    }
}
