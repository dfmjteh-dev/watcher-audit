using System.Security.Cryptography;
using System.Text.Json;

namespace Watcher.Uploader;

public interface IGuardPlatform
{
    bool Running(string kind);
    void EnsureRegistered(string kind, GuardPolicy policy) { }
    void Start(string kind, GuardPolicy policy);
    void ReconcileCapture(GuardPolicy policy) { }
    void Stop(string kind, GuardPolicy policy);
    void Install(string package, string platform, bool repair, bool downgrade = false);
    void Uninstall(string kind, GuardPolicy policy);
    Dictionary<string, string> CaptureStartup(GuardPolicy policy) => new();
    void RestoreStartup(GuardPolicy policy, Dictionary<string, string> modes) { }
    void Rollback(string package, string packagePlatform, GuardPolicy policy) => Install(package, packagePlatform, true);
    void VerifyRuntime(GuardPolicy policy, HashSet<string> expectedRunning) { }
    // Версия установленного агента (X.Y.Z) — null, если не определить.
    string? InstalledVersion(GuardPolicy policy) => null;
}
public sealed record GuardExecution(string GrantId, string Binding, string Kind, string Action, string Status, string Code, DateTime At);
public sealed record GuardRetry(int Attempts, DateTime NextAt);

// Выполняет только фиксированные действия. Ни shell-команд, ни путей из разрешения.
public sealed partial class GuardEngine(string root, IGuardPlatform platform, Func<DateTime>? clock = null)
{
    private DateTime Now => (clock ?? (() => DateTime.UtcNow))();
    public void Tick()
    {
        Directory.CreateDirectory(root);
        using var gate = new FileStream(Path.Combine(root, "tick.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var policy = GuardPaths.Load(root) ?? throw new IOException("Guard policy missing");
        ValidatePolicy(policy);
        var migrated = GuardBootstrap.MigrateExecutableName(policy);
        if (!ReferenceEquals(migrated, policy))
        {
            GuardPaths.Write(Path.Combine(root, "policy.json"), migrated);
            policy = migrated;
        }
        // Незавершённая локальная транзакция имеет приоритет над inbox/lease/часами.
        if (!RecoverUpdate(ref policy)) return;
        string inbox = Path.Combine(root, "inbox"); Directory.CreateDirectory(inbox);
        string clockFile = Path.Combine(root, "clock.json");
        DateTime watermark = File.Exists(clockFile) ? JsonSerializer.Deserialize<DateTime>(File.ReadAllBytes(clockFile), GuardPaths.Json) : Now;
        bool clockTrusted = Now >= watermark.AddSeconds(-60);
        if (clockTrusted) GuardPaths.Write(clockFile, Now > watermark ? Now : watermark);
        if (!clockTrusted) DiagnosticJournal.At(Path.Combine(root, "recovery-diagnostics.json")).Record("recovery", "maintenance-rejected", true, Now);
        foreach (string path in Directory.EnumerateFiles(inbox, "*.json").Take(clockTrusted ? 16 : 0))
        {
            SignedMaintenanceGrant? signed;
            try
            {
                signed = ReadSubmission(path);
                if (signed?.Grant is not { } candidate || !MaintenanceSigning.Shape(candidate) || !policy.Credentials.ContainsKey(candidate.Kind))
                { File.Delete(path); continue; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            { try { File.Delete(path); } catch (IOException) { } continue; }
            var grant = signed.Grant; string credentialPath = policy.Credentials[grant.Kind];
            var credential = CredentialStore.Load(credentialPath, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            string binding = credential is null ? "" : MaintenanceSigning.Binding(credential.Secret);
            var replay = new MaintenanceReplay(root);
            bool accepted;
            try { accepted = replay.Consume(signed, policy.MaintenancePublicKey, binding, grant.Kind, Now); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { Report(grant, "rejected", "replay-store-unavailable"); continue; }
            if (!accepted) { DiagnosticJournal.At(Path.Combine(root, "recovery-diagnostics.json")).Record("recovery", "maintenance-rejected", true, Now); File.Delete(path); continue; }
            GuardPaths.Write(Path.Combine(root, "active.json"), signed);
            Report(grant, "accepted", "accepted");
            try
            {
                switch (grant.Action)
                {
                    case "stop": foreach (string kind in policy.Credentials.Keys) platform.Stop(kind, policy); break;
                    case "resume":
                        File.Delete(Path.Combine(root, "uninstalled.json"));
                        File.Delete(Path.Combine(root, "active.json"));
                        if (Healthy(policy)) { foreach (string kind in policy.Credentials.Keys) { platform.EnsureRegistered(kind, policy); platform.Start(kind, policy); } } else throw new IOException("Files require repair");
                        break;
                    case "repair": Repair(policy, force: true); File.Delete(Path.Combine(root, "uninstalled.json")); break;
                    case "update":
                        ExecuteUpdate(policy, grant);
                        policy = GuardPaths.Load(root) ?? throw new IOException("Guard policy missing");
                        break;
                    case "uninstall": platform.Uninstall(grant.Kind, policy); GuardPaths.Write(Path.Combine(root, "uninstalled.json"), signed); File.Delete(Path.Combine(root, "active.json")); break;
                    case "rotate-key":
                        using (var key = ECDsa.Create())
                        { key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(grant.NewPublicKey!), out int read); if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" || read != Convert.FromBase64String(grant.NewPublicKey!).Length) throw new CryptographicException(); }
                        GuardPaths.Write(Path.Combine(root, "policy.json"), policy with { MaintenancePublicKey = grant.NewPublicKey! });
                        policy = policy with { MaintenancePublicKey = grant.NewPublicKey! }; break;
                }
                Report(grant, "completed", "completed");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException or ArgumentException or JsonException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                string reason = ex.Message switch
                {
                    "Installed MSI product not found" => "msi-product-missing",
                    "MSI product still installed" => "msi-product-remains",
                    "Package removal failed" => "package-removal-failed",
                    "System tool timeout" => "system-tool-timeout",
                    "Service stop failed" => "service-stop-failed",
                    _ => "operation-failed"
                };
                GuardPaths.Write(Path.Combine(root, "last-failure.json"), new { at = Now, action = grant.Action, code = reason, exceptionType = ex.GetType().Name, hresult = ex.HResult });
                Report(grant, grant.Action == "update" && File.Exists(UpdateJournalPath) ? "accepted" : "failed", grant.Action == "update" && File.Exists(UpdateJournalPath) ? "accepted" : "operation-failed"); DiagnosticJournal.At(Path.Combine(root, "recovery-diagnostics.json")).Record("recovery", "repair-failed", true, Now);
            }
            File.Delete(path);
            if (grant.Action == "update")
            {
                File.Delete(Path.Combine(root, "inbox", grant.Id + ".package"));
                if (File.Exists(UpdateJournalPath)) return;
                File.Delete(Path.Combine(root, "active.json"));
                policy = GuardPaths.Load(root) ?? throw new IOException("Guard policy missing");
            }
        }
        string removed = Path.Combine(root, "uninstalled.json");
        if (File.Exists(removed))
        {
            if (new FileInfo(removed).Length > 16384) throw new IOException("Invalid uninstall marker");
            var signed = JsonSerializer.Deserialize<SignedMaintenanceGrant>(File.ReadAllBytes(removed), GuardPaths.Json);
            if (signed?.Grant is { } g && g.Action == "uninstall" && MaintenanceSigning.Verify(signed, policy.MaintenancePublicKey, g.Binding, g.Kind, g.IssuedAt)) return;
            throw new IOException("Invalid uninstall marker");
        }
        if (clockTrusted && ActiveLease(policy)) return;
        Repair(policy, force: false);
    }
    private static SignedMaintenanceGrant? ReadSubmission(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
        using var stream = OperatingSystem.IsLinux() ? Watcher.TerminalAgent.UnixFile.OpenRegularNoFollow(path, out _, out _)
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream is null || !stream.CanSeek || stream.Length > 16384) return null;
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        return JsonSerializer.Deserialize<SignedMaintenanceGrant>(bytes, GuardPaths.Json);
    }
    private bool ActiveLease(GuardPolicy policy)
    {
        string path = Path.Combine(root, "active.json"); if (!File.Exists(path)) return false;
        if (new FileInfo(path).Length > 16384) throw new IOException("Invalid active lease");
        var signed = JsonSerializer.Deserialize<SignedMaintenanceGrant>(File.ReadAllBytes(path), GuardPaths.Json);
        if (signed?.Grant is not { } grant || grant.Action is not ("stop" or "update" or "uninstall") || !policy.Credentials.TryGetValue(grant.Kind, out var credentialPath)) return false;
        var credential = CredentialStore.Load(credentialPath, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        return credential is not null && MaintenanceSigning.Verify(signed, policy.MaintenancePublicKey, MaintenanceSigning.Binding(credential.Secret), grant.Kind, Now);
    }
    public static void ValidatePolicy(GuardPolicy policy)
    {
        if (!Path.IsPathFullyQualified(policy.InstallDir) || policy.Credentials.Count is < 1 or > 2
            || policy.Credentials.Any(p => p.Key is not ("video" or "terminal") || !Path.IsPathFullyQualified(p.Value))
            || policy.ConfigHashes.Count is < 1 or > 20 || policy.ConfigHashes.Any(p => !SafeRelative(p.Key) || p.Value.Length != 64 || !p.Value.All(char.IsAsciiHexDigit))) throw new IOException("Invalid guard policy");
        if (policy.StartupModes is { } modes && (modes.Count != policy.Credentials.Count || modes.Any(p => !policy.Credentials.ContainsKey(p.Key)
            || p.Value is not ("automatic" or "delayed-auto" or "manual" or "disabled" or "enabled" or "enabled-runtime")))) throw new IOException("Invalid startup policy");
        using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(policy.MaintenancePublicKey), out _); if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") throw new CryptographicException();
    }
    private static bool SafeRelative(string value) => value.Length > 0 && !Path.IsPathRooted(value) && value.Split('/', '\\').All(p => p.Length > 0 && p is not (".." or "."));
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
    public bool Healthy(GuardPolicy policy) => policy.ConfigHashes.All(p => File.Exists(Path.Combine(policy.InstallDir, p.Key)) && Hash(Path.Combine(policy.InstallDir, p.Key)) == p.Value);
    private void RestoreConfigs(GuardPolicy policy)
    {
        foreach (var file in policy.ConfigHashes.Where(p => p.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            string target = Path.Combine(policy.InstallDir, file.Key);
            if (File.Exists(target) && Hash(target) == file.Value) continue;
            string backup = Path.Combine(root, "config-backups", file.Key);
            if (!File.Exists(backup) || new FileInfo(backup).Length > 1048576 || Hash(backup) != file.Value) throw new IOException("Verified configuration backup missing");
            using var parsed = JsonDocument.Parse(File.ReadAllText(backup)); if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("Invalid backup");
            if (File.Exists(target))
            {
                string quarantine = Path.Combine(root, "quarantine", Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(quarantine)!);
                if (new FileInfo(target).Length <= 1048576) File.Copy(target, quarantine);
            }
            GuardPaths.WriteBytes(target, File.ReadAllBytes(backup), readableByUsers: GuardPaths.PublicConfiguration(file.Key));
        }
    }
    private void Repair(GuardPolicy policy, bool force)
    {
        string retryPath = Path.Combine(root, "retry.json");
        var retry = File.Exists(retryPath) ? JsonSerializer.Deserialize<GuardRetry>(File.ReadAllBytes(retryPath), GuardPaths.Json) : null;
        // The retry budget limits package/config repair, not restarting intact services.
        // A transient SCM startup failure must not permanently disable recovery.
        if (!force && retry is not null && (retry.Attempts >= 3 || Now < retry.NextAt) && !Healthy(policy)) return;
        try
        {
            RestoreConfigs(policy);
            if (!Healthy(policy))
            {
                string metadata = Path.Combine(root, "cache", "package.json");
                string verified, packagePlatform;
                if (policy.LastVerifiedPackage is { } last)
                {
                    verified = VerifiedPackage(policy, last); packagePlatform = last.Platform!;
                }
                else if (policy.InitialPackage is { } initial)
                {
                    verified = GuardRecoveryCache.Verify(root, initial, policy.UpdatePublicKey); packagePlatform = initial.Platform;
                }
                else
                {
                    if (!File.Exists(metadata)) throw new IOException("Signed repair package unavailable");
                    var legacy = JsonSerializer.Deserialize<MaintenanceGrant>(File.ReadAllBytes(metadata), GuardPaths.Json) ?? throw new IOException("Invalid package metadata");
                    verified = VerifiedPackage(policy, legacy); packagePlatform = legacy.Platform!;
                }
                GuardPaths.Write(Path.Combine(root, "internal-repair.json"), new GuardRetry(0, Now.AddMinutes(10)));
                try { platform.Install(verified, packagePlatform, true); RestoreConfigs(policy); }
                finally { File.Delete(Path.Combine(root, "internal-repair.json")); }
                if (!Healthy(policy)) throw new IOException("Repair did not restore expected files");
            }
            foreach (string kind in policy.Credentials.Keys) platform.EnsureRegistered(kind, policy);
            if (policy.StartupModes is { } modes) platform.RestoreStartup(policy, modes);
            foreach (string kind in policy.Credentials.Keys)
                if ((policy.AutoStartKinds?.Contains(kind) ?? policy.AutoStart) && !platform.Running(kind)) platform.Start(kind, policy);
            platform.ReconcileCapture(policy);
            if (File.Exists(retryPath)) File.Delete(retryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or CryptographicException)
        {
            string reason = ex.Message switch
            {
                "Verified configuration backup missing" => "config-backup-unavailable",
                "Signed repair package unavailable" => "repair-package-unavailable",
                "Invalid repair package" => "repair-package-invalid",
                "Package installation failed" => "package-install-failed",
                "Repair did not restore expected files" => "repair-files-mismatch",
                "MSI source name unavailable" => "msi-source-unavailable",
                _ => "repair-failed"
            };
            GuardPaths.Write(Path.Combine(root, "last-failure.json"), new { at = Now, action = "repair", code = reason, exceptionType = ex.GetType().Name, hresult = ex.HResult });
            int attempts = (retry?.Attempts ?? 0) + 1; int[] minutes = [1, 5, 30];
            GuardPaths.Write(retryPath, new GuardRetry(attempts, Now.AddMinutes(minutes[Math.Min(attempts - 1, 2)])));
            DiagnosticJournal.At(Path.Combine(root, "recovery-diagnostics.json")).Record("recovery", "repair-failed", true, Now);
            if (force) throw;
        }
    }
    private string VerifiedPackage(GuardPolicy policy, MaintenanceGrant package)
    {
        var descriptor = PackageDescriptor(package);
        GuardRecoveryCache.Validate(descriptor, policy.UpdatePublicKey);
        string path = GuardRecoveryCache.PackagePath(root, descriptor);
        if (!File.Exists(path))
        {
            string candidate = Path.Combine(root, "inbox", package.Id + ".package");
            if (File.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) == 0)
            {
                using var input = OperatingSystem.IsLinux() ? Watcher.TerminalAgent.UnixFile.OpenRegularNoFollow(candidate, out _, out _)
                    : new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                if (input is null || !input.CanSeek || input.Length != descriptor.Size) throw new IOException("Offline package size mismatch");
                GuardRecoveryCache.Store(root, descriptor, input, policy.UpdatePublicKey);
            }
        }
        GuardRecoveryCache.VerifySource(path, descriptor, policy.UpdatePublicKey);
        return path;
    }
    private void Report(MaintenanceGrant grant, string status, string code)
    {
        var value = new GuardExecution(grant.Id, grant.Binding, grant.Kind, grant.Action, status, code, Now);
        GuardPaths.Write(Path.Combine(root, "results", grant.Id + ".json"), value);
        string audit = Path.Combine(root, "operations.jsonl");
        if (File.Exists(audit) && new FileInfo(audit).Length > 1048576) File.Move(audit, audit + ".previous", true);
        using var stream = new FileStream(audit, FileMode.Append, FileAccess.Write, FileShare.Read); var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, GuardPaths.Json) + "\n"); stream.Write(bytes); stream.Flush(true);
    }
}
