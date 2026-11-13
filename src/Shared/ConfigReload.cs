using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Watcher.Capture;

// Общий для агентов захвата Windows и Linux (src/Shared, 0.14.4 — раньше две
// почти одинаковые копии, правки приходилось делать дважды).
//
// Применение конфига, присланного веб-UI через /api/heartbeat и закэшированного
// Watcher.Uploader'ом в ConfigCachePath (тот же файл, Capture его не пишет, только
// читает). CaptureIntervalMs/MotionThresholdPercent/OutputFps/Crf/Preset читаются
// другими объектами лениво из того же CaptureConfig/MotionDetector — присвоение сюда
// достаточно, пересоздавать SegmentWriter/детектор не нужно. Разрешение экрана тоже
// применяется (ScreenGrabber.Resize) — в отличие от первого варианта этой фазы,
// ScreenGrabber НЕ держит никаких хендлов между вызовами Grab() (создаёт и
// освобождает DC/битмап внутри одного вызова), так что это безопасно; единственная
// забота на стороне вызывающего — не смешивать кадры старого/нового размера в одном
// открытом ffmpeg-пайпе (см. Program.cs: при ResolutionChanged закрывается текущий
// сегмент и чистится ring buffer).
public static class ConfigReload
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // R4: пределы — AgentConfigLimits (src/Shared, общие с сервером). Значение
    // вне диапазона не применяется (остаётся текущее), остальные поля конфига
    // применяются как обычно.
    private static bool InRange(string name, double value, ConfigRange range, ILogger logger)
    {
        if (range.Contains(value)) return true;
        logger.LogWarning("config: {Name}={Value} outside {Min}..{Max}, ignored", name, value, range.Min, range.Max);
        return false;
    }

    public static CapturedConfigPayload? TryParse(string json, ILogger logger)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<CapturedConfigPayload>(json, JsonOptions);
            if (payload is null) return null;
            if (payload.SchemaVersion is { } version && version != 1)
            {
                logger.LogWarning("config cache has unknown schemaVersion {Version}, ignoring", version);
                return null;
            }
            return payload;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "config cache file is not valid JSON, ignoring");
            return null;
        }
    }

    // Возвращает true, если разрешение поменялось — вызывающий код (Program.cs) должен
    // в этом случае закрыть текущий открытый сегмент и очистить ring buffer, прежде
    // чем в новый ffmpeg-пайп попадёт смесь кадров старого и нового размера.
    public static bool Apply(CapturedConfigPayload payload, CaptureSettings cfg, IMotionThreshold detector, IResizableGrabber grabber, ILogger logger)
    {
        if (payload.CaptureIntervalMs is { } interval && interval != cfg.CaptureIntervalMs
            && InRange(nameof(cfg.CaptureIntervalMs), interval, AgentConfigLimits.CaptureIntervalMs, logger))
        {
            logger.LogInformation("config: CaptureIntervalMs {Old} -> {New}", cfg.CaptureIntervalMs, interval);
            cfg.CaptureIntervalMs = interval;
        }
        if (payload.MotionThresholdPercent is { } motionPct && motionPct != cfg.MotionThresholdPercent
            && InRange(nameof(cfg.MotionThresholdPercent), motionPct, AgentConfigLimits.MotionThresholdPercent, logger))
        {
            logger.LogInformation("config: MotionThresholdPercent {Old} -> {New}", cfg.MotionThresholdPercent, motionPct);
            cfg.MotionThresholdPercent = motionPct;
        }
        if (payload.MotionThresholdPixelDelta is { } pixelDelta && pixelDelta != detector.PixelDelta
            && InRange("MotionThresholdPixelDelta", pixelDelta, AgentConfigLimits.MotionThresholdPixelDelta, logger))
        {
            logger.LogInformation("config: MotionThresholdPixelDelta {Old} -> {New}", detector.PixelDelta, pixelDelta);
            detector.PixelDelta = pixelDelta;
        }
        if (payload.OutputFps is { } fps && fps != cfg.OutputFps
            && InRange(nameof(cfg.OutputFps), fps, AgentConfigLimits.OutputFps, logger))
        {
            logger.LogInformation("config: OutputFps {Old} -> {New}", cfg.OutputFps, fps);
            cfg.OutputFps = fps;
        }
        if (payload.Crf is { } crf && crf != cfg.Crf
            && InRange(nameof(cfg.Crf), crf, AgentConfigLimits.Crf, logger))
        {
            logger.LogInformation("config: Crf {Old} -> {New}", cfg.Crf, crf);
            cfg.Crf = crf;
        }
        if (payload.Preset is { } preset && preset != cfg.Preset)
        {
            if (AgentConfigLimits.Presets.Contains(preset))
            {
                logger.LogInformation("config: Preset {Old} -> {New}", cfg.Preset, preset);
                cfg.Preset = preset;
            }
            else
            {
                logger.LogWarning("config: Preset {Preset} is not a known x264 preset, ignored", preset);
            }
        }

        if (payload.Activity is { } activity && activity != cfg.Activity)
        {
            if (ActivityLog.Modes.Contains(activity))
            {
                logger.LogInformation("config: Activity {Old} -> {New}", cfg.Activity, activity);
                cfg.Activity = activity;
            }
            else
            {
                logger.LogWarning("config: Activity {Activity} is not one of full/process/off, ignored", activity);
            }
        }

        if (payload.ShowCursor is { } cursor && cursor != cfg.ShowCursor)
        {
            logger.LogInformation("config: ShowCursor {Old} -> {New}", cfg.ShowCursor, cursor);
            cfg.ShowCursor = cursor;
        }

        int? width = payload.DownscaleWidth is { } pw && InRange(nameof(cfg.DownscaleWidth), pw, AgentConfigLimits.DownscaleWidth, logger) ? pw : null;
        int? height = payload.DownscaleHeight is { } ph && InRange(nameof(cfg.DownscaleHeight), ph, AgentConfigLimits.DownscaleHeight, logger) ? ph : null;
        bool widthChanged = width is { } w && w != grabber.OutputWidth;
        bool heightChanged = height is { } h && h != grabber.OutputHeight;
        if (!widthChanged && !heightChanged) return false;

        int newWidth = width ?? grabber.OutputWidth;
        int newHeight = height ?? grabber.OutputHeight;
        logger.LogInformation("config: resolution {OldW}x{OldH} -> {NewW}x{NewH}",
            grabber.OutputWidth, grabber.OutputHeight, newWidth, newHeight);
        grabber.Resize(newWidth, newHeight);
        cfg.DownscaleWidth = newWidth;
        cfg.DownscaleHeight = newHeight;
        return true;
    }

    // payload == null — конфига с сервера ещё нет (файл не появился/невалиден) —
    // по умолчанию разрешено всем, как и до этой фазы, не блокировка "на всякий случай".
    // Сверка — общая (src/Shared/UserNames.cs): без регистра и домена.
    public static bool IsCaptureAllowedForUser(CapturedConfigPayload? payload, string username) =>
        UserNames.IsAllowed(payload?.AllowUsers, payload?.ExcludeUsers, username);
}
