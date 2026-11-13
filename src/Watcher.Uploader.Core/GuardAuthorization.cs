using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Watcher.Uploader;

public static class GuardAuthorization
{
    public static bool Allows(string action, DateTime now, string? root = null)
    {
        root ??= GuardPaths.Root;
        if (!File.Exists(Path.Combine(root, "enabled"))) return true;
        var policy = GuardPaths.Load(root); if (policy is null) return false;
        string path = Path.Combine(root, "active.json");
        if (File.Exists(path) && new FileInfo(path).Length <= 16384)
        {
            var signed = JsonSerializer.Deserialize<SignedMaintenanceGrant>(File.ReadAllBytes(path), GuardPaths.Json);
            if (signed?.Grant is { } grant && policy.Credentials.TryGetValue(grant.Kind, out string? credentialPath))
            {
                var credential = CredentialStore.Load(credentialPath, NullLogger.Instance);
                if (credential is not null && MaintenanceSigning.Verify(signed, policy.MaintenancePublicKey, MaintenanceSigning.Binding(credential.Secret), grant.Kind, now))
                    return action switch { "update" => grant.Action is "update" or "repair", "uninstall" => grant.Action is "uninstall" or "update" or "repair", _ => false };
            }
        }
        path = Path.Combine(root, "internal-repair.json");
        if (File.Exists(path) && new FileInfo(path).Length <= 1024 && (policy.LastVerifiedPackage is not null || policy.InitialPackage is not null))
        {
            var operation = JsonSerializer.Deserialize<GuardRetry>(File.ReadAllBytes(path), GuardPaths.Json);
            if (operation is not null && now <= operation.NextAt && now >= operation.NextAt.AddMinutes(-10)) return action is "update" or "uninstall";
        }
        // Переустановка после уже разрешённого удаления — новый bootstrap с прежним pin.
        path = Path.Combine(root, "uninstalled.json");
        if (File.Exists(path) && new FileInfo(path).Length <= 16384)
        {
            var signed = JsonSerializer.Deserialize<SignedMaintenanceGrant>(File.ReadAllBytes(path), GuardPaths.Json);
            return signed?.Grant is { } g && g.Action == "uninstall" && MaintenanceSigning.Verify(signed, policy.MaintenancePublicKey, g.Binding, g.Kind, g.IssuedAt) && action == "update";
        }
        return false;
    }
}
