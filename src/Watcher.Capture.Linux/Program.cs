using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Watcher.Capture;
using Watcher.Capture.Linux;

// Без [assembly: SupportedOSPlatform("linux")] здесь намеренно, в отличие от
// Watcher.Capture (Windows): [SupportedOSPlatform("linux")] уже стоит точечно
// на ScreenGrabber.cs (там, где реально P/Invoke в libX11/libXext), а
// MotionDetector/RingBuffer/ConfigReload/Frame — чистый управляемый код без
// платформенных зависимостей. Атрибут на сборку пометил бы их тоже как
// "только Linux" для анализатора совместимости платформ — ровно то, чего мы
// добивались не делать, выделяя Frame в чистый byte[] вместо Bitmap (см.
// Frame.cs): эти типы тестируются в CI на любой ОС, не только на Linux.

var cfgRoot = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("Watcher.Capture.Linux.json", optional: true)
    .Build();

var cfg = new CaptureConfig();
cfgRoot.GetSection("Capture").Bind(cfg);
// Значения по умолчанию для ключей, которых нет в конфиге с сервера (см.
// CapturedConfigPayload.WithDefaults) — снимок до первого применения конфига.
var configDefaults = new CapturedConfigPayload(null, cfg.CaptureIntervalMs, cfg.DownscaleWidth, cfg.DownscaleHeight,
    cfg.MotionThresholdPercent, cfg.MotionThresholdPixelDelta, cfg.OutputFps, cfg.Crf, cfg.Preset,
    AllowUsers: null, ExcludeUsers: null, Activity: cfg.Activity, ShowCursor: cfg.ShowCursor);

Directory.CreateDirectory(cfg.QueueDir);
Directory.CreateDirectory(cfg.LogDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Sink(new Watcher.DiagnosticSink((template, error) => Watcher.Uploader.DiagnosticSpool.Write(cfg.QueueDir, template, error)))
    .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(cfg.LogDir, "watcher-capture-.log"),
        outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: cfg.LogRetainedDays,
        fileSizeLimitBytes: cfg.LogFileSizeLimitMb * 1024L * 1024L,
        rollOnFileSizeLimit: true,
        shared: true)
    .CreateLogger();

using var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog());
var logger = loggerFactory.CreateLogger("Watcher.Capture.Linux");

string host = Environment.MachineName;
string currentUser = Environment.UserName;
logger.LogInformation("Watcher.Capture.Linux {Version} starting on {Host} as {User}", AppVersion.Current, host, currentUser);
logger.LogInformation("Queue dir: {QueueDir}", cfg.QueueDir);
logger.LogInformation("FFmpeg path: {FfmpegPath}", cfg.FfmpegPath);
logger.LogInformation("Capture {Width}x{Height} @ {Fps:F1} fps grab, {OutputFps} fps out",
    cfg.DownscaleWidth, cfg.DownscaleHeight, 1000.0 / cfg.CaptureIntervalMs, cfg.OutputFps);

// Program.cs — точка входа приложения, которое собирается только под linux-x64
// (RuntimeIdentifier в .csproj) — платформенная проверка здесь избыточна по
// смыслу, но без assembly-level атрибута (намеренно, см. комментарий выше про
// Frame/MotionDetector/RingBuffer/ConfigReload) анализатор об этом не знает.
#pragma warning disable CA1416
using var grabber = new ScreenGrabber(cfg.DownscaleWidth, cfg.DownscaleHeight);
logger.LogInformation("Screen layout {W}x{H}, monitors: {Monitors}", grabber.Layout.Width, grabber.Layout.Height,
    string.Join("; ", grabber.Layout.Monitors.Select(m => $"{m.W}x{m.H}+{m.X}+{m.Y}{(m.Primary ? " primary" : "")}")));
#pragma warning restore CA1416
var detector = new MotionDetector(cfg.MotionThresholdPixelDelta);
int captureHz = Math.Max(1, 1000 / cfg.CaptureIntervalMs);
var ring = new RingBuffer(cfg.PreRollSeconds * captureHz);
// H9: активное окно этого X-дисплея — в sidecar каждого сегмента (режим — cfg.Activity).
#pragma warning disable CA1416 // см. комментарий у конструктора ScreenGrabber выше
using var activeWindow = new ActiveWindow(loggerFactory.CreateLogger("Watcher.Capture.Linux.ActiveWindow"));
using var writer = new SegmentWriter(cfg, host, currentUser, loggerFactory.CreateLogger("Watcher.Capture.Linux.SegmentWriter"),
    new ActivityLog(activeWindow.Probe));
#pragma warning restore CA1416

using var cts = new CancellationTokenSource();
// AppDomain.ProcessExit НЕ срабатывает на обычный POSIX-сигнал (это поведение
// ASP.NET Core generic host, не голого top-level-statement консольного приложения)
// — без явной регистрации systemd `systemctl stop` (SIGTERM) убивал бы процесс
// мгновенно, не давая дойти до `finally` ниже: текущий открытый сегмент остаётся
// без .json-сайдкара (ffmpeg сам доflush'ивает контейнер по EOF от разорванного
// pipe, mp4 получается валидным, но без метаданных) — проверено живьём на
// watcher-test-1 при отладке этой самой проблемы. PosixSignalRegistration — API
// для обоих сигналов одинаково, обрабатывает и Ctrl+C, и systemd stop.
void RequestShutdown(string reason)
{
    cts.Cancel();
    logger.LogInformation("Shutdown requested ({Reason})", reason);
}
using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; RequestShutdown("SIGINT"); });
using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; RequestShutdown("SIGTERM"); });

int idleTicks = 0;
int idleTicksToClose = cfg.IdleSecondsToCloseSegment * captureHz;
DateTime lastConfigCheck = DateTime.MinValue;
// Фаза 5 (перенесено с Windows-агента): пер-юзерный allow/exclude — см.
// ConfigReload.IsCaptureAllowedForUser. До первой проверки конфига (которая
// случится в самой первой итерации, lastConfigCheck = MinValue) — разрешено всем.
bool capturingEnabled = true;
// Экран сейчас не снять (ScreenUnavailableException) — для лога «пропал/вернулся».
bool screenUnavailable = false;
// H8: расписание и исключённые окна (RecordingPolicy) — из того же конфига,
// пересчитывается при каждой его проверке; проверяется на каждом тике.
RecordingPolicy policy = RecordingPolicy.RecordAlways;
PauseReason paused = PauseReason.None;

// H8: true — запись на паузе (расписание, исключённое окно). Переходы — в
// лог, но без имени окна и заголовка: лог лежит на станции, а исключение
// для того и задано, чтобы их не сохранять. pre-roll очищается — иначе кадры
// до паузы попали бы в следующий сегмент с его временем начала.
bool Paused(PauseReason reason)
{
    if (reason != paused)
    {
        if (reason == PauseReason.None) logger.LogInformation("recording resumed");
        else logger.LogInformation("recording paused: {Reason}", reason);
        paused = reason;
    }
    if (reason == PauseReason.None) return false;
    if (writer.IsOpen)
    {
        logger.LogInformation("Segment closed (paused: {Reason})", reason);
        writer.Close();
        idleTicks = 0;
    }
    ring.Clear();
    return true;
}

try
{
    (int, int) frameSize = default;
    Watcher.CaptureLayout? layout = null;
    while (!cts.IsCancellationRequested)
    {
        var tickStart = DateTime.UtcNow;

        if ((DateTime.UtcNow - lastConfigCheck).TotalSeconds >= cfg.ConfigCheckIntervalSeconds)
        {
            lastConfigCheck = DateTime.UtcNow;
            try
            {
                CapturedConfigPayload? payload = null;
                if (File.Exists(cfg.ConfigCachePath))
                {
                    string json = File.ReadAllText(cfg.ConfigCachePath);
                    payload = ConfigReload.TryParse(json, logger)?.WithDefaults(configDefaults);
                }

                if (payload is not null)
                {
                    bool resolutionChanged = ConfigReload.Apply(payload, cfg, detector, grabber, logger);
                    captureHz = Math.Max(1, 1000 / cfg.CaptureIntervalMs);
                    idleTicksToClose = cfg.IdleSecondsToCloseSegment * captureHz;
                    ring.Resize(cfg.PreRollSeconds * captureHz);

                    if (resolutionChanged)
                    {
                        if (writer.IsOpen)
                        {
                            logger.LogInformation("Segment closed (resolution change)");
                            writer.Close();
                            idleTicks = 0;
                        }
                        ring.Clear();
                    }
                }

                var newPolicy = RecordingPolicy.From(payload);
                if (newPolicy.InvalidScheduleEntries > 0 && newPolicy.InvalidScheduleEntries != policy.InvalidScheduleEntries)
                    logger.LogWarning("config: {Count} schedule entries are invalid and ignored", newPolicy.InvalidScheduleEntries);
                policy = newPolicy;

                bool wasEnabled = capturingEnabled;
                capturingEnabled = ConfigReload.IsCaptureAllowedForUser(payload, currentUser)
                    && !Watcher.RecordingPause.IsActive(payload?.PauseVideo, payload?.PauseUntil, DateTime.Now);
                // 0.13: отметка «кто, какой конфиг применил, пишет ли» — для веб-UI.
                CaptureStatusFile.Write(cfg.QueueDir, currentUser, payload?.ConfigHash, capturingEnabled && policy.InSchedule(DateTime.Now));
                if (capturingEnabled != wasEnabled)
                {
                    logger.LogInformation(
                        capturingEnabled
                            ? "capture enabled for user {User} by config"
                            : "capture disabled for user {User} by config",
                        currentUser);
                    if (!capturingEnabled && writer.IsOpen)
                    {
                        writer.Close();
                        idleTicks = 0;
                    }
                }
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "config cache read failed, will retry next cycle");
            }
        }

#pragma warning disable CA1416 // см. комментарий у конструктора ScreenGrabber выше
        bool recording = capturingEnabled && !Paused(policy.Check(DateTime.Now, activeWindow.Probe));
#pragma warning restore CA1416
        if (recording)
        {
            Frame frame;
            try
            {
#pragma warning disable CA1416 // см. комментарий у конструктора ScreenGrabber выше
                frame = grabber.Grab();
                layout = grabber.Layout;
#pragma warning restore CA1416
                if (screenUnavailable) logger.LogInformation("screen capture resumed");
                screenUnavailable = false;
            }
            catch (ScreenUnavailableException ex)
            {
                // Экран не снять (сменился размер и т.п.) — сегмент закрываем, пробуем
                // на следующем такте; в лог — один раз, а не на каждый такт.
                if (!screenUnavailable) logger.LogWarning("screen capture unavailable, retrying: {Reason}", ex.Message);
                screenUnavailable = true;
                if (writer.IsOpen) { writer.Close(); idleTicks = 0; }
                ring.Clear();
                await Task.Delay(Math.Max(cfg.CaptureIntervalMs, 1000), cts.Token);
                continue;
            }
            // 0.13: монитор подключили/отключили, сменили разрешение — кадр другого
            // размера: в один файл (и в pre-roll) разные размеры не смешиваем.
            if (frameSize != (frame.Width, frame.Height))
            {
                if (frameSize != default)
                {
                    logger.LogInformation("screen layout changed: {W}x{H}, {Count} monitor(s)", frame.Width, frame.Height, layout?.Monitors.Count ?? 1);
                    if (writer.IsOpen) { writer.Close(); idleTicks = 0; }
                    ring.Clear();
                }
                frameSize = (frame.Width, frame.Height);
            }
            double change = detector.ComputeChange(frame);
            // 0.14.3: курсор — после детектора: движение мыши запись не открывает.
#pragma warning disable CA1416 // см. комментарий у конструктора ScreenGrabber выше
            if (cfg.ShowCursor) grabber.DrawCursor(frame);
#pragma warning restore CA1416
            bool motion = change >= cfg.MotionThresholdPercent;

            if (motion)
            {
                if (!writer.IsOpen)
                {
                    writer.Open(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"motion {change:F2}%"), frame.Width, frame.Height, layout);
                    logger.LogInformation("Segment opened (motion {Change:F2}%)", change);
                    foreach (var pre in ring.DrainCopies())
                        writer.WriteFrame(pre, isMotion: false);
                }
                writer.WriteFrame(frame, isMotion: true);
                idleTicks = 0;
            }
            else if (writer.IsOpen)
            {
                writer.WriteFrame(frame, isMotion: false);
                idleTicks++;
            }

            // 0.14.2: MaxSegmentSeconds — на любом кадре, не только без движения: при
            // непрерывном движении на экране (видео, анимация) сегмент иначе не
            // закрывался никогда и рос до гигабайт, больше предела сервера.
            if (writer.IsOpen)
            {
                bool idle = idleTicks >= idleTicksToClose;
                bool exceededMax = (DateTime.Now - writer.StartedAt).TotalSeconds > cfg.MaxSegmentSeconds;
                if (idle || exceededMax)
                {
                    logger.LogInformation("Segment closed (idle={Idle}, max={Max})", idle, exceededMax);
                    writer.Close();
                    idleTicks = 0;
                    // Pre-roll уже в закрытом сегменте — в следующий не повторяем.
                    if (exceededMax) ring.Clear();
                }
            }

            ring.Push(frame);
        }

        var elapsed = (DateTime.UtcNow - tickStart).TotalMilliseconds;
        int wait = cfg.CaptureIntervalMs - (int)elapsed;
        if (wait > 0) await Task.Delay(wait, cts.Token);
    }
}
catch (OperationCanceledException) { }
catch (Exception ex)
{
    logger.LogCritical(ex, "FATAL");
    // Не 0: юнит перезапускает по Restart=on-failure, а с кодом 0 systemd считал
    // падение штатной остановкой — запись экрана вставала до следующего входа.
    Environment.ExitCode = 1;
}
finally
{
    writer.Close();
    logger.LogInformation("Watcher.Capture.Linux stopped");
    Log.CloseAndFlush();
}
