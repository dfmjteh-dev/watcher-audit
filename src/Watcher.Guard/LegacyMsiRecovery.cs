using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Watcher.Guard;

// Журнал подготовки (WatcherUpgradeRecovery\<ProductCode>\state.json). Имена полей —
// как у installer/legacy-msi-recovery.ps1 до 0.16: журналы прежних попыток читаются.
public sealed class LegacyUpgradeState
{
    public string Phase { get; set; } = "prepared";
    public string PreviousProduct { get; set; } = "";
    public string PreviousVersion { get; set; } = "";
    public string PackageHash { get; set; } = "";
    public string Directory { get; set; } = "";
    public string DirectorySddl { get; set; } = "";
    public int Start { get; set; }
    public bool Delayed { get; set; }
    public bool Running { get; set; }
    public string Sddl { get; set; } = "";
    public List<LegacyStateFile> Files { get; set; } = [];
    public string? ServerUrl { get; set; }
    public string? UpdateKey { get; set; }
    public string? ApiToken { get; set; }
    public bool Audit { get; set; }
    public int ProcessAudit { get; set; }
    public string BinaryHash { get; set; } = "";
    public Dictionary<string, string> Binaries { get; set; } = [];
    public List<LegacyAuditBackup> AuditBackups { get; set; } = [];
    public int? CommandLineAudit { get; set; }
    public List<LegacyStateDirectory> Directories { get; set; } = [];
}
public sealed record LegacyStateFile(string Path, string Backup, string Hash, string Sddl, bool AppendOnly = false);
public sealed record LegacyAuditBackup(string Key, string Backup);
public sealed record LegacyStateDirectory(string Path, string Sddl);

// 0.16: подготовка отката при обновлении установки БЕЗ Guard — бывший
// installer/legacy-msi-recovery.ps1, теперь без PowerShell.
//   prepare (immediate CA PrepareLegacyRecovery, до RemoveExistingProducts): снимок
//     конфигов, учёток, транскриптов, настроек аудита и исходного подписанного MSI
//     прежней версии + SYSTEM-задача WatcherUpgradeRecovery-<код>;
//   restore-config (agent-install): вернуть снятые конфиги новой версии;
//   recover (та задача, раз в минуту): новая версия встала и служба в ожидаемом
//     состоянии — журнал committed; иначе вернуть прежнюю версию целиком.
// Защищённые установки (WatcherGuard\enabled) откатывает сам Guard — здесь их не трогаем.
[SupportedOSPlatform("windows")]
public static class LegacyMsiRecovery
{
    private const string SystemSid = "S-1-5-18", AdminsSid = "S-1-5-32-544";
    private const string UpgradeCode = "{B57CFE32-A5D4-4ACC-8E2E-796E4F6B05C7}";
    private const string ServiceName = "WatcherSessionService";
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\" + ServiceName;
    private const string ProcessCreationGuid = "{0CCE922B-69AE-11D9-BED3-505054503030}";
    private const string CmdAuditKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\Audit";
    private static readonly string[] AuditKeys =
        [@"SOFTWARE\Policies\Microsoft\Windows\PowerShell\Transcription", @"SOFTWARE\Policies\Microsoft\PowerShellCore\Transcription", @"SOFTWARE\Watcher\TerminalAudit"];
    private static readonly string[] ConfigNames = ["Watcher.Capture.json", "Watcher.SessionService.json", "Watcher.Uploader.json"];
    private static readonly string[] BinaryNames = ["Watcher.SessionService.exe", "Watcher.Capture.exe", "ffmpeg.exe"];
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static string Base => Path.Combine(ProgramFiles, "WatcherUpgradeRecovery");
    private static string GuardRoot => Path.Combine(ProgramFiles, "WatcherGuard");
    private static bool IsSystem => WindowsIdentity.GetCurrent().User?.Value == SystemSid;

    // С 0.16 MSI всегда кладёт GuardRepair в WatcherGuard\setup — и без защиты.
    // Начатая установка Guard — это любое другое содержимое (policy, учётки, bin).
    public static bool GuardStarted(string guardRoot) =>
        System.IO.Directory.Exists(guardRoot) && System.IO.Directory.EnumerateFileSystemEntries(guardRoot)
            .Any(e => !Path.GetFileName(e).Equals("setup", StringComparison.OrdinalIgnoreCase));

    public static bool ValidProduct(string product) => Regex.IsMatch(product, @"^\{[A-Fa-f0-9-]{36}\}$");
    private static string Code(string product) => ValidProduct(product) ? product.Trim('{', '}') : throw new ArgumentException("Invalid recovery product code");
    public static string Root(string product) => Path.Combine(Base, Code(product));
    private static string StateFile(string product) => Path.Combine(Root(product), "state.json");
    public static string TaskName(string product) => @"\WatcherUpgradeRecovery-" + Code(product);

    // Точка входа подкоманд; ошибка — в %SystemRoot%\Logs\WatcherUpgradeRecovery\recovery.log и код 1.
    public static int Run(string phase, string product, string? resultNonce = null)
    {
        try
        {
            switch (phase)
            {
                case "prepare": Prepare(product); break;
                case "restore-config": RestoreConfig(product); break;
                case "recover": Recover(product); break;
                default: throw new ArgumentException("Unknown recovery phase");
            }
            WriteBootstrapResult(resultNonce, "ok");
            return 0;
        }
        catch (Exception e)
        {
            string message = "Legacy recovery " + phase + ": " + e.Message;
            try
            {
                string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "WatcherUpgradeRecovery");
                System.IO.Directory.CreateDirectory(logs);
                File.AppendAllText(Path.Combine(logs, "recovery.log"), DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine);
            }
            catch (Exception log) when (log is IOException or UnauthorizedAccessException) { }
            WriteBootstrapResult(resultNonce, message);
            Console.Error.WriteLine(message);
            return 1;
        }
    }

    // ---------- prepare ----------

    public static void Prepare(string product)
    {
        _ = Code(product);
        using (var key = Registry.LocalMachine.OpenSubKey(ServiceKey)) if (key is null) return;
        if (File.Exists(Path.Combine(GuardRoot, "enabled"))) return;
        if (!IsSystem) { Bootstrap(product); return; }
        if (GuardStarted(GuardRoot)) throw new IOException("Unfinished Guard installation requires recovery before upgrade");

        string root = Root(product), stateFile = StateFile(product);
        NewPrivateDirectory(Base);
        string? archive = null, priorPackage = null;
        if (System.IO.Directory.Exists(root))
        {
            AssertPrivateDirectory(root);
            var prior = LoadState(stateFile);
            if (prior.Phase is not ("committed" or "rolled-back")) throw new IOException("An unfinished recovery must complete before retrying");
            archive = root + ".previous-" + Guid.NewGuid().ToString("N");
            System.IO.Directory.Move(root, archive);
            priorPackage = Path.Combine(archive, "previous.msi");
        }
        NewPrivateDirectory(root);
        bool prepared = false;
        try
        {
            using var registration = Registry.LocalMachine.OpenSubKey(ServiceKey) ?? throw new IOException("Unexpected legacy service registration");
            string image = ((registration.GetValue("ImagePath") as string) ?? "").Trim().Trim('"');
            string dir = Path.GetDirectoryName(image) ?? throw new IOException("Unexpected legacy service registration");
            int start = registration.GetValue("Start") is int s ? s : 0;
            if (!string.Equals(registration.GetValue("ObjectName") as string, "LocalSystem", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(image), "Watcher.SessionService.exe", StringComparison.OrdinalIgnoreCase) || start is not (2 or 3 or 4))
                throw new IOException("Unexpected legacy service registration");
            if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked legacy installation");

            // Только установленные продукты (state 5): после сбойных установок Windows
            // Installer хранит «осиротевшие» регистрации того же UpgradeCode (state −1) —
            // из-за них обновление останавливалось (найдено на стенде, ps1 до 0.16 так же).
            var related = Msi.RelatedProducts(UpgradeCode).Where(Msi.Installed).ToList();
            if (related.Count != 1) throw new IOException("Expected one installed previous MSI product, found " + related.Count);
            string oldProduct = related[0];
            string localPackage = Msi.ProductInfo(oldProduct, "LocalPackage");
            string oldVersion = Msi.ProductInfo(oldProduct, "VersionString");
            string installerCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer") + "\\";
            if (!Path.GetFullPath(localPackage).StartsWith(installerCache, StringComparison.OrdinalIgnoreCase)) throw new IOException("Previous MSI is outside the trusted Installer cache");

            // Кэш Windows Installer не содержит cabinet. Берём исходный полный MSI,
            // проверяем подпись тем же издателем, что у установленного exe, и потоки.
            string source = Path.Combine(Msi.ProductInfo(oldProduct, "InstallSource"), Msi.ProductInfo(oldProduct, "PackageName"));
            if (!File.Exists(source) && priorPackage is not null) source = priorPackage;
            if (!File.Exists(source)) throw new IOException("Original signed MSI unavailable; upgrade stopped before removing the old product");
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked previous MSI source");
            string previous = Path.Combine(root, "previous.msi");
            CopyPrivateFile(source, previous);
            string? msiSigner = Authenticode.Signer(previous), exeSigner = Authenticode.Signer(image);
            if (msiSigner is null || exeSigner is null || msiSigner != exeSigner) throw new IOException("Previous MSI publisher verification failed");
            Msi.VerifyPackage(previous, oldProduct, oldVersion, UpgradeCode);
            if (new FileInfo(previous).Length > 1L << 30 || FreeSpace(root) < 2L << 30) throw new IOException("Insufficient rollback space or oversized MSI");

            var files = new List<LegacyStateFile>();
            foreach (string name in ConfigNames)
            {
                string from = Path.Combine(dir, name);
                if (new FileInfo(from).Length > 1 << 20) throw new IOException("Oversized legacy configuration");
                files.Add(Snapshot(root, from, name, appendOnly: false));
            }
            var uploader = Section(Path.Combine(dir, "Watcher.Uploader.json"), "Uploader");
            var service = Section(Path.Combine(dir, "Watcher.SessionService.json"), "SessionService");
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string[] credentials =
            [
                Str(uploader, "CredentialPath") is { Length: > 0 } a ? a : Path.Combine(programData, @"Watcher\agent\credential.json"),
                Str(Get(service, "TerminalAudit"), "CredentialPath") is { Length: > 0 } b ? b : Path.Combine(programData, @"Watcher\agent\terminal-credential.json"),
            ];
            foreach (string from in credentials)
            {
                if (!File.Exists(from)) continue;
                if (!Path.IsPathRooted(from) || new FileInfo(from).Length > 64 * 1024) throw new IOException("Invalid legacy identity");
                files.Add(Snapshot(root, from, "identity-" + files.Count + ".json", appendOnly: false));
            }

            var (sdExit, sdOutput) = Exec("sc.exe", ["sdshow", ServiceName]);
            string serviceSddl = string.Concat(sdOutput.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("O:") || l.StartsWith("G:") || l.StartsWith("D:")));
            if (sdExit != 0 || serviceSddl.Length == 0) throw new IOException("Cannot capture service security");
            string auditCsv = Path.Combine(root, "audit.csv");
            if (Exec("auditpol.exe", ["/backup", "/file:" + auditCsv]).Exit != 0) throw new IOException("Cannot snapshot process audit policy");
            int processFlags = ProcessAuditFlags(File.ReadAllText(auditCsv));

            var auditBackups = new List<LegacyAuditBackup>();
            foreach (string key in AuditKeys)
            {
                using var existing = Registry.LocalMachine.OpenSubKey(key);
                if (existing is null) continue;
                string backup = "audit-" + auditBackups.Count + ".reg";
                if (Exec("reg.exe", ["export", @"HKEY_LOCAL_MACHINE\" + key, Path.Combine(root, backup), "/y"]).Exit != 0) throw new IOException("Cannot snapshot Watcher audit configuration");
                auditBackups.Add(new(key, backup));
            }
            int? cmdAudit;
            using (var cmd = Registry.LocalMachine.OpenSubKey(CmdAuditKey)) cmdAudit = cmd?.GetValue("ProcessCreationIncludeCmdLine_Enabled") as int?;

            // Старый uninstall удаляет собственные транскрипты. Сохраняем их до
            // запуска старого CA, иначе откат вернул бы агент без истории аудита.
            var directories = new List<LegacyStateDirectory>();
            string? transcripts;
            using (var audit = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Watcher\TerminalAudit")) transcripts = audit?.GetValue("TranscriptDir") as string;
            if (!string.IsNullOrEmpty(transcripts) && System.IO.Directory.Exists(transcripts))
            {
                if (!Path.IsPathRooted(transcripts) || transcripts.TrimEnd('\\').Equals(Path.GetPathRoot(transcripts)!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Invalid transcript directory");
                var items = new List<FileSystemInfo> { new DirectoryInfo(transcripts) };
                items.AddRange(new DirectoryInfo(transcripts).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }).Take(100001));
                if (items.Count > 100000 || items.Any(i => (i.Attributes & FileAttributes.ReparsePoint) != 0)) throw new IOException("Transcript snapshot exceeds limits or contains links");
                long bytes = items.OfType<FileInfo>().Sum(f => f.Length);
                if (FreeSpace(root) < bytes + (2L << 30)) throw new IOException("Insufficient space to preserve audit history");
                foreach (var item in items)
                {
                    if (item is DirectoryInfo d) { directories.Add(new(d.FullName, Sddl(d))); continue; }
                    files.Add(Snapshot(root, item.FullName, "transcript-" + files.Count + ".bin", appendOnly: true));
                }
            }

            // Бывший recover.ps1: задача отката запускает копию этого GuardRepair из
            // закрытого каталога журнала — после удаления прежней версии и при сбое новой.
            File.Copy(Environment.ProcessPath ?? throw new IOException("GuardRepair path unknown"), Path.Combine(root, "recover.exe"));

            using var controller = new ServiceController(ServiceName);
            var state = new LegacyUpgradeState
            {
                PreviousProduct = oldProduct,
                PreviousVersion = oldVersion,
                PackageHash = Hash(previous),
                Directory = dir,
                DirectorySddl = Sddl(new DirectoryInfo(dir)),
                Start = start,
                Delayed = registration.GetValue("DelayedAutoStart") is 1,
                Running = controller.Status == ServiceControllerStatus.Running,
                Sddl = serviceSddl,
                Files = files,
                ServerUrl = Str(uploader, "ServerUrl"),
                UpdateKey = Str(uploader, "UpdatePublicKey"),
                ApiToken = Str(uploader, "ApiToken"),
                Audit = Get(service, "TerminalAudit")?["Enabled"]?.GetValueKind() == JsonValueKind.True,
                ProcessAudit = processFlags,
                BinaryHash = Hash(image),
                Binaries = BinaryNames.Where(n => File.Exists(Path.Combine(dir, n))).ToDictionary(n => n, n => Hash(Path.Combine(dir, n))),
                AuditBackups = auditBackups,
                CommandLineAudit = cmdAudit,
                Directories = directories,
            };
            WriteState(stateFile, state);
            GuardWindowsTask.Create(TaskName(product), GuardWindowsTask.TaskXml(Path.Combine(root, "recover.exe"), "legacy-msi-recover " + product,
                repeating: true, "Watcher upgrade recovery: confirms the new version or restores the previous one.", interval: "PT1M", limit: "PT15M"));
            prepared = true;
        }
        finally
        {
            if (!prepared) { AssertPrivateDirectory(root); System.IO.Directory.Delete(root, recursive: true); }
        }
        // Архив прошлой попытки нужен был только как запасной источник MSI — 200+ МБ.
        if (archive is not null)
            try { System.IO.Directory.Delete(archive, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // Immediate CA при ручной установке идёт от администратора, не от SYSTEM:
    // временная SYSTEM-задача запускает копию GuardRepair из закрытого каталога.
    private static void Bootstrap(string product)
    {
        string task = TaskName(product) + "Bootstrap";
        if (GuardWindowsTask.Query(task) is not null) throw new IOException("Recovery bootstrap already exists");
        string nonce = Guid.NewGuid().ToString("N");
        string dir = Path.Combine(ProgramFiles, "WatcherUpgradeBootstrap-" + nonce);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (string sid in new[] { SystemSid, AdminsSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).Create(acl);
        try
        {
            string exe = Path.Combine(dir, "GuardRepair.exe");
            File.Copy(Environment.ProcessPath ?? throw new IOException("GuardRepair path unknown"), exe);
            GuardWindowsTask.Create(task, GuardWindowsTask.TaskXml(exe, "legacy-msi-prepare " + product + " --result " + nonce, repeating: false, "Watcher upgrade recovery: one-time preparation.", security: GuardWindowsTask.BootstrapTaskSecurity));
            try
            {
                GuardWindowsTask.Run(task);
                string result = Path.Combine(dir, "result-" + nonce);
                var deadline = DateTime.UtcNow.AddMinutes(4);
                while (!File.Exists(result) && DateTime.UtcNow < deadline) Thread.Sleep(500);
                Thread.Sleep(200);
                string outcome = File.Exists(result) ? File.ReadAllText(result).Trim() : "timed out";
                if (outcome != "ok" || !File.Exists(StateFile(product))) throw new IOException("Legacy recovery preparation failed; old product has not been removed: " + outcome);
            }
            finally { GuardWindowsTask.Delete(task); }
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void WriteBootstrapResult(string? nonce, string outcome)
    {
        if (nonce is not { Length: 32 } || !nonce.All(char.IsAsciiHexDigit)) return;
        string? dir = Path.GetDirectoryName(Environment.ProcessPath);
        if (dir is null || !Path.GetFileName(dir).Equals("WatcherUpgradeBootstrap-" + nonce, StringComparison.OrdinalIgnoreCase)) return;
        try { File.WriteAllText(Path.Combine(dir, "result-" + nonce), outcome); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ---------- restore-config ----------

    public static void RestoreConfig(string product)
    {
        AssertPrivateDirectory(Base);
        AssertPrivateDirectory(Root(product));
        var state = LoadState(StateFile(product));
        if (state.Phase != "prepared") throw new IOException("Unexpected upgrade snapshot phase");
        RestoreConfigs(Root(product), state);
    }

    private static void RestoreConfigs(string root, LegacyUpgradeState state)
    {
        foreach (var directory in state.Directories)
        {
            var info = new DirectoryInfo(directory.Path);
            if (!info.Exists) info.Create();
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked recovery destination directory");
            var acl = new DirectorySecurity(); acl.SetSecurityDescriptorSddlForm(directory.Sddl); info.SetAccessControl(acl);
        }
        foreach (var entry in state.Files)
        {
            string source = Path.Combine(root, entry.Backup);
            if (!string.Equals(Hash(source), entry.Hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Recovery configuration integrity mismatch");
            if (File.Exists(entry.Path))
            {
                if ((File.GetAttributes(entry.Path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked recovery destination file");
                // Открытый транскрипт мог пережить uninstall и продолжать расти —
                // актуальный файл сохраняем, вместо перезаписи старым снимком.
                if (entry.AppendOnly) continue;
            }
            File.WriteAllBytes(entry.Path, File.ReadAllBytes(source));
            var acl = new FileSecurity(); acl.SetSecurityDescriptorSddlForm(entry.Sddl); new FileInfo(entry.Path).SetAccessControl(acl);
        }
    }

    // ---------- recover ----------

    public static void Recover(string product)
    {
        string root = Root(product), task = TaskName(product);
        AssertPrivateDirectory(Base);
        AssertPrivateDirectory(root);
        string stateFile = StateFile(product);
        var state = LoadState(stateFile);
        if (state.Phase is "committed" or "rolled-back") { DeleteTask(task); return; }
        bool wantRunning = state.Start == 2 || (state.Start == 3 && state.Running);

        // Не трогаем продукт, пока Windows Installer выполняет транзакцию.
        using (var mutex = new Mutex(false, @"Global\_MSIExecute"))
        {
            bool acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                using (var busy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\InProgress"))
                    if (!acquired || busy is not null) return;
                bool matches = Msi.Installed(product) && FileVersion(Path.Combine(state.Directory, "Watcher.SessionService.exe")) == Msi.ProductInfo(product, "VersionString");
                var status = ServiceStatus();
                if (matches && status is not null && (wantRunning ? status == ServiceControllerStatus.Running : status == ServiceControllerStatus.Stopped))
                {
                    state.Phase = "committed"; WriteState(stateFile, state);
                    DeleteTask(task);
                    return;
                }
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }

        try
        {
            state.Phase = "rolling-back"; WriteState(stateFile, state);
            string previous = Path.Combine(root, "previous.msi");
            if (!string.Equals(Hash(previous), state.PackageHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Previous MSI integrity mismatch");
            // Частично поставленный новый Guard сохраняем для разбора и не запускаем
            // параллельно с возвращаемой прежней версией.
            if (System.IO.Directory.Exists(GuardRoot) && (new DirectoryInfo(GuardRoot).Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked Guard installation refused");
            if (GuardStarted(GuardRoot))
            {
                if ((new DirectoryInfo(GuardRoot).Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked Guard installation refused");
                string claim = Path.Combine(root, "guard-started");
                if (!File.Exists(claim) || File.ReadAllText(claim).Trim() != product) throw new IOException("Foreign Guard installation refused");
                string policyFile = Path.Combine(GuardRoot, "policy.json");
                if (File.Exists(policyFile))
                {
                    string? installDir = JsonNode.Parse(File.ReadAllText(policyFile), new JsonNodeOptions { PropertyNameCaseInsensitive = true })?["installDir"]?.GetValue<string>();
                    if (installDir is null || !Path.GetFullPath(installDir).TrimEnd('\\').Equals(state.Directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Foreign Guard installation refused");
                }
                if (GuardWindowsTask.Query(GuardWindowsTask.TaskPath) is not null) { GuardWindowsTask.End(GuardWindowsTask.TaskPath); GuardWindowsTask.Delete(GuardWindowsTask.TaskPath); }
                StopService();
                File.Delete(Path.Combine(GuardRoot, "enabled"));
                System.IO.Directory.Move(GuardRoot, Path.Combine(root, "failed-guard-" + Guid.NewGuid().ToString("N")));
            }
            if (Msi.Installed(product))
            {
                int removal = Msiexec("/x " + product + " /qn /norestart");
                if (removal != 0) throw new IOException("Failed product removal exit " + removal);
            }
            if (!System.IO.Directory.Exists(state.Directory))
            {
                var acl = new DirectorySecurity(); acl.SetSecurityDescriptorSddlForm(state.DirectorySddl);
                new DirectoryInfo(state.Directory).Create(acl);
            }
            RestoreConfigs(root, state);
            bool previousValid = Msi.Installed(state.PreviousProduct)
                && state.Binaries.All(b => File.Exists(Path.Combine(state.Directory, b.Key)) && string.Equals(Hash(Path.Combine(state.Directory, b.Key)), b.Value, StringComparison.OrdinalIgnoreCase));
            if (!previousValid)
            {
                int install = Msiexec(RollbackArguments(previous, Path.Combine(root, "rollback-msi.log"), state));
                if (install is not (0 or 3010)) throw new IOException("Previous product install exit " + install);
            }
            RestoreConfigs(root, state);
            // Native MSI rollback возвращает продукт и файлы, но у внешнего uninstall CA
            // нет обратного действия для SCM.
            if (ServiceStatus() is null)
            {
                string binary = Path.Combine(state.Directory, "Watcher.SessionService.exe");
                if (FileVersion(binary) != state.PreviousVersion) throw new IOException("Cannot register an unverified previous service");
                Sc(["create", ServiceName, "binPath=", "\"" + binary + "\"", "DisplayName=", "Watcher Session Service", "start=", "demand"], "Cannot register previous service");
                Sc(["failure", ServiceName, "reset=", "86400", "actions=", "restart/15000/restart/15000/restart/15000"], "Cannot restore service failure actions");
            }
            string mode = state.Start switch { 2 => state.Delayed ? "delayed-auto" : "auto", 3 => "demand", 4 => "disabled", _ => throw new IOException("Unexpected service start mode") };
            Sc(["config", ServiceName, "start=", mode], "Previous service registration missing after rollback");
            Sc(["sdset", ServiceName, state.Sddl], "Service security restoration failed");
            if (Exec("auditpol.exe", ["/set", "/subcategory:" + ProcessCreationGuid, "/success:" + ((state.ProcessAudit & 1) != 0 ? "enable" : "disable"),
                    "/failure:" + ((state.ProcessAudit & 2) != 0 ? "enable" : "disable")]).Exit != 0) throw new IOException("Process audit restoration failed");
            foreach (var entry in state.AuditBackups)
                if (Exec("reg.exe", ["import", Path.Combine(root, entry.Backup)]).Exit != 0) throw new IOException("Watcher audit registry restoration failed");
            using (var cmd = Registry.LocalMachine.CreateSubKey(CmdAuditKey))
            {
                if (state.CommandLineAudit is int value) cmd.SetValue("ProcessCreationIncludeCmdLine_Enabled", value, RegistryValueKind.DWord);
                else cmd.DeleteValue("ProcessCreationIncludeCmdLine_Enabled", throwOnMissingValue: false);
            }
            using (var service = new ServiceController(ServiceName))
            {
                if (wantRunning)
                {
                    if (service.Status != ServiceControllerStatus.Running) service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                }
                else StopService();
            }
            string sessionExe = Path.Combine(state.Directory, "Watcher.SessionService.exe");
            if (FileVersion(sessionExe) != state.PreviousVersion || !Msi.Installed(state.PreviousProduct)) throw new IOException("Previous version restoration not verified");
            if (!string.Equals(Hash(sessionExe), state.BinaryHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Previous binary hash mismatch after restoration");
            foreach (var binary in state.Binaries)
                if (!string.Equals(Hash(Path.Combine(state.Directory, binary.Key)), binary.Value, StringComparison.OrdinalIgnoreCase)) throw new IOException("Previous payload integrity mismatch after restoration");
            state.Phase = "rolled-back"; WriteState(stateFile, state);
            DeleteTask(task);
        }
        catch (Exception e)
        {
            // Закрытый снимок и повторяемая SYSTEM-задача остаются; ложного успеха нет.
            try { File.AppendAllText(Path.Combine(root, "recovery-errors.log"), DateTime.UtcNow.ToString("o") + " " + e.Message + Environment.NewLine); }
            catch (Exception log) when (log is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    // msiexec разбирает СВОЙСТВО="значение" сам — строка целиком, кавычки в значениях запрещены.
    public static string RollbackArguments(string package, string log, LegacyUpgradeState state)
    {
        static string Q(string? value) => (value ?? "").Contains('"') ? throw new IOException("Quote in msiexec argument") : "\"" + value + "\"";
        var parts = new List<string> { "/i", Q(package), "/qn", "/norestart", "/l*v", Q(log), "SERVERURL=" + Q(state.ServerUrl), "UPDATEKEY=" + Q(state.UpdateKey), "TERMINALAUDIT=" + (state.Audit ? "1" : "0"),
            // Путь legacy — установка без Guard: откат возвращает её без защиты, даже если
            // в прежний MSI комплекта зашит ключ обслуживания (на стенде включалась защита).
            "MAINTENANCEKEY=\"\"" };
        if (!string.IsNullOrEmpty(state.ApiToken)) parts.Add("APITOKEN=" + Q(state.ApiToken));
        return string.Join(" ", parts);
    }

    // Строка подкатегории «Создание процесса» из auditpol /backup. Заголовки
    // локализованы (и в SYSTEM могут быть в OEM-кодировке) — опираемся только на
    // GUID и последнее, числовое поле.
    public static int ProcessAuditFlags(string csv)
    {
        var rows = csv.Split('\n').Skip(1).Select(l => l.TrimEnd('\r').Split(','))
            .Where(f => f.Any(x => x.Trim().Equals(ProcessCreationGuid, StringComparison.OrdinalIgnoreCase))).ToList();
        if (rows.Count != 1) throw new IOException("Cannot identify process creation audit policy");
        string flags = rows[0][^1].Trim();
        if (flags.Length != 1 || flags[0] is < '0' or > '3') throw new IOException("Invalid process audit setting");
        return flags[0] - '0';
    }

    // ---------- helpers ----------

    public static LegacyUpgradeState LoadState(string path) =>
        JsonSerializer.Deserialize<LegacyUpgradeState>(File.ReadAllBytes(path), Json) ?? throw new IOException("Invalid upgrade snapshot");

    private static void WriteState(string path, LegacyUpgradeState state)
    {
        string tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(state, Json));
            stream.Flush(true);
        }
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }

    private static LegacyStateFile Snapshot(string root, string from, string backup, bool appendOnly)
    {
        string target = Path.Combine(root, backup);
        CopyPrivateFile(from, target);
        return new LegacyStateFile(from, backup, Hash(target), Sddl(new FileInfo(from)), appendOnly);
    }

    private static void CopyPrivateFile(string source, string target)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked recovery input");
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(true);
    }

    private static void AssertPrivateDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked recovery directory");
        var acl = info.GetAccessControl();
        if (acl.GetOwner(typeof(SecurityIdentifier))?.Value != SystemSid) throw new IOException("Recovery directory owner mismatch");
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference.Value != SystemSid && (rule.FileSystemRights & writes) != 0)
                throw new IOException("Recovery directory is writable outside SYSTEM");
    }

    private static void NewPrivateDirectory(string path)
    {
        if (System.IO.Directory.Exists(path)) { AssertPrivateDirectory(path); return; }
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier(SystemSid));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(SystemSid), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(AdminsSid), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(acl);
        AssertPrivateDirectory(path);
    }

    private const AccessControlSections SddlSections = AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access;
    private static string Sddl(FileInfo file) => file.GetAccessControl(SddlSections).GetSecurityDescriptorSddlForm(SddlSections);
    private static string Sddl(DirectoryInfo dir) => dir.GetAccessControl(SddlSections).GetSecurityDescriptorSddlForm(SddlSections);

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static long FreeSpace(string path) => new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;

    private static string FileVersion(string path)
    {
        if (!File.Exists(path)) return "";
        var v = FileVersionInfo.GetVersionInfo(path);
        return $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}";
    }

    private static JsonNode? Section(string path, string name) =>
        JsonNode.Parse(File.ReadAllText(path), new JsonNodeOptions { PropertyNameCaseInsensitive = true })?[name];
    private static JsonNode? Get(JsonNode? node, string name) => node is JsonObject o ? o[name] : null;
    private static string? Str(JsonNode? node, string name) => Get(node, name) is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static ServiceControllerStatus? ServiceStatus()
    {
        try { using var service = new ServiceController(ServiceName); return service.Status; }
        catch (InvalidOperationException) { return null; }
    }

    private static void StopService()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            if (service.Status != ServiceControllerStatus.Stopped) { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ServiceProcess.TimeoutException) { }
    }

    private static void DeleteTask(string task)
    {
        if (GuardWindowsTask.Query(task) is not null) GuardWindowsTask.Delete(task);
    }

    private static void Sc(string[] arguments, string error)
    {
        if (Exec("sc.exe", arguments).Exit != 0) throw new IOException(error);
    }

    private static int Msiexec(string arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), arguments) { UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(info) ?? throw new IOException("msiexec did not start");
        process.WaitForExit();
        return process.ExitCode;
    }

    private static (int Exit, string Output) Exec(string tool, string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, tool))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException(tool + " did not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000)) { process.Kill(entireProcessTree: true); throw new IOException(tool + " timed out"); }
        return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }

    // Windows Installer API (msi.dll): только чтение регистрации и базы MSI.
    private static class Msi
    {
        public static List<string> RelatedProducts(string upgradeCode)
        {
            var products = new List<string>();
            for (uint index = 0; index < 64; index++)
            {
                var buffer = new StringBuilder(39);
                uint status = MsiEnumRelatedProducts(upgradeCode, 0, index, buffer);
                if (status == 259) return products;
                if (status != 0) throw new IOException("MSI registration enumeration failed: " + status);
                products.Add(buffer.ToString());
            }
            throw new IOException("Too many related MSI registrations");
        }

        public static bool Installed(string product) => MsiQueryProductState(product) == 5;

        public static string ProductInfo(string product, string property)
        {
            uint length = 512;
            var buffer = new StringBuilder((int)length);
            uint status = MsiGetProductInfo(product, property, buffer, ref length);
            if (status == 234) { length++; buffer = new StringBuilder((int)length); status = MsiGetProductInfo(product, property, buffer, ref length); }
            if (status != 0) throw new IOException("MSI product information unavailable: " + property);
            return buffer.ToString();
        }

        public static void VerifyPackage(string path, string product, string version, string upgradeCode)
        {
            Check(MsiOpenDatabase(path, IntPtr.Zero, out uint database));
            try
            {
                foreach (var (name, expected) in new[] { ("ProductCode", product), ("ProductVersion", version), ("UpgradeCode", upgradeCode) })
                    if (Query(database, "SELECT `Value` FROM `Property` WHERE `Property`='" + name + "'") != expected) throw new IOException("Previous MSI metadata mismatch");
                if (Query(database, "SELECT `Name` FROM `_Streams` WHERE `Name`='watcher.cab'") is null) throw new IOException("Previous MSI does not contain its cabinet");
            }
            finally { MsiCloseHandle(database); }
        }

        private static string? Query(uint database, string sql)
        {
            Check(MsiDatabaseOpenView(database, sql, out uint view));
            try
            {
                Check(MsiViewExecute(view, 0));
                uint fetched = MsiViewFetch(view, out uint record);
                if (fetched == 259) return null;
                Check(fetched);
                try
                {
                    uint length = 1024;
                    var buffer = new StringBuilder((int)length);
                    Check(MsiRecordGetString(record, 1, buffer, ref length));
                    return buffer.ToString();
                }
                finally { MsiCloseHandle(record); }
            }
            finally { MsiCloseHandle(view); }
        }

        private static void Check(uint error) { if (error != 0) throw new IOException("Windows Installer database error " + error); }

        [DllImport("msi.dll", EntryPoint = "MsiEnumRelatedProductsW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint MsiEnumRelatedProducts(string code, uint reserved, uint index, StringBuilder product);
        [DllImport("msi.dll", EntryPoint = "MsiQueryProductStateW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int MsiQueryProductState(string product);
        [DllImport("msi.dll", EntryPoint = "MsiGetProductInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint MsiGetProductInfo(string product, string property, StringBuilder value, ref uint length);
        [DllImport("msi.dll", EntryPoint = "MsiOpenDatabaseW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint MsiOpenDatabase(string path, IntPtr persistence, out uint database);
        [DllImport("msi.dll", EntryPoint = "MsiDatabaseOpenViewW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
        [DllImport("msi.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint MsiViewExecute(uint view, uint record);
        [DllImport("msi.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint MsiViewFetch(uint view, out uint record);
        [DllImport("msi.dll", EntryPoint = "MsiRecordGetStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint MsiRecordGetString(uint record, uint field, StringBuilder value, ref uint length);
        [DllImport("msi.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint MsiCloseHandle(uint handle);
    }

    // Authenticode: WinVerifyTrust (целостность подписи) + отпечаток подписанта.
    // Вызывающий сравнивает отпечатки MSI и установленного exe — доверие доказывает
    // совпадение издателя, а не корень. Поэтому самоподписанный сертификат комплекта
    // без GPO «Trusted Root» (CERT_E_UNTRUSTEDROOT/CERT_E_CHAINING) принимается;
    // в ps1 до 0.16 такая станция не могла обновиться. Подмена байт (TRUST_E_BAD_DIGEST),
    // отсутствие подписи и прочие ошибки — отказ. Отзыв не проверяется: станции бывают без CRL.
    private static class Authenticode
    {
        public static string? Signer(string path)
        {
            if (!Verify(path)) return null;
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return certificate.Thumbprint;
        }

        private static bool Verify(string path)
        {
            IntPtr file = Marshal.StringToHGlobalUni(path);
            var fileInfo = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = file };
            IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
                var data = new WinTrustData
                {
                    Size = (uint)Marshal.SizeOf<WinTrustData>(),
                    UiChoice = 2 /* WTD_UI_NONE */,
                    RevocationChecks = 0 /* WTD_REVOKE_NONE */,
                    UnionChoice = 1 /* WTD_CHOICE_FILE */,
                    File = fileInfoPtr,
                    StateAction = 1 /* VERIFY */,
                    ProvFlags = 0x10 | 0x1000 /* REVOCATION_CHECK_NONE | CACHE_ONLY_URL_RETRIEVAL */,
                };
                int result = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
                data.StateAction = 2; // CLOSE
                _ = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
                return result is 0 or unchecked((int)0x800B0109) or unchecked((int)0x800B010A);
            }
            finally { Marshal.FreeHGlobal(fileInfoPtr); Marshal.FreeHGlobal(file); }
        }

        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo { public uint Size; public IntPtr FilePath; public IntPtr FileHandle; public IntPtr KnownSubject; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint Size; public IntPtr PolicyCallbackData; public IntPtr SipClientData; public uint UiChoice; public uint RevocationChecks;
            public uint UnionChoice; public IntPtr File; public uint StateAction; public IntPtr StateData; public IntPtr UrlReference;
            public uint ProvFlags; public uint UiContext; public IntPtr SignatureSettings;
        }
        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int WinVerifyTrust(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WinTrustData data);
    }
}
