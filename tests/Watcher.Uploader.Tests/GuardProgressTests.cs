using System.Text.Json;
using Watcher;
using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

public sealed class GuardProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "guard-progress-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = DateTime.UtcNow;
    private MaintenanceGrant Grant() => new(Guid.NewGuid().ToString("N"), new string('a', 64), "video", "update", _now, _now.AddMinutes(15), "0.15.2", new string('b', 64), "windows-msi", "signature", PackageId: 1, PackageSize: 1);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    [Fact]
    public void Empty_acknowledgements_do_not_touch_unprotected_installation()
    {
        string missing = Path.Combine(Path.GetTempPath(), "watcher-no-guard-" + Guid.NewGuid().ToString("N"));
        GuardUpdateProgress.Acknowledge(missing, "binding", "video", []);
        Assert.False(Directory.Exists(missing));
    }
    [Fact]
    public void Durable_revision_survives_ack_restart_and_backward_clock_without_deleting_new_state()
    {
        var g = Grant(); GuardUpdateProgress.Publish(_root, g, "downloading", null, _now);
        var first = Assert.Single(GuardUpdateProgress.Pending(_root, g.Binding, g.Kind));
        GuardUpdateProgress.Acknowledge(_root, g.Binding, g.Kind, [new(g.Id, first.Revision)]);
        Assert.Empty(GuardUpdateProgress.Pending(_root, g.Binding, g.Kind));
        GuardUpdateProgress.Publish(_root, g, "installing", "0.15.1", _now.AddHours(-1));
        var next = Assert.Single(GuardUpdateProgress.Pending(_root, g.Binding, g.Kind)); Assert.Equal(first.Revision + 1, next.Revision);
        GuardUpdateProgress.Acknowledge(_root, g.Binding, g.Kind, [new(g.Id, first.Revision)]);
        Assert.Single(GuardUpdateProgress.Pending(_root, g.Binding, g.Kind));
        Assert.Empty(GuardUpdateProgress.Pending(_root, new string('c', 64), g.Kind));
    }
    [Fact]
    public void Terminal_progress_is_not_reopened_and_only_matching_identity_can_ack()
    {
        var g = Grant(); GuardUpdateProgress.Publish(_root, g, "rolled-back", "0.15.1", _now, "install-failed", "succeeded", errorCode: 1603);
        var done = Assert.Single(GuardUpdateProgress.Pending(_root, g.Binding, g.Kind));
        GuardUpdateProgress.Publish(_root, g, "downloading", null, _now.AddSeconds(1));
        Assert.Equal(done, Assert.Single(GuardUpdateProgress.Pending(_root, g.Binding, g.Kind)));
        GuardUpdateProgress.Acknowledge(_root, g.Binding, "terminal", [new(g.Id, done.Revision)]);
        Assert.Single(GuardUpdateProgress.Pending(_root, g.Binding, g.Kind));
    }
    [Fact]
    public void Closed_failure_mapping_does_not_serialize_exception_text_or_secrets()
    {
        var g = Grant(); var failure = GuardUpdateProgress.Failure(new IOException("token=PRIVATE-DO-NOT-SEND"), "installing");
        GuardUpdateProgress.Publish(_root, g, "failed", "0.15.1", _now, failure.Reason);
        string json = JsonSerializer.Serialize(GuardUpdateProgress.Pending(_root, g.Binding, g.Kind));
        Assert.DoesNotContain("PRIVATE", json); Assert.Equal("operation-failed", failure.Reason);
        Assert.Throws<IOException>(() => GuardUpdateProgress.Publish(_root, Grant(), "failed", null, _now, "token=PRIVATE"));
    }
    [Theory]
    [InlineData("publisher-signature")]
    [InlineData("package-integrity")]
    [InlineData("installer-busy")]
    public void Typed_reasons_and_native_codes_are_preserved(string reason)
    {
        var failure = GuardUpdateProgress.Failure(new GuardUpdateFailure(reason, 1618), "installing");
        Assert.Equal(reason, failure.Reason); Assert.Equal(1618, failure.Code);
    }
    private sealed record LegacyBatch(List<AgentDiagnostic>? Events, List<MaintenanceResult>? MaintenanceResults);
    [Fact]
    public void Old_server_ignores_optional_progress_without_treating_download_as_acceptance()
    {
        var g = Grant(); var p = new UpdateProgressReport(g.Id, 1, _now, "downloading", null, "0.15.2");
        string json = JsonSerializer.Serialize(new DiagnosticBatch([], UpdateProgress: [p]), GuardPaths.Json);
        var legacy = JsonSerializer.Deserialize<LegacyBatch>(json, GuardPaths.Json)!;
        Assert.True(legacy.MaintenanceResults is null or { Count: 0 });
        Assert.False(UpdateProgressContract.Valid(p with { ToVersion = "password=secret" }));
        Assert.False(UpdateProgressContract.Valid(p with { Revision = long.MaxValue }));
    }
}
