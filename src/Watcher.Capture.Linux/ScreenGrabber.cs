using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

// H11 (CA5392): на Linux .NET учитывает только флаг AssemblyDirectory — без него
// библиотеки ищутся штатным dlopen (системные пути), не рядом со сборкой, куда
// можно подложить свою .so.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Watcher.Capture.Linux;

// Аналог Watcher.Capture (Windows)/ScreenGrabber.cs (BitBlt), но через X11 +
// MIT-SHM (XShm) — тот же принцип "дешёвый снимок корневого окна", без
// System.Drawing/GDI, которых на Linux просто нет. Код не переиспользуется
// между агентами (см. roadmap.md, Фаза 7, "Явно отложено") — независимая
// реализация под другое API захвата.
//
// Требует реальный X11-дисплей с MIT-SHM (стандартно для Xorg). Wayland
// рассматривался и осознанно отклонён — см. roadmap.md, Фаза 7: rootless
// XWayland не даёт полноценное корневое окно всего экрана. Операционный
// обход — форсировать Xorg-сессию (WaylandEnable=false в gdm3) на
// аудируемых десктопах.
[SupportedOSPlatform("linux")]
public sealed class ScreenGrabber : IScreenGrabber, IDisposable
{
    private const int ZPixmap = 2;
    private const int IpcCreat = 0x200;       // IPC_CREAT, <sys/ipc.h>
    private const int Perm0777 = 0x1FF;       // права на сегмент (0777 восьмеричное)

    [StructLayout(LayoutKind.Sequential)]
    private struct XImage
    {
        public int width, height;
        public int xoffset;
        public int format;
        public IntPtr data;
        public int byte_order;
        public int bitmap_unit;
        public int bitmap_bit_order;
        public int bitmap_pad;
        public int depth;
        public int bytes_per_line;
        public int bits_per_pixel;
        public UIntPtr red_mask;
        public UIntPtr green_mask;
        public UIntPtr blue_mask;
        // Хвост структуры (XPointer obdata + таблица из 6 функций) нам не нужен —
        // мы не вызываем image->f.*, кроме как через XDestroyImage напрямую, и
        // Marshal.PtrToStructure читает только объявленные здесь поля.
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XShmSegmentInfo
    {
        public UIntPtr shmseg;
        public int shmid;
        public IntPtr shmaddr;
        public int readOnly;
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(string? displayName);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XDefaultScreen(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XRootWindow(IntPtr display, int screenNumber);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultVisual(IntPtr display, int screenNumber);
    [DllImport("libX11.so.6")] private static extern int XDefaultDepth(IntPtr display, int screenNumber);
    [DllImport("libX11.so.6")] private static extern int XDestroyImage(IntPtr image);
    [DllImport("libX11.so.6")] private static extern int XSync(IntPtr display, int discard);
    [DllImport("libX11.so.6")]
    private static extern int XGetGeometry(IntPtr display, IntPtr drawable, out IntPtr root, out int x, out int y,
        out uint width, out uint height, out uint borderWidth, out uint depth);

    [DllImport("libXext.so.6")] private static extern int XShmQueryExtension(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XFree(IntPtr data);

    // 0.14.3: курсор — XFixes (libXfixes.so.3, есть на любом X11-десктопе). Нет
    // библиотеки — курсор не рисуем, кадр пишется как раньше.
    [StructLayout(LayoutKind.Sequential)]
    private struct XFixesCursorImage
    {
        public short x, y;
        public ushort width, height;
        public ushort xhot, yhot;
        public UIntPtr cursor_serial;
        public IntPtr pixels;   // unsigned long* — по 8 байт на пиксель на x86_64, ARGB в младших 32 битах
    }

    [DllImport("libXfixes.so.3")] private static extern IntPtr XFixesGetCursorImage(IntPtr display);
    private bool _cursorUnavailable;
    [DllImport("libXext.so.6")]
    private static extern IntPtr XShmCreateImage(
        IntPtr display, IntPtr visual, uint depth, int format, IntPtr data,
        ref XShmSegmentInfo shmInfo, uint width, uint height);
    [DllImport("libXext.so.6")] private static extern int XShmAttach(IntPtr display, ref XShmSegmentInfo shmInfo);
    [DllImport("libXext.so.6")] private static extern int XShmDetach(IntPtr display, ref XShmSegmentInfo shmInfo);
    [DllImport("libXext.so.6")]
    private static extern int XShmGetImage(
        IntPtr display, IntPtr drawable, IntPtr image, int x, int y, UIntPtr planeMask);

    [DllImport("libc", SetLastError = true)] private static extern int shmget(int key, nuint size, int shmflg);
    [DllImport("libc", SetLastError = true)] private static extern IntPtr shmat(int shmid, IntPtr shmaddr, int shmflg);
    [DllImport("libc")] private static extern int shmdt(IntPtr shmaddr);
    [DllImport("libc")] private static extern int shmctl(int shmid, int cmd, IntPtr buf);
    private const int IpcPrivate = 0;
    private const int IpcRmid = 0;

    private readonly IntPtr _display;
    private IntPtr _root;
    private int _screenW, _screenH;
    private IntPtr _imagePtr;
    private int _bytesPerLine, _bitsPerPixel;
    private ulong _redMask, _greenMask, _blueMask;
    private XShmSegmentInfo _shmInfo;
    // 0.13: _boxW×_boxH — предел для одного монитора, кадр — весь экран с
    // сохранением пропорций (CaptureGeometry); _outW×_outH — из раскладки.
    private int _boxW, _boxH;
    private int _outW, _outH;
    private CaptureLayout _layout = new(2, 2, []);
    public CaptureLayout Layout => _layout;

    // Мониторы — XRandR 1.5 (XRRGetMonitors). Нет библиотеки или расширения —
    // один монитор на весь экран, как до 0.13.
    [StructLayout(LayoutKind.Sequential)]
    private struct XRRMonitorInfo
    {
        public ulong name;
        public int primary, automatic, noutput, x, y, width, height, mwidth, mheight;
        public IntPtr outputs;
    }
    [DllImport("libXrandr.so.2")] private static extern IntPtr XRRGetMonitors(IntPtr display, IntPtr window, int getActive, out int nmonitors);
    [DllImport("libXrandr.so.2")] private static extern void XRRFreeMonitors(IntPtr monitors);

    private List<MonitorRect> Monitors()
    {
        var list = new List<MonitorRect>();
        try
        {
            IntPtr arr = XRRGetMonitors(_display, _root, 1, out int n);
            if (arr == IntPtr.Zero) return list;
            try
            {
                int size = Marshal.SizeOf<XRRMonitorInfo>();
                for (int i = 0; i < n; i++)
                {
                    var m = Marshal.PtrToStructure<XRRMonitorInfo>(arr + i * size);
                    list.Add(new MonitorRect(m.x, m.y, m.width, m.height, m.primary != 0));
                }
            }
            finally { XRRFreeMonitors(arr); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return list;
    }

    private void RecalcLayout()
    {
        _layout = CaptureGeometry.Layout(0, 0, _screenW, _screenH, Monitors(), _boxW, _boxH);
        _outW = _layout.Width;
        _outH = _layout.Height;
    }

    public int OutputWidth => _outW;
    public int OutputHeight => _outH;

    public ScreenGrabber(int outWidth, int outHeight)
    {
        _boxW = outWidth;
        _boxH = outHeight;

        _display = XOpenDisplay(null);
        if (_display == IntPtr.Zero)
            throw new InvalidOperationException(
                "XOpenDisplay failed — нет доступа к X11-дисплею (DISPLAY/XAUTHORITY не заданы, " +
                "или это Wayland-сессия без Xorg, см. docs/architecture.md)");

        if (XShmQueryExtension(_display) == 0)
        {
            XCloseDisplay(_display);
            throw new NotSupportedException("X-сервер не поддерживает MIT-SHM (XShm) — захват невозможен");
        }

        try { AttachShm(); }
        catch
        {
            XCloseDisplay(_display);
            throw;
        }
    }

    // Сегмент SHM и XImage размером с экран. Вызывается из конструктора и
    // заново, если экран сменил размер (см. Grab).
    private void AttachShm()
    {
        int screen = XDefaultScreen(_display);
        _root = XRootWindow(_display, screen);
        // Размер — у X-сервера, а не из кэша Xlib (XDisplayWidth): после смены
        // разрешения (RandR — подстройка под окно консоли VM, второй монитор)
        // кэш остаётся старым.
        if (XGetGeometry(_display, _root, out _, out _, out _, out uint w, out uint h, out _, out _) == 0)
            throw new InvalidOperationException("XGetGeometry failed");
        _screenW = (int)w;
        _screenH = (int)h;
        RecalcLayout();
        IntPtr visual = XDefaultVisual(_display, screen);
        int depth = XDefaultDepth(_display, screen);

        _shmInfo = default;
        _imagePtr = XShmCreateImage(_display, visual, (uint)depth, ZPixmap, IntPtr.Zero,
            ref _shmInfo, (uint)_screenW, (uint)_screenH);
        if (_imagePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("XShmCreateImage failed");
        }

        var image = Marshal.PtrToStructure<XImage>(_imagePtr);
        _bytesPerLine = image.bytes_per_line;
        _bitsPerPixel = image.bits_per_pixel;
        _redMask = (ulong)image.red_mask;
        _greenMask = (ulong)image.green_mask;
        _blueMask = (ulong)image.blue_mask;

        if (_bitsPerPixel != 32 && _bitsPerPixel != 24)
        {
            XDestroyImage(_imagePtr);
            _imagePtr = IntPtr.Zero;
            throw new NotSupportedException(
                $"X-визуал с {_bitsPerPixel} бит/пиксель не поддерживается (нужен 24 или 32) — " +
                "нестандартная глубина цвета, см. ограничения в docs/architecture.md");
        }

        nuint shmSize = (nuint)(_bytesPerLine * _screenH);
        _shmInfo.shmid = shmget(IpcPrivate, shmSize, IpcCreat | Perm0777);
        if (_shmInfo.shmid < 0)
        {
            XDestroyImage(_imagePtr);
            _imagePtr = IntPtr.Zero;
            throw new InvalidOperationException($"shmget failed (errno {Marshal.GetLastWin32Error()})");
        }

        _shmInfo.shmaddr = shmat(_shmInfo.shmid, IntPtr.Zero, 0);
        if (_shmInfo.shmaddr == new IntPtr(-1))
        {
            shmctl(_shmInfo.shmid, IpcRmid, IntPtr.Zero);
            XDestroyImage(_imagePtr);
            _imagePtr = IntPtr.Zero;
            throw new InvalidOperationException($"shmat failed (errno {Marshal.GetLastWin32Error()})");
        }
        _shmInfo.readOnly = 0;

        // XImage.data должен указывать на тот же сегмент, что XShmAttach прикрепит
        // к X-серверу — записываем это напрямую в структуру, на которую XShmGetImage
        // будет писать результат.
        IntPtr dataFieldOffset = Marshal.OffsetOf<XImage>(nameof(XImage.data));
        Marshal.WriteIntPtr(_imagePtr, (int)dataFieldOffset, _shmInfo.shmaddr);

        if (XShmAttach(_display, ref _shmInfo) == 0)
        {
            shmdt(_shmInfo.shmaddr);
            shmctl(_shmInfo.shmid, IpcRmid, IntPtr.Zero);
            XDestroyImage(_imagePtr);
            _imagePtr = IntPtr.Zero;
            throw new InvalidOperationException("XShmAttach failed");
        }
        XSync(_display, 0);

        // IPC-сегмент можно помечать на удаление сразу после attach — ядро не
        // освободит его, пока есть хоть одно присоединение (наше и X-сервера);
        // так он не переживёт процесс, если тот упадёт без Dispose().
        shmctl(_shmInfo.shmid, IpcRmid, IntPtr.Zero);
    }

    private void DetachShm()
    {
        XShmDetach(_display, ref _shmInfo);
        shmdt(_shmInfo.shmaddr);
        // data указывает на SHM-память, не на malloc Xlib — XDestroyImage не должен
        // её освобождать сам; обнуляем поле перед вызовом (см. AttachShm).
        IntPtr dataFieldOffset = Marshal.OffsetOf<XImage>(nameof(XImage.data));
        Marshal.WriteIntPtr(_imagePtr, (int)dataFieldOffset, IntPtr.Zero);
        XDestroyImage(_imagePtr);
        _imagePtr = IntPtr.Zero;
    }

    // Как и у Windows ScreenGrabber — Resize не трогает ничего нативного: сегмент
    // SHM всегда размером с физический экран, downscale применяется на managed-стороне
    // при каждом Grab(), так что смена _outW/_outH безопасна без пересоздания XShm.
    public void Resize(int outWidth, int outHeight)
    {
        _boxW = outWidth;
        _boxH = outHeight;
        RecalcLayout();
    }

    // Снимок не получился — чаще всего экран сменил размер (сегмент SHM под старый
    // размер). Пересоздаём сегмент под текущий и пробуем ещё раз; не вышло —
    // ScreenUnavailableException: Program пропускает такт и пробует дальше. До
    // 0.12 здесь был фатальный выход — и запись экрана молча вставала до
    // следующего входа пользователя.
    public Frame Grab()
    {
        if (_imagePtr == IntPtr.Zero || XShmGetImage(_display, _root, _imagePtr, 0, 0, unchecked((UIntPtr)ulong.MaxValue)) == 0)
        {
            try
            {
                if (_imagePtr != IntPtr.Zero) DetachShm();
                AttachShm();
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
            {
                throw new ScreenUnavailableException("screen re-attach failed: " + e.Message);
            }
            if (XShmGetImage(_display, _root, _imagePtr, 0, 0, unchecked((UIntPtr)ulong.MaxValue)) == 0)
                throw new ScreenUnavailableException("XShmGetImage failed after re-attach");
        }

        int srcLen = _bytesPerLine * _screenH;
        var src = new byte[srcLen];
        Marshal.Copy(_shmInfo.shmaddr, src, 0, srcLen);

        return Downscale(src);
    }

    // 0.14.3: курсор поверх уже снятого кадра (после детектора движения, Program.cs).
    public void DrawCursor(Frame frame)
    {
        if (_cursorUnavailable || frame.Width != _outW || frame.Height != _outH) return;
        IntPtr img;
        try { img = XFixesGetCursorImage(_display); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _cursorUnavailable = true;
            return;
        }
        if (img == IntPtr.Zero) return;
        try
        {
            var ci = Marshal.PtrToStructure<XFixesCursorImage>(img);
            int cw = ci.width, ch = ci.height;
            if (cw == 0 || ch == 0 || ci.pixels == IntPtr.Zero) return;
            var argb = new uint[cw * ch];
            int stride = IntPtr.Size;   // unsigned long
            for (int i = 0; i < argb.Length; i++)
                argb[i] = (uint)Marshal.ReadInt32(ci.pixels, i * stride);   // little-endian: младшие 32 бита
            double sx = (double)_outW / _screenW, sy = (double)_outH / _screenH;
            double k = CursorOverlay.SizeScale(cw, sx);
            CursorOverlay.Blend(frame, argb, cw, ch, ci.x * sx - ci.xhot * k, ci.y * sy - ci.yhot * k, k);
        }
        finally { XFree(img); }
    }

    // Nearest-neighbour, не билинейно как у Windows-агента (GDI там делает это за нас) —
    // для 2-4 fps motion-preview разница не играет роли, см. docs/architecture.md.
    // Цвет извлекается через red/green/blue_mask из самого XImage (не хардкодится
    // порядок байт) — покрывает и BGRX, и RGBX раскладку 24/32-битных TrueColor-визуалов,
    // которые на практике встречаются на Linux-десктопах; экзотические глубины/палитры —
    // осознанно не поддерживаются (см. проверку в конструкторе).
    private Frame Downscale(byte[] src)
    {
        int bpp = _bitsPerPixel / 8;
        int redShift = BitOperations.TrailingZeroCount(_redMask);
        int greenShift = BitOperations.TrailingZeroCount(_greenMask);
        int blueShift = BitOperations.TrailingZeroCount(_blueMask);

        var outPixels = new byte[_outW * _outH * 3];
        for (int y = 0; y < _outH; y++)
        {
            int srcY = y * _screenH / _outH;
            int rowOffset = srcY * _bytesPerLine;
            int outRowOffset = y * _outW * 3;
            for (int x = 0; x < _outW; x++)
            {
                int srcX = x * _screenW / _outW;
                int pixelOffset = rowOffset + srcX * bpp;

                uint pixel = bpp == 4
                    ? BitConverter.ToUInt32(src, pixelOffset)
                    : (uint)(src[pixelOffset] | (src[pixelOffset + 1] << 8) | (src[pixelOffset + 2] << 16));

                byte r = (byte)((pixel & _redMask) >> redShift);
                byte g = (byte)((pixel & _greenMask) >> greenShift);
                byte b = (byte)((pixel & _blueMask) >> blueShift);

                int o = outRowOffset + x * 3;
                outPixels[o] = r;
                outPixels[o + 1] = g;
                outPixels[o + 2] = b;
            }
        }

        return new Frame(_outW, _outH, outPixels);
    }

    public void Dispose()
    {
        if (_imagePtr != IntPtr.Zero) DetachShm();
        XCloseDisplay(_display);
    }
}

// Экран сейчас не снять (сменился размер и пересоздать сегмент не вышло, дисплей
// занят) — такт пропускается, агент не падает.
public sealed class ScreenUnavailableException(string message) : Exception(message);
