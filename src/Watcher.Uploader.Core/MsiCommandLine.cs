namespace Watcher.Uploader;

public static class MsiCommandLine
{
    // msiexec разбирает свойства из исходной командной строки: NAME="value",
    // а не "NAME=value", как автоматически делает ProcessStartInfo.ArgumentList.
    // downgrade: пакет старше установленного. Windows Installer решает про файлы до
    // удаления новой версии и не ставит «младший» exe (2753: GuardExe is not marked for
    // installation) — REINSTALLMODE=amus ставит все файлы пакета независимо от версии.
    public static string Install(string package, bool repair, string log, string installDir, string serverUrl, string updateKey, string maintenanceKey, bool downgrade = false) =>
        string.Join(" ", new[] { repair ? "/fa" : "/i", Quote(package), "/qn", "/norestart", "/l*v", Quote(log),
            downgrade ? "REINSTALLMODE=amus" : "",
            "INSTALLFOLDER=" + Quote(installDir.TrimEnd('\\')), "SERVERURL=" + Quote(serverUrl),
            "UPDATEKEY=" + Quote(updateKey), "MAINTENANCEKEY=" + Quote(maintenanceKey),
            // База восстановления 0.16 — настроенный MSI комплекта с зашитым токеном
            // регистрации: при ремонте и откате станция уже зарегистрирована.
            "ENROLLTOKEN=\"\"" }.Where(part => part.Length > 0));

    private static string Quote(string value)
    {
        if (value.Any(c => c == '"' || char.IsControl(c))) throw new ArgumentException("Invalid MSI argument");
        if (value.EndsWith('\\')) throw new ArgumentException("MSI argument must not end in a backslash");
        return "\"" + value + "\"";
    }
}
