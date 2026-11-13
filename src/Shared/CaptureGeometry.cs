namespace Watcher;

// 0.13 (п.2 обратной связи 2026-10-14): несколько мониторов и разные разрешения.
// Раньше весь рабочий стол (все мониторы) растягивался в DownscaleWidth×Height
// без сохранения пропорций: два монитора 1920×1080 сжимались по ширине вдвое,
// экран 1280×1024 растягивался в 16:9. Теперь DownscaleWidth×Height — предел
// для ОДНОГО монитора (самого большого): масштаб общий, пропорции сохраняются,
// мелкий экран не растягивается (масштаб не больше 1). Два монитора 1920×1080
// при пределе 1920×1080 — видео 3840×1080; раскладка мониторов в координатах
// видео уходит в sidecar сегмента — плеер умеет показать один монитор.
public readonly record struct MonitorRect(int X, int Y, int W, int H, bool Primary);

public sealed record CaptureLayout(int Width, int Height, IReadOnlyList<MonitorRect> Monitors);

public static class CaptureGeometry
{
    // Предел кадра целиком — тот же, что AgentConfigLimits для ширины/высоты.
    public const int MaxWidth = 7680, MaxHeight = 4320;

    // screenX/Y/W/H — весь рабочий стол (на Windows у виртуального экрана может
    // быть отрицательное начало), monitors — в тех же координатах. Пусто — один
    // монитор на весь стол.
    public static CaptureLayout Layout(int screenX, int screenY, int screenW, int screenH, IReadOnlyList<MonitorRect> monitors, int boxW, int boxH)
    {
        screenW = Math.Max(1, screenW);
        screenH = Math.Max(1, screenH);
        var mons = monitors.Where(m => m.W > 0 && m.H > 0).ToList();
        if (mons.Count == 0) mons.Add(new MonitorRect(screenX, screenY, screenW, screenH, true));
        int mw = mons.Max(m => m.W), mh = mons.Max(m => m.H);
        double scale = Math.Min(1.0, Math.Min((double)Math.Max(1, boxW) / mw, (double)Math.Max(1, boxH) / mh));
        scale = Math.Min(scale, Math.Min((double)MaxWidth / screenW, (double)MaxHeight / screenH));
        int outW = Even(screenW * scale), outH = Even(screenH * scale);
        double sx = (double)outW / screenW, sy = (double)outH / screenH;
        var outMons = mons
            .OrderBy(m => m.Primary ? 0 : 1).ThenBy(m => m.X).ThenBy(m => m.Y)
            .Select(m => new MonitorRect(
                (int)Math.Round((m.X - screenX) * sx), (int)Math.Round((m.Y - screenY) * sy),
                (int)Math.Round(m.W * sx), (int)Math.Round(m.H * sy), m.Primary))
            .ToList();
        return new CaptureLayout(outW, outH, outMons);
    }

    // libx264 + yuv420p требуют чётных размеров.
    private static int Even(double v) => Math.Max(2, (int)Math.Round(v) & ~1);
}
