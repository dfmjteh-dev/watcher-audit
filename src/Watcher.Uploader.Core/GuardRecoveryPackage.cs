using System.Security.Cryptography;
using System.Text.Json;

namespace Watcher.Uploader;

public sealed record GuardRecoveryPackage(string Platform, string Version, string Sha256, long Size, string Signature);

// Подпись — существующий publisher/update контекст. В манифесте нет путей,
// команд, персональных настроек или разрешения обслуживания.
public static class GuardRecoveryCache
{
    public const long MaxPackageBytes = 1073741824;
    public static GuardRecoveryPackage ReadManifest(Stream source)
    {
        using var limited = new MemoryStream();
        var buffer = new byte[4096]; int read;
        while ((read = source.Read(buffer)) > 0)
        {
            if (limited.Length + read > 16384) throw new IOException("Recovery manifest exceeds limit");
            limited.Write(buffer, 0, read);
        }
        return JsonSerializer.Deserialize<GuardRecoveryPackage>(limited.ToArray(), GuardPaths.Json)
            ?? throw new IOException("Invalid recovery manifest");
    }

    public static void Validate(GuardRecoveryPackage package, string publicKey)
    {
        if (package.Platform is not ("windows-msi" or "linux-deb" or "linux-rpm") || !UpdateSigning.IsValidVersion(package.Version)
            || package.Sha256 is not { Length: 64 } || !package.Sha256.All(char.IsAsciiHexDigit)
            || package.Size is <= 0 or > MaxPackageBytes || package.Signature is not { Length: > 0 and < 512 })
            throw new IOException("Invalid recovery package metadata");
        using var key = UpdateSigning.LoadPublicKey(publicKey);
        if (key is null || !UpdateSigning.Verify(key, package.Platform, package.Version, package.Sha256, package.Signature))
            throw new CryptographicException("Recovery publisher signature invalid");
    }

    public static string PackagePath(string root, GuardRecoveryPackage package) =>
        Path.Combine(root, "cache", package.Sha256.ToLowerInvariant() + UpdateSigning.PackageExtension(package.Platform));

    public static string Verify(string root, GuardRecoveryPackage package, string publicKey)
    {
        string path = PackagePath(root, package);
        VerifySource(path, package, publicKey);
        return path;
    }

    public static void VerifySource(string path, GuardRecoveryPackage package, string publicKey)
    {
        Validate(package, publicKey);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Recovery package missing or linked");
        using var stream = File.OpenRead(path);
        if (stream.Length != package.Size || !string.Equals(Convert.ToHexString(SHA256.HashData(stream)), package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("Recovery package integrity failure");
    }

    // Вызывать только из доверенного bootstrap от SYSTEM/root, до включения timer.
    // Первая база закрепляется один раз; обновления принимаются GuardEngine после проверки служб.
    public static void Seed(string root, GuardRecoveryPackage package, Stream source)
    {
        var policy = GuardPaths.Load(root) ?? throw new IOException("Guard policy missing");
        if (policy.InitialPackage is not null || policy.LastVerifiedPackage is not null) return;
        Store(root, package, source, policy.UpdatePublicKey);
        // Файл уже durably записан и проверен; только теперь публикуется ссылка в policy.
        GuardPaths.Write(Path.Combine(root, "policy.json"), policy with { InitialPackage = package });
    }
    public static void Store(string root, GuardRecoveryPackage package, Stream source, string publicKey)
    {
        Validate(package, publicKey);
        GuardPaths.WriteStream(PackagePath(root, package), output =>
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; long total = 0; int read;
            while ((read = source.Read(buffer)) > 0)
            {
                total += read;
                if (total > package.Size) throw new IOException("Recovery package exceeds declared size");
                digest.AppendData(buffer, 0, read); output.Write(buffer, 0, read);
            }
            if (total != package.Size || !string.Equals(Convert.ToHexString(digest.GetHashAndReset()), package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Recovery package integrity failure");
        });
    }

}
