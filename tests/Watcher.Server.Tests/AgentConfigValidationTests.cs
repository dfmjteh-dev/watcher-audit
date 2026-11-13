using System.Text.Json;
using Xunit;

namespace Watcher.Server.Tests;

public class AgentConfigValidationTests
{
    private static string? V(string json) => AgentConfigValidation.Validate(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Config_as_sent_by_web_ui_is_valid()
    {
        Assert.Null(V("""
            {"schemaVersion":1,"captureIntervalMs":500,"downscaleWidth":1280,"downscaleHeight":720,
             "motionThresholdPercent":0.5,"motionThresholdPixelDelta":25,"outputFps":2,"crf":30,
             "preset":"ultrafast","allowUsers":["kassir1"],"excludeUsers":[]}
            """));
    }

    [Fact]
    public void Empty_object_and_null_values_are_valid()
    {
        Assert.Null(V("{}"));
        Assert.Null(V("""{"crf":null,"preset":null,"allowUsers":null}"""));
    }

    [Theory]
    [InlineData("""{"preset":"ultrafast -y /tmp/x.mp4"}""")]
    [InlineData("""{"preset":"placebo"}""")]
    [InlineData("""{"preset":1}""")]
    [InlineData("""{"captureIntervalMs":0}""")]
    [InlineData("""{"captureIntervalMs":99999}""")]
    [InlineData("""{"downscaleWidth":0}""")]
    [InlineData("""{"downscaleHeight":10000}""")]
    [InlineData("""{"crf":52}""")]
    [InlineData("""{"crf":23.5}""")]
    [InlineData("""{"crf":"23"}""")]
    [InlineData("""{"outputFps":0}""")]
    [InlineData("""{"motionThresholdPercent":101}""")]
    [InlineData("""{"motionThresholdPixelDelta":0}""")]
    [InlineData("""{"schemaVersion":2}""")]
    [InlineData("""{"allowUsers":"kassir1"}""")]
    [InlineData("""{"allowUsers":[1]}""")]
    [InlineData("""{"excludeUsers":[""]}""")]
    [InlineData("""{"excludeUsers":["a\nb"]}""")]
    [InlineData("""{"fps":2}""")]
    [InlineData("""[]""")]
    public void Invalid_config_is_rejected(string json)
    {
        Assert.NotNull(V(json));
    }

    // 0.14.3: правила по домену вкладки — списки строк, как правила по заголовку.
    [Fact]
    public void Site_rules_are_string_lists()
    {
        Assert.Null(V("""{"excludeSites":["sberbank.ru","https://hh.ru/x"],"includeSites":["crm.example.ru"]}"""));
        Assert.NotNull(V("""{"excludeSites":"sberbank.ru"}"""));
        Assert.NotNull(V("""{"includeSites":[1]}"""));
    }

    // 0.14.3: курсор мыши на видео.
    [Fact]
    public void Show_cursor_is_boolean()
    {
        Assert.Null(V("""{"showCursor":true}"""));
        Assert.Null(V("""{"showCursor":false}"""));
        Assert.NotNull(V("""{"showCursor":"yes"}"""));
    }

    [Fact]
    public void Queue_max_age_days_range()
    {
        Assert.Null(V("{\"queueMaxAgeDays\":30}"));
        Assert.Null(V("{\"queueMaxAgeDays\":0}"));
        Assert.NotNull(V("{\"queueMaxAgeDays\":-1}"));
        Assert.NotNull(V("{\"queueMaxAgeDays\":4000}"));
        Assert.NotNull(V("{\"queueMaxAgeDays\":1.5}"));
    }

    [Fact]
    public void Include_lists_are_validated_like_exclusions()
    {
        Assert.Null(V("{\"includeProcesses\":[\"1cv8\"],\"includeTitles\":[\"Банк-клиент\"]}"));
        Assert.NotNull(V("{\"includeProcesses\":\"1cv8\"}"));
        Assert.NotNull(V("{\"includeTitles\":[\"\"]}"));
    }
}
