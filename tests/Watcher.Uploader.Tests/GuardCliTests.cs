using Watcher.Guard;
using Xunit;

namespace Watcher.Uploader.Tests;

// 0.16, п.7: вывод GuardRepair submit/status для администратора станции.
public class GuardCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "watcher-cli-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);
    public GuardCliTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private static SignedMaintenanceGrant Grant(string action = "stop", string kind = "video", int minutes = 30, int issuedAgo = 0) =>
        new(new MaintenanceGrant(Guid.NewGuid().ToString("N"), new string('a', 64), kind, action, Now.AddMinutes(-issuedAgo), Now.AddMinutes(minutes - issuedAgo)), "sig", "key");

    private static GuardPolicy Policy() => new("/opt", "m", "u", new() { ["video"] = "/x/credential.json" }, new());

    [Fact]
    public void Expired_or_foreign_grants_are_explained_before_queueing()
    {
        Assert.Contains("expired", GuardCli.Problem(Grant(minutes: 30, issuedAgo: 40), Policy(), Now, _root));
        Assert.Contains("not set up", GuardCli.Problem(Grant(), null, Now, _root));
        Assert.Contains("'terminal'", GuardCli.Problem(Grant(kind: "terminal"), Policy(), Now, _root));
        Assert.Null(GuardCli.Problem(Grant(), Policy(), Now, _root));
        var used = Grant();
        Directory.CreateDirectory(Path.Combine(_root, "results"));
        File.WriteAllText(Path.Combine(_root, "results", used.Grant.Id + ".json"), "{}");
        Assert.Contains("already been processed", GuardCli.Problem(used, Policy(), Now, _root));
    }

    [Fact]
    public void Accepted_message_says_what_and_when()
    {
        string text = GuardCli.Accepted(Grant("stop"), "GuardRepair");
        Assert.Contains("stop the agent (video)", text);
        Assert.Contains("within 2 minutes", text);
        Assert.Contains("then protection starts it again", text);
        Assert.Contains("GuardRepair status", text);
        Assert.DoesNotContain("starts it again", GuardCli.Accepted(Grant("repair"), "GuardRepair"));
    }

    [Fact]
    public void Status_lists_protection_pending_and_results()
    {
        Assert.Contains("Agent protection: not set up", GuardCli.Report(_root, Now));
        GuardPaths.Write(Path.Combine(_root, "policy.json"), Policy());
        File.WriteAllText(Path.Combine(_root, "enabled"), "1");
        GuardPaths.Queue(Grant("repair"), _root);
        GuardPaths.Write(Path.Combine(_root, "results", "r1.json"), new GuardExecution("r1", "b", "video", "stop", "completed", "completed", Now.AddMinutes(-3)));
        GuardPaths.Write(Path.Combine(_root, "results", "r2.json"), new GuardExecution("r2", "b", "video", "uninstall", "failed", "package-removal-failed", Now.AddMinutes(-1)));
        string report = GuardCli.Report(_root, Now);
        Assert.Contains("Agent protection: on", report);
        Assert.Contains("Recovery base: none yet", report);
        Assert.Contains("Waiting to run: 1", report);
        Assert.Contains("repair the agent (video)", report);
        Assert.Contains("remove the agent (video): failed (package-removal-failed)", report);
        Assert.True(report.IndexOf("remove the agent", StringComparison.Ordinal) < report.IndexOf("stop the agent", StringComparison.Ordinal));
    }
}
