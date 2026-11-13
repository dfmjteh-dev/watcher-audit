namespace Watcher.Capture.Linux;

// 0.14.3: курсор мыши на кадре. Захват корневого окна (XShm) курсор не
// содержит — рисуем его сами после детектора движения (Program.cs): само
// движение мыши запись не открывает, а в записанном видео курсор есть.
public static class CursorOverlay
{
    // На уменьшенном кадре курсор мельче экрана; меньше этого его не видно.
    public const int MinCursorPx = 16;

    // Во сколько раз рисовать курсор шириной cursorW при масштабе кадра scale.
    public static double SizeScale(int cursorW, double scale) =>
        cursorW <= 0 ? scale : Math.Max(scale, (double)MinCursorPx / cursorW);

    // argb — пиксели курсора XFixes: ARGB32 с premultiplied alpha. (left, top) —
    // левый верхний угол курсора в координатах кадра, k — масштаб курсора.
    // Ближайший сосед, как и сам кадр (ScreenGrabber.Downscale).
    public static void Blend(Frame frame, ReadOnlySpan<uint> argb, int cw, int ch, double left, double top, double k)
    {
        if (cw <= 0 || ch <= 0 || argb.Length < cw * ch || k <= 0) return;
        int dw = Math.Max(1, (int)Math.Round(cw * k)), dh = Math.Max(1, (int)Math.Round(ch * k));
        int x0 = (int)Math.Round(left), y0 = (int)Math.Round(top);
        var px = frame.Pixels;
        for (int dy = 0; dy < dh; dy++)
        {
            int fy = y0 + dy;
            if (fy < 0 || fy >= frame.Height) continue;
            int sy = Math.Min(ch - 1, (int)(dy / k));
            for (int dx = 0; dx < dw; dx++)
            {
                int fx = x0 + dx;
                if (fx < 0 || fx >= frame.Width) continue;
                uint s = argb[sy * cw + Math.Min(cw - 1, (int)(dx / k))];
                uint a = s >> 24;
                if (a == 0) continue;
                int o = (fy * frame.Width + fx) * 3;
                uint inv = 255 - a;
                px[o] = (byte)Math.Min(255, ((s >> 16) & 0xFF) + px[o] * inv / 255);
                px[o + 1] = (byte)Math.Min(255, ((s >> 8) & 0xFF) + px[o + 1] * inv / 255);
                px[o + 2] = (byte)Math.Min(255, (s & 0xFF) + px[o + 2] * inv / 255);
            }
        }
    }
}
