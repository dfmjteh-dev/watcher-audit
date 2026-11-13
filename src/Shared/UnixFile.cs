using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// H11 (CA5392): на Linux .NET учитывает только флаг AssemblyDirectory — без него
// библиотеки ищутся штатным dlopen (системные пути), не рядом со сборкой, куда
// можно подложить свою .so (агент работает от root).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Watcher.TerminalAgent;

// R5 (docs/code-review-2026-10.md): raw-output/ — 1777, туда пишут
// пользователи, а читает root. Безопасно открыть чужой файл из такого каталога
// managed-API не умеет: File.OpenRead проходит по симлинку и блокируется на
// FIFO, а владельца файла .NET 8 на Unix не отдаёт вовсе. Поэтому — libc
// напрямую: open(O_NOFOLLOW|O_NONBLOCK), и тип/владелец берутся через statx
// с уже открытого дескриптора, а не по пути (иначе между проверкой и open
// пользователь успевает подменить файл — каталог его, rename ему доступен).
//
// statx, а не fstat: раскладка struct statx одинакова на всех архитектурах, у
// struct stat — нет; glibc >= 2.28 (Debian 10+, RHEL 8+, всё, где вообще
// запускается .NET 8).
public static class UnixFile
{
    public static uint CurrentUid => GetEuid();
    private const int O_RDONLY = 0;
    private const int O_NONBLOCK = 0x800;
    private const int O_CLOEXEC = 0x80000;
    private const int AT_SYMLINK_NOFOLLOW = 0x100;
    private const int AT_EMPTY_PATH = 0x1000;
    private const int AT_FDCWD = -100;
    private const uint STATX_TYPE = 0x1;
    private const uint STATX_MODE = 0x2;
    private const uint STATX_UID = 0x8;
    private const int S_IFMT = 0xF000;
    private const int S_IFREG = 0x8000;
    private const int S_IFDIR = 0x4000;

    // Значение O_NOFOLLOW в Linux зависит от архитектуры.
    private static int O_NOFOLLOW => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => 0x20000,
        Architecture.Arm64 => 0x8000,
        var a => throw new PlatformNotSupportedException($"O_NOFOLLOW is not defined for {a}"),
    };

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buf);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    [DllImport("libc", EntryPoint = "getpwnam_r")]
    private static extern int GetPwNamR([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [Out] byte[] pwd, [Out] byte[] buf, nuint buflen, out IntPtr result);

    // Открывает path на чтение, только если это обычный файл и не симлинк.
    // null + error — отказ (симлинк, FIFO, устройство, каталог, нет файла).
    public static FileStream? OpenRegularNoFollow(string path, out uint ownerUid, out string? error)
    {
        ownerUid = 0;
        int fd = Open(path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC);
        if (fd < 0)
        {
            // ELOOP — это симлинк (O_NOFOLLOW).
            error = $"open failed, errno {Marshal.GetLastPInvokeError()}";
            return null;
        }
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        try
        {
            var stx = new byte[256];
            if (Statx(fd, "", AT_EMPTY_PATH, STATX_TYPE | STATX_MODE | STATX_UID, stx) != 0)
            {
                error = $"statx failed, errno {Marshal.GetLastPInvokeError()}";
                handle.Dispose();
                return null;
            }
            // struct statx: stx_uid — смещение 20 (u32), stx_mode — 28 (u16).
            ownerUid = BitConverter.ToUInt32(stx, 20);
            int mode = BitConverter.ToUInt16(stx, 28);
            if ((mode & S_IFMT) != S_IFREG)
            {
                error = $"not a regular file (mode {mode:x})";
                handle.Dispose();
                return null;
            }
            error = null;
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    // 0.12.2: inode файла (по пути, без открытия) — по нему AuditLogTailer
    // отличает новый audit.log после ротации от дописанного старого. null —
    // файла нет или statx не сработал.
    public static ulong? Inode(string path)
    {
        var stx = new byte[256];
        // STATX_INO = 0x100; struct statx: stx_ino — смещение 32 (u64).
        if (Statx(AT_FDCWD, path, 0, 0x100, stx) != 0) return null;
        return BitConverter.ToUInt64(stx, 32);
    }

    // uid пользователя через NSS (getpwnam_r) — видит и доменных (sssd/winbind),
    // в отличие от разбора /etc/passwd. null — пользователя нет.
    public static uint? LookupUid(string userName)
    {
        // struct passwd на 64-битном Linux: pw_name, pw_passwd (указатели), pw_uid — смещение 16.
        var pwd = new byte[64];
        var buf = new byte[16384];
        int rc = GetPwNamR(userName, pwd, buf, (nuint)buf.Length, out IntPtr result);
        if (rc != 0 || result == IntPtr.Zero) return null;
        return BitConverter.ToUInt32(pwd, 16);
    }

    // Каталог (не симлинк на каталог) и принадлежит этому процессу. Для
    // raw-output/sent: он лежит внутри 1777-каталога, и до первого старта
    // агента его мог создать (или подложить симлинком) любой пользователь.
    public static bool IsDirectoryOwnedBySelf(string path)
    {
        var stx = new byte[256];
        if (Statx(AT_FDCWD, path, AT_SYMLINK_NOFOLLOW, STATX_TYPE | STATX_UID, stx) != 0) return false;
        int mode = BitConverter.ToUInt16(stx, 28);
        return (mode & S_IFMT) == S_IFDIR && BitConverter.ToUInt32(stx, 20) == GetEuid();
    }
}
