using System.Runtime.Versioning;
using System.Security.Cryptography;
using Watcher.Uploader;

namespace Watcher.Guard;

// 0.16: база восстановления — сам устанавливаемый MSI, если рядом лежит его подпись
// ключом обновлений (<msi>.sig, формат пакетов «Обновлений»): комплект кладёт
// watcher-agent.msi.sig, агент — package.msi.sig рядом со скачанным обновлением.
// Копии общего MSI внутри настроенного (поток WatcherRecoveryPackage) больше нет.
[SupportedOSPlatform("windows")]
public static class MsiRecoverySource
{
    public const string Platform = "windows-msi";

    // Delegated — установка ушла во временную SYSTEM-задачу (запуск не от SYSTEM).
    public enum Result { Seeded, NoSignature, SignatureMismatch, Delegated }

    // NoSignature/SignatureMismatch — защита работает без базы до первого обновления
    // через Guard. Несовпадение (на шаре остался .sig прежнего комплекта) не роняет
    // установку: подделанная пара даёт только отсутствие базы, не чужой пакет.
    public static Result Seed(string root, string msi)
    {
        var policy = GuardPaths.Load(root) ?? throw new IOException("Guard policy missing");
        if (policy.InitialPackage is not null || policy.LastVerifiedPackage is not null) return Result.Seeded;
        string signaturePath = msi + ".sig";
        if (!File.Exists(msi) || !File.Exists(signaturePath)) return Result.NoSignature;
        if ((File.GetAttributes(msi) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(signaturePath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked recovery source");
        if (new FileInfo(signaturePath).Length > 1024) throw new IOException("Recovery signature exceeds limit");
        string signature = File.ReadAllText(signaturePath).Trim();
        string version = typeof(GuardEngine).Assembly.GetName().Version?.ToString(3) ?? throw new IOException("Guard version unknown");
        // Хеш считается по закрытой копии в cache, а не по исходнику (шара, Загрузки):
        // проверенное и сохранённое — один и тот же набор байт.
        string part = Path.Combine(root, "cache", "seed-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var source = new FileStream(msi, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (source.Length is <= 0 or > GuardRecoveryCache.MaxPackageBytes) throw new IOException("Recovery package exceeds limit");
                GuardPaths.WriteStream(part, output => source.CopyTo(output));
            }
            GuardRecoveryPackage package;
            using (var copy = File.OpenRead(part))
                package = new GuardRecoveryPackage(Platform, version, Convert.ToHexString(SHA256.HashData(copy)).ToLowerInvariant(), copy.Length, signature);
            try { GuardRecoveryCache.Validate(package, policy.UpdatePublicKey); }
            catch (Exception e) when (e is CryptographicException or IOException) { return Result.SignatureMismatch; }
            using var payload = File.OpenRead(part);
            GuardRecoveryCache.Seed(root, package, payload);
            return Result.Seeded;
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }
}
