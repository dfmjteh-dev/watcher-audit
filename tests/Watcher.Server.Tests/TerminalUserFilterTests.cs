using Xunit;

namespace Watcher.Server.Tests;

public class TerminalUserFilterTests
{
    [Theory]
    [InlineData(null, "alice", true)]
    [InlineData("{}", "alice", true)]
    [InlineData("{\"excludeUsers\":[\"svc\"]}", "alice", true)]
    [InlineData("{\"excludeUsers\":[\"svc\"]}", "SVC", false)]
    [InlineData("{\"excludeUsers\":[\"svc\"]}", "CORP\\svc", false)]
    [InlineData("{\"excludeUsers\":[\"svc\"]}", "svc@corp.local", false)]
    [InlineData("{\"excludeUsers\":[\"CORP\\\\svc\"]}", "CORP\\svc", false)]
    [InlineData("{\"allowUsers\":[\"alice\"],\"excludeUsers\":[\"alice\"]}", "alice", true)]
    [InlineData("{\"allowUsers\":[\"alice\"]}", "bob", false)]
    [InlineData("{\"allowUsers\":[]}", "bob", true)]
    [InlineData("{not json", "bob", true)]
    // Проверка RC 0.13: доменные учётки с точкой (a.butakov). До исправления
    // Windows-запись отдавала имя очищенным (abutakov) — оно есть в старых
    // списках и совпадает с настоящим.
    [InlineData("{\"allowUsers\":[\"a.butakov\"]}", "a.butakov", true)]
    [InlineData("{\"allowUsers\":[\"abutakov\"]}", "a.butakov", true)]
    [InlineData("{\"allowUsers\":[\"a.butakov\"]}", "AVZ\\a.butakov", true)]
    [InlineData("{\"allowUsers\":[\"AVZ\\\\a.butakov\"]}", "a.butakov", true)]
    [InlineData("{\"allowUsers\":[\"a.butakov\"]}", "arsenui.butakov", false)]
    [InlineData("{\"excludeUsers\":[\"arsenui.butakov\"]}", "a.butakov", true)]
    [InlineData("{\"excludeUsers\":[\"arsenuibutakov\"]}", "arsenui.butakov", false)]
    public void Same_rules_as_screen_plus_domain_names(string? config, string user, bool expected) =>
        Assert.Equal(expected, TerminalUserFilter.IsAllowed(config, user));
}
