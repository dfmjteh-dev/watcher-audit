using System.Security.Cryptography;
using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

public sealed class GuardNameMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "watcher-name-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Bytes = [1, 2, 3, 4];
    private static string Hash => Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();
    public GuardNameMigrationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private GuardPolicy Policy() => new(_root, "maintenance", "publisher", new() { ["video"] = Path.Combine(_root, "credential.json") }, new() { ["Watcher.Guard.exe"] = Hash });

    [Fact]
    public void Verified_alias_migrates_name_without_changing_identity_or_other_policy()
    {
        var policy = Policy();
        File.WriteAllBytes(Path.Combine(_root, "GuardRepair.exe"), Bytes);
        var migrated = GuardBootstrap.MigrateExecutableName(policy);
        Assert.False(migrated.ConfigHashes.ContainsKey("Watcher.Guard.exe"));
        Assert.Equal(Hash, migrated.ConfigHashes["GuardRepair.exe"]);
        Assert.True(policy.ConfigHashes.ContainsKey("Watcher.Guard.exe"));
        Assert.Same(policy.Credentials, migrated.Credentials);
        Assert.Equal(policy.MaintenancePublicKey, migrated.MaintenancePublicKey);
        Assert.Equal(policy.UpdatePublicKey, migrated.UpdatePublicKey);
        Assert.Equal(policy.AutoStart, migrated.AutoStart);
        Assert.Same(migrated, GuardBootstrap.MigrateExecutableName(migrated));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_unverified_new_name_does_not_change_policy(bool wrongBytes)
    {
        var policy = Policy();
        if (wrongBytes) File.WriteAllBytes(Path.Combine(_root, "GuardRepair.exe"), [9]);
        Assert.Same(policy, GuardBootstrap.MigrateExecutableName(policy));
    }

    [Fact]
    public void Conflicting_pinned_new_name_is_not_silently_replaced()
    {
        var policy = Policy();
        policy.ConfigHashes["GuardRepair.exe"] = new string('a', 64);
        File.WriteAllBytes(Path.Combine(_root, "GuardRepair.exe"), Bytes);
        Assert.Throws<IOException>(() => GuardBootstrap.MigrateExecutableName(policy));
        Assert.Equal(new string('a', 64), policy.ConfigHashes["GuardRepair.exe"]);
    }
}
