using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Watcher.Uploader;

// Этот файл root/SYSTEM-only. Каталоги очереди, захвата и секретов разделены установщиком.
public sealed record GuardPolicy(string InstallDir, string MaintenancePublicKey, string UpdatePublicKey,
    Dictionary<string, string> Credentials, Dictionary<string, string> ConfigHashes, bool AutoStart = true, MaintenanceGrant? LastVerifiedPackage = null, string ServerUrl = "", Dictionary<string, string>? UnitHashes = null, HashSet<string>? AutoStartKinds = null, GuardRecoveryPackage? InitialPackage = null, GuardRecoveryPackage? PreviousPackage = null, Dictionary<string, string>? StartupModes = null);
public static class GuardPaths
{
    public static string Root => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WatcherGuard") : "/var/lib/watcher/guard";
    public static bool Enabled => File.Exists(Path.Combine(Root, "enabled"));
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static GuardPolicy? Load(string? root = null)
    {
        string path = Path.Combine(root ?? Root, "policy.json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw new IOException("Guard policy exceeds limit");
        return JsonSerializer.Deserialize<GuardPolicy>(File.ReadAllBytes(path), Json);
    }
    public static void Write<T>(string path, T value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (value is GuardPolicy && bytes.Length > 65536) throw new IOException("Guard policy exceeds limit");
        WriteBytes(path, bytes);
    }
    public static void WriteBytes(string path, byte[] bytes, bool privateFile = true, bool readableByUsers = false)
        => WriteStream(path, stream => stream.Write(bytes), privateFile, readableByUsers);

    public static void WriteStream(string path, Action<Stream> write, bool privateFile = true, bool readableByUsers = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            FileStream stream;
            if (privateFile && OperatingSystem.IsWindows())
            {
                var security = new FileSecurity(); security.SetAccessRuleProtection(true, false);
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var owner = WindowsIdentity.GetCurrent().User ?? system; security.SetOwner(owner);
                security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.Read, AccessControlType.Allow));
                if (readableByUsers) security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
                // ACL задаётся при создании; обычный write-handle не имеет WRITE_DAC/WRITE_OWNER.
                stream = new FileInfo(temp).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
            }
            else
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | (readableByUsers ? UnixFileMode.GroupRead | UnixFileMode.OtherRead : 0);
                stream = new FileStream(temp, options);
            }
            using (stream) { write(stream); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }
    public static bool PublicConfiguration(string relative) => Path.GetFileName(relative) is "Watcher.Capture.json" or "Watcher.Capture.Linux.json";
    public static void Queue(SignedMaintenanceGrant grant, string? root = null)
    {
        if (!MaintenanceSigning.Shape(grant.Grant)) throw new ArgumentException("Invalid maintenance request");
        WriteBytes(Path.Combine(root ?? Root, "inbox", grant.Grant.Id + ".json"), JsonSerializer.SerializeToUtf8Bytes(grant, Json), privateFile: false);
    }
}
