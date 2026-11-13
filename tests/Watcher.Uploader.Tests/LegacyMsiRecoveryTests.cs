using System.Runtime.Versioning;
using Watcher.Guard;
using Xunit;

namespace Watcher.Uploader.Tests;

// 0.16: порт installer/legacy-msi-recovery.ps1 — части без Windows API.
[SupportedOSPlatform("windows")]
public class LegacyMsiRecoveryTests
{
    // Так ConvertTo-Json писал state.json в 0.15.x: журнал прошлой попытки должен читаться.
    private const string Ps1State = """
    {
        "phase":  "committed",
        "previousProduct":  "{11111111-2222-3333-4444-555555555555}",
        "previousVersion":  "0.15.2",
        "packageHash":  "ABCDEF",
        "directory":  "C:\\Program Files\\Watcher",
        "directorySddl":  "O:SYG:SYD:(A;;FA;;;SY)",
        "start":  2,
        "delayed":  true,
        "running":  true,
        "sddl":  "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)",
        "files":  [
                      { "path": "C:\\Program Files\\Watcher\\Watcher.Uploader.json", "backup": "Watcher.Uploader.json", "hash": "01", "sddl": "O:SY" },
                      { "path": "C:\\ProgramData\\Watcher\\transcripts\\a.txt", "backup": "transcript-3.bin", "hash": "02", "sddl": "O:SY", "appendOnly": true }
                  ],
        "serverUrl":  "https://watcher.test",
        "updateKey":  "MFkw",
        "apiToken":  "",
        "audit":  true,
        "processAudit":  1,
        "binaryHash":  "03",
        "binaries":  { "Watcher.SessionService.exe": "03", "install.ps1": "04" },
        "auditBackups":  [ { "key": "SOFTWARE\\Watcher\\TerminalAudit", "backup": "audit-0.reg" } ],
        "commandLineAudit":  null,
        "directories":  [ ]
    }
    """;

    [Fact]
    public void Ps1_written_journal_is_readable()
    {
        string file = Path.Combine(Path.GetTempPath(), "watcher-state-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, Ps1State);
        try
        {
            var state = LegacyMsiRecovery.LoadState(file);
            Assert.Equal("committed", state.Phase);
            Assert.Equal(2, state.Start);
            Assert.True(state.Delayed && state.Running && state.Audit);
            Assert.Equal(2, state.Files.Count);
            Assert.False(state.Files[0].AppendOnly);
            Assert.True(state.Files[1].AppendOnly);
            Assert.Equal("04", state.Binaries["install.ps1"]);
            Assert.Null(state.CommandLineAudit);
            Assert.Equal("audit-0.reg", Assert.Single(state.AuditBackups).Backup);
            Assert.Empty(state.Directories);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("{B57CFE32-A5D4-4ACC-8E2E-796E4F6B05C7}", true)]
    [InlineData("B57CFE32-A5D4-4ACC-8E2E-796E4F6B05C7", false)]
    [InlineData("{..\\..\\Windows}", false)]
    [InlineData("{B57CFE32-A5D4-4ACC-8E2E-796E4F6B05C7}\" /x", false)]
    public void Product_code_is_validated_before_building_paths(string product, bool valid)
    {
        Assert.Equal(valid, LegacyMsiRecovery.ValidProduct(product));
        if (!valid) Assert.Throws<ArgumentException>(() => LegacyMsiRecovery.Root(product));
    }

    [Theory]
    [InlineData("Machine Name,Policy Target,Subcategory,Subcategory GUID,Inclusion Setting,Exclusion Setting,Setting Value\r\nPC,System,Process Creation,{0CCE922B-69AE-11D9-BED3-505054503030},Success,,1\r\nPC,System,Logon,{0CCE9215-69AE-11D9-BED3-505054503030},Success,,1\r\n", 1)]
    // Локализованные заголовки в OEM-кодировке и запятая в имени подкатегории — смотрим только GUID и последнее поле.
    [InlineData("\u008f\u00e0,\u0090,x\r\nPC,\u0421\u0438\u0441\u0442\u0435\u043c\u0430,\u0421\u043e\u0437\u0434\u0430\u043d\u0438\u0435, \u043f\u0440\u043e\u0446\u0435\u0441\u0441\u0430,{0cce922b-69ae-11d9-bed3-505054503030},,,3\n", 3)]
    public void Process_audit_flags_come_from_guid_row(string csv, int flags) =>
        Assert.Equal(flags, LegacyMsiRecovery.ProcessAuditFlags(csv));

    [Theory]
    [InlineData("header\r\nPC,System,Logon,{0CCE9215-69AE-11D9-BED3-505054503030},Success,,1\r\n")]
    [InlineData("header\r\nPC,System,Process Creation,{0CCE922B-69AE-11D9-BED3-505054503030},Success,,7\r\n")]
    public void Missing_or_invalid_process_audit_row_stops_preparation(string csv) =>
        Assert.Throws<IOException>(() => LegacyMsiRecovery.ProcessAuditFlags(csv));

    [Fact]
    public void Rollback_reinstalls_previous_msi_with_saved_server_settings()
    {
        var state = new LegacyUpgradeState { ServerUrl = "https://watcher.test", UpdateKey = "MFkw", Audit = true };
        string args = LegacyMsiRecovery.RollbackArguments(@"C:\Program Files\WatcherUpgradeRecovery\X\previous.msi", @"C:\log.txt", state);
        Assert.Equal("/i \"C:\\Program Files\\WatcherUpgradeRecovery\\X\\previous.msi\" /qn /norestart /l*v \"C:\\log.txt\" SERVERURL=\"https://watcher.test\" UPDATEKEY=\"MFkw\" TERMINALAUDIT=1 MAINTENANCEKEY=\"\"", args);
        state.ApiToken = "tok";
        Assert.EndsWith(" APITOKEN=\"tok\"", LegacyMsiRecovery.RollbackArguments("p.msi", "l", state));
        state.ServerUrl = "https://x\" REBOOT=Force";
        Assert.Throws<IOException>(() => LegacyMsiRecovery.RollbackArguments("p.msi", "l", state));
    }

    [Fact]
    public void Recovery_task_runs_every_minute_from_private_copy()
    {
        string root = LegacyMsiRecovery.Root("{B57CFE32-A5D4-4ACC-8E2E-796E4F6B05C7}");
        string xml = GuardWindowsTask.TaskXml(Path.Combine(root, "recover.exe"), "legacy-msi-recover {B57CFE32-A5D4-4ACC-8E2E-796E4F6B05C7}", repeating: true, "d", interval: "PT1M", limit: "PT15M");
        Assert.Contains("<Interval>PT1M</Interval>", xml);
        Assert.Contains("<ExecutionTimeLimit>PT15M</ExecutionTimeLimit>", xml);
        var action = GuardWindowsTask.ParseAction(xml)!;
        Assert.EndsWith("recover.exe", action.Command);
        Assert.Equal("S-1-5-18", action.UserId);
        Assert.Equal(@"\WatcherUpgradeRecovery-B57CFE32-A5D4-4ACC-8E2E-796E4F6B05C7", LegacyMsiRecovery.TaskName("{B57CFE32-A5D4-4ACC-8E2E-796E4F6B05C7}"));
        Assert.Contains("<SecurityDescriptor>O:SYG:SYD:", xml);
        // Bootstrap от администратора: владельца SYSTEM он назначить не может.
        Assert.Contains("<SecurityDescriptor>D:P(A;;GA;;;SY)(A;;GA;;;BA)</SecurityDescriptor>",
            GuardWindowsTask.TaskXml("a", "b", repeating: false, "d", security: GuardWindowsTask.BootstrapTaskSecurity));
        // Задача Guard по умолчанию — прежние 2 минуты.
        Assert.Contains("<Interval>PT2M</Interval>", GuardWindowsTask.TaskXml("a", "tick", repeating: true, "d"));
    }

    [Fact]
    public void Msi_owned_guard_setup_folder_is_not_an_unfinished_guard()
    {
        string root = Path.Combine(Path.GetTempPath(), "watcher-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(LegacyMsiRecovery.GuardStarted(root));
            Directory.CreateDirectory(Path.Combine(root, "setup"));
            File.WriteAllText(Path.Combine(root, "setup", "GuardRepair.exe"), "x");
            Assert.False(LegacyMsiRecovery.GuardStarted(root));
            File.WriteAllText(Path.Combine(root, "policy.json"), "{}");
            Assert.True(LegacyMsiRecovery.GuardStarted(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
