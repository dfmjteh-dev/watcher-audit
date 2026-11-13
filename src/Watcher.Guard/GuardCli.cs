using System.Text;
using System.Text.Json;
using Watcher.Uploader;

namespace Watcher.Guard;

// 0.16, п.7: понятный вывод для администратора станции без связи.
//   GuardRepair submit <grant.json> [--package <file>] — принять файл разрешения;
//   GuardRepair status — состояние защиты, ожидающие и выполненные разрешения.
public static class GuardCli
{
    private static readonly Dictionary<string, string> Actions = new()
    {
        ["stop"] = "stop the agent",
        ["resume"] = "resume the agent",
        ["repair"] = "repair the agent",
        ["update"] = "update the agent",
        ["uninstall"] = "remove the agent",
        ["rotate-key"] = "change the maintenance key",
    };
    public static string Describe(string action) => Actions.TryGetValue(action, out var text) ? text : action;
    private static string Local(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    // Проверки до постановки в очередь — то, что админ может исправить сам. Подпись и
    // привязку к учётке всё равно проверяет Guard на тике (файл из inbox не доверенный).
    public static string? Problem(SignedMaintenanceGrant signed, GuardPolicy? policy, DateTime now, string root)
    {
        var grant = signed.Grant;
        if (!MaintenanceSigning.Shape(grant)) return "The file is not a Watcher maintenance grant.";
        if (grant.ExpiresAt < now) return $"The grant expired at {Local(grant.ExpiresAt)}. Ask the Watcher administrator for a new file.";
        if (policy is null) return "Agent protection is not set up on this computer; the grant is not needed.";
        if (!policy.Credentials.ContainsKey(grant.Kind)) return $"This computer has no '{grant.Kind}' agent protected by GuardRepair. The file was issued for another agent.";
        if (File.Exists(Path.Combine(root, "results", grant.Id + ".json"))) return "This grant has already been processed. Run 'GuardRepair status' to see the result.";
        return null;
    }

    public static string Accepted(SignedMaintenanceGrant signed, string executable) =>
        $"Accepted: {Describe(signed.Grant.Action)} ({signed.Grant.Kind}), valid until {Local(signed.Grant.ExpiresAt)}." + Environment.NewLine
        + "GuardRepair verifies the signature and runs it on its next check, within 2 minutes." + Environment.NewLine
        + (signed.Grant.Action == "stop" ? $"The agent stays stopped until {Local(signed.Grant.ExpiresAt)}, then protection starts it again." + Environment.NewLine : "")
        + $"Check the result: {executable} status" + Environment.NewLine
        + "The server shows the result once this computer connects again.";

    public static int Submit(string file, string? package)
    {
        var info = new FileInfo(file);
        if (!info.Exists) { Console.Error.WriteLine($"File not found: {file}"); return 1; }
        if (info.Length > 16384) { Console.Error.WriteLine("The file is too large to be a maintenance grant."); return 1; }
        SignedMaintenanceGrant? signed;
        try { signed = JsonSerializer.Deserialize<SignedMaintenanceGrant>(File.ReadAllBytes(info.FullName), GuardPaths.Json); }
        catch (JsonException) { signed = null; }
        if (signed?.Grant is null) { Console.Error.WriteLine("The file is not a Watcher maintenance grant."); return 1; }
        string root = GuardPaths.Root;
        GuardPolicy? policy;
        try { RequireAccess(root); policy = GuardPaths.Load(root); }
        catch (UnauthorizedAccessException) { Console.Error.WriteLine(OperatingSystem.IsWindows() ? "Run the command prompt as administrator." : "Run as root (sudo)."); return 1; }
        if (Problem(signed, policy, DateTime.UtcNow, root) is { } problem) { Console.Error.WriteLine(problem); return 1; }
        if (package is not null)
        {
            if (signed.Grant.Action != "update") { Console.Error.WriteLine("--package is only for an update grant."); return 1; }
            string inbox = Path.Combine(root, "inbox"); Directory.CreateDirectory(inbox);
            using var source = File.OpenRead(package);
            if (source.Length != signed.Grant.PackageSize) { Console.Error.WriteLine("The package does not match the grant (size). Download the package shown next to the grant."); return 1; }
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)).ToLowerInvariant();
            if (!string.Equals(hash, signed.Grant.Sha256, StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine("The package does not match the grant (SHA-256)."); return 1; }
            source.Position = 0;
            using var output = new FileStream(Path.Combine(inbox, signed.Grant.Id + ".package"), FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(output); output.Flush(true);
        }
        else if (signed.Grant.Action == "update") { Console.Error.WriteLine("An update grant needs the package: add --package <file>."); return 1; }
        GuardPaths.Queue(signed);
        Console.WriteLine(Accepted(signed, Executable()));
        return 0;
    }

    public static int Status()
    {
        try { Console.Write(Report(GuardPaths.Root, DateTime.UtcNow)); return 0; }
        catch (UnauthorizedAccessException) { Console.Error.WriteLine(OperatingSystem.IsWindows() ? "Run the command prompt as administrator." : "Run as root (sudo)."); return 1; }
    }

    // Каталог Guard закрыт (Linux 0700, Windows — чтение только администраторам):
    // без прав File.Exists молчит, и защита выглядела бы «не настроенной».
    public static void RequireAccess(string root)
    {
        if (Directory.Exists(root)) _ = Directory.EnumerateFileSystemEntries(root).Any();
    }

    public static string Report(string root, DateTime now)
    {
        RequireAccess(root);
        var text = new StringBuilder();
        var policy = Directory.Exists(root) ? GuardPaths.Load(root) : null;
        bool removed = File.Exists(Path.Combine(root, "uninstalled.json"));
        string state = policy is null ? "not set up" : removed ? "agent removed by an authorized grant" : File.Exists(Path.Combine(root, "enabled")) ? "on" : "not finished";
        text.AppendLine($"Agent protection: {state}");
        if (policy is null) return text.ToString();
        text.AppendLine($"GuardRepair: {typeof(GuardEngine).Assembly.GetName().Version?.ToString(3)}");
        var basePackage = policy.LastVerifiedPackage?.Version ?? policy.InitialPackage?.Version;
        text.AppendLine("Recovery base: " + (basePackage ?? "none yet (deleted agent files are restored after the first update)"));
        text.AppendLine("Protected agents: " + string.Join(", ", policy.Credentials.Keys));

        string active = Path.Combine(root, "active.json");
        if (File.Exists(active) && Read<SignedMaintenanceGrant>(active)?.Grant is { } lease && lease.ExpiresAt > now)
            text.AppendLine($"Active: {Describe(lease.Action)} until {Local(lease.ExpiresAt)}");

        string inbox = Path.Combine(root, "inbox");
        var pending = Directory.Exists(inbox) ? Directory.EnumerateFiles(inbox, "*.json").Select(Read<SignedMaintenanceGrant>).Where(g => g?.Grant is not null).Select(g => g!.Grant).ToList() : [];
        text.AppendLine($"Waiting to run: {pending.Count}");
        foreach (var g in pending.OrderBy(g => g.IssuedAt))
            text.AppendLine($"  {Describe(g.Action)} ({g.Kind}), valid until {Local(g.ExpiresAt)}" + (g.ExpiresAt < now ? " — expired, will be dropped" : ""));

        string results = Path.Combine(root, "results");
        var done = Directory.Exists(results) ? Directory.EnumerateFiles(results, "*.json").Select(Read<GuardExecution>).Where(r => r is not null).Select(r => r!).OrderByDescending(r => r.At).Take(10).ToList() : [];
        text.AppendLine("Recent results:" + (done.Count == 0 ? " none" : ""));
        foreach (var r in done)
            text.AppendLine($"  {Local(r.At)}  {Describe(r.Action)} ({r.Kind}): {Outcome(r.Status, r.Code)}");
        return text.ToString();
    }

    public static string Outcome(string status, string code) => status switch
    {
        "completed" => "done",
        "accepted" => "in progress",
        "failed" => "failed (" + code + ")",
        "rejected" => "rejected (" + code + ")",
        _ => status,
    };

    private static T? Read<T>(string path) where T : class
    {
        try { return new FileInfo(path).Length > 65536 ? null : JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), GuardPaths.Json); }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    // Стабильный путь, который печатаем в подсказке «как проверить».
    private static string Executable() => OperatingSystem.IsWindows()
        ? "\"" + GuardLauncher.Path(GuardPaths.Root) + "\""
        : "sudo " + GuardLauncher.Path(GuardPaths.Root);
}
