using Watcher.SessionService;
using Xunit;

namespace Watcher.SessionService.Tests;

// 0.13 (п.16): обновление агента из MSI — новым MSI; 0.16 — без PowerShell,
// помощником --apply-msi-update по job.json.
public class WindowsMsiInstallerTests
{
    private static MsiUpdateJob J(string serverUrl = "https://watcher.corp", bool audit = true) => WindowsMsiInstaller.Job(
        @"C:\ProgramData\Watcher\upd\0.13.0\package.msi", "0.13.0", @"C:\ProgramData\Watcher\upd\last-result.json",
        @"C:\ProgramData\Watcher\upd\0.13.0\msi.log", @"C:\Program Files\Watcher", serverUrl, "MFkwEwYHKoZIzj0C", audit);

    [Fact]
    public void Runs_msiexec_with_current_settings()
    {
        string line = MsiUpdateHelper.CommandLine(J());
        Assert.StartsWith("/i \"C:\\ProgramData\\Watcher\\upd\\0.13.0\\package.msi\" /qn /norestart /l*v ", line);
        Assert.Contains("SERVERURL=\"https://watcher.corp\"", line);
        Assert.Contains("UPDATEKEY=\"MFkwEwYHKoZIzj0C\"", line);
        Assert.EndsWith("TERMINALAUDIT=1", line);
        Assert.DoesNotContain("ENROLLTOKEN", line);
        Assert.EndsWith("TERMINALAUDIT=0", MsiUpdateHelper.CommandLine(J(audit: false)));
        Assert.DoesNotContain("REINSTALLMODE", line);
        // Понижение версии целью группы — все файлы пакета, независимо от версии установленных.
        Assert.Contains(" REINSTALLMODE=amus ", MsiUpdateHelper.CommandLine(J(), downgrade: true));
        Assert.False(MsiUpdateHelper.IsDowngrade("0.16.0", @"C:\nonexistent\Watcher.SessionService.exe"));
    }

    [Fact]
    public void Quotes_in_values_are_refused() =>
        Assert.Throws<ArgumentException>(() => MsiUpdateHelper.CommandLine(J("https://x\" EVIL=1")));

    [Fact]
    public void Job_round_trips_through_json()
    {
        var job = J();
        var back = System.Text.Json.JsonSerializer.Deserialize<MsiUpdateJob>(System.Text.Json.JsonSerializer.Serialize(job, MsiUpdateHelper.Json), MsiUpdateHelper.Json);
        Assert.Equal(job, back);
    }

    [Theory]
    [InlineData(true, true, "windows-msi")]
    [InlineData(true, false, null)]   // ещё не зарегистрирован — MSI-апгрейду нечем заменить токен
    [InlineData(false, true, null)]   // не из MSI — обновлений нет (0.16: zip убран)
    public void Msi_only_for_registered_msi_installs(bool msi, bool cred, string? expected) =>
        Assert.Equal(expected, WindowsUpdateInstaller.Choose(msi, cred));
}

// 0.15.0: зависшая задача обновления не блокирует обновления навсегда.
public class StaleUpdateTaskTests
{
    [Fact]
    public void Task_older_than_two_hours_is_stale()
    {
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(UpdateTask.IsStale(now.AddMinutes(-20), now));
        Assert.True(UpdateTask.IsStale(now.AddHours(-3), now));
        Assert.True(UpdateTask.IsStale(now.AddHours(3), now));   // часы перевели
    }
}
