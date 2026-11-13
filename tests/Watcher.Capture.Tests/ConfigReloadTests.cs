using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Capture;
using Xunit;

namespace Watcher.Capture.Tests;

// Фейк вместо настоящего ScreenGrabber (0.14.4, ConfigReload принимает
// IResizableGrabber): настоящий с 0.13 не растягивает кадр больше монитора, и
// «1920×1080» зависело от экрана, на котором идёт тест (через SSH — нет).
file sealed class FakeScreenGrabber(int w, int h) : IResizableGrabber
{
    public int OutputWidth { get; private set; } = w;
    public int OutputHeight { get; private set; } = h;

    public void Resize(int outWidth, int outHeight)
    {
        OutputWidth = outWidth;
        OutputHeight = outHeight;
    }
}

public class ConfigReloadTests
{
    private static readonly ILogger NoopLogger = NullLogger.Instance;

    [Fact]
    public void TryParse_valid_json_returns_payload()
    {
        string json = """{"schemaVersion":1,"captureIntervalMs":750,"crf":28}""";

        var payload = ConfigReload.TryParse(json, NoopLogger);

        Assert.NotNull(payload);
        Assert.Equal(750, payload!.CaptureIntervalMs);
        Assert.Equal(28, payload.Crf);
        Assert.Null(payload.Preset);
    }

    [Fact]
    public void TryParse_invalid_json_returns_null()
    {
        Assert.Null(ConfigReload.TryParse("not json", NoopLogger));
    }

    [Fact]
    public void TryParse_unknown_schema_version_returns_null()
    {
        string json = """{"schemaVersion":99,"crf":28}""";

        Assert.Null(ConfigReload.TryParse(json, NoopLogger));
    }

    [Fact]
    public void TryParse_missing_schema_version_is_accepted()
    {
        // Отсутствие schemaVersion трактуется как "совместимо" — не повод отбрасывать,
        // только явно другое число (99) сигналит несовместимость.
        string json = """{"crf":28}""";

        Assert.NotNull(ConfigReload.TryParse(json, NoopLogger));
    }

    private static CaptureConfig MakeCfg() => new()
    {
        CaptureIntervalMs = 500,
        DownscaleWidth = 640,
        DownscaleHeight = 480,
        MotionThresholdPercent = 0.5,
        OutputFps = 2,
        Crf = 30,
        Preset = "ultrafast",
    };

    [Fact]
    public void Apply_updates_fields_present_in_payload()
    {
        var cfg = MakeCfg();
        var detector = new MotionDetector(pixelDelta: 25);
        var payload = new CapturedConfigPayload(
            SchemaVersion: 1, CaptureIntervalMs: 750, DownscaleWidth: null, DownscaleHeight: null,
            MotionThresholdPercent: 1.5, MotionThresholdPixelDelta: 40, OutputFps: 5, Crf: 18, Preset: "veryfast", AllowUsers: null, ExcludeUsers: null);

        var grabber = new FakeScreenGrabber(cfg.DownscaleWidth, cfg.DownscaleHeight);

        ConfigReload.Apply(payload, cfg, detector, grabber, NoopLogger);

        Assert.Equal(750, cfg.CaptureIntervalMs);
        Assert.Equal(1.5, cfg.MotionThresholdPercent);
        Assert.Equal(40, detector.PixelDelta);
        Assert.Equal(5, cfg.OutputFps);
        Assert.Equal(18, cfg.Crf);
        Assert.Equal("veryfast", cfg.Preset);
    }

    [Fact]
    public void Apply_leaves_fields_absent_from_payload_untouched()
    {
        var cfg = MakeCfg();
        var detector = new MotionDetector(pixelDelta: 25);
        var payload = new CapturedConfigPayload(
            SchemaVersion: 1, CaptureIntervalMs: null, DownscaleWidth: null, DownscaleHeight: null,
            MotionThresholdPercent: null, MotionThresholdPixelDelta: null, OutputFps: null, Crf: 18, Preset: null, AllowUsers: null, ExcludeUsers: null);

        var grabber = new FakeScreenGrabber(cfg.DownscaleWidth, cfg.DownscaleHeight);

        ConfigReload.Apply(payload, cfg, detector, grabber, NoopLogger);

        Assert.Equal(500, cfg.CaptureIntervalMs);
        Assert.Equal(0.5, cfg.MotionThresholdPercent);
        Assert.Equal(25, detector.PixelDelta);
        Assert.Equal(2, cfg.OutputFps);
        Assert.Equal(18, cfg.Crf);
        Assert.Equal("ultrafast", cfg.Preset);
    }

    [Fact]
    public void Apply_changes_resolution_on_grabber_and_cfg_and_returns_true()
    {
        // ScreenGrabber не хранит хендлов между вызовами Grab() — безопасно менять
        // _outW/_outH на лету (см. ScreenGrabber.Resize).
        var cfg = MakeCfg();
        var detector = new MotionDetector(pixelDelta: 25);
        var grabber = new FakeScreenGrabber(cfg.DownscaleWidth, cfg.DownscaleHeight);
        var payload = new CapturedConfigPayload(
            SchemaVersion: 1, CaptureIntervalMs: null, DownscaleWidth: 1920, DownscaleHeight: 1080,
            MotionThresholdPercent: null, MotionThresholdPixelDelta: null, OutputFps: null, Crf: null, Preset: null, AllowUsers: null, ExcludeUsers: null);

        bool changed = ConfigReload.Apply(payload, cfg, detector, grabber, NoopLogger);

        Assert.True(changed);
        Assert.Equal(1920, grabber.OutputWidth);
        Assert.Equal(1080, grabber.OutputHeight);
        Assert.Equal(1920, cfg.DownscaleWidth);
        Assert.Equal(1080, cfg.DownscaleHeight);
    }

    [Fact]
    public void Apply_returns_false_when_resolution_matches_current()
    {
        var cfg = MakeCfg();
        var detector = new MotionDetector(pixelDelta: 25);
        var grabber = new FakeScreenGrabber(cfg.DownscaleWidth, cfg.DownscaleHeight);
        var payload = new CapturedConfigPayload(
            SchemaVersion: 1, CaptureIntervalMs: null, DownscaleWidth: 640, DownscaleHeight: 480,
            MotionThresholdPercent: null, MotionThresholdPixelDelta: null, OutputFps: null, Crf: null, Preset: null, AllowUsers: null, ExcludeUsers: null);

        bool changed = ConfigReload.Apply(payload, cfg, detector, grabber, NoopLogger);

        Assert.False(changed);
    }

    [Fact]
    public void Apply_leaves_resolution_untouched_when_absent_from_payload()
    {
        var cfg = MakeCfg();
        var detector = new MotionDetector(pixelDelta: 25);
        var grabber = new FakeScreenGrabber(cfg.DownscaleWidth, cfg.DownscaleHeight);
        var payload = new CapturedConfigPayload(
            SchemaVersion: 1, CaptureIntervalMs: 750, DownscaleWidth: null, DownscaleHeight: null,
            MotionThresholdPercent: null, MotionThresholdPixelDelta: null, OutputFps: null, Crf: null, Preset: null, AllowUsers: null, ExcludeUsers: null);

        bool changed = ConfigReload.Apply(payload, cfg, detector, grabber, NoopLogger);

        Assert.False(changed);
        Assert.Equal(640, grabber.OutputWidth);
        Assert.Equal(480, grabber.OutputHeight);
    }

    private static CapturedConfigPayload MakePayload(List<string>? allow = null, List<string>? exclude = null) =>
        new(SchemaVersion: 1, CaptureIntervalMs: null, DownscaleWidth: null, DownscaleHeight: null,
            MotionThresholdPercent: null, MotionThresholdPixelDelta: null, OutputFps: null, Crf: null,
            Preset: null, AllowUsers: allow, ExcludeUsers: exclude);

    [Fact]
    public void IsCaptureAllowedForUser_null_payload_allows_everyone()
    {
        Assert.True(ConfigReload.IsCaptureAllowedForUser(null, "kassir1"));
    }

    [Fact]
    public void IsCaptureAllowedForUser_no_lists_allows_everyone()
    {
        var payload = MakePayload();

        Assert.True(ConfigReload.IsCaptureAllowedForUser(payload, "kassir1"));
    }

    [Fact]
    public void IsCaptureAllowedForUser_allow_list_permits_only_listed_users_case_insensitive()
    {
        var payload = MakePayload(allow: new List<string> { "Kassir1", "kassir2" });

        Assert.True(ConfigReload.IsCaptureAllowedForUser(payload, "kassir1"));
        Assert.True(ConfigReload.IsCaptureAllowedForUser(payload, "KASSIR2"));
        Assert.False(ConfigReload.IsCaptureAllowedForUser(payload, "admin"));
    }

    [Fact]
    public void IsCaptureAllowedForUser_exclude_list_blocks_only_listed_users_case_insensitive()
    {
        var payload = MakePayload(exclude: new List<string> { "Admin" });

        Assert.False(ConfigReload.IsCaptureAllowedForUser(payload, "admin"));
        Assert.False(ConfigReload.IsCaptureAllowedForUser(payload, "ADMIN"));
        Assert.True(ConfigReload.IsCaptureAllowedForUser(payload, "kassir1"));
    }

    [Fact]
    public void IsCaptureAllowedForUser_allow_list_takes_precedence_over_exclude_list()
    {
        var payload = MakePayload(
            allow: new List<string> { "kassir1" },
            exclude: new List<string> { "kassir1" });

        Assert.True(ConfigReload.IsCaptureAllowedForUser(payload, "kassir1"));
    }

    [Fact]
    public void IsCaptureAllowedForUser_empty_lists_are_treated_as_unset()
    {
        var payload = MakePayload(allow: new List<string>(), exclude: new List<string>());

        Assert.True(ConfigReload.IsCaptureAllowedForUser(payload, "anyone"));
    }

    // R4 (docs/code-review-2026-10.md): preset уходил в командную строку ffmpeg,
    // нули давали busy-loop/падение Resize. Невалидное поле не применяется,
    // валидные соседние — применяются.
    [Fact]
    public void Apply_ignores_unknown_preset_and_out_of_range_values()
    {
        var cfg = MakeCfg();
        var detector = new MotionDetector(pixelDelta: 25);
        var grabber = new FakeScreenGrabber(cfg.DownscaleWidth, cfg.DownscaleHeight);
        var payload = new CapturedConfigPayload(
            SchemaVersion: 1, CaptureIntervalMs: 0, DownscaleWidth: 0, DownscaleHeight: 0,
            MotionThresholdPercent: 500, MotionThresholdPixelDelta: 0, OutputFps: 0, Crf: 18,
            Preset: "ultrafast -y /tmp/x.mp4", AllowUsers: null, ExcludeUsers: null);

        bool changed = ConfigReload.Apply(payload, cfg, detector, grabber, NoopLogger);

        Assert.False(changed);
        Assert.Equal(500, cfg.CaptureIntervalMs);
        Assert.Equal(640, grabber.OutputWidth);
        Assert.Equal(480, grabber.OutputHeight);
        Assert.Equal(0.5, cfg.MotionThresholdPercent);
        Assert.Equal(25, detector.PixelDelta);
        Assert.Equal(2, cfg.OutputFps);
        Assert.Equal("ultrafast", cfg.Preset);
        Assert.Equal(18, cfg.Crf);
    }
}
