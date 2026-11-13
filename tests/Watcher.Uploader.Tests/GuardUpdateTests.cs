using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Watcher;
using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

public sealed class GuardUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "watcher-update-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private DateTime _now = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);
    private readonly GuardPolicy _before;
    private readonly byte[] _config = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("{\r\n  \"operator\":true\r\n}")];
    private readonly byte[] _identity;
    private readonly Platform _platform;
    public GuardUpdateTests()
    {
        string directory = Path.Combine(_root, "program"); Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "agent"), [1]); File.WriteAllBytes(Path.Combine(directory, "settings.json"), _config);
        string credential = Path.Combine(_root, "identity.json");
        _identity = JsonSerializer.SerializeToUtf8Bytes(new StoredCredential("test-secret-not-production", "same-host", "video", "hash", _now));
        File.WriteAllBytes(credential, _identity);
        string key = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
        _before = new(directory, key, key, new() { ["video"] = credential }, new() { ["agent"] = Hash([1]), ["settings.json"] = Hash(_config) });
        GuardPaths.Write(Path.Combine(_root, "policy.json"), _before);
        GuardPaths.WriteBytes(Path.Combine(_root, "config-backups", "settings.json"), _config);
        GuardPaths.WriteBytes(Path.Combine(_root, "enabled"), [1]);
        GuardRecoveryCache.Seed(_root, Descriptor([1], "0.15.1"), new MemoryStream([1]));
        Directory.CreateDirectory(Path.Combine(_root, "queue")); File.WriteAllBytes(Path.Combine(_root, "queue", "recording"), [9, 9, 9]);
        _platform = new(_root, directory); _platform.Helper([1]);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private GuardRecoveryPackage Descriptor(byte[] bytes, string version) => new("linux-deb", version, Hash(bytes), bytes.Length,
        Convert.ToBase64String(_key.SignData(UpdateSigning.Message("linux-deb", version, Hash(bytes)), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));
    private SignedMaintenanceGrant Queue(byte value = 2, bool cached = false)
    {
        var package = Descriptor([value], "0.15." + value);
        var grant = MaintenanceSigning.Sign(new(Guid.NewGuid().ToString("N"), MaintenanceSigning.Binding("test-secret-not-production"), "video", "update", _now, _now.AddMinutes(15),
            package.Version, package.Sha256, package.Platform, package.Signature, PackageId: value, PackageSize: 1), _key);
        if (cached) GuardRecoveryCache.Store(_root, package, new MemoryStream([value]), _before.UpdatePublicKey);
        GuardPaths.Queue(grant, _root);
        if (!cached) File.WriteAllBytes(Path.Combine(_root, "inbox", grant.Grant.Id + ".package"), [value]);
        return grant;
    }
    private GuardPolicy Policy() => GuardPaths.Load(_root)!;
    private UpdateProgressReport Progress(string id) => GuardUpdateProgress.Pending(_root, MaintenanceSigning.Binding("test-secret-not-production"), "video").Single(p => p.GrantId == id);
    private void Tick() => new GuardEngine(_root, _platform, () => _now).Tick();
    private bool Pending => File.Exists(Path.Combine(_root, "update-state", "transaction.json"));
    private GuardExecution Result(string id) => JsonSerializer.Deserialize<GuardExecution>(File.ReadAllBytes(Path.Combine(_root, "results", id + ".json")), GuardPaths.Json)!;
    private sealed class Crash : Exception { }
    private sealed class Platform(string root, string directory) : IGuardPlatform
    {
        public int Installs, Rollbacks; public bool Active = true;
        public bool FailInstall, FailNewStart, FailNewRegistration, ChangeIdentity, FailRollback, CrashInstall, CrashRollback, FailStartup;
        public string Mode = "enabled"; public string? NativeError;
        public Action? DuringRollback, DuringCapture;
        private byte Version => File.ReadAllBytes(Path.Combine(directory, "agent"))[0];
        public void Helper(byte[] bytes)
        {
            string hash = Hash(bytes); string bin = Path.Combine(root, "bin", hash); Directory.CreateDirectory(bin);
            File.WriteAllBytes(Path.Combine(bin, "GuardRepair.exe"), bytes);
            File.WriteAllText(Path.Combine(root, "launcher"), hash);
        }
        public bool Running(string kind) => Active;
        public void Start(string kind, GuardPolicy policy) => Active = !FailNewStart || Version == 1;
        public void Stop(string kind, GuardPolicy policy) => Active = false;
        public void EnsureRegistered(string kind, GuardPolicy policy) { if (FailNewRegistration && Version != 1) throw new IOException("registration failed"); }
        public Dictionary<string, string> CaptureStartup(GuardPolicy policy) { DuringCapture?.Invoke(); return new() { ["video"] = Mode }; }
        public void RestoreStartup(GuardPolicy policy, Dictionary<string, string> modes) { if (FailStartup) throw new IOException("startup failed"); Mode = modes["video"]; }
        public bool LastDowngrade;
        public void Install(string package, string platform, bool repair, bool downgrade = false)
        {
            Installs++; Active = false; LastDowngrade = downgrade;
            byte[] bytes = File.ReadAllBytes(package); File.WriteAllBytes(Path.Combine(directory, "agent"), bytes); Helper(bytes);
            File.WriteAllText(Path.Combine(directory, "settings.json"), "{}");
            if (ChangeIdentity) File.WriteAllText(Path.Combine(root, "identity.json"), "changed");
            if (CrashInstall) throw new Crash();
            if (NativeError == "win32") throw new System.ComponentModel.Win32Exception(2);
            if (NativeError == "timeout") throw new TimeoutException();
            if (FailInstall) throw new IOException("installer failed");
        }
        public void Rollback(string package, string platform, GuardPolicy policy)
        {
            Rollbacks++; DuringRollback?.Invoke();
            if (CrashRollback) throw new Crash();
            if (FailRollback) throw new IOException("rollback failed");
            Active = false; File.WriteAllBytes(Path.Combine(directory, "agent"), File.ReadAllBytes(package)); Helper([7]);
        }
        public void Uninstall(string kind, GuardPolicy policy) { }
        public string? InstalledVersion(GuardPolicy policy) => "0.15." + Version;
    }
    public void Dispose() { _key.Dispose(); Directory.Delete(_root, true); }
    private void AssertOld()
    {
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(_before.InstallDir, "agent")));
        Assert.Equal(_config, File.ReadAllBytes(Path.Combine(_before.InstallDir, "settings.json")));
        Assert.Equal(_identity, File.ReadAllBytes(_before.Credentials["video"]));
        Assert.Equal(Hash([1]), File.ReadAllText(Path.Combine(_root, "launcher")));
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(Path.Combine(_root, "queue", "recording")));
        Assert.Null(Policy().LastVerifiedPackage); Assert.NotNull(Policy().InitialPackage);
    }
    [Theory]
    [InlineData("install")]
    [InlineData("start")]
    [InlineData("registration")]
    [InlineData("identity")]
    public void Failed_update_returns_verified_package_exact_config_identity_and_helper(string failure)
    {
        _platform.FailInstall = failure == "install"; _platform.FailNewStart = failure == "start";
        _platform.FailNewRegistration = failure == "registration"; _platform.ChangeIdentity = failure == "identity";
        var grant = Queue(); Tick();
        AssertOld(); Assert.True(_platform.Active); Assert.Equal(1, _platform.Rollbacks); Assert.False(Pending);
        Assert.Equal("failed", Result(grant.Grant.Id).Status);
        Assert.Contains("rolled-back", File.ReadAllText(Path.Combine(_root, "last-update.json")));
    }
    [Fact]
    public void Interrupted_update_recovers_before_expired_lease_and_clock_check_without_replay()
    {
        var grant = Queue(cached: true); _platform.CrashInstall = true;
        Assert.Throws<Crash>(Tick); Assert.True(Pending);
        Assert.Equal("installing", Progress(grant.Grant.Id).Stage);
        _now = _now.AddHours(3); _platform.CrashInstall = false;
        _platform.DuringRollback = () => Assert.True(GuardAuthorization.Allows("uninstall", _now, _root));
        Tick(); AssertOld(); Assert.Equal(1, _platform.Installs); Assert.Equal(1, _platform.Rollbacks);
        Assert.Equal("failed", Result(grant.Grant.Id).Status); Assert.False(Pending);
        Tick(); Assert.Equal(1, _platform.Installs);
        var progress = Progress(grant.Grant.Id); Assert.Equal("rolled-back", progress.Stage);
        Assert.Equal("process-interrupted", progress.Reason); Assert.Equal("succeeded", progress.Rollback);
    }
    [Fact]
    public void Pending_rollback_survives_failure_reboot_backoff_and_blocks_new_commands()
    {
        var grant = Queue(); _platform.FailInstall = _platform.FailRollback = true; Tick();
        Assert.True(Pending); Assert.Equal("accepted", Result(grant.Grant.Id).Status);
        int rollbacks = _platform.Rollbacks;
        _now = _now.AddSeconds(30); Queue(3); Tick(); Assert.Equal(rollbacks, _platform.Rollbacks); Assert.Equal(1, _platform.Installs);
        _now = _now.AddHours(3); _platform.FailInstall = _platform.FailRollback = false;
        Tick(); Assert.False(Pending); Assert.Equal(1, _platform.Installs); AssertOld();
    }
    [Fact]
    public void Process_exit_during_rollback_is_resumed_without_reinstalling_candidate()
    {
        Queue(); _platform.FailInstall = _platform.CrashRollback = true;
        Assert.Throws<Crash>(Tick); Assert.True(Pending);
        _platform.CrashRollback = false; _now = _now.AddHours(2); Tick();
        AssertOld(); Assert.False(Pending); Assert.Equal(1, _platform.Installs); Assert.Equal(2, _platform.Rollbacks);
    }
    [Fact]
    public void Startup_restore_retry_does_not_reinstall_old_package_repeatedly()
    {
        Queue(); _platform.FailInstall = _platform.FailStartup = true; Tick(); Assert.True(Pending);
        Assert.Equal(1, _platform.Rollbacks); _platform.FailStartup = false; _now = _now.AddMinutes(2); Tick();
        AssertOld(); Assert.False(Pending); Assert.Equal(1, _platform.Rollbacks);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Manual_startup_and_running_state_survive_rollback(bool running)
    {
        GuardPaths.Write(Path.Combine(_root, "policy.json"), Policy() with { AutoStart = false });
        _platform.Active = running; _platform.Mode = "manual"; _platform.FailInstall = true;
        Queue(); Tick(); AssertOld(); Assert.Equal(running, _platform.Active); Assert.Equal("manual", _platform.Mode);
    }
    [Fact]
    public void Missing_or_tampered_previous_package_refuses_install()
    {
        File.WriteAllBytes(GuardRecoveryCache.PackagePath(_root, Policy().InitialPackage!), [9]);
        Queue(); Tick(); Assert.Equal(0, _platform.Installs); Assert.False(Pending);
    }
    [Fact]
    public void Successful_updates_keep_current_and_previous_and_preserve_unrelated_cache_files()
    {
        Directory.CreateDirectory(Path.Combine(_root, "cache"));
        File.WriteAllText(Path.Combine(_root, "cache", "operator-note"), "keep");
        Queue(cached: true); Tick(); Assert.Equal("0.15.2", Policy().LastVerifiedPackage!.Version); Assert.Equal("0.15.1", Policy().PreviousPackage!.Version);
        Assert.Null(Policy().InitialPackage);
        Queue(3, cached: true); Tick(); Assert.Equal("0.15.3", Policy().LastVerifiedPackage!.Version); Assert.Equal("0.15.2", Policy().PreviousPackage!.Version);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "cache"), "*.deb").Length);
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(_root, "bin")).Length);
        Assert.True(File.Exists(Path.Combine(_root, "cache", "operator-note"))); Assert.False(Pending);
    }
    [Fact]
    public void Group_target_downgrade_installs_older_cached_package_and_commits()
    {
        Queue(cached: true); Tick(); Assert.Equal("0.15.2", Policy().LastVerifiedPackage!.Version);
        // Цель группы вернули на 0.15.1: пакет есть в кэше Guard (PreviousPackage).
        var back = Descriptor([1], "0.15.1");
        var grant = MaintenanceSigning.Sign(new(Guid.NewGuid().ToString("N"), MaintenanceSigning.Binding("test-secret-not-production"), "video", "update", _now, _now.AddMinutes(15),
            back.Version, back.Sha256, back.Platform, back.Signature, PackageId: 1, PackageSize: 1), _key);
        GuardPaths.Queue(grant, _root); Tick();
        Assert.Equal("completed", Result(grant.Grant.Id).Status);
        Assert.Equal("0.15.1", Policy().LastVerifiedPackage!.Version); Assert.Equal("0.15.2", Policy().PreviousPackage!.Version);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(_before.InstallDir, "agent")));
        // deb понижается обычной установкой (dpkg), без удаления.
        Assert.Equal(2, _platform.Installs); Assert.Equal(0, _platform.Rollbacks); Assert.False(Pending);
        Assert.True(_platform.LastDowngrade);
    }
    [Theory]
    [InlineData("windows-msi", "0.16.0", "0.16.1", false)]  // MSI понижается сам
    [InlineData("linux-rpm", "0.16.0", "0.16.1", true)]     // rpm без --oldpackage не понижает
    [InlineData("linux-deb", "0.15.1", "0.16.1", false)]
    [InlineData("windows-msi", "0.16.2", "0.16.1", false)]  // не понижение
    public void Reinstall_is_used_only_where_the_package_cannot_downgrade(string platform, string target, string current, bool reinstall) =>
        Assert.Equal(reinstall, GuardEngine.ReinstallForDowngrade(platform, target, current));
    [Fact]
    public void Grant_for_installed_version_seeds_missing_base_without_install()
    {
        // Защиту включило обновление с 0.15.x: базы нет, обновление отклоняется.
        GuardPaths.Write(Path.Combine(_root, "policy.json"), Policy() with { InitialPackage = null });
        var update = Queue(2, cached: true); Tick();
        Assert.Equal("failed", Result(update.Grant.Id).Status);
        Assert.Equal("base-unavailable", Progress(update.Grant.Id).Reason);
        Assert.Equal(0, _platform.Installs);
        // Сервер прислал пакет текущей версии (0.15.1) — он становится базой, установки нет.
        var current = Descriptor([1], "0.15.1");
        var seed = MaintenanceSigning.Sign(new(Guid.NewGuid().ToString("N"), MaintenanceSigning.Binding("test-secret-not-production"), "video", "update", _now, _now.AddMinutes(15),
            current.Version, current.Sha256, current.Platform, current.Signature, PackageId: 1, PackageSize: 1), _key);
        GuardPaths.Queue(seed, _root); Tick();
        Assert.Equal("completed", Result(seed.Grant.Id).Status);
        Assert.Equal("0.15.1", Policy().InitialPackage!.Version); Assert.Equal(0, _platform.Installs); Assert.False(Pending);
        // Теперь обновление проходит.
        var again = Queue(2, cached: true); Tick();
        Assert.Equal("completed", Result(again.Grant.Id).Status); Assert.Equal("0.15.2", Policy().LastVerifiedPackage!.Version);
    }
    [Fact]
    public void Grant_for_other_version_never_becomes_base()
    {
        GuardPaths.Write(Path.Combine(_root, "policy.json"), Policy() with { InitialPackage = null });
        var update = Queue(3, cached: true); Tick();
        Assert.Equal("failed", Result(update.Grant.Id).Status);
        Assert.Null(Policy().InitialPackage); Assert.Equal(0, _platform.Installs);
    }
    [Fact]
    public void Grant_handed_to_guard_is_not_downloaded_again()
    {
        // Сервер повторяет заявку, пока Guard её не принял: повторная загрузка ломала установку.
        var grant = Queue(2, cached: true);
        Assert.True(DiagnosticClient.HandedToGuard(_root, grant.Grant.Id));
        Assert.False(DiagnosticClient.HandedToGuard(_root, Guid.NewGuid().ToString("N")));
        File.Delete(Path.Combine(_root, "inbox", grant.Grant.Id + ".json"));
        GuardPaths.Write(Path.Combine(_root, "results", grant.Grant.Id + ".json"), new GuardExecution(grant.Grant.Id, "b", "video", "update", "accepted", "accepted", _now));
        Assert.True(DiagnosticClient.HandedToGuard(_root, grant.Grant.Id));
    }
    [Fact]
    public void Backward_clock_does_not_postpone_recovery_of_interrupted_update()
    {
        Queue(); _platform.CrashInstall = true; Assert.Throws<Crash>(Tick);
        _now = _now.AddHours(-3); _platform.CrashInstall = false;
        Tick(); AssertOld(); Assert.False(Pending); Assert.Equal(1, _platform.Rollbacks);
    }
    [Fact]
    public void Corrupt_snapshot_keeps_recovery_pending_without_executing_unverified_rollback()
    {
        Queue(); _platform.CrashInstall = true; Assert.Throws<Crash>(Tick);
        File.WriteAllText(Path.Combine(_root, "update-state", "snapshots", "credentials", "video.json"), "tampered");
        _now = _now.AddHours(3); Tick(); Assert.True(Pending); Assert.Equal(0, _platform.Rollbacks);
    }
    [Fact]
    public void Disabled_stopped_service_stays_stopped_during_rollback()
    {
        GuardPaths.Write(Path.Combine(_root, "policy.json"), Policy() with { AutoStart = false });
        _platform.Active = false; _platform.Mode = "disabled"; _platform.FailInstall = true;
        Queue(); Tick(); AssertOld(); Assert.False(_platform.Active); Assert.Equal("disabled", _platform.Mode);
    }
    [Fact]
    public void Cleanup_failure_after_commit_does_not_downgrade_or_report_update_as_failed()
    {
        if (OperatingSystem.IsWindows()) return;
        string foreign = Path.Combine(_root, "foreign"); Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "keep"), "unrelated");
        Directory.CreateSymbolicLink(Path.Combine(_root, "previous-update"), foreign);
        var grant = Queue(); Tick();
        Assert.Equal(grant.Grant.Id, Policy().LastVerifiedPackage!.Id); Assert.True(Pending);
        Assert.Equal(0, _platform.Rollbacks); Assert.Equal("completed", Result(grant.Grant.Id).Status);
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(foreign, "keep")));
        Directory.Delete(Path.Combine(_root, "previous-update")); Tick();
        Assert.False(Pending); Assert.Equal(0, _platform.Rollbacks);
    }
    [Fact]
    public void Garbage_collection_waits_for_cache_publication_lock()
    {
        string garbage = Path.Combine(_root, "cache", Hash([8]) + ".deb"); File.WriteAllBytes(garbage, [8]);
        using (var gate = new FileStream(Path.Combine(_root, "cache.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        { Tick(); Assert.True(File.Exists(garbage)); }
        Tick(); Assert.False(File.Exists(garbage));
    }
    [Fact]
    public void Ordinary_repair_restores_remembered_disabled_mode_after_missing_program()
    {
        GuardPaths.Write(Path.Combine(_root, "policy.json"), Policy() with { AutoStart = false, AutoStartKinds = [], StartupModes = new() { ["video"] = "disabled" } });
        _platform.Active = false; _platform.Mode = "enabled";
        File.Delete(Path.Combine(_before.InstallDir, "agent")); Tick();
        Assert.Equal("disabled", _platform.Mode); Assert.False(_platform.Active); Assert.Equal(1, _platform.Installs);
    }
    [Fact]
    public void No_previous_base_refuses_update_before_touching_program()
    {
        GuardPaths.Write(Path.Combine(_root, "policy.json"), Policy() with { InitialPackage = null });
        Queue(); Tick(); Assert.Equal(0, _platform.Installs); Assert.False(Pending);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(_before.InstallDir, "agent")));
    }
    [Fact]
    public void Identity_changed_during_preparation_cannot_authorize_update_for_new_binding()
    {
        _platform.DuringCapture = () => File.WriteAllBytes(_before.Credentials["video"], JsonSerializer.SerializeToUtf8Bytes(new StoredCredential("rotated-test-secret", "host", "video", "hash", _now)));
        Queue(); Tick(); Assert.Equal(0, _platform.Installs); Assert.False(Pending);
    }
    [Fact]
    public void Baseline_predating_journal_aware_helper_is_not_reinstalled_after_interruption()
    {
        var old = Descriptor([1], "0.15.0");
        GuardPaths.Write(Path.Combine(_root, "policy.json"), Policy() with { InitialPackage = old });
        Queue(); Tick(); Assert.Equal(0, _platform.Installs); Assert.False(Pending);
    }
    [Theory]
    [InlineData("win32")]
    [InlineData("timeout")]
    public void Native_start_and_timeout_errors_also_trigger_rollback(string error)
    {
        _platform.NativeError = error; var grant = Queue(); _platform.FailRollback = true; Tick();
        var pending = Progress(grant.Grant.Id); Assert.Equal("rollback-pending", pending.Stage);
        Assert.Equal(error == "win32" ? "install-failed" : "installer-timeout", pending.Reason);
        Assert.Equal(error == "win32" ? (int?)2 : null, pending.ErrorCode);
        _now = _now.AddHours(3); _platform.FailRollback = false; Tick();
        AssertOld(); Assert.False(Pending); Assert.Equal(2, _platform.Rollbacks);
        var done = Progress(grant.Grant.Id); Assert.Equal("rolled-back", done.Stage);
        Assert.Equal(pending.Reason, done.Reason); Assert.Equal(pending.ErrorCode, done.ErrorCode);
        Assert.Equal("succeeded", done.Rollback); Assert.Null(done.RollbackReason);
    }
    [Fact]
    public void Oversized_serialized_policy_cannot_leave_an_unreadable_journal_after_install()
    {
        byte[] original = File.ReadAllBytes(Path.Combine(_root, "policy.json"));
        Assert.Throws<IOException>(() => GuardPaths.Write(Path.Combine(_root, "policy.json"), Policy() with { ServerUrl = new string('x', 70000) }));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(_root, "policy.json")));
        var before = Policy() with { ServerUrl = new string('я', 15000) };
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(before, new JsonSerializerOptions(GuardPaths.Json) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.True(raw.Length < 65536); File.WriteAllBytes(Path.Combine(_root, "policy.json"), raw);
        Queue(); Tick(); Assert.Equal(0, _platform.Installs); Assert.False(Pending);
    }
    [Fact]
    public void Stale_download_parts_are_cleaned_without_removing_active_or_recent_parts()
    {
        string stale = Path.Combine(_root, "cache", Hash([8]) + "." + new string('a', 32) + ".tmp");
        string active = Path.Combine(_root, "cache", Hash([9]) + "." + new string('b', 32) + ".tmp");
        string recent = Path.Combine(_root, "cache", Hash([7]) + "." + new string('c', 32) + ".tmp");
        foreach (string path in new[] { stale, active, recent }) File.WriteAllBytes(path, [8]);
        File.SetLastWriteTimeUtc(stale, _now.AddHours(-2)); File.SetLastWriteTimeUtc(active, _now.AddHours(-2)); File.SetLastWriteTimeUtc(recent, _now);
        using (var lease = new FileStream(active + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        { Tick(); Assert.False(File.Exists(stale)); Assert.True(File.Exists(active)); Assert.True(File.Exists(recent)); }
        Tick(); Assert.False(File.Exists(active)); Assert.False(File.Exists(active + ".lock"));
    }
    [Fact]
    public void Policy_commit_followed_by_crash_is_not_rolled_back()
    {
        var grant = Queue(); _platform.CrashInstall = true; Assert.Throws<Crash>(Tick);
        GuardPaths.Write(Path.Combine(_root, "policy.json"), Policy() with
        {
            LastVerifiedPackage = grant.Grant,
            InitialPackage = null,
            PreviousPackage = Descriptor([1], "0.15.1"),
            ConfigHashes = new(_before.ConfigHashes) { ["agent"] = Hash([2]) }
        });
        _platform.CrashInstall = false; Tick(); Assert.Equal(0, _platform.Rollbacks); Assert.False(Pending);
        Assert.Equal("completed", Result(grant.Grant.Id).Status); Assert.Equal("0.15.2", Policy().LastVerifiedPackage!.Version);
    }
}
