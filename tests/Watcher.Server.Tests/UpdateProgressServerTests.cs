using System.Security.Cryptography;
using Watcher;
using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

public sealed partial class MaintenanceServerTests
{
    private SignedMaintenanceGrant Update(AgentCredential credential, DateTime now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var grant = MaintenanceSigning.Sign(new(Guid.NewGuid().ToString("N"), _db.CredentialBinding(credential.Id)!, "video", "update", now, now.AddMinutes(15), "0.15.2", new string('a', 64), "windows-msi", "test-signature", PackageId: 1, PackageSize: 1), key);
        _db.IssueMaintenance(credential, grant, "admin"); return grant;
    }
    [Fact]
    public void Progress_is_owned_version_bound_and_does_not_execute_or_complete_command()
    {
        var a = Credential("progress-a"); var b = Credential("progress-b"); var now = DateTime.UtcNow; var grant = Update(a, now);
        var report = new UpdateProgressReport(grant.Grant.Id, 1, now, "downloading", "0.15.1", "0.15.2");
        Assert.Empty(_db.AcceptUpdateProgress(new(b.Id, b.HostId, "progress-b", "video"), [report], now));
        var actor = new AgentIdentity(a.Id, a.HostId, "progress-a", "video");
        Assert.Empty(_db.AcceptUpdateProgress(actor, [report with { ToVersion = "0.99.0" }], now));
        Assert.Single(_db.AcceptUpdateProgress(actor, [report], now));
        var row = Assert.Single(_db.MaintenanceHistory(a.HostId));
        Assert.Equal("queued", row.Status); Assert.Equal(report, row.Progress);
        Assert.Equal(grant, _db.PendingMaintenance(a.Id, now));
        Assert.Single(_db.AcceptUpdateProgress(actor, [report with { Revision = 2, Stage = "completed" }], now.AddSeconds(1)));
        Assert.Equal("queued", Assert.Single(_db.MaintenanceHistory(a.HostId)).Status);
    }
    [Fact]
    public void Duplicate_out_of_order_and_terminal_progress_cannot_replace_newer_state()
    {
        var a = Credential("ordered"); var now = DateTime.UtcNow; var grant = Update(a, now); var actor = new AgentIdentity(a.Id, a.HostId, "ordered", "video");
        var report = new UpdateProgressReport(grant.Grant.Id, 5, now, "rollback-pending", "0.15.1", "0.15.2", "install-failed", "pending", "installer-busy", 1603, 1618);
        _db.AcceptUpdateProgress(actor, [report], now);
        _db.AcceptUpdateProgress(actor, [report with { Revision = 2, Stage = "installing" }], now.AddSeconds(1));
        Assert.Equal(report, Assert.Single(_db.MaintenanceHistory(a.HostId)).Progress);
        var done = report with { Revision = 6, Stage = "rolled-back", Rollback = "succeeded", RollbackReason = null, RollbackErrorCode = null };
        _db.AcceptMaintenanceResults(actor, [new(grant.Grant.Id, "failed", "operation-failed")], now.AddSeconds(2));
        _db.AcceptUpdateProgress(actor, [done], now.AddSeconds(2));
        Assert.Single(_db.AcceptUpdateProgress(actor, [report with { Revision = 7, Stage = "installing" }], now.AddSeconds(3)));
        Assert.Equal(done, Assert.Single(_db.MaintenanceHistory(a.HostId)).Progress);
        Assert.Equal("failed", Assert.Single(_db.MaintenanceHistory(a.HostId)).Status);
    }
    [Fact]
    public void Old_agent_results_still_work_without_progress_and_invalid_reason_is_not_persisted()
    {
        var a = Credential("old-agent"); var now = DateTime.UtcNow; var grant = Update(a, now); var actor = new AgentIdentity(a.Id, a.HostId, "old-agent", "video");
        var bad = new UpdateProgressReport(grant.Grant.Id, 1, now, "failed", null, "0.15.2", "password=DO-NOT-PERSIST");
        Assert.Empty(_db.AcceptUpdateProgress(actor, [bad], now));
        _db.AcceptMaintenanceResults(actor, [new(grant.Grant.Id, "completed", "completed")], now);
        var row = Assert.Single(_db.MaintenanceHistory(a.HostId)); Assert.Null(row.Progress); Assert.Equal("completed", row.Status);
    }
}
