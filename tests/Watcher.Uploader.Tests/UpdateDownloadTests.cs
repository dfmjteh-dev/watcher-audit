using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

public sealed class UpdateDownloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "watcher-download-" + Guid.NewGuid().ToString("N"));
    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response());
    }

    [Fact]
    public async Task Short_http_body_is_not_published_and_retry_replaces_partial_file()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "package.msi");
        File.WriteAllText(path, "previous verified file");
        bool complete = false;
        using var http = new HttpClient(new Handler(() =>
        {
            var content = new ByteArrayContent(complete ? [1, 2, 3, 4] : [1, 2]);
            content.Headers.ContentLength = 4;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        var uploader = new Uploader(new UploaderConfig { ServerUrl = "https://test", CredentialPath = Path.Combine(_dir, "identity.json") }, NullLogger.Instance, http);
        await Assert.ThrowsAsync<IOException>(() => uploader.DownloadAsync("/package", path, default));
        Assert.Equal("previous verified file", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".part"));
        complete = true;
        await uploader.DownloadAsync("/package", path, default);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".part"));
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}
