using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Watcher.Uploader.Tests;

// 0.16: Linux-агент включает защиту сам по ключу обслуживания, заверенному ключом обновлений.
public class ProtectionActivationTests : IDisposable
{
    private readonly ECDsa _update = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _state = Path.Combine(Path.GetTempPath(), "watcher-protect-" + Guid.NewGuid().ToString("N") + ".json");
    private static readonly DateTime Now = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
    private const string Maintenance = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEmaintenancekey=";
    public void Dispose() { _update.Dispose(); File.Delete(_state); }

    private string UpdateKey => Convert.ToBase64String(_update.ExportSubjectPublicKeyInfo());
    private MaintenanceKeyOffer Offer(ECDsa? signer = null, string key = Maintenance) =>
        new(key, Convert.ToBase64String((signer ?? _update).SignData(UpdateSigning.MaintenanceKeyMessage(key), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));

    [Fact]
    public void Signed_key_activates_unprotected_agent_once_per_period()
    {
        Assert.True(ProtectionActivation.ShouldActivate(Offer(), UpdateKey, guardEnabled: false, _state, Now));
        Assert.False(ProtectionActivation.ShouldActivate(Offer(), UpdateKey, guardEnabled: true, _state, Now));
        File.WriteAllText(_state, JsonSerializer.Serialize(new ProtectionActivation.Attempt(Now.AddHours(-1), Maintenance), GuardPaths.Json));
        Assert.False(ProtectionActivation.ShouldActivate(Offer(), UpdateKey, false, _state, Now));
        Assert.True(ProtectionActivation.ShouldActivate(Offer(), UpdateKey, false, _state, Now.AddHours(6)));
    }

    [Fact]
    public void Key_not_signed_by_own_update_key_is_ignored()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(ProtectionActivation.ShouldActivate(Offer(other), UpdateKey, false, _state, Now));
        Assert.False(ProtectionActivation.ShouldActivate(Offer() with { Key = Maintenance.Replace('m', 'n') }, UpdateKey, false, _state, Now));
        Assert.False(ProtectionActivation.ShouldActivate(Offer(), "", false, _state, Now));
        Assert.False(ProtectionActivation.ShouldActivate(Offer(key: "bad key\" --x"), UpdateKey, false, _state, Now));
    }
}
