using System.Security.Cryptography;
using System.Text;

namespace Watcher;

// H2 (roadmap-next.md): подпись пакета обновления агента — ECDSA P-256 / SHA-256
// по каноническому сообщению (платформа, версия, SHA-256 файла), DER в base64.
// Подписывает владелец закрытым ключом (tools/update-signing/sign-update.sh,
// ключ вне сервера); проверяет станция своим открытым ключом — это граница
// безопасности. Сервер проверяет тем же ключом при загрузке, только чтобы
// поймать ошибку администратора (не тот файл, не та версия) сразу, а не на
// всём парке. Один файл на сервер и Watcher.Uploader.Core (src/Shared,
// подключается ссылкой) — формат сообщения не может разойтись.
public static class UpdateSigning
{
    // windows-msi (0.13) — MSI, msiexec с MajorUpgrade: у установленного из MSI
    // агента в «Программах и компонентах» остаётся настоящая версия. windows-zip
    // (zip + update.ps1, агенты до 0.13) убран в 0.16: на проде все Windows-агенты
    // из MSI; heartbeat с ним сохраняется без платформы — обновлений не будет.
    // android (Этап 2, docs/android-plan.md) — Android-агент: платформа нужна
    // уже сейчас, чтобы heartbeat с ней валидировался (AgentEndpoints) и хост
    // попадал в «Обновления». Сам формат пакета и самообновление (APK через
    // PackageInstaller) — Этап 8; до него для android пакеты не грузятся.
    public static readonly string[] Platforms = ["windows-msi", "linux-deb", "linux-rpm", "android"];

    public static string PackageExtension(string platform) => platform switch
    {
        "windows-msi" => ".msi",
        "linux-deb" => ".deb",
        // Этап 8: доставка APK — здесь появится реальная упаковка android-пакета.
        "android" => ".apk",
        _ => ".rpm",
    };

    public static byte[] Message(string platform, string version, string sha256Hex) =>
        Encoding.UTF8.GetBytes($"watcher-agent-update/1\n{platform}\n{version}\n{sha256Hex.ToLowerInvariant()}\n");

    // Открытый ключ — base64 SubjectPublicKeyInfo одной строкой (то, что печатает
    // new-update-key.sh) или PEM целиком. null — ключ не разобрался.
    public static ECDsa? LoadPublicKey(string? keyText)
    {
        if (string.IsNullOrWhiteSpace(keyText)) return null;
        string b64 = string.Concat(keyText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal)));
        try
        {
            var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(b64), out _);
            if (key.KeySize != 256) { key.Dispose(); return null; }
            return key;
        }
        catch (Exception e) when (e is FormatException or CryptographicException) { return null; }
    }

    public static bool Verify(ECDsa key, string platform, string version, string sha256Hex, string signatureB64)
    {
        byte[] sig;
        try { sig = Convert.FromBase64String(signatureB64.Trim()); }
        catch (FormatException) { return false; }
        return key.VerifyData(Message(platform, version, sha256Hex), sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    // 0.16: открытый ключ обслуживания, заверенный ключом обновлений (build-kit.sh,
    // certs/maintenance-key.sig). Linux-агент включает по нему защиту сам — как
    // Windows-агент из MSI, куда ключ зашит в пакет, подписанный тем же ключом.
    public static byte[] MaintenanceKeyMessage(string maintenanceKey) =>
        System.Text.Encoding.UTF8.GetBytes("watcher-maintenance-key/1\n" + maintenanceKey + "\n");

    public static bool VerifyMaintenanceKey(ECDsa updateKey, string maintenanceKey, string signatureB64)
    {
        if (maintenanceKey.Length is 0 or > 512 || !maintenanceKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')) return false;
        byte[] sig;
        try { sig = Convert.FromBase64String(signatureB64.Trim()); }
        catch (FormatException) { return false; }
        return updateKey.VerifyData(MaintenanceKeyMessage(maintenanceKey), sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    // SemVer без суффиксов — то же, что <Version> в Directory.Build.props.
    public static bool IsValidVersion(string? v) =>
        v is not null && System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d{1,4}\.\d{1,4}\.\d{1,6}$");
}
