using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Watcher;
using Xunit;

namespace Watcher.Server.Tests;

public partial class AgentEnrollmentTests
{
    [Fact]
    public async Task Archive_request_upload_download_and_closed_schema_enforce_access()
    {
        string token = await CreateEnrollToken(new { name = "archive-http", ttlHours = 24 });
        using var agent = Agent(await EnrollSecret(token, "archive-http")); using var other = Agent(await EnrollSecret(token, "archive-other"));
        using var requested = await _admin.PostAsJsonAsync("/api/hosts/archive-http/diagnostics/archives", new { }); Assert.Equal(HttpStatusCode.OK, requested.StatusCode);
        using var poll = await agent.PostAsJsonAsync("/api/agent-diagnostics", new DiagnosticBatch([]));
        var receipt = await poll.Content.ReadFromJsonAsync<DiagnosticReceipt>(); Assert.NotNull(receipt!.ArchiveRequest);
        var report = new DiagnosticArchiveReport(receipt.ArchiveRequest.Id, DateTime.UtcNow, "0.15.1", "windows", [], [], [new("msi", 1, 0, 0, false, [1603])]);
        using var wrong = await other.PostAsJsonAsync("/api/agent-diagnostics/archive", report); Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var unsafeResponse = await agent.PostAsync("/api/agent-diagnostics/archive", new StringContent(json[..^1] + ",\"rawLog\":\"PRIVATE\"}", Encoding.UTF8, "application/json")); Assert.Equal(HttpStatusCode.BadRequest, unsafeResponse.StatusCode);
        using var oversized = await agent.PostAsync("/api/agent-diagnostics/archive", new StringContent(new string('x', 131073), Encoding.UTF8, "application/json")); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        using var ok = await agent.PostAsJsonAsync("/api/agent-diagnostics/archive", report); Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var legacy = Agent(SharedToken); using var forbidden = await legacy.PostAsJsonAsync("/api/agent-diagnostics/archive", report); Assert.Equal(HttpStatusCode.Unauthorized, forbidden.StatusCode);
        string path = "/api/hosts/archive-http/diagnostics/archives/" + report.RequestId;
        using var download = await _admin.GetAsync(path); Assert.Equal(HttpStatusCode.OK, download.StatusCode); Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-store", download.Headers.CacheControl!.ToString());
        using var zip = new ZipArchive(new MemoryStream(await download.Content.ReadAsByteArrayAsync()));
        Assert.Equal(["diagnostics.json", "README.txt"], zip.Entries.Select(e => e.FullName));
        using var reader = new StreamReader(zip.GetEntry("diagnostics.json")!.Open()); string exported = await reader.ReadToEndAsync(); Assert.Contains("1603", exported); Assert.DoesNotContain("PRIVATE", exported);
        using var mismatch = await _admin.GetAsync("/api/hosts/archive-other/diagnostics/archives/" + report.RequestId); Assert.Equal(HttpStatusCode.NotFound, mismatch.StatusCode);
        using var machine = await agent.GetAsync(path); Assert.Equal(HttpStatusCode.Unauthorized, machine.StatusCode);
    }
}
