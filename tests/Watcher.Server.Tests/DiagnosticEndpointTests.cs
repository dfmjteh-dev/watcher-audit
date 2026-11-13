using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Watcher;
using Xunit;

namespace Watcher.Server.Tests;

public partial class AgentEnrollmentTests
{
    [Fact]
    public async Task Diagnostics_require_per_agent_identity_and_never_accept_arbitrary_messages()
    {
        string token = await CreateEnrollToken(new { name = "diag", ttlHours = 24 }); string secret = await EnrollSecret(token, "diag-host"); using var agent = Agent(secret);
        var e = new AgentDiagnostic(Guid.NewGuid().ToString("N"), DateTime.UtcNow, "error", "uploader", "upload-failed");
        using (var accepted = await agent.PostAsJsonAsync("/api/agent-diagnostics", new DiagnosticBatch([e]))) Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var page = await _admin.GetFromJsonAsync<JsonElement>("/api/hosts/diag-host/diagnostics"); Assert.Equal(e.Id, page.GetProperty("items")[0].GetProperty("event").GetProperty("id").GetString());
        using var legacy = Agent(SharedToken); using var denied = await legacy.PostAsJsonAsync("/api/agent-diagnostics", new DiagnosticBatch([e])); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var anon = Agent(null); using var unauthenticated = await anon.PostAsJsonAsync("/api/agent-diagnostics", new DiagnosticBatch([])); Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        string other = await EnrollSecret(token, "diag-other"); using var another = Agent(other);
        using var invalid = await another.PostAsJsonAsync("/api/agent-diagnostics", new DiagnosticBatch([e with { Code = "password=do-not-store" }])); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var otherPage = await _admin.GetFromJsonAsync<JsonElement>("/api/hosts/diag-other/diagnostics"); Assert.Equal(0, otherPage.GetProperty("items").GetArrayLength());
    }
    [Fact]
    public async Task Diagnostic_body_limit_applies_before_deserialization()
    {
        string token = await CreateEnrollToken(new { name = "diag-limit", ttlHours = 24 }); string secret = await EnrollSecret(token, "diag-limit"); using var agent = Agent(secret);
        using var response = await agent.PostAsync("/api/agent-diagnostics", new StringContent(new string(' ', 65537))); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }
}
