using System.Runtime.Versioning;
using Watcher.Guard;
using Xunit;

namespace Watcher.Uploader.Tests;

// 0.16: установщик агента — аргументы custom action MSI (--name=value).
[SupportedOSPlatform("windows")]
public class AgentSetupTests
{
    [Fact]
    public void Msi_arguments_with_empty_properties_and_trailing_dot_are_parsed()
    {
        // Так MSI разворачивает Target RunInstallScript: пустые свойства — пустые строки.
        var o = AgentSetup.Parse(["--install-dir=C:\\Program Files\\Watcher\\.", "--server-url=https://watcher.corp", "--api-token=",
            "--enroll-token=wenr_x", "--terminal-audit=1", "--update-key=MFkw", "--maintenance-key=", "--ca-cert=",
            "--recovery-source=C:\\Users\\a\\Downloads\\watcher-agent.msi", "--upgrade-recovery-product={B57CFE32-A5D4-4ACC-8E2E-796E4F6B05C7}",
            "--previous-service-start=#3"]);
        Assert.Equal("https://watcher.corp", o.ServerUrl);
        Assert.Equal("wenr_x", o.EnrollToken);
        Assert.Equal("", o.ApiToken);
        Assert.Equal("", o.MaintenanceKey);
        Assert.Equal("#3", o.PreviousServiceStart);
        Assert.Equal("{B57CFE32-A5D4-4ACC-8E2E-796E4F6B05C7}", o.UpgradeRecoveryProduct);
        Assert.EndsWith("Watcher", o.InstallDir);
        Assert.False(o.InstallDir.EndsWith('.') || o.InstallDir.EndsWith('\\'));
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("", "1")]       // по умолчанию — включён (как TERMINALAUDIT=1 в MSI)
    [InlineData("0", "0")]
    public void Terminal_audit_defaults_to_on(string value, string expected) =>
        Assert.Equal(expected, AgentSetup.Parse(["--install-dir=C:\\W", "--terminal-audit=" + value]).TerminalAudit);

    [Fact]
    public void Missing_install_dir_and_bad_syntax_are_refused()
    {
        Assert.Throws<ArgumentException>(() => AgentSetup.Parse(["--server-url=https://x"]));
        Assert.Throws<ArgumentException>(() => AgentSetup.Parse(["--install-dir=C:\\W", "server-url"]));
    }

    [Theory]
    [InlineData("{\"phase\":\"prepared\",\"files\":[]}", true)]
    [InlineData("{\"phase\":\"committed\"}", false)]
    [InlineData("{\"phase\":\"rolled-back\"}", false)]
    [InlineData("not json", false)]
    [InlineData(null, false)]
    public void Only_prepared_upgrade_journal_restores_configuration(string? state, bool expected)
    {
        // Завершённый журнал прошлой установки того же продукта не должен ронять переустановку.
        string dir = Path.Combine(Path.GetTempPath(), "watcher-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            if (state is not null) File.WriteAllText(Path.Combine(dir, "state.json"), state);
            Assert.Equal(expected, AgentSetup.PreparedUpgradeJournal(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
