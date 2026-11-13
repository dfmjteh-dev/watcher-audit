using System.Diagnostics;
using System.Runtime.Versioning;
using Watcher.Uploader;

namespace Watcher.Guard;

// Включение защиты на Linux и постоянный лаунчер таймера.
//   GuardRepair setup MAINTENANCE_KEY UPDATE_KEY SERVER_URL [PACKAGE MANIFEST]
//     — watcher-agent-setup --maintenance-key: включить защиту;
//   GuardRepair setup --refresh
//     — postinstall пакета при включённой защите: новая версия GuardRepair в bin/<hash>.
// watcher-guard.service запускает /var/lib/watcher/guard/GuardRepair tick (GuardLauncher).
[SupportedOSPlatform("linux")]
public static class GuardLinuxSetup
{
    public const string SourceExecutable = "/opt/watcher-guard/GuardRepair";
    public const string ServiceUnit = "/etc/systemd/system/watcher-guard.service";
    public const string TimerUnit = "/etc/systemd/system/watcher-guard.timer";
    private static readonly string[] AgentUnits = ["watcher-uploader.service", "watcher-terminal-agent.service"];
    private static readonly string[] ProgramDirectories = ["/opt/watcher-uploader", "/opt/watcher-terminal-agent", "/opt/watcher-capture"];
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode GroupOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public static string ServiceText(string root) => $"""
        [Unit]
        Description=Watcher independent recovery
        After=local-fs.target
        [Service]
        Type=oneshot
        User=root
        Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR={GuardLauncher.BundleDirectory(root)}
        ExecStart={GuardLauncher.Path(root)} tick
        TimeoutStartSec=20min
        UMask=0077

        """;

    public const string TimerText = """
        [Unit]
        Description=Check Watcher service and protected files
        [Timer]
        OnBootSec=60s
        OnUnitActiveSec=2min
        Unit=watcher-guard.service
        [Install]
        WantedBy=timers.target

        """;

    public sealed record Options(bool Refresh, string MaintenanceKey = "", string UpdateKey = "", string ServerUrl = "", string Package = "", string Manifest = "");

    // Аргументы — как у setup-guard.sh: 3 или 5 позиционных (пустые package/manifest
    // допустимы — watcher-agent-setup передаёт их всегда) либо --refresh.
    public static Options Parse(string[] args) => args switch
    {
        ["--refresh"] => new Options(true),
        [var m, var u, var s] => new Options(false, m, u, s),
        [var m, var u, var s, var p, var f] => new Options(false, m, u, s, p, f),
        _ => throw new ArgumentException("Usage: GuardRepair setup MAINTENANCE_PUBLIC_KEY UPDATE_PUBLIC_KEY SERVER_URL [PACKAGE MANIFEST] | setup --refresh"),
    };

    public static void Run(string[] args)
    {
        var options = Parse(args);
        if (!Environment.IsPrivilegedProcess) throw new UnauthorizedAccessException("root required");
        string root = GuardPaths.Root;
        if (IsLink(root)) throw new IOException("guard directory is a symlink");
        Directory.CreateDirectory(root, Private);
        Directory.CreateDirectory(Path.Combine(root, "bin"), Private);

        // Версия из пакета → bin/<sha256>/GuardRepair (её выбирает launcher).
        string source = File.Exists(SourceExecutable) ? SourceExecutable : Environment.ProcessPath ?? throw new IOException("GuardRepair path unknown");
        string hash = GuardLauncher.Sha256(source);
        string versionDir = Path.Combine(root, "bin", hash);
        Directory.CreateDirectory(versionDir, Private);
        string versioned = Path.Combine(versionDir, "GuardRepair");
        GuardLauncher.CopyIfChanged(source, versioned);
        File.SetUnixFileMode(versioned, Private);
        GuardLauncher.VerifiedExecutable(root, hash);

        var restart = new List<string>();
        try
        {
            if (!options.Refresh)
            {
                foreach (string unit in AgentUnits)
                    if (Systemctl("is-active", "--quiet", unit) == 0) { restart.Add(unit); Require(Systemctl("stop", unit), "stop " + unit); }
                GuardBootstrap.Configure("/opt", options.MaintenanceKey, options.UpdateKey, options.ServerUrl);
            }
            GuardSnapshot.Take(root);
            if (!options.Refresh)
            {
                if (options.Package.Length > 0 && options.Manifest.Length > 0) Seed(root, options.Package, options.Manifest);
                // 0.16: как на Windows без .sig — защита включается и без базы (агент
                // включает её сам при обновлении); база придёт с сервера с первым обновлением.
                else if (!File.Exists(Path.Combine(root, "enabled")))
                    Console.Error.WriteLine("WARNING: no signed recovery package — protection is on; deleted agent files are restored after the first update from the server.");
            }
            File.WriteAllText(Path.Combine(root, "launcher"), hash + "\n");
            File.WriteAllText(Path.Combine(root, "enabled"), "1\n");
            InstallLauncher(root, versioned);
            ClosePermissions(root);
            // Программы root-owned; записи и очереди пользователей лежат отдельно.
            foreach (string directory in ProgramDirectories)
            {
                if (!Directory.Exists(directory) || IsLink(directory)) continue;
                Require(Tool("/usr/bin/chown", "-R", "root:root", directory), "chown " + directory);
                Require(Tool("/usr/bin/chmod", "-R", "go-w", directory), "chmod " + directory);
            }
            Require(Systemctl("daemon-reload"), "daemon-reload");
            Require(Systemctl("enable", "--now", "watcher-guard.timer"), "enable watcher-guard.timer");
        }
        finally
        {
            foreach (string unit in restart) Systemctl("start", unit);
        }
        GuardBootstrap.FinalizeMigration();
    }

    // Лаунчер и юниты таймера.
    public static void InstallLauncher(string root, string versioned)
    {
        string launcher = GuardLauncher.Path(root);
        GuardLauncher.CopyIfChanged(versioned, launcher);
        File.SetUnixFileMode(launcher, Private);
        WriteUnit(ServiceUnit, ServiceText(root));
        WriteUnit(TimerUnit, TimerText);
    }

    private static void Seed(string root, string package, string manifestPath)
    {
        using var manifest = File.OpenRead(manifestPath);
        var metadata = GuardRecoveryCache.ReadManifest(manifest);
        string expected = File.Exists("/usr/bin/dpkg") ? "linux-deb" : "linux-rpm";
        if (metadata.Version != typeof(GuardEngine).Assembly.GetName().Version?.ToString(3)) throw new IOException("Recovery package version mismatch");
        if (metadata.Platform != expected) throw new IOException("Recovery platform mismatch");
        using var payload = File.OpenRead(package);
        GuardRecoveryCache.Seed(root, metadata, payload);
    }

    // chmod -R go-rwx: ссылки не трогаем и не проходим.
    private static void ClosePermissions(string root)
    {
        foreach (string path in new[] { root }.Concat(Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })))
        {
            if (IsLink(path)) continue;
            var mode = File.GetUnixFileMode(path);
            if ((mode & GroupOther) != 0) File.SetUnixFileMode(path, mode & ~GroupOther);
        }
    }

    private static void WriteUnit(string path, string text)
    {
        if (IsLink(path)) throw new IOException("Linked systemd unit refused: " + path);
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        string temporary = path + ".new";
        File.WriteAllText(temporary, text);
        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.Move(temporary, path, overwrite: true);
    }

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    private static void Require(int exit, string what) { if (exit != 0) throw new IOException(what + " failed with exit code " + exit); }
    private static int Systemctl(params string[] arguments) => Tool("/usr/bin/systemctl", arguments);

    private static int Tool(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException(file + " did not start");
        if (!process.WaitForExit(TimeSpan.FromMinutes(5))) { process.Kill(entireProcessTree: true); throw new IOException(file + " timed out"); }
        return process.ExitCode;
    }
}
