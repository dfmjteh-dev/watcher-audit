using System.Security.Cryptography;
using Watcher.Uploader;

namespace Watcher.Guard;

// Первичный снимок проверенных конфигов при включении защиты (команда bootstrap:
// Linux GuardLinuxSetup.Run, Windows — GuardWindowsSetup.Install).
public static class GuardSnapshot
{
    public static void Take(string root)
    {
        var policy = GuardPaths.Load(root) ?? throw new IOException("Guard policy missing");
        GuardEngine.ValidatePolicy(policy);
        if (File.Exists(Path.Combine(root, "initialized"))) return;
        if (policy.StartupModes is null)
        {
            policy = policy with { StartupModes = new GuardPlatform().CaptureStartup(policy) };
            GuardPaths.Write(Path.Combine(root, "policy.json"), policy);
        }
        foreach (var file in policy.ConfigHashes.Where(p => p.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            string source = Path.Combine(policy.InstallDir, file.Key);
            if (GuardHash.Of(source) != file.Value) throw new IOException("Initial configuration hash mismatch");
            string dest = Path.Combine(root, "config-backups", file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            GuardPaths.WriteBytes(dest, File.ReadAllBytes(source));
        }
        File.WriteAllText(Path.Combine(root, "initialized"), DateTime.UtcNow.ToString("o"));
    }

}

internal static class GuardHash
{
    public static string Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
