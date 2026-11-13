using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

// Версии агента и сервера раздельные: агент сравнивается с версией, назначенной
// его группе, а без неё — с последним пакетом своей платформы, но не с сервером.
public class AgentVersionPolicyTests
{
    private static AgentVersionPolicy Policy() => new(
        groupTargets: [(1, "0.14.0"), (2, null)],
        packages: [("windows-msi", "0.14.0"), ("windows-msi", "0.13.2"), ("linux-deb", "0.13.2")],
        hostPlatforms: [("win-a", "windows-msi"), ("lin-a", "linux-deb")]);

    [Theory]
    [InlineData("win-a", 1L, "0.13.2", "pending", "0.14.0")]    // группе назначена новее — ждёт
    [InlineData("win-a", 1L, "0.14.0", "current", "0.14.0")]
    [InlineData("win-a", 1L, "0.15.0", "ahead", "0.14.0")]
    [InlineData("win-a", 2L, "0.13.2", "available", null)]       // группе не назначено, пакет новее есть
    [InlineData("win-a", null, "0.13.2", "available", null)]
    [InlineData("lin-a", null, "0.13.2", "current", null)]       // для Linux новее 0.13.2 пакета нет
    [InlineData("new-host", null, null, "unknown", null)]
    public void State_is_relative_to_group_target_or_latest_package(string host, long? group, string? agent, string status, string? target)
    {
        var st = Policy().For(host, group, agent);
        Assert.Equal(status, st.Status);
        Assert.Equal(target, st.Target);
    }

    [Fact]
    public void Server_release_alone_does_not_make_agents_outdated()
    {
        // Сервер 0.20.0, пакетов агентов новее 0.13.2 нет, группе ничего не назначено.
        var p = new AgentVersionPolicy([], [("windows-msi", "0.13.2")], [("win-a", "windows-msi")]);
        Assert.False(p.IsOutdated("win-a", null, "0.13.2"));
        Assert.Equal("0.13.2", p.LatestAgentVersion);
    }

    [Fact]
    public void Latest_version_compares_semantically_not_as_strings()
    {
        var p = new AgentVersionPolicy([], [("linux-deb", "0.9.0"), ("linux-deb", "0.10.0")], []);
        Assert.Equal("0.10.0", p.LatestAgentVersion);
    }
}
