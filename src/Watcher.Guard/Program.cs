using System.Security.Cryptography;
using System.Text.Json;
using Watcher;
using Watcher.Guard;
using Watcher.Uploader;

// 0.16: подготовка/откат обновления установки без Guard — бывший legacy-msi-recovery.ps1.
// prepare — immediate CA MSI (при ручной установке через временную SYSTEM-задачу с
// --result), recover — задача WatcherUpgradeRecovery-<код>.
if (OperatingSystem.IsWindows() && args.Length is 2 or 4 && args[0] is "legacy-msi-prepare" or "legacy-msi-restore" or "legacy-msi-recover"
    && (args.Length == 2 || (args[0] == "legacy-msi-prepare" && args[2] == "--result")))
{
    string phase = args[0] switch { "legacy-msi-prepare" => "prepare", "legacy-msi-restore" => "restore-config", _ => "recover" };
    Environment.ExitCode = LegacyMsiRecovery.Run(phase, args[1], args.Length == 4 ? args[3] : null);
    return;
}

// 0.16: постоянный лаунчер (Windows — задача, WatcherGuard\GuardRepair.exe tick;
// Linux — watcher-guard.service, /var/lib/watcher/guard/GuardRepair tick) запускает
// проверенную версию из bin/<hash> — бывшие recover.ps1 и recover.sh.
if (args is ["tick"] && GuardLauncher.IsLauncher(GuardPaths.Root))
{
    try { Environment.ExitCode = GuardLauncher.Launch(GuardPaths.Root, args, OperatingSystem.IsWindows() ? GuardWindowsSetup.IsSystem : Environment.IsPrivilegedProcess); }
    catch (Exception e)
    {
        // Посреди обновления (launcher/bin в переходном состоянии) — повтор через 2 минуты;
        // без перехвата был бы WER-краш 0xE0434352 без текста ошибки.
        GuardLauncher.LogFailure(GuardPaths.Root, e);
        Environment.ExitCode = 1;
    }
    return;
}

// 0.16: включение защиты на Linux — бывший setup-guard.sh.
if (OperatingSystem.IsLinux() && args.Length >= 2 && args[0] == "setup")
{
    try { GuardLinuxSetup.Run(args[1..]); }
    catch (Exception e)
    {
        Console.Error.WriteLine("GuardRepair setup: " + e.Message);
        Environment.ExitCode = 1;
    }
    return;
}

// 0.16: включение защиты Windows — бывший guard-install.ps1 (вызывает install.ps1).
// --result — запуск временной SYSTEM-задачей: итог для ожидающего администратора.
if (OperatingSystem.IsWindows() && args.Length is 6 or 8 && args[0] == "install" && (args.Length == 6 || args[6] == "--result"))
{
    try
    {
        GuardWindowsSetup.Install(GuardPaths.Root, args[1], args[2], args[3], args[4], args[5] == "-" ? "" : args[5]);
        if (args.Length == 8) GuardWindowsSetup.WriteBootstrapResult(GuardPaths.Root, args[7], "ok");
    }
    catch (Exception e)
    {
        // Вывод install.ps1 (watcher-install-ca.log) перезаписывает откат MSI прежней
        // версией — полный текст ошибки остаётся в закрытом каталоге Guard.
        try
        {
            string logs = Path.Combine(GuardPaths.Root, "logs"); Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "install-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + ".log"), e.ToString());
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { /* лог — вспомогательный, исходная ошибка важнее */ }
        Console.Error.WriteLine("Guard installation failed: " + e);
        if (args.Length == 8) GuardWindowsSetup.WriteBootstrapResult(GuardPaths.Root, args[7], e.GetType().Name + ": " + e.Message);
        Environment.ExitCode = 1;
    }
    return;
}

// 0.16: custom actions MSI — установка и удаление агента (бывший install.ps1).
if (OperatingSystem.IsWindows() && args.Length > 0 && args[0] is "agent-install" or "agent-uninstall")
{
    bool install = args[0] == "agent-install";
    using var log = AgentSetup.OpenLog(install ? "" : "-uninstall");
    try
    {
        var setup = new AgentSetup(AgentSetup.Parse(args.Skip(1)), log);
        if (install) setup.Install(); else setup.Uninstall();
    }
    catch (Exception e)
    {
        // Любая ошибка — ненулевой код для MSI (откат) и полный текст в журнале.
        log.WriteLine("FAILED: " + e);
        Environment.ExitCode = 1;
    }
    return;
}

// За пределами установки агента: продолжает работать при отсутствии основной службы/exe.
// 0.16, п.7: офлайн-разрешение и состояние защиты — понятный вывод для админа станции.
if ((args.Length == 2 || args.Length == 4 && args[2] == "--package") && args[0] == "submit")
{
    try { Environment.ExitCode = GuardCli.Submit(args[1], args.Length == 4 ? args[3] : null); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
    { Console.Error.WriteLine("GuardRepair submit: " + e.Message); Environment.ExitCode = 1; }
    return;
}
if (args is ["status"])
{
    Environment.ExitCode = GuardCli.Status();
    return;
}
if (args is not ["tick"] && args is not ["bootstrap"] && args is not ["finalize"] && args is not ["authorize", _] && args is not ["configure", _, _, _, _] && args is not ["seed-msi", _] && args is not ["seed", _, _]) throw new ArgumentException("Usage: GuardRepair submit <grant.json> [--package <file>] | status");
if (OperatingSystem.IsWindows())
{
    if (System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value != "S-1-5-18") throw new UnauthorizedAccessException("Guard requires LocalSystem");
}
else if (Environment.UserName != "root") throw new UnauthorizedAccessException("Guard requires root");
string root = GuardPaths.Root;
try
{
    if (args[0] == "authorize") { if (!GuardAuthorization.Allows(args[1], DateTime.UtcNow)) throw new UnauthorizedAccessException("Signed maintenance authorization required"); return; }
    if (args[0] == "finalize") { GuardBootstrap.FinalizeMigration(); return; }
    if (args[0] == "configure") { GuardBootstrap.Configure(args[1], args[2], args[3], args[4]); return; }
    if (args[0] == "seed-msi")
    {
        if (!OperatingSystem.IsWindows()) throw new IOException("MSI recovery requires Windows");
        var seeded = MsiRecoverySource.Seed(root, args[1]);
        if (seeded != MsiRecoverySource.Result.Seeded) throw new IOException("Recovery base not seeded: " + seeded);
        return;
    }
    if (args[0] == "seed")
    {
        using var manifest = File.OpenRead(args[2]);
        var metadata = GuardRecoveryCache.ReadManifest(manifest);
        string expected = OperatingSystem.IsLinux() ? (File.Exists("/usr/bin/dpkg") ? "linux-deb" : "linux-rpm") : "windows-msi";
        if (metadata.Version != typeof(GuardEngine).Assembly.GetName().Version?.ToString(3)) throw new IOException("Recovery package version mismatch");
        if (metadata.Platform != expected) throw new IOException("Recovery platform mismatch");
        using var package = File.OpenRead(args[1]); GuardRecoveryCache.Seed(root, metadata, package); return;
    }
    if (args[0] == "bootstrap") { GuardSnapshot.Take(root); return; }
    if (OperatingSystem.IsWindows())
    {
        // Перевод задачи на лаунчер и уборка — не мешают проверке агента.
        try { GuardWindowsSetup.CleanupBootstrapResults(root); GuardWindowsSetup.RefreshLauncher(root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Xml.XmlException)
        { DiagnosticJournal.At(Path.Combine(root, "recovery-diagnostics.json")).Capture("recovery-task", true, "task migration failed"); }
    }
    var policyForDelivery = GuardPaths.Load();
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var clients = new List<(string Kind, DiagnosticClient Client)>();
    if (policyForDelivery is not null)
        foreach (var entry in policyForDelivery.Credentials)
        {
            var credential = CredentialStore.Load(entry.Value, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            if (credential is null) continue;
            var identity = new AgentIdentity(new AgentIdentityOptions(policyForDelivery.ServerUrl, "", entry.Value), http, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, credential.Host, entry.Key);
            clients.Add((entry.Key, new DiagnosticClient(http, identity, policyForDelivery.ServerUrl, DiagnosticJournal.For(entry.Value))));
        }
    string version = typeof(GuardEngine).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    var work = Task.Run(() => new GuardEngine(root, new GuardPlatform()).Tick());
    while (!work.IsCompleted)
    {
        foreach (var item in clients) await item.Client.SendAsync(version, null, root, CancellationToken.None, allowMaintenance: false);
        await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(5)));
    }
    await work;
    string removedPath = Path.Combine(root, "uninstalled.json");
    SignedMaintenanceGrant? removed = File.Exists(removedPath) ? JsonSerializer.Deserialize<SignedMaintenanceGrant>(File.ReadAllBytes(removedPath), GuardPaths.Json) : null;
    foreach (var item in clients.Where(c => removed is null || c.Kind == removed.Grant.Kind))
    {
        await item.Client.SendAsync(version, null, root, CancellationToken.None, force: true);
        await item.Client.FinishPendingAsync();
    }
    if (removed is not null && File.Exists(removedPath) && JsonSerializer.Deserialize<SignedMaintenanceGrant>(File.ReadAllBytes(removedPath), GuardPaths.Json)?.Grant.Id == removed.Grant.Id && !File.Exists(Path.Combine(root, "results", removed.Grant.Id + ".json")))
    {
        if (OperatingSystem.IsWindows()) GuardWindowsTask.Remove(root);
        else
        {
            var command = new System.Diagnostics.ProcessStartInfo("/usr/bin/systemctl") { UseShellExecute = false };
            foreach (string argument in new[] { "disable", "--now", "watcher-guard.timer" }) command.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(command) ?? throw new IOException("Recovery schedule removal did not start");
            if (!process.WaitForExit(10000)) { process.Kill(entireProcessTree: true); throw new IOException("Recovery schedule removal timed out"); }
            if (process.ExitCode != 0) throw new IOException("Recovery schedule removal failed");
        }
        if (policyForDelivery is not null) foreach (string credential in policyForDelivery.Credentials.Values) if (File.Exists(credential)) File.Delete(credential);
    }

}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException or InvalidOperationException or FormatException)
{
    DiagnosticJournal.At(Path.Combine(root, "recovery-diagnostics.json")).Capture("recovery", true, "repair failed");
    Environment.ExitCode = 1;
}
