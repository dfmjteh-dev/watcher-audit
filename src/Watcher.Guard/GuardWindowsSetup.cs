using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using Watcher.Uploader;

namespace Watcher.Guard;

// 0.16: включение защиты Windows — бывший guard-install.ps1, и постоянный лаунчер
// задачи — бывший recover.ps1. Без PowerShell в установленном продукте.
[SupportedOSPlatform("windows")]
public static class GuardWindowsSetup
{
    private const string BootstrapTask = @"\GuardRepairBootstrap";
    private static readonly string[] OldBootstrapTasks = [@"\GuardRepairBootstrap", @"\WatcherGuardBootstrap"];

    public static bool IsSystem => WindowsIdentity.GetCurrent().User?.Value == "S-1-5-18";

    public static string Sha256(string path) => GuardLauncher.Sha256(path);

    // Проверенный exe версии из bin\<hash> — общий с Linux код в GuardLauncher.
    public static string VerifiedExecutable(string root, string hash) => GuardLauncher.VerifiedExecutable(root, hash);
    public static int Launch(string root, string[] arguments) => GuardLauncher.Launch(root, arguments, IsSystem);
    public static void LogLauncherFailure(string root, Exception error) => GuardLauncher.LogFailure(root, error);
    public static bool IsLauncher(string root) => GuardLauncher.IsLauncher(root);

    // Включить защиту: от SYSTEM — сразу, от администратора — временной SYSTEM-задачей
    // (её результат — в bootstrap-result в закрытом каталоге, его администратор читает).
    // Возвращает результат закрепления базы восстановления (MsiRecoverySource.Result).
    public static MsiRecoverySource.Result Install(string root, string installDir, string maintenanceKey, string updateKey, string serverUrl, string recoverySource)
    {
        installDir = Path.GetFullPath(installDir).TrimEnd('\\');
        foreach (string key in new[] { maintenanceKey, updateKey })
            if (key.Length == 0 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')) throw new ArgumentException("Expected a base64 public key");
        if (!IsSystem) { Bootstrap(root, installDir, maintenanceKey, updateKey, serverUrl, recoverySource); return MsiRecoverySource.Result.Delegated; }

        // Первый запуск с C:\Watcher не меняет ACL корня диска: нужен защищённый родитель.
        string parent = Path.GetDirectoryName(installDir) ?? throw new IOException("Invalid installation directory");
        if (string.Equals(parent.TrimEnd('\\'), Path.GetPathRoot(installDir)!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Protected installations require a trusted parent such as Program Files");
        if (string.Equals(installDir, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Installation must have its own directory");
        foreach (string path in new[] { root, installDir })
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse point refused");
        Directory.CreateDirectory(root);
        // Закрыть каталог до помещения туда конфигурации и учёток.
        GuardWindowsProtection.ProtectDirectory(root, publicRead: false);

        // 0.16: MSI кладёт GuardRepair в WatcherGuard\setup, а не в каталог агента.
        string source = Environment.ProcessPath ?? throw new IOException("GuardRepair path unknown");
        string hash = Sha256(source);
        string bin = Path.Combine(root, "bin", hash);
        Directory.CreateDirectory(bin);
        string versioned = Path.Combine(bin, "GuardRepair.exe");
        if (!File.Exists(versioned)) File.Copy(source, versioned);
        VerifiedExecutable(root, hash);
        // Постоянный путь: лаунчер задачи и `GuardRepair.exe submit` для офлайн-разрешений.
        CopyIfChanged(source, GuardWindowsTask.Launcher(root));

        bool existing = File.Exists(Path.Combine(root, "enabled"));
        bool wasRunning = ServiceRunning();
        var before = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[] { "Watcher.Uploader.json", "Watcher.SessionService.json", "Watcher.Capture.json" })
        {
            string path = Path.Combine(installDir, name);
            if (File.Exists(path)) before[path] = File.ReadAllBytes(path);
        }
        var seeded = MsiRecoverySource.Result.NoSignature;
        try
        {
            if (wasRunning) StopService();
            GuardBootstrap.Configure(installDir, maintenanceKey, updateKey, serverUrl);
            WriteLegacyPlaceholders(root);
            GuardSnapshot.Take(root);
            // 0.16: без подписанной базы защита всё равно включается — конфиги, учётки и
            // служба восстанавливаются; файлы агента — после первого обновления через Guard.
            if (recoverySource.Length > 0) seeded = MsiRecoverySource.Seed(root, recoverySource);
            else if (GuardPaths.Load(root) is { } loaded && (loaded.InitialPackage is not null || loaded.LastVerifiedPackage is not null)) seeded = MsiRecoverySource.Result.Seeded;
            if (seeded != MsiRecoverySource.Result.Seeded)
                DiagnosticJournal.At(Path.Combine(root, "recovery-diagnostics.json")).Capture("recovery-base", false,
                    seeded == MsiRecoverySource.Result.SignatureMismatch ? "installer signature does not match package" : "no installer signature next to package");
            var policy = GuardPaths.Load(root) ?? throw new IOException("Guard policy missing");
            GuardWindowsProtection.Protect(root, policy);
            File.WriteAllText(Path.Combine(root, "launcher"), hash);
            File.WriteAllText(Path.Combine(root, "enabled"), "1");
            // Действующую прежнюю задачу (PowerShell) не меняем во время установки: при
            // откате обновления её снова регистрирует прежний установщик, а свою чужую он
            // не тронет. Её переводит на лаунчер уже зафиксированная версия (MigrateTask).
            if (GuardWindowsTask.Inspect(GuardWindowsTask.TaskPath, root) != GuardWindowsTask.Kind.Legacy
                && GuardWindowsTask.Inspect(GuardWindowsTask.LegacyTaskPath, root) != GuardWindowsTask.Kind.Legacy)
                GuardWindowsTask.Register(root);
        }
        catch
        {
            if (!existing)
            {
                foreach (var (path, bytes) in before) File.WriteAllBytes(path, bytes);
                foreach (string name in new[] { "enabled", "policy.json", "initialized" }) File.Delete(Path.Combine(root, name));
            }
            throw;
        }
        finally
        {
            bool enabled = File.Exists(Path.Combine(root, "enabled"));
            if (wasRunning || (enabled && GuardPaths.Load(root)?.AutoStart == true)) StartService();
        }
        if (File.Exists(Path.Combine(root, "enabled"))) GuardBootstrap.FinalizeMigration();
        return seeded;
    }

    // Зафиксированная версия (журнала обновления нет) переводит прежнюю задачу
    // PowerShell на лаунчер и убирает recover.ps1/protect.ps1. Ошибка не мешает тику.
    public static void MigrateTask(string root)
    {
        if (Directory.Exists(Path.Combine(root, "update-state")) || !File.Exists(Path.Combine(root, "enabled"))) return;
        string? own = Environment.ProcessPath;
        if (own is null || !own.StartsWith(Path.Combine(root, "bin") + "\\", StringComparison.OrdinalIgnoreCase)) return;
        RemoveOldLaunchers(root);
        string launcherHash = File.ReadAllText(Path.Combine(root, "launcher")).Trim();
        if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(own)), launcherHash, StringComparison.OrdinalIgnoreCase)) return;
        CopyIfChanged(own, GuardWindowsTask.Launcher(root));
        var current = GuardWindowsTask.Inspect(GuardWindowsTask.TaskPath, root);
        var legacy = GuardWindowsTask.Inspect(GuardWindowsTask.LegacyTaskPath, root);
        if (current != GuardWindowsTask.Kind.Native || legacy != GuardWindowsTask.Kind.Missing) GuardWindowsTask.Register(root);
        foreach (string name in new[] { "recover.ps1", "protect.ps1" }) File.Delete(Path.Combine(root, name));
        PruneLegacyPlaceholders(root);
    }

    // install.ps1, guard-*.ps1, GuardRepair.exe и его alias (и update.ps1 ZIP-обновления) до 0.16 лежали в каталоге
    // агента, и Guard 0.15.x после установки обновления пересчитывает хеши всех файлов
    // своей политики: без них он откатывает обновление. Пока политика их перечисляет, на их месте лежит однострочная
    // заглушка без кода; зафиксированная новая версия убирает имена из политики
    // и заглушки (PruneLegacyPlaceholders). Свежая установка их не получает.
    public static readonly string[] LegacyScripts = ["install.ps1", "update.ps1", "guard-install.ps1", "guard-protect.ps1", "guard-recover.ps1", "GuardRepair.exe", "Watcher.Guard.exe"];
    public const string PlaceholderText = "# Watcher 0.16: GuardRepair works without PowerShell. Placeholder for GuardRepair 0.15 update verification; removed after the update is committed.\r\n";

    private static void WriteLegacyPlaceholders(string root)
    {
        var policy = GuardPaths.Load(root);
        if (policy is null) return;
        foreach (string name in LegacyScripts)
        {
            string path = Path.Combine(policy.InstallDir, name);
            if (policy.ConfigHashes.ContainsKey(name) && !File.Exists(path)) File.WriteAllText(path, PlaceholderText);
        }
    }

    private static void PruneLegacyPlaceholders(string root)
    {
        var policy = GuardPaths.Load(root);
        if (policy is null || !LegacyScripts.Any(policy.ConfigHashes.ContainsKey)) return;
        var hashes = new Dictionary<string, string>(policy.ConfigHashes, StringComparer.OrdinalIgnoreCase);
        foreach (string name in LegacyScripts) hashes.Remove(name);
        GuardPaths.Write(Path.Combine(root, "policy.json"), policy with { ConfigHashes = hashes });
        foreach (string name in LegacyScripts)
        {
            string path = Path.Combine(policy.InstallDir, name);
            // Удаляем только своё: заглушку или файл с хешем из политики, не чужой файл с тем же именем.
            if (!File.Exists(path)) continue;
            bool placeholder = new FileInfo(path).Length < 1024 && File.ReadAllText(path) == PlaceholderText;
            bool verified = policy.ConfigHashes.TryGetValue(name, out string? expected) && string.Equals(Sha256(path), expected, StringComparison.OrdinalIgnoreCase);
            if (placeholder || verified) File.Delete(path);
        }
    }

    private static void CopyIfChanged(string source, string target)
    {
        if (File.Exists(target) && Sha256(target) == Sha256(source)) return;
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.Copy(source, temp);
        try { File.Move(temp, target, overwrite: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException && File.Exists(target))
        {
            // Лаунчер сейчас запущен задачей (он и ведёт это обновление): запущенный exe
            // Windows перезаписать не даёт (Access denied), а переименовать — даёт.
            // Старую копию удаляет следующий тик (RemoveOldLaunchers).
            File.Move(target, target + "." + Guid.NewGuid().ToString("N") + ".old");
            File.Move(temp, target);
        }
        finally { File.Delete(temp); }
    }

    // Переименованные при замене лаунчеры — удалить, когда они уже не запущены.
    private static void RemoveOldLaunchers(string root)
    {
        foreach (string file in Directory.EnumerateFiles(root, "GuardRepair.exe.*.old"))
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void Bootstrap(string root, string installDir, string maintenanceKey, string updateKey, string serverUrl, string recoverySource)
    {
        foreach (string task in OldBootstrapTasks)
            if (GuardWindowsTask.Query(task) is not null) throw new IOException("Watcher bootstrap task already exists; inspect it before retrying");
        string exe = Environment.ProcessPath ?? throw new IOException("GuardRepair path unknown");
        string nonce = Guid.NewGuid().ToString("N");
        string Quote(string value) => "\"" + value.Replace("\"", "") + "\"";
        string arguments = string.Join(" ", new[] { "install", Quote(installDir), maintenanceKey, updateKey, Quote(serverUrl), recoverySource.Length > 0 ? Quote(recoverySource) : "-", "--result", nonce });
        GuardWindowsTask.Create(BootstrapTask, GuardWindowsTask.TaskXml(exe, arguments, repeating: false, "Watcher GuardRepair one-time setup.", security: GuardWindowsTask.BootstrapTaskSecurity));
        try
        {
            GuardWindowsTask.Run(BootstrapTask);
            string result = Path.Combine(root, "bootstrap-result-" + nonce);
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (!File.Exists(result) && DateTime.UtcNow < deadline) Thread.Sleep(500);
            if (!File.Exists(result)) throw new IOException("Guard bootstrap timed out; inspect \\GuardRepairBootstrap task before retrying");
            string outcome = File.ReadAllText(result).Trim();
            if (outcome != "ok" || !File.Exists(Path.Combine(root, "enabled"))) throw new IOException("Guard bootstrap failed: " + outcome);
        }
        finally { GuardWindowsTask.Delete(BootstrapTask); }
    }

    // Итог bootstrap-задачи для ожидающего администратора; файл удаляет следующий тик.
    public static void WriteBootstrapResult(string root, string nonce, string outcome)
    {
        if (nonce.Length != 32 || !nonce.All(char.IsAsciiHexDigit)) return;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "bootstrap-result-" + nonce), outcome);
    }

    public static void CleanupBootstrapResults(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (string file in Directory.EnumerateFiles(root, "bootstrap-result-*"))
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromMinutes(10)) File.Delete(file);
    }

    private static bool ServiceRunning()
    {
        try { using var service = new ServiceController("WatcherSessionService"); return service.Status == ServiceControllerStatus.Running; }
        catch (InvalidOperationException) { return false; }
    }

    private static void StopService()
    {
        using var service = new ServiceController("WatcherSessionService");
        service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    }

    // Как Start-Service прежнего guard-install.ps1: дождаться Running — install.ps1
    // сразу после проверяет состояние службы и считает StartPending ошибкой.
    private static void StartService()
    {
        using var service = new ServiceController("WatcherSessionService");
        if (service.Status == ServiceControllerStatus.Stopped) service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
    }

}
