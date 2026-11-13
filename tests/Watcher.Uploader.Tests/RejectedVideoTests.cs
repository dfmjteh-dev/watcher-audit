using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

public sealed class RejectedVideoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "watcher-rejected-" + Guid.NewGuid().ToString("N"));
    public RejectedVideoTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);
    private sealed class Server(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Assert.Equal("1", Assert.Single(req.Headers.GetValues("X-Watcher-Video-Validation")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnsupportedMediaType) { Content = new StringContent(body) });
        }
    }
    [Theory]
    [InlineData("{\"code\":\"invalid-video\"}", UploadOutcome.Rejected)]
    [InlineData("{}", UploadOutcome.Failed)]
    [InlineData("\"proxy error\"", UploadOutcome.Failed)]
    [InlineData("{\"code\":123}", UploadOutcome.Failed)]
    [InlineData("proxy error", UploadOutcome.Failed)]
    public async Task Only_typed_415_is_terminal_rejection(string body, UploadOutcome expected)
    {
        string path = Path.Combine(_dir, "clip.mp4"); File.WriteAllText(path, "bad"); File.WriteAllText(path + ".json", "{}");
        using var http = new HttpClient(new Server(body));
        var uploader = new Uploader(new UploaderConfig { ApiToken = "test", ServerUrl = "http://server" }, NullLogger.Instance, http);
        Assert.Equal(expected, await uploader.UploadAsync(path, path + ".json", CancellationToken.None));
        Assert.True(File.Exists(path));
    }
    [Fact]
    public void Rejected_pair_is_retained_and_loss_is_durable()
    {
        string path = Path.Combine(_dir, "clip.mp4"); File.WriteAllText(path, "bad"); File.WriteAllText(path + ".json", "{}");
        var losses = LossJournal.At(Path.Combine(_dir, "losses.jsonl"));
        Assert.True(UploaderLoop.RetainRejected(path, path + ".json", Path.Combine(_dir, "sent"), losses, _ => { }));
        Assert.False(File.Exists(path));
        string retained = Assert.Single(Directory.GetFiles(Path.Combine(_dir, "sent", "rejected"), "*.mp4"));
        Assert.Equal("bad", File.ReadAllText(retained)); Assert.Equal("{}", File.ReadAllText(retained + ".json"));
        Assert.Equal(LossJournal.ReasonBroken, Assert.Single(losses.Pending(10)).Reason);
    }
    [Fact]
    public void Loss_persistence_failure_keeps_queue_original()
    {
        string path = Path.Combine(_dir, "clip.mp4"); File.WriteAllText(path, "bad"); File.WriteAllText(path + ".json", "{}");
        string blocker = Path.Combine(_dir, "blocker"); File.WriteAllText(blocker, "block");
        var losses = LossJournal.At(Path.Combine(blocker, "losses.jsonl"));
        Assert.False(UploaderLoop.RetainRejected(path, path + ".json", Path.Combine(_dir, "sent"), losses, _ => { }));
        Assert.True(File.Exists(path)); Assert.True(File.Exists(path + ".json"));
    }
}
