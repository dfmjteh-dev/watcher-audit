using System.Diagnostics;
using System.Globalization;
using Watcher.Uploader;

namespace Watcher.Guard;

public sealed partial class GuardPlatform
{
    private static string Output(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Session query failed");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000)) { process.Kill(true); throw new IOException("Session query timeout"); }
        Task.WaitAll(output, error);
        if (process.ExitCode != 0) throw new IOException("Session query failed");
        return output.Result;
    }
    private static Dictionary<string, string> Properties(string text) => text.Split('\n')
        .Where(line => line.Contains('='))
        .Select(line => line.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);

    public void ReconcileCapture(GuardPolicy policy)
    {
        if (!OperatingSystem.IsLinux() || !policy.Credentials.ContainsKey("video") ||
            !(policy.AutoStartKinds?.Contains("video") ?? policy.AutoStart)) return;
        var active = new HashSet<uint>();
        foreach (string line in Output("/usr/bin/loginctl", "list-sessions", "--no-legend", "--no-pager").Split('\n'))
        {
            string? session = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (session is null || session.Length > 64 || !session.All(char.IsAsciiLetterOrDigit)) continue;
            var properties = Properties(Output("/usr/bin/loginctl", "show-session", session,
                "--property=User", "--property=Type", "--property=State", "--property=Class"));
            if (properties.GetValueOrDefault("Type") != "x11" || properties.GetValueOrDefault("Class") != "user" ||
                properties.GetValueOrDefault("State") is not ("active" or "online") ||
                !uint.TryParse(properties.GetValueOrDefault("User"), NumberStyles.None, CultureInfo.InvariantCulture, out uint uid) || uid == 0) continue;
            if (!active.Add(uid)) continue;
            string id = uid.ToString(CultureInfo.InvariantCulture);
            string[] account = Output("/usr/bin/getent", "passwd", id).Trim().Split(':');
            if (account.Length != 7 || account[2] != id) throw new IOException("Session account invalid");
            string[] userCommand = ["--user", account[0], "--", "/usr/bin/env", "XDG_RUNTIME_DIR=/run/user/" + id, "/usr/bin/systemctl", "--user"];
            var environment = Properties(Output("/usr/sbin/runuser", [.. userCommand, "show-environment"]));
            if (!environment.TryGetValue("DISPLAY", out string? display) || display.Length > 128 || !display.StartsWith(':')) continue;
            string unit = "watcher-capture-guard-" + id;
            if (Run("/usr/bin/systemctl", ["is-active", "--quiet", unit + ".service"]) == 0) continue;
            Run("/usr/sbin/runuser", [.. userCommand, "stop", "watcher-capture.service"]);
            Run("/usr/bin/systemctl", ["reset-failed", unit + ".service"]);
            // Fixed root-owned executable, run as the desktop user. No user command
            // or shell script is ever evaluated with root privileges.
            var args = new List<string> { "--quiet", "--collect", "--unit=" + unit, "--uid=" + id,
                "--gid=" + account[3], "--property=Restart=always", "--property=RestartSec=5s",
                "--property=StartLimitIntervalSec=0", "--property=UMask=0077", "--property=NoNewPrivileges=yes",
                "--setenv=HOME=" + account[5], "--setenv=USER=" + account[0], "--setenv=LOGNAME=" + account[0],
                "--setenv=XDG_RUNTIME_DIR=/run/user/" + id, "--setenv=DISPLAY=" + display };
            foreach (string key in new[] { "XAUTHORITY", "DBUS_SESSION_BUS_ADDRESS" })
                if (environment.TryGetValue(key, out string? value) && value.Length <= 4096 && !value.Contains('\0')) args.Add("--setenv=" + key + "=" + value);
            args.Add("/opt/watcher-capture/Watcher.Capture.Linux");
            if (Run("/usr/bin/systemd-run", args) != 0) throw new IOException("Capture supervisor start failed");
        }
        foreach (uint uid in CaptureUnits())
            if (!active.Contains(uid)) Run("/usr/bin/systemctl", ["stop", CaptureUnit(uid)]);
    }
    private static string CaptureUnit(uint uid) => "watcher-capture-guard-" + uid.ToString(CultureInfo.InvariantCulture) + ".service";
    private static IEnumerable<uint> CaptureUnits()
    {
        foreach (string line in Output("/usr/bin/systemctl", "list-units", "--all", "--plain", "--no-legend", "--no-pager", "watcher-capture-guard-*.service").Split('\n'))
        {
            string? unit = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            const string prefix = "watcher-capture-guard-";
            if (unit is not null && unit.StartsWith(prefix, StringComparison.Ordinal) && unit.EndsWith(".service", StringComparison.Ordinal) &&
                uint.TryParse(unit[prefix.Length..^8], NumberStyles.None, CultureInfo.InvariantCulture, out uint uid)) yield return uid;
        }
    }
    private static void StopCaptureSessions()
    {
        foreach (uint uid in CaptureUnits())
            if (Run("/usr/bin/systemctl", ["stop", CaptureUnit(uid)]) != 0) throw new IOException("Capture supervisor stop failed");
    }
}
