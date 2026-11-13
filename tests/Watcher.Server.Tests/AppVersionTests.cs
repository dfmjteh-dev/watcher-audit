using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

public class AppVersionTests
{
    [Fact]
    public void Current_reflects_Directory_Build_Props_version_not_default()
    {
        // Guards the Directory.Build.props -> AssemblyInformationalVersion pipeline:
        // the version must be exactly <ServerVersion>, not "unknown" or the SDK
        // default (comparing with a literal "1.0.0" broke on the real 1.0.0).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string props = File.ReadAllText(Path.Combine(dir!.FullName, "Directory.Build.props"));
        string expected = System.Text.RegularExpressions.Regex.Match(props, @"<ServerVersion>([^<]+)</ServerVersion>").Groups[1].Value;
        Assert.Matches(@"^\d+\.\d+\.\d+$", expected);
        Assert.Equal(expected, AppVersion.Core(AppVersion.Current));
    }

    // Этап E2: «агент старше сервера» — без хвоста +<коммит> и именно «старше».
    [Theory]
    [InlineData("0.8.0", "0.8.0+1be3ed0431ed", false)]
    [InlineData("0.8.0+aaaa", "0.8.0+bbbb", false)]
    [InlineData("0.7.2", "0.8.0+1be3ed0431ed", true)]
    [InlineData("0.9.0", "0.8.0", false)]
    [InlineData("0.10.0", "0.9.0", false)]
    [InlineData(null, "0.8.0", false)]
    [InlineData("dev-build", "0.8.0", true)]
    public void IsAgentOutdated_compares_semantic_core_versions(string? agent, string server, bool expected) =>
        Assert.Equal(expected, AppVersion.IsAgentOutdated(agent, server));
}
