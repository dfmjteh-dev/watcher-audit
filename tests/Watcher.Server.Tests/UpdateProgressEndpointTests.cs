using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Watcher;
using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

public partial class AgentEnrollmentTests
{
    [Fact]
    public async Task Progress_endpoint_enforces_identity_versions_limits_and_returns_revision_ack()
    {
        string token = await CreateEnrollToken(new { name = "progress", ttlHours = 24 }); string secret = await EnrollSecret(token, "progress-http"); using var agent = Agent(secret);
        var db = _factory.Services.GetRequiredService<Db>(); var host = db.Inventory(onlyName: "progress-http").Single(); var credential = db.ListAgentCredentials(host.Id).Single();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var now = DateTime.UtcNow;
        var grant = MaintenanceSigning.Sign(new(Guid.NewGuid().ToString("N"), db.CredentialBinding(credential.Id)!, "video", "update", now, now.AddMinutes(15), "0.15.2", new string('a', 64), "windows-msi", "signature", PackageId: 1, PackageSize: 1), key);
        db.IssueMaintenance(credential, grant, "admin");
        var progress = new UpdateProgressReport(grant.Grant.Id, 1, now, "installing", "0.15.1", "0.15.2");
        using (var invalid = await agent.PostAsJsonAsync("/api/agent-diagnostics", new DiagnosticBatch([], UpdateProgress: [progress with { Reason = "token=PRIVATE-SECRET" }]))) Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var oversized = await agent.PostAsJsonAsync("/api/agent-diagnostics", new DiagnosticBatch([], UpdateProgress: Enumerable.Repeat(progress, 17).ToList()))) Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        using (var ok = await agent.PostAsJsonAsync("/api/agent-diagnostics", new DiagnosticBatch([], UpdateProgress: [progress])))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode); var receipt = await ok.Content.ReadFromJsonAsync<DiagnosticReceipt>();
            Assert.Equal(new UpdateProgressAck(grant.Grant.Id, 1), Assert.Single(receipt!.ProgressAccepted!));
        }
        var history = await _admin.GetFromJsonAsync<JsonElement>("/api/hosts/progress-http/maintenance");
        Assert.Equal("installing", history[0].GetProperty("progress").GetProperty("stage").GetString());
        Assert.Equal("queued", history[0].GetProperty("status").GetString());
        string otherSecret = await EnrollSecret(token, "progress-other"); using var other = Agent(otherSecret);
        using var denied = await other.PostAsJsonAsync("/api/agent-diagnostics", new DiagnosticBatch([], UpdateProgress: [progress with { Revision = 2 }]));
        Assert.Empty((await denied.Content.ReadFromJsonAsync<DiagnosticReceipt>())!.ProgressAccepted!);
        using var legacy = Agent(SharedToken); using var forbidden = await legacy.PostAsJsonAsync("/api/agent-diagnostics", new DiagnosticBatch([], UpdateProgress: [progress])); Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }
}
