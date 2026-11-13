using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Watcher;

public sealed record MaintenanceGrant(string Id, string Binding, string Kind, string Action, DateTime IssuedAt, DateTime ExpiresAt,
    string? Version = null, string? Sha256 = null, string? Platform = null, string? PackageSignature = null, string? NewPublicKey = null, long? PackageId = null, long? PackageSize = null);
public sealed record SignedMaintenanceGrant(MaintenanceGrant Grant, string Signature, string KeyId);
public sealed record MaintenanceResult(string GrantId, string Status, string Code);

public static class MaintenanceSigning
{
    public const string Context = "Watcher.AgentMaintenance.v1";
    public static readonly HashSet<string> Actions = ["stop", "resume", "update", "repair", "uninstall", "rotate-key"];
    public static string BindingFromHash(string secretHash) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Context + ".credential|" + secretHash.ToLowerInvariant()))).ToLowerInvariant();
    public static string Binding(string secret) => BindingFromHash(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))));
    public static string KeyId(string publicKey) => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(publicKey))).ToLowerInvariant();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static byte[] Message(MaintenanceGrant grant) => Encoding.UTF8.GetBytes(Context + "\n" + JsonSerializer.Serialize(grant, Json));
    public static SignedMaintenanceGrant Sign(MaintenanceGrant grant, ECDsa key)
    {
        if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" || !Shape(grant)) throw new ArgumentException("Invalid maintenance grant");
        string pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        return new(grant, Convert.ToBase64String(key.SignData(Message(grant), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)), KeyId(pub));
    }
    public static bool Verify(SignedMaintenanceGrant signed, string publicKey, string binding, string kind, DateTime now)
    {
        if (signed.Grant is not { } g || !Shape(g) || g.Binding != binding || g.Kind != kind || now.Kind != DateTimeKind.Utc
            || g.IssuedAt > now.AddSeconds(60) || g.ExpiresAt <= now) return false;
        try
        {
            if (signed.KeyId != KeyId(publicKey)) return false;
            using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out int read);
            return key.KeySize == 256 && key.ExportParameters(false).Curve.Oid.Value == "1.2.840.10045.3.1.7" && read == Convert.FromBase64String(publicKey).Length
                && key.VerifyData(Message(g), Convert.FromBase64String(signed.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException) { return false; }
    }
    public static bool Shape(MaintenanceGrant g) => Guid.TryParseExact(g.Id, "N", out _) && g.Binding is { Length: 64 } && g.Binding.All(char.IsAsciiHexDigit)
        && g.Kind is "video" or "terminal" && Actions.Contains(g.Action) && g.IssuedAt.Kind == DateTimeKind.Utc && g.ExpiresAt.Kind == DateTimeKind.Utc
        && g.ExpiresAt > g.IssuedAt && g.ExpiresAt - g.IssuedAt <= TimeSpan.FromHours(1)
        && (g.Action != "update" || (g.PackageId is > 0 && g.PackageSize is > 0 and <= 1073741824 && UpdateSigning.IsValidVersion(g.Version ?? "") && g.Sha256 is { Length: 64 } && g.Sha256.All(char.IsAsciiHexDigit)
            && UpdateSigning.Platforms.Contains(g.Platform ?? "") && g.PackageSignature is { Length: > 0 and < 512 }))
        && (g.Action != "rotate-key" || g.NewPublicKey is { Length: > 0 and < 256 });
}
