using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using Watcher.Uploader;

namespace Watcher.Guard;

// 0.16: права защищённой установки Windows — бывший guard-protect.ps1, теперь в
// самом GuardRepair (без PowerShell). Вызывается только от SYSTEM: владелец —
// SYSTEM, администраторам чтение, широких DENY нет. Каталог агента читают
// пользователи (Capture запускается в их сессиях), каталог Guard — только
// SYSTEM/Administrators; inbox — запись администратору (разрешения проверяются
// подписью). Исходные SDDL сохраняются один раз (acl-backup.json, формат прежнего
// скрипта) и возвращаются при частичной ошибке.
[SupportedOSPlatform("windows")]
public static class GuardWindowsProtection
{
    public const string ServiceDescriptor = "O:SYG:SYD:P(A;;GA;;;SY)(A;;CCLCSWLORC;;;BA)(A;;CCLCSWLORC;;;AU)";
    private const string RegistryPath = @"SYSTEM\CurrentControlSet\Services\WatcherSessionService";
    // Ключ в acl-backup.json — как писал guard-protect.ps1.
    private const string RegistryBackupName = @"Registry::HKEY_LOCAL_MACHINE\" + RegistryPath;
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    public static void Protect(string root, GuardPolicy policy)
    {
        if (WindowsIdentity.GetCurrent().User?.Value != "S-1-5-18") throw new UnauthorizedAccessException("SYSTEM required");
        string install = Path.GetFullPath(policy.InstallDir).TrimEnd('\\');
        string parent = Path.GetDirectoryName(install) ?? throw new IOException("Invalid installation directory");
        // Не меняем ACL корня диска или произвольного пользовательского родителя.
        if (string.Equals(parent.TrimEnd('\\'), Path.GetPathRoot(install)!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Use a protected installation parent, e.g. Program Files");
        foreach (string path in new[] { parent, Path.GetDirectoryName(root)! }) AssertTrustedParent(path);

        var items = Enumerate(install).Concat(Enumerate(root)).ToList();
        if (items.Count > 3000) throw new IOException("Unexpected installation size");
        string backup = Path.Combine(root, "acl-backup.json");
        if (!File.Exists(backup)) SaveBackup(backup, new[] { install, root }.Concat(items.Select(i => i.FullName)));
        string serviceBackup = Path.Combine(root, "service-sddl-backup.txt");
        if (ServiceKeyExists() && !File.Exists(serviceBackup))
            File.WriteAllText(serviceBackup, ServiceSddl() + Environment.NewLine);
        try
        {
            ProtectDirectory(install, publicRead: true);
            ProtectDirectory(root, publicRead: false);
            foreach (var item in items)
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse point refused");
                bool isPublic = item.FullName.StartsWith(install + "\\", StringComparison.OrdinalIgnoreCase);
                // Работающий Guard (тот, что проводит обновление) атомарно переписывает свои
                // файлы через *.tmp — такой файл может исчезнуть между перебором и установкой
                // прав. Новые файлы наследуют права каталога; исчезнувший пропускаем, иначе
                // установка падала (1603, стенд 0.16.2 → 0.16.3).
                try
                {
                    if (item is DirectoryInfo) ProtectDirectory(item.FullName, isPublic);
                    else ProtectFile(item.FullName, FileSystemRights.ReadAndExecute, isPublic);
                }
                catch (Exception e) when ((e is FileNotFoundException or DirectoryNotFoundException) && !File.Exists(item.FullName) && !Directory.Exists(item.FullName)) { }
            }
            foreach (string credential in policy.Credentials.Values)
                ProtectDirectory(Path.GetDirectoryName(Path.GetFullPath(credential))!, publicRead: false);
            string inbox = Path.Combine(root, "inbox");
            Directory.CreateDirectory(inbox);
            var inboxAcl = new DirectoryInfo(inbox).GetAccessControl();
            inboxAcl.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.Modify, Inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(inbox).SetAccessControl(inboxAcl);
            string secret = Path.Combine(install, "Watcher.Uploader.json");
            if (File.Exists(secret)) ProtectFile(secret, FileSystemRights.Read, publicRead: false);
            if (ServiceKeyExists())
            {
                using var key = Registry.LocalMachine.OpenSubKey(RegistryPath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.TakeOwnership | RegistryRights.ReadKey)!;
                var acl = new RegistrySecurity();
                acl.SetAccessRuleProtection(true, false);
                acl.SetOwner(SystemSid);
                acl.AddAccessRule(new RegistryAccessRule(SystemSid, RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
                acl.AddAccessRule(new RegistryAccessRule(Admins, RegistryRights.ReadKey, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
                key.SetAccessControl(acl);
                SetServiceSddl(ServiceDescriptor);
            }
        }
        catch
        {
            Restore(backup, serviceBackup);
            throw;
        }
    }

    // Родитель не должен давать широким группам удалять детей или менять DACL —
    // иначе защищённый каталог удаляется/перехватывается через родителя.
    public static void AssertTrustedParent(string path)
    {
        var acl = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        string? owner = acl.GetOwner(typeof(SecurityIdentifier))?.Value;
        if (owner is "S-1-5-32-544" or "S-1-5-32-545") throw new IOException("Parent owner can change DACL; choose a trusted parent");
        const FileSystemRights unsafeRights = FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 || rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference.Value is "S-1-1-0" or "S-1-5-11" or "S-1-5-32-545" or "S-1-5-32-544" && (rule.FileSystemRights & unsafeRights) != 0)
                throw new IOException("Parent grants delete-child or DACL modification");
        }
    }

    public static void ProtectDirectory(string path, bool publicRead)
    {
        var info = new DirectoryInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse point refused");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(SystemSid);
        acl.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
        if (publicRead) acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
    }

    private static void ProtectFile(string path, FileSystemRights adminRights, bool publicRead)
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(SystemSid);
        acl.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(Admins, adminRights, AccessControlType.Allow));
        if (publicRead) acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }

    private static IEnumerable<FileSystemInfo> Enumerate(string directory) =>
        new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false });

    private static bool ServiceKeyExists()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
        return key is not null;
    }

    private static void SaveBackup(string backup, IEnumerable<string> paths)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            try
            {
                if (Directory.Exists(path)) snapshot[path] = new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
                else if (File.Exists(path)) snapshot[path] = new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { } // исчез между перебором и чтением
        }
        if (ServiceKeyExists())
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPath)!;
            snapshot[RegistryBackupName] = key.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        }
        File.WriteAllText(backup, JsonSerializer.Serialize(snapshot));
    }

    // Возврат прежних ACL при частичной ошибке; исходные файлы не удаляются.
    private static void Restore(string backup, string serviceBackup)
    {
        if (File.Exists(backup))
        {
            var snapshot = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(backup)) ?? [];
            foreach (var (path, sddl) in snapshot)
            {
                try
                {
                    if (path.StartsWith("Registry::", StringComparison.OrdinalIgnoreCase))
                    {
                        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.TakeOwnership | RegistryRights.ReadKey);
                        if (key is null) continue;
                        var acl = new RegistrySecurity(); acl.SetSecurityDescriptorSddlForm(sddl); key.SetAccessControl(acl);
                    }
                    else if (Directory.Exists(path)) { var acl = new DirectorySecurity(); acl.SetSecurityDescriptorSddlForm(sddl); new DirectoryInfo(path).SetAccessControl(acl); }
                    else if (File.Exists(path)) { var acl = new FileSecurity(); acl.SetSecurityDescriptorSddlForm(sddl); new FileInfo(path).SetAccessControl(acl); }
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException) { }
            }
        }
        if (ServiceKeyExists() && File.Exists(serviceBackup))
            try { SetServiceSddl(File.ReadAllText(serviceBackup).Trim()); }
            catch (IOException) { }
    }

    private static string ServiceSddl()
    {
        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("sdshow"); info.ArgumentList.Add("WatcherSessionService");
        using var process = System.Diagnostics.Process.Start(info) ?? throw new IOException("Cannot snapshot service descriptor");
        string output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(30000) || process.ExitCode != 0) throw new IOException("Cannot snapshot service descriptor");
        return output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("O:") || l.StartsWith("G:") || l.StartsWith("D:"))
            ?? throw new IOException("Cannot snapshot service descriptor");
    }

    public static void SetServiceSddl(string sddl)
    {
        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("sdset"); info.ArgumentList.Add("WatcherSessionService"); info.ArgumentList.Add(sddl);
        using var process = System.Diagnostics.Process.Start(info) ?? throw new IOException("Service descriptor update failed");
        if (!process.WaitForExit(30000) || process.ExitCode != 0) throw new IOException("Service descriptor update failed");
    }
}
