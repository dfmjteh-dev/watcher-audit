using Xunit;
namespace Watcher.Server.Tests;

public sealed class CaptureStateTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
    [Fact]
    public void Uploader_without_capture_reports_does_not_confirm_recording()
    {
        Assert.Equal("unknown", Db.CaptureStateOf(null, Now));
        Assert.Equal("missing", Db.CaptureStateOf("[]", Now));
        Assert.Equal("missing", Db.CaptureStateOf("invalid", Now));
    }
    [Fact]
    public void Older_capture_without_recording_flag_does_not_claim_a_pause()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new[] { new { user = "test", at = Now } });
        Assert.Equal("unknown", Db.CaptureStateOf(json, Now));
    }
    [Theory]
    [InlineData(-1, true, "active")]
    [InlineData(-1, false, "paused")]
    [InlineData(-6, true, "missing")]
    [InlineData(5, true, "missing")]
    public void Capture_liveness_expires_independently_of_uploader(int minutes, bool recording, string expected)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new[] { new { user = "test", recording, at = Now.AddMinutes(minutes) } });
        Assert.Equal(expected, Db.CaptureStateOf(json, Now));
    }
}
