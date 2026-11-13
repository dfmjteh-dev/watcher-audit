using System.Text.Json;
using Xunit;

namespace Watcher.Server.Tests;

public class AndroidPolicyValidationTests
{
    private static string? Validate(string json) =>
        AgentConfigValidation.Validate(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void AcceptsValidPolicy()
    {
        Assert.Null(Validate("""{"androidPolicy":{"suspendedPackages":["com.foo.game"],"hiddenPackages":["com.bar"],"restrictions":["no_install_unknown_sources","no_factory_reset"],"meteredDataDisabledPackages":["com.baz"]}}"""));
        Assert.Null(Validate("""{"androidPolicy":{}}"""));
    }

    [Fact]
    public void RejectsProtectedPackageInSuspendOrHide()
    {
        Assert.Contains("protected", Validate("""{"androidPolicy":{"suspendedPackages":["com.android.settings"]}}""")!);
        Assert.Contains("protected", Validate("""{"androidPolicy":{"hiddenPackages":["com.watcher.agent"]}}""")!);
    }

    [Fact]
    public void RejectsUnknownRestrictionAndBadPackage()
    {
        Assert.NotNull(Validate("""{"androidPolicy":{"restrictions":["no_such_restriction"]}}"""));
        Assert.NotNull(Validate("""{"androidPolicy":{"suspendedPackages":["not a package"]}}"""));
        Assert.NotNull(Validate("""{"androidPolicy":{"unknownKey":[]}}"""));
    }

    [Fact]
    public void ScreenRecordValidation()
    {
        // Валидные режимы и список приложений для perApp.
        Assert.Null(Validate("""{"androidPolicy":{"screenRecord":{"mode":"off"}}}"""));
        Assert.Null(Validate("""{"androidPolicy":{"screenRecord":{"mode":"device"}}}"""));
        Assert.Null(Validate("""{"androidPolicy":{"screenRecord":{"mode":"perApp","apps":["com.foo.bar"]}}}"""));
        // Неизвестный режим и невалидный пакет — отказ.
        Assert.Contains("mode", Validate("""{"androidPolicy":{"screenRecord":{"mode":"always"}}}""")!);
        Assert.NotNull(Validate("""{"androidPolicy":{"screenRecord":{"apps":["not a pkg"]}}}"""));
        Assert.Contains("unknown key", Validate("""{"androidPolicy":{"screenRecord":{"fps":60}}}""")!);
        Assert.NotNull(Validate("""{"androidPolicy":{"screenRecord":"on"}}"""));
    }

    [Fact]
    public void KioskPackagesAreAllowlistNotFilteredByProtected()
    {
        // kioskPackages — разрешённые в киоске приложения, защитный список не применяется.
        Assert.Null(Validate("""{"androidPolicy":{"kioskPackages":["com.example.work","com.android.settings"]}}"""));
        // Но невалидное имя пакета отклоняется.
        Assert.NotNull(Validate("""{"androidPolicy":{"kioskPackages":["not a pkg"]}}"""));
    }

    [Fact]
    public void MeteredMayListAnyValidPackageEvenProtected()
    {
        Assert.Null(Validate("""{"androidPolicy":{"meteredDataDisabledPackages":["com.android.vending"]}}"""));
    }
}
