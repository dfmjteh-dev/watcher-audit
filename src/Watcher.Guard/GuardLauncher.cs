using System.Diagnostics;
using System.Security.Cryptography;

namespace Watcher.Guard;

// Постоянный лаунчер GuardRepair (Windows — задача планировщика, Linux — watcher-guard.service):
// <root>\GuardRepair(.exe) tick запускает версию из bin/<sha256> по файлу launcher.
// Каталог версии назван её SHA-256, поэтому подмена файла в bin не проходит,
// а откат Guard на прежнюю версию — это только смена строки в launcher.
// До 0.16 то же делали recover.ps1 (Windows) и recover.sh (Linux).
public static class GuardLauncher
{
    public static string ExecutableName => OperatingSystem.IsWindows() ? "GuardRepair.exe" : "GuardRepair";
    // Watcher.Guard — имя исполняемого файла до переименования (0.14).
    private static string[] Names => OperatingSystem.IsWindows() ? ["GuardRepair.exe", "Watcher.Guard.exe"] : ["GuardRepair", "Watcher.Guard"];

    public static string Path(string root) => System.IO.Path.Combine(root, ExecutableName);
    public static string BundleDirectory(string root) => System.IO.Path.Combine(root, "bundle");

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static bool IsLauncher(string root, string? processPath = null) =>
        string.Equals(processPath ?? Environment.ProcessPath, Path(root), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // Проверенный исполняемый файл версии из bin/<hash>: имя каталога — его SHA-256.
    public static string VerifiedExecutable(string root, string hash)
    {
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) throw new IOException("Invalid GuardRepair hash");
        foreach (string name in Names)
        {
            string exe = System.IO.Path.Combine(root, "bin", hash, name);
            if (!File.Exists(exe)) continue;
            if ((File.GetAttributes(exe) & FileAttributes.ReparsePoint) != 0) throw new IOException("GuardRepair reparse point refused");
            if (Sha256(exe) != hash.ToLowerInvariant()) throw new IOException("GuardRepair checksum mismatch");
            return exe;
        }
        throw new IOException("Verified GuardRepair executable missing");
    }

    // Версия, на которую указывает launcher (одна строка с SHA-256).
    public static string Current(string root)
    {
        string launcher = System.IO.Path.Combine(root, "launcher");
        if (new FileInfo(launcher).Length > 80) throw new IOException("Invalid GuardRepair launcher");
        return VerifiedExecutable(root, File.ReadAllText(launcher).Trim());
    }

    public static int Launch(string root, string[] arguments, bool privileged)
    {
        if (!privileged) throw new UnauthorizedAccessException(OperatingSystem.IsWindows() ? "Guard requires LocalSystem" : "Guard requires root");
        string exe = Current(root);
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = BundleDirectory(root);
        using var process = Process.Start(info) ?? throw new IOException("GuardRepair did not start");
        if (!process.WaitForExit(TimeSpan.FromMinutes(19))) { process.Kill(entireProcessTree: true); return 1; }
        return process.ExitCode;
    }

    // Журнал сбоев лаунчера: дописывается, при 256 КБ начинается заново.
    public static void LogFailure(string root, Exception error)
    {
        try
        {
            string logs = System.IO.Path.Combine(root, "logs"); Directory.CreateDirectory(logs);
            string file = System.IO.Path.Combine(logs, "launcher.log");
            if (File.Exists(file) && new FileInfo(file).Length > 262144) File.Delete(file);
            File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + error + Environment.NewLine);
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { /* лог — вспомогательный */ }
    }

    // Копия поверх, только если байты отличаются (лаунчер может быть занят задачей).
    public static void CopyIfChanged(string source, string target)
    {
        if (File.Exists(target) && Sha256(target) == Sha256(source)) return;
        string temporary = target + ".new";
        File.Copy(source, temporary, overwrite: true);
        File.Move(temporary, target, overwrite: true);
    }
}
