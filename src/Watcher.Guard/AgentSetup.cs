using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Watcher.Uploader;

namespace Watcher.Guard;

// 0.16: установка и удаление Windows-агента — бывший install.ps1, теперь в
// GuardRepair (custom actions MSI вызывают его напрямую, без PowerShell).
// MSI сам кладёт файлы: Capture, SessionService и ffmpeg — в каталог агента,
// GuardRepair — в WatcherGuard\setup. Здесь — конфиги, консольный аудит,
// служба и защита Guard. Порядок и поведение — как у install.ps1 0.15.
[SupportedOSPlatform("windows")]
public sealed class AgentSetup
{
    public const string ServiceName = "WatcherSessionService";

    // Журнал WatcherUpgradeRecovery\<ProductCode> от прошлой установки этого же продукта
    // (phase committed/rolled-back) — завершён и к текущей установке не относится:
    // восстанавливать из него конфиг нельзя (на стенде такой журнал ронял переустановку).
    public static bool PreparedUpgradeJournal(string recoveryRoot)
    {
        string state = Path.Combine(recoveryRoot, "state.json");
        if (!File.Exists(state) || new FileInfo(state).Length > 16 * 1024 * 1024) return false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(state));
            return document.RootElement.TryGetProperty("phase", out var phase) && phase.GetString() == "prepared";
        }
        catch (System.Text.Json.JsonException) { return false; }
    }
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\" + ServiceName;
    private const string AuditMarker = @"SOFTWARE\Watcher\TerminalAudit";
    private static readonly string[] TranscriptionKeys =
        [@"SOFTWARE\Policies\Microsoft\Windows\PowerShell\Transcription", @"SOFTWARE\Policies\Microsoft\PowerShellCore\Transcription"];
    private const string AuditKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\Audit";
    private const string ProcessCreationGuid = "{0CCE922B-69AE-11D9-BED3-505054503030}";

    public sealed record Options(string InstallDir, string ServerUrl = "", string ApiToken = "", string EnrollToken = "",
        string TerminalAudit = "1", string UpdateKey = "", string MaintenanceKey = "", string CaCert = "",
        string RecoverySource = "", string UpgradeRecoveryProduct = "", string PreviousServiceStart = "0");

    // --name=value: пустое значение свойства MSI доходит как есть (в отличие от
    // аргументов PowerShell 5.1 для нативных команд).
    public static Options Parse(IEnumerable<string> arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string argument in arguments)
        {
            int eq = argument.IndexOf('=');
            if (!argument.StartsWith("--", StringComparison.Ordinal) || eq < 3) throw new ArgumentException("Expected --name=value");
            values[argument[2..eq]] = argument[(eq + 1)..];
        }
        string V(string name) => values.GetValueOrDefault(name, "").Trim();
        string installDir = V("install-dir");
        if (installDir.Length == 0) throw new ArgumentException("--install-dir is required");
        return new Options(Path.GetFullPath(installDir.TrimEnd('.')).TrimEnd('\\'), V("server-url"), V("api-token"), V("enroll-token"),
            V("terminal-audit") is "0" ? "0" : "1", V("update-key"), V("maintenance-key"), V("ca-cert"), V("recovery-source"),
            V("upgrade-recovery-product"), V("previous-service-start") is { Length: > 0 } s ? s : "0");
    }

    private readonly Options _o;
    private readonly TextWriter _log;
    private readonly string _programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private string QueueDir => Path.Combine(_programData, "Watcher", "queue");
    private string SentDir => Path.Combine(_programData, "Watcher", "sent");
    private string LogDir => Path.Combine(_programData, "Watcher", "logs");
    private string TranscriptDir => Path.Combine(_programData, "Watcher", "transcripts");
    private string SessionJson => Path.Combine(_o.InstallDir, "Watcher.SessionService.json");

    public AgentSetup(Options options, TextWriter log)
    {
        _o = options;
        _log = log;
    }

    private void Say(string line) { _log.WriteLine(line); _log.Flush(); }

    // Журнал установки — в ProgramData\Watcher\logs: C:\Windows\Temp\watcher-install-ca.log
    // при неудачном обновлении перезаписывает откат прежней версией.
    public static TextWriter OpenLog(string suffix)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Watcher", "logs");
        Directory.CreateDirectory(directory);
        var file = new StreamWriter(Path.Combine(directory, "install-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + suffix + ".log"), append: true);
        return TextWriter.Synchronized(new TeeWriter(file, Console.Out));
    }

    public void Install()
    {
        RequireAdmin();
        string root = GuardPaths.Root;
        string serverUrl = _o.ServerUrl, updateKey = _o.UpdateKey, maintenanceKey = _o.MaintenanceKey;
        bool preserve = false;
        // Защищённая установка обслуживается только по подписанному разрешению.
        if (File.Exists(Path.Combine(root, "enabled")))
        {
            Authorize(root, "update");
            var policy = GuardPaths.Load(root) ?? throw new IOException("Guard policy missing");
            maintenanceKey = policy.MaintenancePublicKey; updateKey = policy.UpdatePublicKey; serverUrl = policy.ServerUrl;
            preserve = File.Exists(Path.Combine(root, "initialized")) && !File.Exists(Path.Combine(root, "uninstalled.json"));
        }
        if (serverUrl.Length == 0) throw new ArgumentException("SERVERURL is required, e.g. msiexec /i watcher-agent.msi SERVERURL=https://watcher.corp");
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server) || server.Scheme is not ("http" or "https"))
            throw new ArgumentException("SERVERURL must start with http:// or https://");
        if (server.Scheme == "http" && server.Host is not ("localhost" or "127.0.0.1"))
            Say($"WARNING: SERVERURL uses http:// to {server.Host} — video, console input and tokens travel unencrypted. Use https://.");
        string credential = Path.Combine(_programData, "Watcher", "agent", "credential.json");
        if (_o.EnrollToken.Length == 0 && _o.ApiToken.Length == 0 && !File.Exists(credential) && !preserve)
            Say("WARNING: no ENROLLTOKEN, no APITOKEN and no agent credential yet — uploads will not work until the agent is registered.");
        if (_o.CaCert.Length > 0) ImportCa(_o.CaCert);
        foreach (string file in new[] { "Watcher.Capture.exe", "Watcher.SessionService.exe", "ffmpeg.exe" })
            if (!File.Exists(Path.Combine(_o.InstallDir, file))) throw new FileNotFoundException($"{file} not found in {_o.InstallDir}");

        Say("[1/6] Stopping previous installation (if any)...");
        // Считать до удаления службы: ручной и отключённый режимы сохраняются.
        string start = "delayed-auto";
        int previous = int.TryParse(_o.PreviousServiceStart.TrimStart('#'), out int p) ? p : 0;
        if (previous == 0) previous = Registry.LocalMachine.OpenSubKey(ServiceKey)?.GetValue("Start") is int s ? s : 0;
        if (previous == 3) start = "demand";
        if (previous == 4) start = "disabled";
        StopLegacyTasks();
        StopAndDeleteService();

        Say("[2/6] Preparing directories...");
        foreach (string directory in new[] { _o.InstallDir, QueueDir, SentDir, LogDir }) Directory.CreateDirectory(directory);
        Say("[3/6] Binaries are installed by MSI");

        Say("[4/6] Writing configs...");
        string? recoveryRoot = null;
        if (_o.UpgradeRecoveryProduct.Length > 0)
        {
            recoveryRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WatcherUpgradeRecovery", _o.UpgradeRecoveryProduct.Trim('{', '}'));
            if (PreparedUpgradeJournal(recoveryRoot))
            {
                LegacyMsiRecovery.RestoreConfig(_o.UpgradeRecoveryProduct);
                preserve = true;
            }
        }
        bool terminalAudit = _o.TerminalAudit == "1";
        if (!preserve)
        {
            WriteConfigs(serverUrl, updateKey, terminalAudit);
            if (terminalAudit) { Say("      Console audit: enabling PowerShell transcription and process creation audit..."); EnableTerminalAudit(TranscriptDir); }
            else DisableTerminalAudit();
        }
        // Защищённое обновление: удаление прежней версии MSI стёрло Watcher.*.json, их
        // вернёт Guard ниже. До этого конфиг может отсутствовать.
        if (preserve && File.Exists(SessionJson)) terminalAudit = SavedAudit().Enabled;

        Say("[5/6] Registering service...");
        string exe = Path.Combine(_o.InstallDir, "Watcher.SessionService.exe");
        Sc(["create", ServiceName, "binPath=", "\"" + exe + "\"", "start=", start == "delayed-auto" ? "auto" : start, "obj=", "LocalSystem", "DisplayName=", "Watcher Session Service"]);
        if (start == "delayed-auto") Sc(["config", ServiceName, "start=", "delayed-auto"]);
        Say("Service startup: " + start);
        // Перезапуск службы при падении: 3 попытки с паузой 15 с, счётчик — за сутки.
        Sc(["failure", ServiceName, "reset=", "86400", "actions=", "restart/15000/restart/15000/restart/15000"]);

        if (maintenanceKey.Length > 0)
        {
            if (recoveryRoot is not null && PreparedUpgradeJournal(recoveryRoot))
                File.WriteAllText(Path.Combine(recoveryRoot, "guard-started"), _o.UpgradeRecoveryProduct);
            var seeded = GuardWindowsSetup.Install(root, _o.InstallDir, maintenanceKey, updateKey, serverUrl, _o.RecoverySource);
            if (seeded == MsiRecoverySource.Result.NoSignature)
                Say("WARNING: no watcher-agent.msi.sig next to the MSI — protection is on, but deleted agent files are restored only after the first update via Guard.");
            else if (seeded == MsiRecoverySource.Result.SignatureMismatch)
                Say("WARNING: the .sig next to the MSI does not match it (left over from another kit?) — protection is on without a recovery base until the first update via Guard.");
            if (preserve)
            {
                // Прежний MSI снимает настройки аудита при удалении; Guard вернул конфиг.
                var saved = SavedAudit();
                terminalAudit = saved.Enabled;
                if (saved.Enabled) EnableTerminalAudit(saved.TranscriptDir); else DisableTerminalAudit();
            }
        }

        if (start == "delayed-auto")
        {
            Say("[6/6] Starting service...");
            using var service = new ServiceController(ServiceName);
            if (maintenanceKey.Length == 0)
            {
                if (service.Status == ServiceControllerStatus.Stopped) service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            }
            else if (service.Status != ServiceControllerStatus.Running) throw new InvalidOperationException("Guard did not start the service");
        }
        else Say($"[6/6] Preserved service startup: {start} (not started automatically)");
        Say($"Installed: {_o.InstallDir}; server {serverUrl}; console audit {(terminalAudit ? "on" : "off")}; service {new ServiceController(ServiceName).Status}");
    }

    // RunUninstallScript MSI (REMOVE="ALL"), в том числе при удалении прежней версии во время обновления.
    public void Uninstall()
    {
        RequireAdmin();
        if (File.Exists(Path.Combine(GuardPaths.Root, "enabled"))) Authorize(GuardPaths.Root, "uninstall");
        Say("[1/3] Stopping service...");
        StopLegacyTasks();
        StopAndDeleteService();
        DisableTerminalAudit();
        // Конфиги пишет установщик, а не MSI (их нет в таблице File) — RemoveFiles их не
        // тронет, и Watcher.Uploader.json с токеном остался бы на диске.
        Say("[2/3] Removing configs; binaries are removed by MSI...");
        if (Directory.Exists(_o.InstallDir))
            foreach (string file in Directory.EnumerateFiles(_o.InstallDir, "Watcher.*.json")) File.Delete(file);
        Say($"[3/3] Keeping data in {Path.Combine(_programData, "Watcher")}");
    }

    private static void RequireAdmin()
    {
        var identity = WindowsIdentity.GetCurrent();
        if (identity.IsSystem) return;
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("Run as Administrator");
    }

    // Как прежний install.ps1: проверяет действующий Guard из bin\<launcher>.
    private static void Authorize(string root, string action)
    {
        string hash = File.ReadAllText(Path.Combine(root, "launcher")).Trim();
        string exe = GuardWindowsSetup.VerifiedExecutable(root, hash);
        if (Run(exe, ["authorize", action], 60) != 0) throw new UnauthorizedAccessException("Signed maintenance authorization required");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5380", Justification = "Корневой CA собственного сервера Watcher (Caddy tls internal) из подписанного комплекта install-server.sh — так же импортировал install.ps1; без него агент не доверяет серверу.")]
    private void ImportCa(string base64)
    {
        Say("[0/6] Importing the server root CA into LocalMachine\\Root...");
        using var certificate = new X509Certificate2(Convert.FromBase64String(base64.Trim()));
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
    }

    private void StopLegacyTasks()
    {
        // Задачи установок до D1/D2: WatcherCapture, WatcherUploader. Ошибка снятия не
        // должна проваливать установку (задача могла как раз запускаться).
        foreach (string task in new[] { "WatcherCapture", "WatcherUploader" })
            if (GuardWindowsTask.Query(@"\" + task) is not null)
                try { GuardWindowsTask.Delete(@"\" + task); }
                catch (IOException e) { Say($"WARNING: could not unregister task {task}: {e.Message}"); }
        foreach (string name in new[] { "Watcher.Capture", "Watcher.Uploader" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static void StopAndDeleteService()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
        if (key is null) return;
        try
        {
            using var service = new ServiceController(ServiceName);
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ServiceProcess.TimeoutException) { }
        Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["delete", ServiceName], 30);
        Thread.Sleep(500);
    }

    private void WriteConfigs(string serverUrl, string updateKey, bool terminalAudit)
    {
        WriteJson("Watcher.Capture.json", new JsonObject
        {
            ["Capture"] = new JsonObject
            {
                ["QueueDir"] = QueueDir,
                ["LogDir"] = LogDir,
                ["FfmpegPath"] = Path.Combine(_o.InstallDir, "ffmpeg.exe"),
                ["CaptureIntervalMs"] = 500,
                ["DownscaleWidth"] = 1920,
                ["DownscaleHeight"] = 1080,
                ["MotionThresholdPercent"] = 0.5,
                ["MotionThresholdPixelDelta"] = 25,
                ["IdleSecondsToCloseSegment"] = 10,
                ["MaxSegmentSeconds"] = 300,
                ["PreRollSeconds"] = 10,
                ["OutputFps"] = 2,
                ["Crf"] = 30,
                ["Preset"] = "ultrafast",
            },
        });
        // Ключ обновлений не передан — оставить тот, что уже стоит (переустановка без
        // UPDATEKEY не должна молча выключать автообновление).
        string uploader = Path.Combine(_o.InstallDir, "Watcher.Uploader.json");
        if (updateKey.Length == 0 && File.Exists(uploader))
            try { updateKey = JsonNode.Parse(File.ReadAllText(uploader))?["Uploader"]?["UpdatePublicKey"]?.GetValue<string>() ?? ""; }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        WriteJson("Watcher.Uploader.json", new JsonObject
        {
            ["Uploader"] = new JsonObject
            {
                ["UpdatePublicKey"] = updateKey,
                ["UpdateDir"] = Path.Combine(_programData, "Watcher", "upd"),
                ["QueueDir"] = QueueDir,
                ["SentDir"] = SentDir,
                ["LogDir"] = LogDir,
                ["ServerUrl"] = serverUrl,
                ["PollIntervalSeconds"] = 10,
                ["FileQuietSeconds"] = 5,
                ["MaxRetryDelaySeconds"] = 300,
                ["DeleteAfterUpload"] = true,
                ["SentRetentionDays"] = 1,
                ["SentMaxBytes"] = 1073741824,
                ["ApiToken"] = _o.ApiToken,
                ["EnrollToken"] = _o.EnrollToken,
                ["FfmpegPath"] = Path.Combine(_o.InstallDir, "ffmpeg.exe"),
            },
        });
        ProtectSecret(uploader);
        WriteJson("Watcher.SessionService.json", new JsonObject
        {
            ["SessionService"] = new JsonObject
            {
                ["CaptureExePath"] = Path.Combine(_o.InstallDir, "Watcher.Capture.exe"),
                ["LogDir"] = Path.Combine(_programData, "Watcher", "session-service-logs"),
                ["LogRetainedDays"] = 14,
                ["LogFileSizeLimitMb"] = 10,
                ["TerminalAudit"] = new JsonObject { ["Enabled"] = terminalAudit, ["TranscriptDir"] = TranscriptDir },
            },
        });
    }

    private void WriteJson(string name, JsonObject value) =>
        File.WriteAllText(Path.Combine(_o.InstallDir, name), value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    // Watcher.Uploader.json содержит токены — только SYSTEM и Administrators.
    private static void ProtectSecret(string path)
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        foreach (var sid in new[] { system, admins }) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.SetOwner(admins);
        new FileInfo(path).SetAccessControl(acl);
    }

    private (bool Enabled, string TranscriptDir) SavedAudit()
    {
        var audit = JsonNode.Parse(File.ReadAllText(SessionJson))?["SessionService"]?["TerminalAudit"];
        return (audit?["Enabled"]?.GetValue<bool>() == true, audit?["TranscriptDir"]?.GetValue<string>() ?? TranscriptDir);
    }

    // --- Консольный аудит (Задача F1): транскрипция PowerShell + события 4688 ------
    // Что включил именно установщик — в HKLM\SOFTWARE\Watcher\TerminalAudit: при
    // выключении откатываем только своё (политику GPO и аудит, включённый до нас, не трогаем).
    public void EnableTerminalAudit(string transcriptDir)
    {
        Directory.CreateDirectory(transcriptDir);
        // SYSTEM/Administrators — полный; Users — создать файл/каталог и писать в свой;
        // OWNER RIGHTS урезает неявные права владельца: после закрытия сессии
        // пользователь не может ни прочитать, ни дописать, ни удалить свой транскрипт.
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        const FileSystemRights users = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.ExecuteFile | FileSystemRights.ReadAttributes | FileSystemRights.Synchronize;
        const FileSystemRights owner = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.ReadAttributes | FileSystemRights.Synchronize;
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-18"), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-544"), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), users, inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-3-4"), owner, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(transcriptDir).SetAccessControl(acl);

        // Сначала 4688 и командная строка, затем транскрипция: новые процессы не должны
        // создавать транскрипты раньше появления событий их запуска.
        using var marker = Registry.LocalMachine.CreateSubKey(AuditMarker);
        if (!ProcessCreationAudit())
        {
            if (Run(Path.Combine(Environment.SystemDirectory, "auditpol.exe"), ["/set", "/subcategory:" + ProcessCreationGuid, "/success:enable"], 60) != 0)
                throw new IOException("auditpol /set failed");
            marker.SetValue("EnabledProcessAudit", 1, RegistryValueKind.DWord);
            if (!ProcessCreationAudit()) throw new IOException("Process creation audit is not enabled; transcription was not enabled");
        }
        using (var audit = Registry.LocalMachine.CreateSubKey(AuditKey))
            if (audit.GetValue("ProcessCreationIncludeCmdLine_Enabled") is not 1)
            {
                audit.SetValue("ProcessCreationIncludeCmdLine_Enabled", 1, RegistryValueKind.DWord);
                marker.SetValue("EnabledCmdLine", 1, RegistryValueKind.DWord);
            }
        foreach (string path in TranscriptionKeys)
        {
            using var key = Registry.LocalMachine.CreateSubKey(path);
            key.SetValue("EnableTranscripting", 1, RegistryValueKind.DWord);
            key.SetValue("EnableInvocationHeader", 1, RegistryValueKind.DWord);
            key.SetValue("OutputDirectory", transcriptDir);
        }
        using (var core = Registry.LocalMachine.CreateSubKey(TranscriptionKeys[1])) core.SetValue("UseWindowsPowerShellPolicySetting", 0, RegistryValueKind.DWord);
        marker.SetValue("TranscriptDir", transcriptDir);
        // С cmd.exe в роли shell у sshd PowerShell не запускается — транскрипта нет.
        string? shell = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\OpenSSH")?.GetValue("DefaultShell") as string;
        if (shell is null || !System.Text.RegularExpressions.Regex.IsMatch(shell, @"(powershell|pwsh)\.exe$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            Say($"WARNING: sshd DefaultShell is '{shell}' (cmd.exe by default): SSH sessions will NOT be recorded. Set HKLM\\SOFTWARE\\OpenSSH\\DefaultShell to powershell.exe.");
    }

    public void DisableTerminalAudit()
    {
        using var marker = Registry.LocalMachine.OpenSubKey(AuditMarker);
        if (marker is null) return;
        string? transcriptDir = marker.GetValue("TranscriptDir") as string;
        foreach (string path in TranscriptionKeys)
        {
            // Только если политика всё ещё наша — GPO мог перенастроить.
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key is not null && transcriptDir is not null && string.Equals(key.GetValue("OutputDirectory") as string, transcriptDir, StringComparison.OrdinalIgnoreCase))
            { key.Dispose(); Registry.LocalMachine.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
        }
        if (marker.GetValue("EnabledProcessAudit") is 1)
            Run(Path.Combine(Environment.SystemDirectory, "auditpol.exe"), ["/set", "/subcategory:" + ProcessCreationGuid, "/success:disable"], 60);
        if (marker.GetValue("EnabledCmdLine") is 1)
            using (var audit = Registry.LocalMachine.OpenSubKey(AuditKey, writable: true)) audit?.DeleteValue("ProcessCreationIncludeCmdLine_Enabled", throwOnMissingValue: false);
        if (transcriptDir is not null && Directory.Exists(transcriptDir))
            try { Directory.Delete(transcriptDir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        marker.Dispose();
        Registry.LocalMachine.DeleteSubKeyTree(AuditMarker, throwOnMissingSubKey: false);
        Say("Console audit disabled (transcription policies and process audit enabled by the installer were reverted).");
    }

    // Флаг аудита создания процессов читает собранный агент через WinAPI (не разбор
    // локализованного вывода auditpol).
    private bool ProcessCreationAudit()
    {
        var info = new ProcessStartInfo(Path.Combine(_o.InstallDir, "Watcher.SessionService.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("--process-creation-audit");
        using var process = Process.Start(info) ?? throw new IOException("Cannot query process creation audit policy");
        string state = process.StandardOutput.ReadToEnd().Trim();
        if (!process.WaitForExit(60000) || process.ExitCode != 0 || state is not ("enabled" or "disabled")) throw new IOException("Cannot query process creation audit policy");
        return state == "enabled";
    }

    private static void Sc(string[] arguments)
    {
        if (Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), arguments, 60) != 0) throw new IOException("sc.exe " + arguments[0] + " failed");
    }

    private static int Run(string file, string[] arguments, int seconds)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start " + Path.GetFileName(file));
        if (!process.WaitForExit(seconds * 1000)) { process.Kill(entireProcessTree: true); throw new IOException(Path.GetFileName(file) + " timed out"); }
        return process.ExitCode;
    }

    private sealed class TeeWriter(TextWriter first, TextWriter second) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value) { first.Write(value); second.Write(value); }
        public override void Write(string? value) { first.Write(value); second.Write(value); }
        public override void WriteLine(string? value) { first.WriteLine(value); second.WriteLine(value); }
        public override void Flush() { first.Flush(); second.Flush(); }
        protected override void Dispose(bool disposing) { if (disposing) first.Dispose(); base.Dispose(disposing); }
    }
}
