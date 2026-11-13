using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Watcher.Capture;

// H9: активное окно сессии, в которой запущен Capture (служба запускает его в
// каждой сессии пользователя — GetForegroundWindow видит именно её рабочий стол).
[SupportedOSPlatform("windows")]
public sealed class ActiveWindow
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    // InternalGetWindowText, а не GetWindowText: тот шлёт окну WM_GETTEXT и
    // подвисает вместе с зависшим приложением, этот читает текст напрямую.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(IntPtr hWnd, char[] text, int maxCount);

    private readonly char[] _buf = new char[ActivityLog.MaxTextLength + 1];
    private uint _cachedPid;
    private string? _cachedName;

    public ActiveWindowInfo? Probe()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;   // блокировка, secure desktop, переключение
        GetWindowThreadProcessId(hwnd, out uint pid);
        int len = InternalGetWindowText(hwnd, _buf, _buf.Length);
        string? title = len > 0 ? new string(_buf, 0, len) : null;
        return new ActiveWindowInfo(ProcessName(pid), title, hwnd.ToInt64());
    }

    private string? ProcessName(uint pid)
    {
        if (pid == 0) return null;
        if (pid == _cachedPid) return _cachedName;
        string? name;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { name = null; }   // процесс уже завершился
        _cachedPid = pid;
        _cachedName = name;
        return name;
    }
}
