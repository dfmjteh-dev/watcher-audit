using Xunit;

namespace Watcher.Server.Tests;

public class KitMaintenanceKeyTests : IDisposable
{
    private readonly string _kit = Path.Combine(Path.GetTempPath(), "watcher-kit-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_kit)) Directory.Delete(_kit, true); }

    [Fact]
    public void Reads_signed_key_from_kit_and_nothing_without_signature()
    {
        Assert.Null(KitMaintenanceKey.Read(null));
        Directory.CreateDirectory(Path.Combine(_kit, "certs"));
        File.WriteAllText(Path.Combine(_kit, "certs", "maintenance-key.pub.txt"), "MFkwKEY=\n");
        Assert.Null(KitMaintenanceKey.Read(_kit));
        File.WriteAllText(Path.Combine(_kit, "certs", "maintenance-key.sig"), "c2ln\n");
        Assert.Equal(new KitMaintenanceKey("MFkwKEY=", "c2ln"), KitMaintenanceKey.Read(_kit));
    }
}
