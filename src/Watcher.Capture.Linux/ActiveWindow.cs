using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Watcher.Capture.Linux;

// H9: активное окно X-дисплея (EWMH): _NET_ACTIVE_WINDOW корневого окна →
// заголовок _NET_WM_NAME (UTF-8, запасной — WM_NAME) и _NET_WM_PID →
// /proc/<pid>/comm. Своё соединение с дисплеем, не ScreenGrabber'а — у
// Xlib без XInitThreads одно соединение не делят между объектами.
[SupportedOSPlatform("linux")]
public sealed class ActiveWindow : IDisposable
{
    private const int Success = 0;
    private static readonly IntPtr AnyPropertyType = IntPtr.Zero;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(string? displayName);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XInternAtom(IntPtr display, string atomName, int onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XFree(IntPtr data);
    [DllImport("libX11.so.6")] private static extern IntPtr XSetErrorHandler(XErrorHandler handler);
    // long/unsigned long в Xlib — 64 бита на linux-x64: nint/nuint.
    [DllImport("libX11.so.6")]
    private static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, nint offset, nint length,
        int delete, IntPtr reqType, out IntPtr actualType, out int actualFormat, out nuint nitems, out nuint bytesAfter, out IntPtr prop);

    // Обработчик X-ошибок по умолчанию завершает процесс: окно, закрытое между
    // _NET_ACTIVE_WINDOW и чтением его заголовка (BadWindow), роняло бы запись
    // экрана. Обработчик общий на процесс — ошибки ScreenGrabber тоже больше не
    // убивают процесс; его вызовы (XShmGetImage) и так возвращают статус ошибки.
    // Делегат — в статическом поле, чтобы его не собрал GC.
    private static readonly XErrorHandler IgnoreErrors = (_, _) => 0;
    private static bool _handlerSet;

    private readonly IntPtr _display;
    private readonly IntPtr _root;
    private readonly IntPtr _netActiveWindow, _netWmName, _utf8String, _netWmPid, _wmName;

    public ActiveWindow(ILogger logger)
    {
        _display = XOpenDisplay(null);
        if (_display == IntPtr.Zero)
        {
            logger.LogWarning("active window: XOpenDisplay failed, window activity will not be recorded");
            return;
        }
        if (!_handlerSet)
        {
            XSetErrorHandler(IgnoreErrors);
            _handlerSet = true;
        }
        _root = XDefaultRootWindow(_display);
        _netActiveWindow = XInternAtom(_display, "_NET_ACTIVE_WINDOW", 0);
        _netWmName = XInternAtom(_display, "_NET_WM_NAME", 0);
        _utf8String = XInternAtom(_display, "UTF8_STRING", 0);
        _netWmPid = XInternAtom(_display, "_NET_WM_PID", 0);
        _wmName = XInternAtom(_display, "WM_NAME", 0);
    }

    public ActiveWindowInfo? Probe()
    {
        if (_display == IntPtr.Zero) return null;
        // Формат 32 Xlib отдаёт массивом C long (8 байт), не int.
        IntPtr window = ReadLong(_root, _netActiveWindow) is { } w ? (IntPtr)w : IntPtr.Zero;
        if (window == IntPtr.Zero) return null;   // рабочий стол / нет EWMH-менеджера окон
        string? title = ReadString(window, _netWmName, _utf8String, Encoding.UTF8)
                        ?? ReadString(window, _wmName, AnyPropertyType, Encoding.Latin1);
        string? process = ReadLong(window, _netWmPid) is { } pid and > 0 ? ProcessName(pid) : null;
        return new ActiveWindowInfo(process, title);
    }

    private long? ReadLong(IntPtr window, IntPtr property)
    {
        if (XGetWindowProperty(_display, window, property, 0, 1, 0, AnyPropertyType,
                out _, out int format, out nuint n, out _, out IntPtr data) != Success || data == IntPtr.Zero)
            return null;
        try { return format == 32 && n >= 1 ? Marshal.ReadInt64(data) : null; }
        finally { XFree(data); }
    }

    private string? ReadString(IntPtr window, IntPtr property, IntPtr type, Encoding encoding)
    {
        // Длина — в 32-битных словах: 256 слов = 1 КБ, заголовок длиннее
        // ActivityLog.MaxTextLength всё равно обрежется.
        if (XGetWindowProperty(_display, window, property, 0, 256, 0, type,
                out _, out int format, out nuint n, out _, out IntPtr data) != Success || data == IntPtr.Zero)
            return null;
        try
        {
            if (format != 8 || n == 0) return null;
            var bytes = new byte[(int)n];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return encoding.GetString(bytes);
        }
        finally { XFree(data); }
    }

    // comm — имя исполняемого файла (до 15 символов), читается и для чужих
    // процессов, в отличие от /proc/<pid>/exe.
    private static string? ProcessName(long pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/comm").Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        if (_display != IntPtr.Zero) XCloseDisplay(_display);
    }
}
