using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using System.Xml;

namespace Watcher.Guard;

// 0.16: задача \Microsoft\Watcher\GuardRepair без PowerShell. Действие — постоянный
// лаунчер WatcherGuard\GuardRepair.exe tick: он проверяет SHA-256 и запускает
// версию из bin\<hash> по файлу launcher (как раньше recover.ps1), поэтому откат
// Guard на прежнюю версию задачу не трогает. Регистрация — schtasks /XML: в XML
// задаются и права самой задачи (SYSTEM — всё, администраторам чтение/запуск).
// Чужую задачу с тем же именем не меняем: принимается только прежнее действие
// (powershell … recover.ps1) или новое.
[SupportedOSPlatform("windows")]
public static class GuardWindowsTask
{
    public const string TaskPath = @"\Microsoft\Watcher\GuardRepair";
    public const string LegacyTaskPath = @"\WatcherGuard";
    public const string TaskSecurity = "O:SYG:SYD:P(A;;GA;;;SY)(A;;GRGX;;;BA)";
    private const string Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string Launcher(string root) => Path.Combine(root, "GuardRepair.exe");
    public static string LegacyCommand => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
    public static string LegacyArguments(string root) => "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + Path.Combine(root, "recover.ps1") + "\"";

    public enum Kind { Missing, Legacy, Native }
    public sealed record Action(string Command, string Arguments, string? UserId);

    // XML задачи: при загрузке и каждые 2 минуты, от SYSTEM, одна копия, до 20 минут.
    // Одноразовая bootstrap-задача регистрируется администратором, а он не может
    // назначить задаче владельца SYSTEM (schtasks: «Отказано в доступе») — только DACL.
    public const string BootstrapTaskSecurity = "D:P(A;;GA;;;SY)(A;;GA;;;BA)";

    public static string TaskXml(string command, string arguments, bool repeating, string description, string interval = "PT2M", string limit = "PT20M", string security = TaskSecurity)
    {
        string triggers = repeating
            ? $"<Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger><TimeTrigger><Repetition><Interval>{interval}</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>"
              + "<StartBoundary>2026-01-01T00:00:00</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>"
            : "<Triggers />";
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>"
            + $"<Task version=\"1.2\" xmlns=\"{Ns}\">"
            + $"<RegistrationInfo><Description>{SecurityElement.Escape(description)}</Description><SecurityDescriptor>{security}</SecurityDescriptor></RegistrationInfo>"
            + triggers
            + "<Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>"
            + "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"
            + "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><ExecutionTimeLimit>" + limit + "</ExecutionTimeLimit><Enabled>true</Enabled></Settings>"
            + $"<Actions Context=\"Author\"><Exec><Command>{SecurityElement.Escape(command)}</Command><Arguments>{SecurityElement.Escape(arguments)}</Arguments></Exec></Actions>"
            + "</Task>";
    }

    public static Action? ParseAction(string xml)
    {
        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            doc.Load(reader);
        var ns = new XmlNamespaceManager(doc.NameTable); ns.AddNamespace("t", Ns);
        var execs = doc.SelectNodes("/t:Task/t:Actions/t:Exec", ns);
        if (execs is null || execs.Count != 1 || doc.SelectNodes("/t:Task/t:Actions/*", ns)?.Count != 1) return null;
        string command = (execs[0]!.SelectSingleNode("t:Command", ns)?.InnerText ?? "").Trim().Trim('"');
        string arguments = (execs[0]!.SelectSingleNode("t:Arguments", ns)?.InnerText ?? "").Trim();
        string? user = doc.SelectSingleNode("/t:Task/t:Principals/t:Principal/t:UserId", ns)?.InnerText.Trim();
        return new Action(command, arguments, user);
    }

    // Чья задача: прежняя (PowerShell recover.ps1), новая (лаунчер) или чужая — null.
    public static Kind? Classify(Action? action, string root, Func<string, string?>? sidOf = null)
    {
        if (action is null) return null;
        bool system = action.UserId is "S-1-5-18" or "SYSTEM" || string.Equals(action.UserId, @"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase)
            || (action.UserId is not null && sidOf?.Invoke(action.UserId) == "S-1-5-18");
        if (!system) return null;
        if (string.Equals(action.Command, LegacyCommand, StringComparison.OrdinalIgnoreCase)
            && string.Equals(action.Arguments, LegacyArguments(root), StringComparison.OrdinalIgnoreCase)) return Kind.Legacy;
        if (string.Equals(action.Command, Launcher(root), StringComparison.OrdinalIgnoreCase) && action.Arguments == "tick") return Kind.Native;
        return null;
    }

    public static Kind Inspect(string taskPath, string root)
    {
        string? xml = Query(taskPath);
        if (xml is null) return Kind.Missing;
        return Classify(ParseAction(xml), root, SidOf) ?? throw new IOException("Refusing to change a task not owned by this Watcher installation");
    }

    // Новая задача (лаунчер); прежняя \WatcherGuard снимается только после успеха.
    public static void Register(string root)
    {
        Inspect(TaskPath, root);
        var legacy = Inspect(LegacyTaskPath, root);
        string? previous = Query(TaskPath);
        try
        {
            Create(TaskPath, TaskXml(Launcher(root), "tick", repeating: true, "Watcher GuardRepair: verifies and restores the Watcher agent and service."));
        }
        catch
        {
            try { if (previous is not null) Create(TaskPath, previous); else Delete(TaskPath); }
            catch (IOException) { throw new IOException("GuardRepair task migration and rollback failed; inspect both Watcher task locations"); }
            throw;
        }
        if (legacy != Kind.Missing) Delete(LegacyTaskPath);
    }

    public static void Remove(string root)
    {
        var current = Inspect(TaskPath, root);
        var legacy = Inspect(LegacyTaskPath, root);
        if (current != Kind.Missing) Delete(TaskPath);
        if (legacy != Kind.Missing) Delete(LegacyTaskPath);
    }

    public static void Create(string taskPath, string xml)
    {
        string file = Path.Combine(Path.GetTempPath(), "watcher-task-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try
        {
            if (Schtasks(["/Create", "/F", "/TN", taskPath, "/XML", file], out string output) != 0)
                throw new IOException("Task registration failed: " + output);
        }
        finally { File.Delete(file); }
    }

    public static void Delete(string taskPath)
    {
        if (Schtasks(["/Delete", "/F", "/TN", taskPath], out string output) != 0) throw new IOException("Task removal failed: " + output);
    }

    public static void End(string taskPath) => Schtasks(["/End", "/TN", taskPath], out _);

    public static void Run(string taskPath)
    {
        if (Schtasks(["/Run", "/TN", taskPath], out string output) != 0) throw new IOException("Task start failed: " + output);
    }

    public static string? Query(string taskPath) =>
        Schtasks(["/Query", "/TN", taskPath, "/XML"], out string output) == 0 ? output : null;

    private static string? SidOf(string account)
    {
        try { return ((System.Security.Principal.SecurityIdentifier)new System.Security.Principal.NTAccount(account).Translate(typeof(System.Security.Principal.SecurityIdentifier))).Value; }
        catch (Exception e) when (e is System.Security.Principal.IdentityNotMappedException or SystemException) { return null; }
    }

    private static int Schtasks(string[] arguments, out string output)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("schtasks did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60000)) { process.Kill(entireProcessTree: true); throw new IOException("schtasks timed out"); }
        output = stdout.GetAwaiter().GetResult() + stderr;
        return process.ExitCode;
    }
}
