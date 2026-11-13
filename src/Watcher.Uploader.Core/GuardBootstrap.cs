using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Watcher.Uploader;

public static class GuardBootstrap
{
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    public static void Configure(string installDir, string maintenanceKey, string updateKey, string serverUrl)
    {
        string root = GuardPaths.Root; Directory.CreateDirectory(root);
        using (var key = UpdateSigning.LoadPublicKey(updateKey)) { if (key is null) throw new CryptographicException("Publisher public key required"); }
        using (var key = ECDsa.Create()) { key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(maintenanceKey), out _); if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") throw new CryptographicException("P-256 maintenance public key required"); }
        var old = GuardPaths.Load();
        if (old is not null && File.Exists(Path.Combine(root, "uninstalled.json")))
        {
            if (old.MaintenancePublicKey != maintenanceKey) throw new CryptographicException("Use the previous pinned key for bootstrap");
            File.Delete(Path.Combine(root, "initialized")); File.Delete(Path.Combine(root, "active.json")); File.Delete(Path.Combine(root, "uninstalled.json")); old = null;
        }
        if (old is not null)
        {
            if (old.MaintenancePublicKey != maintenanceKey) throw new CryptographicException("Use signed key rotation");
            foreach (var entry in old.ConfigHashes.Where(p => p.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                string backup = Path.Combine(root, "config-backups", entry.Key);
                if (!File.Exists(backup) || Hash(backup) != entry.Value) throw new IOException("Verified configuration backup missing");
                GuardPaths.WriteBytes(Path.Combine(old.InstallDir, entry.Key), File.ReadAllBytes(backup), readableByUsers: GuardPaths.PublicConfiguration(entry.Key));
            }
            return;
        }
        installDir = Path.GetFullPath(installDir);
        var migrations = new Dictionary<string, string>();
        var files = new List<string>(); var credentials = new Dictionary<string, string>(); var kinds = new HashSet<string>();
        string[] configurations = OperatingSystem.IsWindows() ? ["Watcher.Uploader.json", "Watcher.SessionService.json", "Watcher.Capture.json"]
            : ["watcher-uploader/Watcher.Uploader.json", "watcher-terminal-agent/Watcher.TerminalAgent.json", "watcher-capture/Watcher.Capture.Linux.json"];
        foreach (string relative in configurations)
        {
            string path = Path.Combine(installDir, relative); if (!File.Exists(path)) continue;
            if (new FileInfo(path).Length > 1048576) throw new IOException("Configuration exceeds limit");
            var document = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new IOException("Invalid initial configuration");
            string? section = relative.Contains("Uploader", StringComparison.Ordinal) ? "Uploader"
                : relative.Contains("TerminalAgent", StringComparison.Ordinal) ? "TerminalAgent"
                : OperatingSystem.IsWindows() && relative.Contains("SessionService", StringComparison.Ordinal) ? "TerminalAudit" : null;
            JsonObject? config = section == "TerminalAudit" ? document["SessionService"]?["TerminalAudit"] as JsonObject
                : section is not null ? document[section] as JsonObject : null;
            if (config is not null)
            {
                string kind = section == "Uploader" ? "video" : "terminal";
                string previous = config["CredentialPath"]?.GetValue<string>() ?? (OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Watcher", "agent", kind == "terminal" ? "terminal-credential.json" : "credential.json")
                    : kind == "video" ? "/var/lib/watcher/agent/credential.json" : "/var/lib/watcher-terminal-agent/agent/credential.json");
                string folder = Path.Combine(root, "credentials", kind); Directory.CreateDirectory(folder);
                string destination = Path.Combine(folder, "credential.json");
                // Переносим учётку, журнал потерь и диагностику без новой регистрации.
                foreach (string name in new[] { Path.GetFileName(previous), "losses.jsonl", "diagnostics.json", "diagnostics.json.capture-acked" })
                {
                    string source = Path.Combine(Path.GetDirectoryName(previous)!, name);
                    string target = name == Path.GetFileName(previous) ? destination : Path.Combine(folder, name);
                    if (File.Exists(source) && !string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    {
                        if (File.Exists(target))
                        {
                            if (Hash(source) != Hash(target))
                            {
                                if (name == Path.GetFileName(previous)) throw new IOException("Existing credential differs");
                                // После разрешённого удаления старый журнал мог сохраниться
                                // в обоих каталогах. Не терять ни одну версию состояния.
                                GuardPaths.WriteBytes(Path.Combine(root, "migration-state", kind, Hash(source) + "-" + name), File.ReadAllBytes(source));
                            }
                        }
                        else GuardPaths.WriteBytes(target, File.ReadAllBytes(source));
                        if (name == Path.GetFileName(previous)) migrations[source] = target;
                    }
                }
                config["CredentialPath"] = destination; GuardPaths.Write(path, document); credentials[kind] = destination;
                if (OperatingSystem.IsWindows())
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WatcherSessionService");
                    if (key?.GetValue("Start") is int start && start == 2) kinds.Add(kind);
                }
                else
                {
                    var info = new System.Diagnostics.ProcessStartInfo("/usr/bin/systemctl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    info.ArgumentList.Add("is-enabled"); info.ArgumentList.Add("--quiet"); info.ArgumentList.Add(kind == "video" ? "watcher-uploader.service" : "watcher-terminal-agent.service");
                    using var check = System.Diagnostics.Process.Start(info) ?? throw new IOException("Cannot inspect service startup");
                    if (!check.WaitForExit(10000)) { check.Kill(); throw new IOException("Service startup query timed out"); }
                    if (check.ExitCode == 0) kinds.Add(kind);
                }
            }
            files.Add(relative);
        }
        if (OperatingSystem.IsWindows()) files.AddRange(new[] { "Watcher.Capture.exe", "Watcher.SessionService.exe", "ffmpeg.exe" }.Where(f => File.Exists(Path.Combine(installDir, f))));
        else files.AddRange(new[] { "watcher-capture/Watcher.Capture.Linux", "watcher-uploader/Watcher.Uploader", "watcher-terminal-agent/Watcher.TerminalAgent" }.Where(f => File.Exists(Path.Combine(installDir, f))));
        var policy = new GuardPolicy(installDir, maintenanceKey, updateKey, credentials, files.ToDictionary(f => f, f => Hash(Path.Combine(installDir, f))), kinds.Count > 0, ServerUrl: serverUrl, AutoStartKinds: kinds);
        GuardEngine.ValidatePolicy(policy);
        if (!policy.AutoStart && credentials.Values.All(p => !File.Exists(p))) throw new IOException("Enroll the agent before protecting a manual or disabled service");
        var units = new Dictionary<string, string>();
        if (OperatingSystem.IsLinux())
            foreach (string unit in new[] { "watcher-uploader.service", "watcher-terminal-agent.service" })
            {
                string source = File.Exists("/etc/systemd/system/" + unit) ? "/etc/systemd/system/" + unit : "/usr/lib/systemd/system/" + unit;
                if (!File.Exists(source)) continue;
                string directory = Path.Combine(root, "units"); Directory.CreateDirectory(directory); File.Copy(source, Path.Combine(directory, unit), true); units[unit] = Hash(source);
            }
        GuardPaths.Write(Path.Combine(root, "migration.json"), migrations);
        GuardPaths.Write(Path.Combine(root, "policy.json"), policy with { UnitHashes = units });
    }
    // Старый GuardEngine после проверенного MSI обновляет хеш alias старого имени.
    // Переносим его только если новое имя содержит ровно те же проверенные байты.
    public static GuardPolicy MigrateExecutableName(GuardPolicy policy)
    {
        if (!policy.ConfigHashes.TryGetValue("Watcher.Guard.exe", out string? expected)) return policy;
        string current = Path.Combine(policy.InstallDir, "GuardRepair.exe");
        if (!File.Exists(current)) return policy;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("GuardRepair reparse point refused");
        if (Hash(current) != expected) return policy;
        var hashes = new Dictionary<string, string>(policy.ConfigHashes);
        if (hashes.TryGetValue("GuardRepair.exe", out string? existing) && existing != expected)
            throw new IOException("GuardRepair hash conflicts with existing policy");
        hashes.Remove("Watcher.Guard.exe");
        hashes["GuardRepair.exe"] = expected;
        return policy with { ConfigHashes = hashes };
    }

    public static void FinalizeMigration()
    {
        string path = Path.Combine(GuardPaths.Root, "migration.json"); if (!File.Exists(path)) return;
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path), GuardPaths.Json) ?? [];
        foreach (var pair in entries)
            if (File.Exists(pair.Key) && File.Exists(pair.Value) && Hash(pair.Key) == Hash(pair.Value)) File.Delete(pair.Key);
        File.Delete(path);
    }

}
