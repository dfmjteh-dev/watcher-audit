using System.Security.Cryptography;
using System.Text.Json;

namespace Watcher.Uploader;

public sealed record GuardUpdateJournal(int Format, MaintenanceGrant Grant, GuardPolicy Before, GuardRecoveryPackage Previous,
    HashSet<string> Running, Dictionary<string, string> Startup, Dictionary<string, string> Snapshots,
    string Phase = "installing", int Attempts = 0, DateTime NextRetryAt = default, string? FailureReason = null, int? FailureCode = null);

public sealed partial class GuardEngine
{
    private string UpdateDirectory => Path.Combine(root, "update-state");
    private string UpdateJournalPath => Path.Combine(UpdateDirectory, "transaction.json");
    private static GuardRecoveryPackage PackageDescriptor(MaintenanceGrant grant) => new(grant.Platform ?? "", grant.Version ?? "", grant.Sha256 ?? "", grant.PackageSize ?? 0, grant.PackageSignature ?? "");
    private void SaveUpdate(GuardUpdateJournal state)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, GuardPaths.Json);
        if (bytes.Length > 131072 || JsonSerializer.SerializeToUtf8Bytes(state.Before, GuardPaths.Json).Length > 65536) throw new IOException("Update journal exceeds limit");
        GuardPaths.WriteBytes(UpdateJournalPath, bytes);
    }
    private GuardUpdateJournal? ReadUpdate()
    {
        if (!File.Exists(UpdateJournalPath)) return null;
        if (new FileInfo(UpdateJournalPath).Length > 131072 || Linked(UpdateDirectory) || Linked(UpdateJournalPath)) throw new IOException("Invalid update journal");
        var state = JsonSerializer.Deserialize<GuardUpdateJournal>(File.ReadAllBytes(UpdateJournalPath), GuardPaths.Json) ?? throw new IOException("Invalid update journal");
        if (state.Before is null || state.Grant is null || state.Previous is null || state.Running is null || state.Startup is null || state.Snapshots is null) throw new IOException("Invalid update journal");
        ValidatePolicy(state.Before);
        if (state.Format != 1 || state.Grant.Action != "update" || !MaintenanceSigning.Shape(state.Grant)
            || state.Phase is not ("installing" or "rolling-back" or "restoring" or "rolled-back") || state.Attempts < 0
            || state.Running.Any(k => !state.Before.Credentials.ContainsKey(k)) || state.Startup.Count > 2
            || state.Startup.Any(p => !state.Before.Credentials.ContainsKey(p.Key) || p.Value is not ("automatic" or "delayed-auto" or "manual" or "disabled" or "enabled" or "enabled-runtime"))
            || state.Snapshots.Count > 30 || state.Snapshots.Any(p => !SafeRelative(p.Key) || p.Value.Length != 64 || !p.Value.All(char.IsAsciiHexDigit)))
            throw new IOException("Invalid update journal");
        GuardRecoveryCache.Validate(state.Previous, state.Before.UpdatePublicKey);
        return state;
    }
    private static bool Linked(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private GuardRecoveryPackage CurrentPackage(GuardPolicy policy)
    {
        if (policy.LastVerifiedPackage is { } last) { VerifiedPackage(policy, last); return PackageDescriptor(last); }
        if (policy.InitialPackage is { } initial) { GuardRecoveryCache.Verify(root, initial, policy.UpdatePublicKey); return initial; }
        // Старые защищённые установки могли хранить дескриптор отдельно.
        string legacy = Path.Combine(root, "cache", "package.json");
        if (File.Exists(legacy) && new FileInfo(legacy).Length <= 16384 && !Linked(legacy))
        {
            var grant = JsonSerializer.Deserialize<MaintenanceGrant>(File.ReadAllBytes(legacy), GuardPaths.Json) ?? throw new IOException("Invalid repair package");
            var descriptor = PackageDescriptor(grant);
            if (grant.PackageSize is null && descriptor.Sha256.Length == 64 && descriptor.Sha256.All(char.IsAsciiHexDigit))
                descriptor = descriptor with { Size = new FileInfo(GuardRecoveryCache.PackagePath(root, descriptor)).Length };
            GuardRecoveryCache.Verify(root, descriptor, policy.UpdatePublicKey); return descriptor;
        }
        throw new IOException("Verified rollback base unavailable");
    }
    private GuardUpdateJournal PrepareUpdate(GuardPolicy policy, MaintenanceGrant grant)
    {
        var previous = CurrentPackage(policy);
        // Опубликованная baseline 0.15.1 должна содержать journal-aware Guard.
        // Старый installer может вернуть старый launcher ещё до завершения rollback.
        if (!Version.TryParse(previous.Version, out var oldVersion) || oldVersion < new Version(0, 15, 1)
            || !Version.TryParse(grant.Version, out var newVersion) || newVersion < new Version(0, 15, 1))
            throw new IOException("Rollback requires a journal-aware package baseline");
        if (previous.Platform != grant.Platform) throw new IOException("Protected package platform change unsupported");
        if (policy.LastVerifiedPackage is null && policy.InitialPackage is null) policy = policy with { InitialPackage = previous };
        RestoreConfigs(policy);
        if (!Healthy(policy)) throw new IOException("Current files require repair before update");
        if (Directory.Exists(UpdateDirectory)) throw new IOException("Update state requires inspection");
        var snapshots = new Dictionary<string, string>();
        var running = policy.Credentials.Keys.Where(platform.Running).ToHashSet();
        var startup = platform.CaptureStartup(policy);
        if (startup.Count == policy.Credentials.Count)
        {
            var automatic = startup.Where(p => p.Value is "automatic" or "delayed-auto" or "enabled" or "enabled-runtime").Select(p => p.Key).ToHashSet();
            policy = policy with { StartupModes = startup, AutoStart = automatic.Count > 0, AutoStartKinds = automatic };
        }
        void Snapshot(string source, string name, int maximum)
        {
            var info = new FileInfo(source);
            if (info.Length > maximum || Linked(source)) throw new IOException("Invalid update snapshot source");
            byte[] bytes = File.ReadAllBytes(source);
            GuardPaths.WriteBytes(Path.Combine(UpdateDirectory, "snapshots", name), bytes);
            snapshots[name] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        try
        {
            foreach (var file in policy.ConfigHashes.Where(p => p.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                Snapshot(Path.Combine(root, "config-backups", file.Key), "configs/" + file.Key, 1048576);
            foreach (var credential in policy.Credentials.Where(p => File.Exists(p.Value)))
                Snapshot(credential.Value, "credentials/" + credential.Key + ".json", 65536);
            foreach (string name in new[] { "launcher", "recover.ps1", "protect.ps1", "recover.sh" })
                if (File.Exists(Path.Combine(root, name))) Snapshot(Path.Combine(root, name), "helper/" + name, 1048576);
            var state = new GuardUpdateJournal(1, grant, policy, previous, running, startup, snapshots);
            var sender = JsonSerializer.Deserialize<StoredCredential>(SnapshotBytes(state, "credentials/" + grant.Kind + ".json"));
            if (sender is null || sender.Kind != grant.Kind || string.IsNullOrEmpty(sender.Secret) || MaintenanceSigning.Binding(sender.Secret) != grant.Binding)
                throw new CryptographicException("Update identity changed before preparation");
            SaveUpdate(state); return state;
        }
        catch
        {
            // Установщик ещё не запускался; удаляем только созданную здесь подготовку.
            if (Directory.Exists(UpdateDirectory)) Directory.Delete(UpdateDirectory, true);
            throw;
        }
    }
    private byte[] SnapshotBytes(GuardUpdateJournal state, string name)
    {
        if (!state.Snapshots.TryGetValue(name, out string? expected)) throw new IOException("Update snapshot missing");
        string path = Path.Combine(UpdateDirectory, "snapshots", name);
        if (!File.Exists(path) || Linked(path) || new FileInfo(path).Length > 1048576) throw new IOException("Update snapshot missing");
        byte[] bytes = File.ReadAllBytes(path);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expected, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("Update snapshot integrity failure");
        return bytes;
    }
    private void RestoreUpdateSnapshots(GuardUpdateJournal state)
    {
        foreach (var file in state.Before.ConfigHashes.Where(p => p.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            byte[] bytes = SnapshotBytes(state, "configs/" + file.Key);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), file.Value, StringComparison.OrdinalIgnoreCase)) throw new IOException("Update configuration snapshot mismatch");
            GuardPaths.WriteBytes(Path.Combine(root, "config-backups", file.Key), bytes);
            GuardPaths.WriteBytes(Path.Combine(state.Before.InstallDir, file.Key), bytes, readableByUsers: GuardPaths.PublicConfiguration(file.Key));
        }
        foreach (var credential in state.Before.Credentials)
            if (state.Snapshots.ContainsKey("credentials/" + credential.Key + ".json"))
                GuardPaths.WriteBytes(credential.Value, SnapshotBytes(state, "credentials/" + credential.Key + ".json"));
        foreach (string name in new[] { "launcher", "recover.ps1", "protect.ps1", "recover.sh" })
            if (state.Snapshots.ContainsKey("helper/" + name)) GuardPaths.WriteBytes(Path.Combine(root, name), SnapshotBytes(state, "helper/" + name));
        // На Linux recover.sh должен остаться исполняемым после атомарной замены.
        if (!OperatingSystem.IsWindows() && state.Snapshots.ContainsKey("helper/recover.sh"))
            File.SetUnixFileMode(Path.Combine(root, "recover.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private void VerifyIdentities(GuardUpdateJournal state)
    {
        foreach (var credential in state.Before.Credentials)
            if (state.Snapshots.TryGetValue("credentials/" + credential.Key + ".json", out string? hash)
                && (!File.Exists(credential.Value) || Hash(credential.Value) != hash)) throw new IOException("Update changed an existing identity");
    }
    private void RestoreUpdateRuntime(GuardUpdateJournal state, GuardPolicy policy)
    {
        foreach (string kind in policy.Credentials.Keys) platform.EnsureRegistered(kind, policy);
        platform.RestoreStartup(policy, state.Startup);
        var expectedRunning = new HashSet<string>();
        foreach (string kind in policy.Credentials.Keys)
        {
            bool shouldRun = state.Startup.TryGetValue(kind, out string? mode) ? mode switch
            {
                "disabled" => OperatingSystem.IsWindows() ? false : state.Running.Contains(kind),
                "manual" => state.Running.Contains(kind),
                _ => true
            } : state.Running.Contains(kind) || (policy.AutoStartKinds?.Contains(kind) ?? policy.AutoStart);
            if (shouldRun) expectedRunning.Add(kind);
            if (shouldRun && !platform.Running(kind)) platform.Start(kind, policy);
            if (!shouldRun && platform.Running(kind)) platform.Stop(kind, policy);
            if (platform.Running(kind) != shouldRun) throw new IOException("Updated service did not reach expected state");
        }
        platform.VerifyRuntime(policy, expectedRunning);
        if (!Healthy(policy)) throw new IOException("Updated files did not pass verification");
    }
    // 0.16: понижение версии целью группы. MSI 0.16+ сами допускают понижение (одна
    // транзакция Windows Installer), deb — dpkg тоже. Через удаление и установку
    // (тот же путь, что у автоматического отката) — только где пакет так не может:
    // MSI 0.15.x (MajorUpgrade с запретом) и rpm (нужен --oldpackage).
    public static readonly Version MsiDowngradeAllowedSince = new(0, 16, 0);
    public static bool ReinstallForDowngrade(string platform, string? target, string current) =>
        Version.TryParse(target, out var wanted) && Version.TryParse(current, out var installed) && wanted < installed
        && (platform == "linux-rpm" || platform == "windows-msi" && wanted < MsiDowngradeAllowedSince);

    // 0.16: защита, включённая обновлением с 0.15.x, остаётся без базы (их загрузчик не
    // сохранял подпись пакета), а обновление без базы отката не начинается. Сервер в
    // ответ на отказ base-unavailable присылает пакет ТЕКУЩЕЙ версии: подписанный пакет
    // ровно установленной версии закрепляется базой без переустановки.
    private bool SeedBaseFromGrant(GuardPolicy policy, MaintenanceGrant grant)
    {
        if (policy.LastVerifiedPackage is not null || policy.InitialPackage is not null) return false;
        string legacy = Path.Combine(root, "cache", "package.json");
        if (File.Exists(legacy)) return false;
        string? installed = platform.InstalledVersion(policy);
        if (installed is null || installed != grant.Version) return false;
        GuardUpdateProgress.TryPublish(root, grant, "verifying-package", installed, Now);
        _ = VerifiedPackage(policy, grant);
        GuardPaths.Write(Path.Combine(root, "policy.json"), policy with { InitialPackage = PackageDescriptor(grant) });
        GuardUpdateProgress.TryPublish(root, grant, "completed", installed, Now);
        return true;
    }

    private void ExecuteUpdate(GuardPolicy policy, MaintenanceGrant grant)
    {
        if (SeedBaseFromGrant(policy, grant)) return;
        string? from = policy.LastVerifiedPackage?.Version ?? policy.InitialPackage?.Version;
        string package; GuardUpdateJournal state;
        try
        {
            GuardUpdateProgress.TryPublish(root, grant, "verifying-package", from, Now);
            package = VerifiedPackage(policy, grant);
            GuardUpdateProgress.TryPublish(root, grant, "preparing", from, Now);
            state = PrepareUpdate(policy, grant);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException or ArgumentException or JsonException)
        {
            var failure = GuardUpdateProgress.Failure(ex, "preparing");
            GuardUpdateProgress.TryPublish(root, grant, "failed", from, Now, failure.Reason, errorCode: failure.Code);
            throw;
        }
        policy = state.Before;
        try
        {
            GuardPaths.Write(Path.Combine(root, "policy.json"), policy);
            GuardUpdateProgress.TryPublish(root, grant, "installing", state.Previous.Version, Now);
            if (ReinstallForDowngrade(grant.Platform!, grant.Version, state.Previous.Version))
            {
                foreach (string kind in policy.Credentials.Keys) if (platform.Running(kind)) platform.Stop(kind, policy);
                platform.Rollback(package, grant.Platform!, policy);
            }
            else platform.Install(package, grant.Platform!, false, downgrade: Version.TryParse(grant.Version, out var wanted) && Version.TryParse(state.Previous.Version, out var installed) && wanted < installed);
            RestoreConfigs(policy);
            var refreshed = policy.ConfigHashes.ToDictionary(p => p.Key, p => p.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? p.Value : Hash(Path.Combine(policy.InstallDir, p.Key)));
            var updated = policy with { ConfigHashes = refreshed, LastVerifiedPackage = grant, InitialPackage = null, PreviousPackage = state.Previous };
            GuardUpdateProgress.TryPublish(root, grant, "checking", state.Previous.Version, Now);
            VerifyIdentities(state);
            RestoreUpdateRuntime(state, updated);
            // Единственная точка commit. Recovery узнаёт её даже при обрыве до cleanup.
            GuardPaths.Write(Path.Combine(root, "policy.json"), updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException or ArgumentException or JsonException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            var failure = GuardUpdateProgress.Failure(ex, "installing");
            state = state with { FailureReason = failure.Reason, FailureCode = failure.Code }; SaveUpdate(state);
            GuardPolicy before = policy;
            RecoverUpdate(ref before, force: true);
            throw;
        }
        // Ошибка housekeeping после commit не запускает откат принятой версии.
        FinishCommittedUpdate(state, GuardPaths.Load(root)!);
    }
    private bool RecoverUpdate(ref GuardPolicy policy, bool force = false)
    {
        var state = ReadUpdate(); if (state is null) { TryPrunePackageCache(policy); return true; }
        if (policy.LastVerifiedPackage?.Id == state.Grant.Id)
        {
            bool finished = FinishCommittedUpdate(state, policy); Report(state.Grant, "completed", "completed"); return finished;
        }
        if (state.Phase == "rolled-back") return FinishRolledBackUpdate(state, policy);
        if (state.FailureReason is null && state.Phase == "installing")
        { state = state with { FailureReason = "process-interrupted" }; SaveUpdate(state); }
        if (!force && state.NextRetryAt > Now && state.NextRetryAt - Now <= TimeSpan.FromMinutes(30)) return false;
        try
        {
            string package = GuardRecoveryCache.Verify(root, state.Previous, state.Before.UpdatePublicKey);
            foreach (string name in state.Snapshots.Keys) _ = SnapshotBytes(state, name);
            GuardPaths.Write(Path.Combine(root, "policy.json"), state.Before);
            policy = state.Before;
            GuardPaths.Write(Path.Combine(root, "internal-repair.json"), new GuardRetry(0, Now.AddMinutes(10)));
            try
            {
                if (state.Phase != "restoring")
                {
                    state = state with { Phase = "rolling-back" }; SaveUpdate(state);
                    GuardUpdateProgress.TryPublish(root, state.Grant, "rolling-back", state.Previous.Version, Now, state.FailureReason, "pending", errorCode: state.FailureCode);
                    foreach (string kind in policy.Credentials.Keys) if (platform.Running(kind)) platform.Stop(kind, policy);
                    RestoreUpdateSnapshots(state);
                    platform.Rollback(package, state.Previous.Platform, policy);
                    state = state with { Phase = "restoring" }; SaveUpdate(state);
                    GuardUpdateProgress.TryPublish(root, state.Grant, "restoring", state.Previous.Version, Now, state.FailureReason, "pending", errorCode: state.FailureCode);
                }
                RestoreUpdateSnapshots(state);
                if (!Healthy(policy)) { state = state with { Phase = "rolling-back" }; SaveUpdate(state); throw new IOException("Rollback files do not match previous policy"); }
                RestoreUpdateRuntime(state, policy); VerifyIdentities(state);
            }
            finally { File.Delete(Path.Combine(root, "internal-repair.json")); }
            state = state with { Phase = "rolled-back" }; SaveUpdate(state);
            return FinishRolledBackUpdate(state, policy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException or JsonException or ArgumentException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            // Повторяется без срока lease; максимум 30 минут между попытками.
            int attempts = Math.Min(state.Attempts + 1, 1000000);
            SaveUpdate(state with { Attempts = attempts, NextRetryAt = Now.AddMinutes(attempts == 1 ? 1 : attempts == 2 ? 5 : 30) });
            var failure = GuardUpdateProgress.Failure(ex, "rolling-back");
            GuardUpdateProgress.TryPublish(root, state.Grant, "rollback-pending", state.Previous.Version, Now, state.FailureReason, "pending", failure.Reason, state.FailureCode, failure.Code);
            WriteUpdateOutcome(state, "rollback-pending");
            Report(state.Grant, "accepted", "accepted");
            DiagnosticJournal.At(Path.Combine(root, "recovery-diagnostics.json")).Record("recovery", "repair-failed", true, Now);
            return false;
        }
    }
    private bool FinishRolledBackUpdate(GuardUpdateJournal state, GuardPolicy policy)
    {
        Report(state.Grant, "failed", "operation-failed");
        return FinishUpdate(state, policy, committed: false);
    }
    private bool FinishCommittedUpdate(GuardUpdateJournal state, GuardPolicy policy) => FinishUpdate(state, policy, committed: true);
    private bool FinishUpdate(GuardUpdateJournal state, GuardPolicy policy, bool committed)
    {
        try
        {
            GuardUpdateProgress.TryPublish(root, state.Grant, committed ? "completed" : "rolled-back", state.Previous.Version, Now,
                state.FailureReason, committed ? null : "succeeded", errorCode: state.FailureCode);
            WriteUpdateOutcome(state, committed ? "committed" : "rolled-back");
            File.Delete(Path.Combine(root, "active.json"));
            File.Delete(Path.Combine(root, "retry.json"));
            File.Delete(Path.Combine(root, "inbox", state.Grant.Id + ".json"));
            File.Delete(Path.Combine(root, "inbox", state.Grant.Id + ".package"));
            string destination = Path.Combine(root, committed ? "previous-update" : "finished-update");
            if (Directory.Exists(destination)) { if (Linked(destination)) throw new IOException("Linked update archive"); Directory.Delete(destination, true); }
            // Архив предыдущей исправной версии не ссылается на удаляемую прабазу.
            if (committed) SaveUpdate(state with { Before = state.Before with { PreviousPackage = null } });
            // После атомарного rename эта операция больше не требует восстановления.
            Directory.Move(UpdateDirectory, destination);
            if (!committed) { try { Directory.Delete(destination, true); } catch (IOException) { } }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return !File.Exists(UpdateJournalPath); }
        TryPrunePackageCache(policy);
        return true;
    }
    private void TryPrunePackageCache(GuardPolicy policy)
    {
        try { PrunePackageCache(policy); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { DiagnosticJournal.At(Path.Combine(root, "recovery-diagnostics.json")).Record("recovery", "repair-failed", true, Now); }
    }
    private void WriteUpdateOutcome(GuardUpdateJournal state, string outcome) => GuardPaths.Write(Path.Combine(root, "last-update.json"),
        new { grantId = state.Grant.Id, fromVersion = state.Previous.Version, toVersion = state.Grant.Version, outcome, reason = state.FailureReason, errorCode = state.FailureCode, at = Now });
    private void PrunePackageCache(GuardPolicy policy)
    {
        if (File.Exists(UpdateJournalPath)) return;
        using var cacheGate = new FileStream(Path.Combine(root, "cache.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // Downloader публикует файл и inbox под тем же lock. Не удаляем queued package.
        string inbox = Path.Combine(root, "inbox");
        if (Directory.Exists(inbox) && Directory.EnumerateFiles(inbox, "*.json").Any()) return;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (policy.LastVerifiedPackage is { } last) keep.Add(GuardRecoveryCache.PackagePath(root, PackageDescriptor(last)));
        else if (policy.InitialPackage is { } initial) keep.Add(GuardRecoveryCache.PackagePath(root, initial));
        else { try { keep.Add(GuardRecoveryCache.PackagePath(root, CurrentPackage(policy))); } catch (IOException) { return; } }
        if (policy.PreviousPackage is { } previous) keep.Add(GuardRecoveryCache.PackagePath(root, previous));
        string directory = Path.Combine(root, "cache"); if (!Directory.Exists(directory) || Linked(directory)) return;
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.Length == 64 && name.All(char.IsAsciiHexDigit) && Path.GetExtension(path) is ".msi" or ".deb" or ".rpm" or ".zip"
                && !keep.Contains(path) && !Linked(path)) File.Delete(path);
        }
        PruneTemporaryPackages(directory);
        PruneHelpers();
    }
    private void PruneTemporaryPackages(string directory)
    {
        foreach (string candidate in Directory.EnumerateFiles(directory, "*.tmp*"))
        {
            string part = candidate.EndsWith(".tmp.lock", StringComparison.Ordinal) ? candidate[..^5] : candidate;
            string name = Path.GetFileName(part);
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[0-9a-f]{64}(?:\.(?:msi|deb|rpm|zip))?(?:\.[0-9a-f]{32})?\.tmp$")) continue;
            string leasePath = part + ".lock";
            string timestamp = File.Exists(part) ? part : leasePath;
            if (!File.Exists(timestamp) || Linked(timestamp) || File.GetLastWriteTimeUtc(timestamp) > Now.AddHours(-1)
                || File.Exists(leasePath) && Linked(leasePath)) continue;
            try
            {
                using (var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    // Downloader держит этот lock от HTTP-запроса до публикации inbox.
                    if (File.Exists(part) && !Linked(part)) File.Delete(part);
                }
                File.Delete(leasePath);
            }
            catch (IOException) { } // Активный downloader/открытый файл не удаляется.
        }
    }
    private void PruneHelpers()
    {
        string bin = Path.Combine(root, "bin"); if (!Directory.Exists(bin) || Linked(bin)) return;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void KeepLauncher(string path)
        {
            if (!File.Exists(path) || Linked(path) || new FileInfo(path).Length > 80) return;
            string hash = File.ReadAllText(path).Trim();
            if (hash.Length == 64 && hash.All(char.IsAsciiHexDigit)) keep.Add(hash);
        }
        KeepLauncher(Path.Combine(root, "launcher"));
        string previous = Path.Combine(root, "previous-update");
        if (Directory.Exists(previous) && !Linked(previous)) KeepLauncher(Path.Combine(previous, "snapshots", "helper", "launcher"));
        string? executing = Path.GetDirectoryName(Environment.ProcessPath);
        if (executing is not null && string.Equals(Path.GetDirectoryName(executing), bin, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            keep.Add(Path.GetFileName(executing));
        foreach (string directory in Directory.EnumerateDirectories(bin))
        {
            string hash = Path.GetFileName(directory);
            if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit) || keep.Contains(hash) || Linked(directory) || Directory.EnumerateDirectories(directory).Any()) continue;
            var files = Directory.GetFiles(directory);
            if (files.Any(f => Path.GetFileName(f) is not ("GuardRepair.exe" or "Watcher.Guard.exe" or "GuardRepair" or "Watcher.Guard") || Linked(f))) continue;
            foreach (string file in files) File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
