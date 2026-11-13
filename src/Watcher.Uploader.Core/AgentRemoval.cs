using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Watcher.Uploader;

// 0.13 (п.14 обратной связи 2026-10-14): хост удалён в веб-UI — сервер отвечает
// учётке этого агента 410 с заголовком X-Watcher-Uninstall (AgentAccess на
// сервере). Агент удаляет себя со станции: сразу, если на связи, или когда
// вернётся. Как удалять — знает процесс-хозяин (Remover): пакет Linux,
// MSI на Windows. Один раз на процесс.
public static class AgentRemoval
{
    public const string Header = "X-Watcher-Uninstall";
    public static Action<ILogger>? Remover { get; set; }
    private static int _started;

    public static bool IsUninstallResponse(HttpResponseMessage resp) =>
        (int)resp.StatusCode == 410 && resp.Headers.Contains(Header);

    public static bool Check(HttpResponseMessage resp, ILogger logger)
    {
        if (!IsUninstallResponse(resp)) return false;
        if (GuardPaths.Enabled)
        { logger.LogWarning("maintenance rejected: signed uninstall authorization required"); return true; }
        if (Interlocked.Exchange(ref _started, 1) != 0) return true;
        if (Remover is not { } remove)
        {
            logger.LogError("server: this host was deleted — remove the agent manually (no uninstaller for this install)");
            return true;
        }
        logger.LogWarning("server: this host was deleted in the web UI — uninstalling the agent");
        try { remove(logger); }
        catch (Exception ex) { logger.LogError(ex, "self-uninstall failed — remove the agent manually"); }
        return true;
    }

    // Агент поставлен пакетом watcher-agent (deb или rpm) — только тогда умеем
    // удалить его сами; установку скриптами старых версий — руками.
    public static bool IsLinuxPackageInstalled()
    {
        if (!OperatingSystem.IsLinux()) return false;
        return Quiet("dpkg-query", "-W", "-f=${Status}", "watcher-agent") is { Code: 0 } d && d.Out.Contains("install ok installed", StringComparison.Ordinal)
            || Quiet("rpm", "-q", "watcher-agent") is { Code: 0 };
    }

    private static (int Code, string Out)? Quiet(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return (p.ExitCode, o);
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    // Linux, пакет .deb/.rpm: отдельный юнит systemd — переживает остановку
    // служб агента, которые пакет при удалении гасит (preremove →
    // watcher-agent-setup --unconfigure: службы, PAM-хук, правило auditd).
    // Данные (очередь, учётки) — тоже: хост на сервере удалён, выгружать некуда.
    public static void RemoveLinuxPackage(ILogger logger)
    {
        const string script =
            "sleep 3; " +
            "if dpkg -s watcher-agent >/dev/null 2>&1; then DEBIAN_FRONTEND=noninteractive apt-get purge -y watcher-agent || dpkg --purge watcher-agent; " +
            "elif rpm -q watcher-agent >/dev/null 2>&1; then dnf remove -y watcher-agent || yum remove -y watcher-agent || rpm -e watcher-agent; " +
            "else echo 'watcher-agent package not found' >&2; exit 1; fi; " +
            "rm -rf /var/lib/watcher /var/lib/watcher-terminal-agent";
        var psi = new ProcessStartInfo("systemd-run") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "--unit=watcher-self-uninstall", "--collect", "--no-block", "/bin/sh", "-c", script })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("systemd-run did not start");
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        // Второй агент той же станции (видео и консоль) запускает тот же юнит —
        // «уже существует» не ошибка.
        if (p.ExitCode != 0 && !err.Contains("already", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"systemd-run exit {p.ExitCode}: {err.Trim()}");
        logger.LogWarning("self-uninstall scheduled (systemd unit watcher-self-uninstall)");
    }
}
