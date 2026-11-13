using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

// 0.14.2: файл больше предела сервера не гоняется по сети заново на каждом проходе
// и не держит за собой очередь — его удаляют с записью в журнал потерь.
public sealed class TooLargeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"watcher-toolarge-{Guid.NewGuid():N}");
    private readonly FakeServer _server = new();

    public TooLargeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private Uploader NewUploader() =>
        new(new UploaderConfig { ServerUrl = "http://srv", ApiToken = "t" }, NullLogger.Instance, new HttpClient(_server));

    private string Segment(int bytes)
    {
        string mp4 = Path.Combine(_dir, "PC1_20261019_090000.mp4");
        File.WriteAllBytes(mp4, new byte[bytes]);
        File.WriteAllText(mp4 + ".json", "{}");
        return mp4;
    }

    [Theory]
    [InlineData(1000, 0, null, false)]                  // предел неизвестен — отправляем
    [InlineData(1000, 0, 0L, false)]                    // 0 — не предел
    [InlineData(1000, 10, 1_000_000L, false)]
    [InlineData(1_000_000, 0, 1_000_000L, true)]        // впритык с multipart-обёрткой — не влезет
    [InlineData(600L * 1024 * 1024, 200, 512L * 1024 * 1024, true)]
    public void ExceedsLimit_counts_mp4_json_and_overhead(long mp4, long json, long? max, bool expected) =>
        Assert.Equal(expected, Uploader.ExceedsLimit(mp4, json, max));

    [Fact]
    public async Task Limit_from_heartbeat_skips_upload_without_sending()
    {
        var uploader = NewUploader();
        _server.HeartbeatBody = "{\"maxUploadBytes\":100000}";
        await uploader.SendHeartbeatAsync("PC-1", "1.0", 0, CancellationToken.None);
        Assert.Equal(100000, uploader.ServerMaxUploadBytes);

        var outcome = await uploader.UploadAsync(Segment(200_000), Path.Combine(_dir, "PC1_20261019_090000.mp4.json"), CancellationToken.None);

        Assert.Equal(UploadOutcome.TooLarge, outcome);
        Assert.Equal(0, _server.UploadCalls);
    }

    [Fact]
    public async Task Heartbeat_without_limit_keeps_it_unknown()
    {
        var uploader = NewUploader();
        await uploader.SendHeartbeatAsync("PC-1", "1.0", 0, CancellationToken.None);
        Assert.Null(uploader.ServerMaxUploadBytes);
    }

    [Fact]
    public async Task Old_server_413_is_too_large_not_retryable_failure()
    {
        var uploader = NewUploader();
        _server.UploadStatus = HttpStatusCode.RequestEntityTooLarge;
        string mp4 = Segment(1000);

        Assert.Equal(UploadOutcome.TooLarge, await uploader.UploadAsync(mp4, mp4 + ".json", CancellationToken.None));
        Assert.Equal(1, _server.UploadCalls);
    }

    [Fact]
    public void DropTooLarge_deletes_pair_and_records_loss()
    {
        string mp4 = Segment(1234);
        var lost = new List<(string File, long Size)>();

        Assert.True(UploaderLoop.DropTooLarge(mp4, mp4 + ".json", _ => { }, (f, s) => lost.Add((f, s))));

        Assert.False(File.Exists(mp4));
        Assert.False(File.Exists(mp4 + ".json"));
        Assert.Equal([("PC1_20261019_090000.mp4", 1234L)], lost);
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public int UploadCalls;
        public string HeartbeatBody = "{}";
        public HttpStatusCode UploadStatus = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/api/upload")
            {
                UploadCalls++;
                return Task.FromResult(new HttpResponseMessage(UploadStatus) { Content = new StringContent("") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(HeartbeatBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
