using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

// H11 (CA5392): системные DLL — только из System32. Без этого Windows сначала ищет
// DLL рядом с exe и в текущем каталоге; подложенная туда DLL загрузилась бы с
// правами процесса.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Watcher.Capture;

[SupportedOSPlatform("windows")]
public sealed class ScreenGrabber : IResizableGrabber, IDisposable
{
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    // 0.13 (проверка RC, ноутбук 125% + внешний 100%): процесс, понимающий DPI
    // только «системный» (SetProcessDPIAware), получал от Windows координаты в
    // разных шкалах: виртуальный экран — в физических пикселях, границы монитора
    // с масштабом — в логических (1536×960 вместо 1920×1200). Кадр и раскладка
    // мониторов не совпадали — плеер обрезал не то. Per-Monitor V2 — всё в
    // физических пикселях. Звать до первого окна/вызова user32 (Program.cs —
    // в самом начале); Windows до 10 1703 его не знает — тогда как раньше.
    public static void EnableDpiAwareness()
    {
        try
        {
            if (SetProcessDpiAwarenessContext(PerMonitorAwareV2)) return;
        }
        catch (EntryPointNotFoundException) { }
        SetProcessDPIAware();
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);

    // 0.14.3: курсор — BitBlt его не снимает.
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);
    private const int CURSOR_SHOWING = 1, DI_NORMAL = 3, SM_CXCURSOR = 13;
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(IntPtr dst, int xd, int yd, int w, int h, IntPtr src, int xs, int ys, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    private const uint MONITORINFOF_PRIMARY = 1;

    private int _x, _y, _w, _h;
    private int _boxW, _boxH;
    private CaptureLayout _layout = new(2, 2, []);

    public int OutputWidth => _layout.Width;
    public int OutputHeight => _layout.Height;
    // 0.13: где какой монитор в кадре (координаты видео) — в sidecar сегмента.
    public CaptureLayout Layout => _layout;

    // outWidth×outHeight — предел для одного монитора (CaptureGeometry): весь
    // рабочий стол снимается с сохранением пропорций.
    public ScreenGrabber(int outWidth, int outHeight)
    {
        _boxW = outWidth;
        _boxH = outHeight;
        Refresh();
    }

    // Безопасно на лету: Grab() не хранит никаких хендлов между вызовами (DC/битмап
    // создаются и освобождаются внутри одного Grab(), см. finally там) — меняется
    // только предел и раскладка, пересоздавать нативные объекты не нужно.
    public void Resize(int outWidth, int outHeight)
    {
        _boxW = outWidth;
        _boxH = outHeight;
        Refresh();
    }

    // Виртуальный экран и мониторы — заново. Grab() зовёт при каждой смене
    // размеров виртуального экрана (монитор подключили/отключили, сменили
    // разрешение): раньше размер запоминался раз при старте.
    private void Refresh()
    {
        _x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        _y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        _w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        _h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        _layout = CaptureGeometry.Layout(_x, _y, _w, _h, EnumMonitors(), _boxW, _boxH);
    }

    private static List<MonitorRect> EnumMonitors()
    {
        var list = new List<MonitorRect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(h, ref info))
                list.Add(new MonitorRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right - info.rcMonitor.Left,
                    info.rcMonitor.Bottom - info.rcMonitor.Top, (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // Код ошибки последнего неудачного Grab() — для лога (Program.cs).
    public int LastError { get; private set; }

    // null — экран сейчас не захватить: сессия заблокирована, UAC secure desktop,
    // переключение пользователей. BitBlt с рабочего стола сессии тогда не
    // срабатывает, а битмап остаётся чёрным; раньше результат не проверялся, и
    // чёрный кадр после рабочего стола считался движением — на каждую блокировку
    // открывался сегмент из pre-roll и черноты (спайк Задачи D1, roadmap-next.md).
    public Bitmap? Grab()
    {
        if (GetSystemMetrics(SM_XVIRTUALSCREEN) != _x || GetSystemMetrics(SM_YVIRTUALSCREEN) != _y ||
            GetSystemMetrics(SM_CXVIRTUALSCREEN) != _w || GetSystemMetrics(SM_CYVIRTUALSCREEN) != _h)
            Refresh();
        int _outW = _layout.Width, _outH = _layout.Height;
        IntPtr screenDC = GetWindowDC(GetDesktopWindow());
        IntPtr memDC = CreateCompatibleDC(screenDC);
        IntPtr hBmp = CreateCompatibleBitmap(screenDC, _w, _h);
        IntPtr oldObj = SelectObject(memDC, hBmp);
        try
        {
            if (!BitBlt(memDC, 0, 0, _w, _h, screenDC, _x, _y, SRCCOPY | CAPTUREBLT))
            {
                LastError = Marshal.GetLastWin32Error();
                return null;
            }
            using var full = Image.FromHbitmap(hBmp);
            var scaled = new Bitmap(_outW, _outH, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(scaled);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(full, 0, 0, _outW, _outH);
            // Второй вид «экран недоступен»: при разблокировке/переподключении RDP
            // BitBlt разово отвечает успехом, но битмап пустой (все нули) — тот же
            // ложный сегмент «рабочий стол → чёрный → рабочий стол», найдено живой
            // проверкой D1. Именно все нули, не «тёмный» кадр: настоящий экран
            // такого не даёт.
            if (IsAllBlack(scaled))
            {
                scaled.Dispose();
                LastError = 0;
                return null;
            }
            return scaled;
        }
        finally
        {
            SelectObject(memDC, oldObj);
            DeleteObject(hBmp);
            DeleteDC(memDC);
            ReleaseDC(GetDesktopWindow(), screenDC);
        }
    }

    // 0.14.3: курсор поверх уже снятого кадра — после детектора движения
    // (Program.cs), чтобы само движение мыши не открывало запись. Курсор
    // уменьшается вместе с экраном, но не мельче 16 px — иначе на 640×480 с
    // 1920 он почти не виден.
    public void DrawCursor(Bitmap frame)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;
        int hx = 0, hy = 0;
        if (GetIconInfo(ci.hCursor, out var ii))
        {
            (hx, hy) = (ii.xHotspot, ii.yHotspot);
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
        int cw = Math.Max(1, GetSystemMetrics(SM_CXCURSOR));
        double sx = (double)frame.Width / _w, sy = (double)frame.Height / _h;
        double k = Math.Max(sx, 16.0 / cw);
        int size = (int)Math.Round(cw * k);
        int x = (int)Math.Round((ci.pt.X - _x) * sx - hx * k), y = (int)Math.Round((ci.pt.Y - _y) * sy - hy * k);
        using var g = Graphics.FromImage(frame);
        IntPtr hdc = g.GetHdc();
        try { DrawIconEx(hdc, x, y, ci.hCursor, size, size, 0, IntPtr.Zero, DI_NORMAL); }
        finally { g.ReleaseHdc(hdc); }
    }

    // Все пиксели 24bpp-кадра — ноль. На обычном кадре выходит на первом же
    // ненулевом байте, полный проход — только у действительно пустого кадра.
    public static bool IsAllBlack(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int rowBytes = bmp.Width * 3;
            var row = new byte[rowBytes];
            for (int y = 0; y < bmp.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, rowBytes);
                foreach (byte b in row)
                {
                    if (b != 0) return false;
                }
            }
            return true;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public void Dispose() { }
}
