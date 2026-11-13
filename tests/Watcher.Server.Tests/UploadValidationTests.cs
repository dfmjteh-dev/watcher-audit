using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

public class UploadValidationTests
{
    [Theory]
    [InlineData("segment_20260101_120000.mp4", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("../../etc/passwd", false)]
    [InlineData("..\\..\\windows\\system32\\evil.mp4", false)]
    [InlineData("sub/dir/file.mp4", false)]
    [InlineData("sub\\dir\\file.mp4", false)]
    [InlineData("..", false)]
    public void IsValidFilename_rejects_path_traversal_and_empty(string filename, bool expected)
    {
        Assert.Equal(expected, UploadValidation.IsValidFilename(filename));
    }

    [Theory]
    [InlineData("desktop-01", "desktop-01")]
    [InlineData("DESKTOP_01", "DESKTOP_01")]
    [InlineData("../../etc/passwd", "etcpasswd")]
    [InlineData("host name with spaces", "hostnamewithspaces")]
    [InlineData("h©llo", "hllo")]
    [InlineData("", "unknown")]
    [InlineData("!!!", "unknown")]
    public void SanitizeHost_keeps_only_safe_characters(string input, string expected)
    {
        Assert.Equal(expected, UploadValidation.SanitizeHost(input));
    }

    [Theory]
    [InlineData("motion 0,52%", "motion 0.52%")]
    [InlineData("motion 12,30%", "motion 12.30%")]
    [InlineData("motion 0.52%", "motion 0.52%")]
    [InlineData("recovered", "recovered")]
    [InlineData("motion 0,52% extra", "motion 0,52% extra")]
    [InlineData("", "")]
    public void Start_reason_decimal_comma_is_normalized(string raw, string expected)
    {
        Assert.Equal(expected, UploadValidation.NormalizeStartReason(raw));
    }

    [Fact]
    public void Monitors_layout_is_validated()
    {
        using var two = System.Text.Json.JsonDocument.Parse("""[{"x":0,"y":0,"w":1920,"h":1080,"primary":true},{"x":1920,"y":0,"w":1920,"h":1080,"primary":false,"extra":"<b>"}]""");
        Assert.Equal("""[{"x":0,"y":0,"w":1920,"h":1080,"primary":true},{"x":1920,"y":0,"w":1920,"h":1080,"primary":false}]""", UploadValidation.ParseMonitors(two.RootElement));
        foreach (var bad in new[] { "[{\"x\":0,\"y\":0,\"w\":10,\"h\":10}]", "[{\"x\":-1,\"y\":0,\"w\":10,\"h\":10},{\"x\":0,\"y\":0,\"w\":10,\"h\":10}]", "\"x\"", "[1,2]" })
        {
            using var d = System.Text.Json.JsonDocument.Parse(bad);
            Assert.Null(UploadValidation.ParseMonitors(d.RootElement));
        }
    }

    // 0.14.3: домен вкладки браузера от агента — только имя хоста.
    [Theory]
    [InlineData("Mail.Example.com", "mail.example.com")]
    [InlineData("почта.рф", "почта.рф")]
    [InlineData("10.0.0.5:8443", "10.0.0.5:8443")]
    [InlineData("localhost", "localhost")]
    [InlineData("https://mail.example.com/x", null)]
    [InlineData("mail example", null)]
    [InlineData("intranet", null)]
    [InlineData(null, null)]
    public void Activity_site_is_a_host_name(string? site, string? expected) =>
        Assert.Equal(expected, UploadValidation.Site(site));

    [Fact]
    public void Activity_entries_carry_site()
    {
        var json = System.Text.Json.JsonDocument.Parse("""[{"t":0,"process":"chrome","title":"Почта","site":"Mail.Example.com"},{"t":1,"process":"1cv8c","title":"x"}]""").RootElement;
        var list = UploadValidation.ParseActivity(json, 60);
        Assert.Equal("mail.example.com", list[0].Site);
        Assert.Null(list[1].Site);
    }
}
