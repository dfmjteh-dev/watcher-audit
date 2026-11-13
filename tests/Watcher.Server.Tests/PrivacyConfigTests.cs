using System.Text.Json;
using Xunit;

namespace Watcher.Server.Tests;

// H8 (roadmap-next.md): ключи конфига группы — расписание записи и исключённые окна.
public class PrivacyConfigTests
{
    private static string? Validate(string json) => AgentConfigValidation.Validate(JsonDocument.Parse(json).RootElement);

    [Theory]
    [InlineData("""{"schedule":[{"days":[1,2,3,4,5],"from":"09:00","to":"18:00"}]}""")]
    [InlineData("""{"schedule":[{"days":[5],"from":"22:00","to":"06:00"},{"days":[6,7],"from":"00:00","to":"00:00"}]}""")]
    [InlineData("""{"schedule":[]}""")]
    [InlineData("""{"excludeProcesses":["KeePass.exe","1cv8"],"excludeTitles":["Сбербанк Онлайн"]}""")]
    public void Valid(string json) => Assert.Null(Validate(json));

    [Theory]
    [InlineData("""{"schedule":{"days":[1]}}""", "schedule")]
    [InlineData("""{"schedule":[{"days":[],"from":"09:00","to":"18:00"}]}""", "days")]
    [InlineData("""{"schedule":[{"days":[0],"from":"09:00","to":"18:00"}]}""", "days")]
    [InlineData("""{"schedule":[{"days":[8],"from":"09:00","to":"18:00"}]}""", "days")]
    [InlineData("""{"schedule":[{"days":[1],"from":"9:00","to":"18:00"}]}""", "from")]
    [InlineData("""{"schedule":[{"days":[1],"from":"09:00","to":"24:00"}]}""", "to")]
    [InlineData("""{"schedule":[{"days":[1],"from":"09:00"}]}""", "to")]
    [InlineData("""{"schedule":[{"days":[1],"from":"09:00","to":"18:00","tz":"UTC"}]}""", "tz")]
    [InlineData("""{"excludeProcesses":"KeePass"}""", "excludeProcesses")]
    [InlineData("""{"excludeTitles":["  "]}""", "excludeTitles")]
    [InlineData("""{"excludeTitles":["a\u0007b"]}""", "excludeTitles")]
    public void Invalid(string json, string mention)
    {
        string? error = Validate(json);
        Assert.NotNull(error);
        Assert.Contains(mention, error);
    }

    [Fact]
    public void Exclusion_list_size_is_limited()
    {
        var many = string.Join(",", Enumerable.Range(0, AgentConfigValidation.MaxExclusions + 1).Select(i => $"\"p{i}\""));
        Assert.NotNull(Validate($$"""{"excludeProcesses":[{{many}}]}"""));
    }
}
