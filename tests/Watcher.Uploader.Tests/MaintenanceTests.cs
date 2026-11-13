using System.Security.Cryptography;
using System.Text.Json;
using Watcher;
using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

public sealed class MaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "watcher-guard-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _public;
    private DateTime _now = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
    private const string Secret = "test-agent-secret-not-production";
    private readonly GuardPolicy _policy;
    private readonly FakePlatform _platform = new();
    public MaintenanceTests()
    {
        Directory.CreateDirectory(_root); string install = Path.Combine(_root, "program"); Directory.CreateDirectory(install);
        _public = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
        string config = Path.Combine(install, "settings.json"); File.WriteAllText(config, "{\"keep\":true}");
        string credential = Path.Combine(_root, "credential.json"); File.WriteAllText(credential, JsonSerializer.Serialize(new StoredCredential(Secret, "host", "video", "hash", _now)));
        _policy = new(install, _public, _public, new() { ["video"] = credential }, new() { ["settings.json"] = Hash(config) });
        GuardPaths.Write(Path.Combine(_root, "policy.json"), _policy); Directory.CreateDirectory(Path.Combine(_root, "config-backups")); File.Copy(config, Path.Combine(_root, "config-backups", "settings.json"));
    }
    public void Dispose() { _key.Dispose(); Directory.Delete(_root, true); }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private SignedMaintenanceGrant Grant(string action = "stop") => MaintenanceSigning.Sign(new(Guid.NewGuid().ToString("N"), MaintenanceSigning.Binding(Secret), "video", action, _now, _now.AddMinutes(15)), _key);
    private GuardEngine Engine() => new(_root, _platform, () => _now);
    private sealed class FakePlatform : IGuardPlatform
    {
        public int CaptureChecks;
        public void ReconcileCapture(GuardPolicy policy) { CaptureChecks++; }
        public bool Active = true; public bool StartWorks = true, RegistrationWorks = true; public Action? DuringInstall; public int Starts, Stops, Installs, Uninstalls, Registrations;
        public bool Running(string kind) => Active;
        public void Start(string kind, GuardPolicy policy) { Starts++; Active = StartWorks; }
        public void EnsureRegistered(string kind, GuardPolicy policy) { Registrations++; if (!RegistrationWorks) throw new IOException("Registration unavailable"); }
        public void Stop(string kind, GuardPolicy policy) { Stops++; Active = false; }
        public void Install(string package, string platform, bool repair, bool downgrade = false) { Installs++; DuringInstall?.Invoke(); }
        public void Uninstall(string kind, GuardPolicy policy) { Uninstalls++; Active = false; }
        public void Rollback(string package, string packagePlatform, GuardPolicy policy) { Installs++; Active = false; StartWorks = true; RegistrationWorks = true; }
    }
    private GuardRecoveryPackage Initial(byte[] bytes)
    {
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new("linux-deb", "1.2.3", hash, bytes.Length,
            Convert.ToBase64String(_key.SignData(UpdateSigning.Message("linux-deb", "1.2.3", hash), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));
    }

    [Fact]
    public void Capture_supervision_runs_while_uploader_is_healthy_but_not_during_signed_stop()
    {
        Engine().Tick();
        Assert.Equal(1, _platform.CaptureChecks);
        GuardPaths.Queue(Grant(), _root);
        Engine().Tick();
        Assert.False(_platform.Active);
        Assert.Equal(1, _platform.CaptureChecks);
        _now = _now.AddMinutes(16);
        Engine().Tick();
        Assert.True(_platform.Active);
        Assert.Equal(2, _platform.CaptureChecks);
    }

    [Fact]
    public void Exhausted_package_retry_budget_does_not_block_intact_service_recovery()
    {
        GuardPaths.Write(Path.Combine(_root, "retry.json"), new GuardRetry(3, _now.AddMinutes(30)));
        _platform.Active = false;
        Engine().Tick();
        Assert.True(_platform.Active);
        Assert.Equal(1, _platform.Starts);
        Assert.Equal(0, _platform.Installs);
        Assert.False(File.Exists(Path.Combine(_root, "retry.json")));
    }

    [Fact]
    public void Initial_base_restores_deleted_program_and_registration_without_network_or_new_identity()
    {
        byte[] payload = [1, 2, 3, 4]; var initial = Initial(payload);
        string executable = Path.Combine(_policy.InstallDir, "agent"); File.WriteAllBytes(executable, payload);
        string configuration = Path.Combine(_policy.InstallDir, "settings.json");
        File.WriteAllText(configuration, "{\r\n  \"keep\": true\r\n}", new System.Text.UTF8Encoding(true));
        File.Copy(configuration, Path.Combine(_root, "config-backups", "settings.json"), true);
        var policy = _policy with { ConfigHashes = new(_policy.ConfigHashes) { ["agent"] = Hash(executable), ["settings.json"] = Hash(configuration) } };
        GuardPaths.Write(Path.Combine(_root, "policy.json"), policy);
        GuardRecoveryCache.Seed(_root, initial, new MemoryStream(payload));
        byte[] credential = File.ReadAllBytes(policy.Credentials["video"]);
        byte[] config = File.ReadAllBytes(Path.Combine(policy.InstallDir, "settings.json"));
        Directory.Delete(policy.InstallDir, true); _platform.Active = false;
        _platform.DuringInstall = () => { Directory.CreateDirectory(policy.InstallDir); File.WriteAllBytes(executable, payload); };
        Engine().Tick();
        Assert.Equal(1, _platform.Installs); Assert.Equal(1, _platform.Starts); Assert.Equal(1, _platform.Registrations);
        Assert.True(Engine().Healthy(GuardPaths.Load(_root)!));
        Assert.Equal(credential, File.ReadAllBytes(policy.Credentials["video"]));
        Assert.Equal(config, File.ReadAllBytes(Path.Combine(policy.InstallDir, "settings.json")));
        Assert.Null(GuardPaths.Load(_root)!.LastVerifiedPackage);
        Assert.False(File.Exists(Path.Combine(_root, "internal-repair.json")));
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("platform")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("truncated")]
    [InlineData("extra")]
    public void Invalid_initial_base_never_publishes_policy_or_partial_package(string mutation)
    {
        byte[] bytes = [1, 2, 3]; var package = Initial(bytes);
        package = mutation switch
        {
            "signature" => package with { Signature = "AAAA" },
            "platform" => package with { Platform = "windows-msi" },
            "version" => package with { Version = "2.0.0" },
            "hash" => package with { Sha256 = new string('f', 64) },
            "size" => package with { Size = GuardRecoveryCache.MaxPackageBytes + 1 },
            _ => package
        };
        byte[] source = mutation == "truncated" ? [1, 2] : mutation == "extra" ? [1, 2, 3, 4] : bytes;
        Assert.ThrowsAny<Exception>(() => GuardRecoveryCache.Seed(_root, package, new MemoryStream(source)));
        Assert.Null(GuardPaths.Load(_root)!.InitialPackage);
        Assert.False(File.Exists(GuardRecoveryCache.PackagePath(_root, package)));
        Assert.Empty(Directory.Exists(Path.Combine(_root, "cache")) ? Directory.GetFiles(Path.Combine(_root, "cache")) : []);
    }

    [Fact]
    public void Seed_is_idempotent_and_cached_bytes_are_rechecked_before_repair()
    {
        byte[] bytes = [1, 2, 3]; var package = Initial(bytes);
        GuardRecoveryCache.Seed(_root, package, new MemoryStream(bytes));
        GuardRecoveryCache.Seed(_root, Initial([4, 5]), new MemoryStream([4, 5]));
        Assert.Equal(package, GuardPaths.Load(_root)!.InitialPackage);
        string path = GuardRecoveryCache.Verify(_root, package, _public);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        File.WriteAllBytes(path, [3, 2, 1]);
        Assert.Throws<CryptographicException>(() => GuardRecoveryCache.Verify(_root, package, _public));
    }

    [Fact]
    public void Recovery_rejects_wrong_publisher_and_linked_cache()
    {
        byte[] bytes = [1, 2, 3]; var package = Initial(bytes);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<CryptographicException>(() => GuardRecoveryCache.Validate(package, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())));
        GuardRecoveryCache.Seed(_root, package, new MemoryStream(bytes));
        if (OperatingSystem.IsWindows()) return;
        string path = GuardRecoveryCache.PackagePath(_root, package);
        string target = Path.Combine(_root, "linked-payload"); File.WriteAllBytes(target, bytes);
        File.Delete(path); File.CreateSymbolicLink(path, target);
        Assert.Throws<IOException>(() => GuardRecoveryCache.Verify(_root, package, _public));
    }

    [Fact]
    public void Recovery_manifest_is_bounded_and_legacy_policy_has_no_initial_package()
    {
        Assert.Throws<IOException>(() => GuardRecoveryCache.ReadManifest(new MemoryStream(new byte[16385])));
        Assert.Null(GuardPaths.Load(_root)!.InitialPackage);
    }

    [Fact]
    public void First_tick_migrates_verified_guard_name_without_reinstall_or_identity_change()
    {
        string executable = Path.Combine(_policy.InstallDir, "GuardRepair.exe");
        File.WriteAllBytes(executable, [1, 2, 3]);
        var policy = _policy with { ConfigHashes = new(_policy.ConfigHashes) { ["Watcher.Guard.exe"] = Hash(executable) } };
        GuardPaths.Write(Path.Combine(_root, "policy.json"), policy);
        byte[] credential = File.ReadAllBytes(_policy.Credentials["video"]);
        byte[] backup = File.ReadAllBytes(Path.Combine(_root, "config-backups", "settings.json"));
        Engine().Tick();
        var migrated = GuardPaths.Load(_root)!;
        Assert.False(migrated.ConfigHashes.ContainsKey("Watcher.Guard.exe"));
        Assert.Equal(Hash(executable), migrated.ConfigHashes["GuardRepair.exe"]);
        Assert.Equal(0, _platform.Installs);
        Assert.Equal(credential, File.ReadAllBytes(_policy.Credentials["video"]));
        Assert.Equal(backup, File.ReadAllBytes(Path.Combine(_root, "config-backups", "settings.json")));
        Engine().Tick();
        Assert.Equal(0, _platform.Installs);
    }

    [Fact]
    public void Stop_is_single_use_and_recovers_after_expiry()
    {
        var grant = Grant(); GuardPaths.Queue(grant, _root); Engine().Tick(); Assert.Equal(1, _platform.Stops); Assert.Equal(0, _platform.Starts);
        GuardPaths.Queue(grant, _root); Engine().Tick(); Assert.Equal(1, _platform.Stops);
        _now = _now.AddMinutes(16); Engine().Tick(); Assert.Equal(1, _platform.Starts);
        Assert.Contains("accepted", File.ReadAllText(Path.Combine(_root, "operations.jsonl"))); Assert.Contains("completed", File.ReadAllText(Path.Combine(_root, "operations.jsonl")));
    }
    [Theory]
    [InlineData("binding")]
    [InlineData("kind")]
    [InlineData("action")]
    [InlineData("expiry")]
    [InlineData("signature")]
    public void Altered_grant_cannot_stop_service(string mutation)
    {
        var signed = Grant(); signed = mutation switch
        {
            "binding" => signed with { Grant = signed.Grant with { Binding = new string('a', 64) } },
            "kind" => signed with { Grant = signed.Grant with { Kind = "terminal" } },
            "action" => signed with { Grant = signed.Grant with { Action = "uninstall" } },
            "expiry" => signed with { Grant = signed.Grant with { ExpiresAt = _now.AddDays(3) } },
            _ => signed with { Signature = "AAAA" }
        };
        Directory.CreateDirectory(Path.Combine(_root, "inbox")); File.WriteAllText(Path.Combine(_root, "inbox", "untrusted.json"), JsonSerializer.Serialize(signed, GuardPaths.Json)); Engine().Tick(); Assert.Equal(0, _platform.Stops); Assert.Equal(0, _platform.Uninstalls);
    }
    [Fact]
    public void Malformed_id_never_creates_a_result_outside_root()
    {
        string inbox = Path.Combine(_root, "inbox"); Directory.CreateDirectory(inbox); var signed = Grant(); signed = signed with { Grant = signed.Grant with { Id = "../../outside" } };
        File.WriteAllText(Path.Combine(inbox, "input.json"), JsonSerializer.Serialize(signed, GuardPaths.Json)); Engine().Tick();
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "outside.json"))); Assert.Equal(0, _platform.Stops);
    }
    [Fact]
    public void Msi_native_properties_quote_values_without_quoting_the_property_name()
    {
        string args = MsiCommandLine.Install(@"C:\Program Files\Guard\package.msi", false, @"C:\Program Files\Guard\msi.log", @"C:\Program Files\Watcher\", "https://watcher.test", "public-update-key", "public-maintenance-key");
        Assert.Contains("INSTALLFOLDER=\"C:\\Program Files\\Watcher\"", args);
        Assert.DoesNotContain("\"INSTALLFOLDER=", args);
        Assert.Contains("/i \"C:\\Program Files\\Guard\\package.msi\" /qn", args);
        // База восстановления — MSI комплекта с токеном: ремонт его не применяет.
        Assert.EndsWith(" ENROLLTOKEN=\"\"", args);
        Assert.DoesNotContain("REINSTALLMODE", args);
        // Понижение версии: все файлы пакета ставятся независимо от версии установленных.
        Assert.Contains(" REINSTALLMODE=amus ", MsiCommandLine.Install("p.msi", false, "l", @"C:\Watcher", "https://w", "k", "k", downgrade: true));
        Assert.Throws<ArgumentException>(() => MsiCommandLine.Install("package.msi", false, "log", @"C:\Watcher", "https://host\" REBOOT=Force", "key", "key"));
    }
    [Theory]
    [InlineData(64, 4, false, true)]
    [InlineData(16777280, 4, false, true)]
    [InlineData(64, 4, true, false)]
    [InlineData(68, 4, false, false)]
    [InlineData(96, 4, false, false)]
    [InlineData(65, 4, false, false)]
    [InlineData(64, 6, false, false)]
    [InlineData(0, 4, false, false)]
    public void Missing_crl_never_bypasses_trust_name_expiry_or_known_revocation(int status, int errors, bool published, bool expected)
    {
        Assert.Equal(expected, AgentTls.AllowsUnpublishedRevocation((System.Net.Security.SslPolicyErrors)errors, [(System.Security.Cryptography.X509Certificates.X509ChainStatusFlags)status], published));
    }
    [Fact]
    public void Private_writes_remain_private_and_capture_configuration_remains_readable()
    {
        string secret = Path.Combine(_root, "private-state.json"), config = Path.Combine(_root, "Watcher.Capture.Linux.json");
        GuardPaths.WriteBytes(secret, [1, 2, 3]); GuardPaths.WriteBytes(config, [1, 2, 3], readableByUsers: true);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(secret));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(secret));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(config));
        }
    }
    [Fact]
    public void Configuration_restores_exact_bytes_and_preserves_identity()
    {
        string credential = File.ReadAllText(_policy.Credentials["video"]); File.WriteAllText(Path.Combine(_policy.InstallDir, "settings.json"), "corrupted");
        _platform.Active = false; Engine().Tick(); Assert.Equal("{\"keep\":true}", File.ReadAllText(Path.Combine(_policy.InstallDir, "settings.json")));
        Assert.Equal(credential, File.ReadAllText(_policy.Credentials["video"])); Assert.Equal(1, _platform.Starts); Assert.Single(Directory.GetFiles(Path.Combine(_root, "quarantine")));
    }
    [Fact]
    public void PowerShell_utf8_bom_backup_restores_exact_bytes()
    {
        string target = Path.Combine(_policy.InstallDir, "settings.json");
        string backup = Path.Combine(_root, "config-backups", "settings.json");
        byte[] bytes = [0xef, 0xbb, 0xbf, .. System.Text.Encoding.UTF8.GetBytes("{\"keep\":true}")];
        File.WriteAllBytes(backup, bytes);
        GuardPaths.Write(Path.Combine(_root, "policy.json"), _policy with { ConfigHashes = new() { ["settings.json"] = Hash(backup) } });
        File.WriteAllText(target, "corrupted"); _platform.Active = false;
        Engine().Tick();
        Assert.Equal(bytes, File.ReadAllBytes(target));
        Assert.Equal(1, _platform.Starts);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "quarantine")));
    }
    [Fact]
    public void Corrupt_backup_does_not_get_replaced_by_defaults()
    {
        File.WriteAllText(Path.Combine(_root, "config-backups", "settings.json"), "bad backup"); File.Delete(Path.Combine(_policy.InstallDir, "settings.json")); _platform.Active = false;
        Engine().Tick(); Assert.False(File.Exists(Path.Combine(_policy.InstallDir, "settings.json"))); Assert.Equal(0, _platform.Starts);
        var retry = JsonSerializer.Deserialize<GuardRetry>(File.ReadAllBytes(Path.Combine(_root, "retry.json")), GuardPaths.Json)!; Assert.Equal(1, retry.Attempts);
        Engine().Tick(); Assert.Equal(1, JsonSerializer.Deserialize<GuardRetry>(File.ReadAllBytes(Path.Combine(_root, "retry.json")), GuardPaths.Json)!.Attempts);
    }
    [Fact]
    public void Invalid_json_backup_reports_failed_signed_repair()
    {
        string backup = Path.Combine(_root, "config-backups", "settings.json");
        File.WriteAllText(backup, "invalid JSON");
        GuardPaths.Write(Path.Combine(_root, "policy.json"), _policy with { ConfigHashes = new() { ["settings.json"] = Hash(backup) } });
        File.WriteAllText(Path.Combine(_policy.InstallDir, "settings.json"), "damaged config");
        var grant = Grant("repair"); GuardPaths.Queue(grant, _root);
        Engine().Tick();
        using var result = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_root, "results", grant.Grant.Id + ".json")));
        Assert.Equal("failed", result.RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain("invalid JSON", File.ReadAllText(Path.Combine(_root, "last-failure.json")));
    }
    [Fact]
    public void Replay_store_failure_never_performs_operation()
    {
        Directory.CreateDirectory(Path.Combine(_root, "consumed.json")); GuardPaths.Queue(Grant(), _root); Engine().Tick(); Assert.Equal(0, _platform.Stops);
    }
    [Fact]
    public void Clock_rollback_denies_new_actions()
    {
        var grant = Grant(); GuardPaths.Queue(grant, _root); Engine().Tick(); _now = _now.AddMinutes(-5);
        var second = Grant(); GuardPaths.Queue(second, _root); Engine().Tick(); Assert.Equal(1, _platform.Stops);
    }
    [Fact]
    public void Maintenance_signature_has_different_purpose_from_package_signature()
    {
        var signed = Grant(); string signature = Convert.ToBase64String(_key.SignData(UpdateSigning.Message("windows-msi", "0.15.1", new string('a', 64)), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Assert.False(MaintenanceSigning.Verify(signed with { Signature = signature }, _public, MaintenanceSigning.Binding(Secret), "video", _now));
        Assert.False(UpdateSigning.Verify(_key, "windows-msi", "0.15.1", new string('a', 64), signed.Signature));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Offline_update_checks_independent_publisher_signature_before_install(bool wrongPublisher)
    {
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var policy = _policy with { UpdatePublicKey = Convert.ToBase64String(publisher.ExportSubjectPublicKeyInfo()) };
        GuardPaths.Write(Path.Combine(_root, "policy.json"), policy);
        EnsureRollbackBase(publisher);
        byte[] bytes = [1, 2, 3, 4]; string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string signature = Convert.ToBase64String((wrongPublisher ? other : publisher).SignData(UpdateSigning.Message("linux-deb", "0.15.1", hash), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        var grant = MaintenanceSigning.Sign(Grant("repair").Grant with { Action = "update", Version = "0.15.1", Platform = "linux-deb", Sha256 = hash, PackageSignature = signature, PackageId = 1, PackageSize = bytes.Length }, _key);
        GuardPaths.Queue(grant, _root); File.WriteAllBytes(Path.Combine(_root, "inbox", grant.Grant.Id + ".package"), bytes);
        Engine().Tick(); Assert.Equal(wrongPublisher ? 0 : 1, _platform.Installs);
        Assert.Equal(wrongPublisher ? null : grant.Grant, GuardPaths.Load(_root)!.LastVerifiedPackage);
        Assert.Equal("{\"keep\":true}", File.ReadAllText(Path.Combine(policy.InstallDir, "settings.json")));
    }
    private void EnsureRollbackBase(ECDsa? publisher = null)
    {
        byte[] bytes = [9, 8, 7];
        var package = Initial(bytes);
        if (publisher is not null) package = package with { Signature = Convert.ToBase64String(publisher.SignData(UpdateSigning.Message(package.Platform, package.Version, package.Sha256), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) };
        GuardRecoveryCache.Seed(_root, package, new MemoryStream(bytes));
    }
    private SignedMaintenanceGrant QueueUpdate()
    {
        EnsureRollbackBase();
        byte[] bytes = [1, 2, 3]; string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string signature = Convert.ToBase64String(_key.SignData(UpdateSigning.Message("linux-deb", "0.15.2", hash), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        var grant = MaintenanceSigning.Sign(Grant().Grant with { Action = "update", Version = "0.15.2", Platform = "linux-deb", Sha256 = hash, PackageSignature = signature, PackageId = 1, PackageSize = bytes.Length }, _key);
        GuardPaths.Queue(grant, _root); File.WriteAllBytes(Path.Combine(_root, "inbox", grant.Grant.Id + ".package"), bytes);
        return grant;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Update_does_not_adopt_new_repair_base_before_service_and_registration_checks(bool registrationFailure)
    {
        _platform.DuringInstall = () => _platform.Active = false;
        _platform.StartWorks = registrationFailure;
        _platform.RegistrationWorks = !registrationFailure;
        var update = QueueUpdate();
        Engine().Tick();
        Assert.Null(GuardPaths.Load(_root)!.LastVerifiedPackage);
        var result = JsonSerializer.Deserialize<GuardExecution>(File.ReadAllBytes(Path.Combine(_root, "results", update.Grant.Id + ".json")), GuardPaths.Json)!;
        Assert.Equal("failed", result.Status);
        Assert.Equal(2, _platform.Installs);
        Assert.True(File.Exists(_policy.Credentials["video"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Manual_service_update_restores_previous_running_state_without_enabling_autostart(bool wasRunning)
    {
        GuardPaths.Write(Path.Combine(_root, "policy.json"), _policy with { AutoStart = false });
        _platform.Active = wasRunning;
        _platform.DuringInstall = () => _platform.Active = false;
        var update = QueueUpdate(); Engine().Tick();
        Assert.Equal(wasRunning, _platform.Active);
        Assert.Equal(wasRunning ? 1 : 0, _platform.Starts);
        Assert.False(GuardPaths.Load(_root)!.AutoStart);
        Assert.Equal(update.Grant, GuardPaths.Load(_root)!.LastVerifiedPackage);
    }

    [Fact]
    public void Authorized_uninstall_survives_expiry_without_automatic_reinstallation()
    {
        GuardPaths.Queue(Grant("uninstall"), _root); Engine().Tick(); Assert.Equal(1, _platform.Uninstalls); Assert.False(File.Exists(Path.Combine(_root, "active.json")));
        _now = _now.AddHours(3); Engine().Tick(); Assert.Equal(0, _platform.Starts); Assert.Equal(0, _platform.Installs);
    }
    [Fact]
    public void Key_rotation_invalidates_old_signer_and_accepts_new_one()
    {
        using var next = ECDsa.Create(ECCurve.NamedCurves.nistP256); string pub = Convert.ToBase64String(next.ExportSubjectPublicKeyInfo());
        var grant = MaintenanceSigning.Sign(Grant("repair").Grant with { Action = "rotate-key", NewPublicKey = pub }, _key); GuardPaths.Queue(grant, _root); Engine().Tick();
        Assert.Equal(pub, GuardPaths.Load(_root)!.MaintenancePublicKey); GuardPaths.Queue(Grant(), _root); Engine().Tick(); Assert.Equal(0, _platform.Stops);
        var stop = MaintenanceSigning.Sign(Grant().Grant, next); GuardPaths.Queue(stop, _root); Engine().Tick(); Assert.Equal(1, _platform.Stops);
    }
}
