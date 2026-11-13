using System.Runtime.InteropServices;
using System.Runtime.Versioning;

// H11 (CA5392): системные DLL — только из System32. Без этого Windows сначала ищет
// DLL рядом с exe и в текущем каталоге; подложенная туда DLL загрузилась бы с
// правами процесса (у службы — LocalSystem).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Watcher.SessionService;

// P/Invoke-сигнатуры для спайка Фазы 6 (roadmap.md). Стиль — как в
// Watcher.Capture/ScreenGrabber.cs: прямые DllImport без обёрточного
// Interop-класса, без доп. абстракций — этот код живёт только на время
// спайка, не продакшен-поверхность.
[SupportedOSPlatform("windows")]
internal static class SessionNative
{
    internal const int WTS_CURRENT_SERVER_HANDLE = 0;

    internal enum WTS_INFO_CLASS
    {
        WTSUserName = 5,
    }

    internal enum WTS_CONNECTSTATE_CLASS
    {
        Active = 0,
        Connected = 1,
        ConnectQuery = 2,
        Shadow = 3,
        Disconnected = 4,
        Idle = 5,
        Listen = 6,
        Reset = 7,
        Down = 8,
        Init = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WTS_SESSION_INFO
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    internal static extern bool WTSQueryUserToken(int sessionId, out IntPtr phToken);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    internal static extern bool WTSEnumerateSessionsW(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int count);

    [DllImport("wtsapi32.dll")]
    internal static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    internal static extern bool WTSQuerySessionInformationW(IntPtr hServer, int sessionId, WTS_INFO_CLASS wtsInfoClass, out IntPtr ppBuffer, out int bytesReturned);

    internal enum SECURITY_IMPERSONATION_LEVEL { Anonymous, Identification, Impersonation, Delegation }
    internal enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation = 2 }

    internal const uint TOKEN_ALL_ACCESS = 0xF01FF;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    internal const uint CREATE_NEW_CONSOLE = 0x00000010;

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes,
        SECURITY_IMPERSONATION_LEVEL impersonationLevel, TOKEN_TYPE tokenType, out IntPtr phNewToken);

    // lpCommandLine — System.Text.StringBuilder, не string: CreateProcess(AsUser)
    // документированно МОЖЕТ модифицировать этот буфер (там дописывается null-
    // terminator на границе argv[0]) — передача обычной (immutable/interned)
    // managed string может молча испортить другие строковые константы с тем же
    // содержимым в процессе. StringBuilder маршалится как мутable-буфер, который
    // безопасно модифицировать. lpApplicationName такого не делает — там string.
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcessAsUser(IntPtr hToken, string? lpApplicationName, System.Text.StringBuilder? lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("userenv.dll", SetLastError = true)]
    internal static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    internal static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);
}
