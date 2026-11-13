using Watcher;
using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

public sealed partial class MaintenanceServerTests
{
    [Fact]
    public void Archives_require_owned_live_request_and_duplicate_does_not_extend_retention()
    {
        var a = Credential("archive-a"); var b = Credential("archive-b"); var now = DateTime.UtcNow;
        var actor = new AgentIdentity(a.Id, a.HostId, "archive-a", "video"); var other = new AgentIdentity(b.Id, b.HostId, "archive-b", "video");
        Assert.Null(_db.PendingDiagnosticArchive(actor, now)); Assert.Equal(1, _db.RequestDiagnosticArchives(a.HostId, now, "admin"));
        var request = _db.PendingDiagnosticArchive(actor, now)!;
        Assert.Equal(0, _db.RequestDiagnosticArchives(a.HostId, now.AddMinutes(1), "admin"));
        Assert.Equal(request, _db.PendingDiagnosticArchive(actor, now)); Assert.Null(_db.PendingDiagnosticArchive(other, now));
        var report = new DiagnosticArchiveReport(request.Id, now, "0.15.1", "windows", [], [], [new("msi", 1, 0, 0, false, [1603])]);
        Assert.False(_db.ReceiveDiagnosticArchive(other, report, now)); Assert.True(_db.ReceiveDiagnosticArchive(actor, report, now));
        string original = _db.DiagnosticArchiveJson(a.HostId, request.Id, now)!;
        Assert.Null(_db.PendingDiagnosticArchive(actor, now)); Assert.Null(_db.DiagnosticArchiveJson(b.HostId, request.Id, now));
        Assert.True(_db.ReceiveDiagnosticArchive(actor, report with { Logs = [] }, now.AddHours(1)));
        Assert.Equal(original, _db.DiagnosticArchiveJson(a.HostId, request.Id, now.AddHours(1)));
        Assert.Null(_db.DiagnosticArchiveJson(a.HostId, request.Id, now.AddDays(1).AddSeconds(1)));
        _db.PruneDiagnosticArchives(now.AddDays(1).AddSeconds(1)); Assert.Empty(_db.DiagnosticArchives(a.HostId, now));
    }
    [Fact]
    public void Expired_requests_and_unbounded_or_unknown_content_are_rejected()
    {
        var a = Credential("archive-expired"); var now = DateTime.UtcNow; var actor = new AgentIdentity(a.Id, a.HostId, "archive-expired", "video");
        _db.RequestDiagnosticArchives(a.HostId, now, "admin"); var request = _db.PendingDiagnosticArchive(actor, now)!;
        var report = new DiagnosticArchiveReport(request.Id, now, "0.15.1", "windows", [], [], []);
        Assert.False(_db.ReceiveDiagnosticArchive(actor, report with { Logs = [new("secret-path", 1, 0, 0, false, [])] }, now));
        Assert.False(_db.ReceiveDiagnosticArchive(actor, report with { Logs = [new("msi", 1, 0, 0, false, [1000000])] }, now));
        Assert.False(_db.ReceiveDiagnosticArchive(actor, report, now.AddHours(2)));
        Assert.Equal(1, _db.RequestDiagnosticArchives(a.HostId, now.AddHours(2), "admin"));
        Assert.NotEqual(request.Id, _db.PendingDiagnosticArchive(actor, now.AddHours(2))!.Id);
    }
}
