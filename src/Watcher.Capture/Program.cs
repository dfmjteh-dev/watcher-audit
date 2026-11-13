using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Watcher.Capture;

[assembly: SupportedOSPlatform("windows")]

// Раньше любого вызова user32 — см. ScreenGrabber.EnableDpiAwareness.
ScreenGrabber.EnableDpiAwareness();

var cfgRoot = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("Watcher.Capture.json", optional: true)
    .Build();

var cfg = new CaptureConfig();
cfgRoot.GetSection("Capture").Bind(cfg);
// Значения по умолчанию для ключей, которых нет в конфиге с сервера (см.
// CapturedConfigPayload.WithDefaults) — снимок до первого применения конфига.
var configDefaults = new CapturedConfigPayload(null, cfg.CaptureIntervalMs, cfg.DownscaleWidth, cfg.DownscaleHeight,
    cfg.MotionThresholdPercent, cfg.MotionThresholdPixelDelta, cfg.OutputFps, cfg.Crf, cfg.Preset,
    AllowUsers: null, ExcludeUsers: null, Activity: cfg.Activity, ShowCursor: cfg.ShowCursor);

// Фаза 6 (roadmap.md): --session-user приходит от Watcher.SessionService, когда
// этот процесс запущен в конкретной RDS-сессии через CreateProcessAsUser — пусто
// для обычных одно-сессионных станций (Scheduled Task), ничего не меняет для них.
string? sessionUser = null;
var rawArgs = Environment.GetCommandLineArgs();
for (int i = 1; i < rawArgs.Length - 1; i++)
{
    if (rawArgs[i] == "--session-user") { sessionUser = rawArgs[i + 1]; break; }
}

Directory.CreateDirectory(cfg.QueueDir);
Directory.CreateDirectory(cfg.LogDir);

// Лог-файл получает суффикс sessionUser, когда он задан — без этого несколько
// одновременных инстансов Capture (RDS) писали бы в один общий файл без разделения
// (найдено спайком Фазы 6, см. roadmap.md).
string logFileNamePrefix = string.IsNullOrWhiteSpace(sessionUser) ? "watcher-" : $"watcher-{sessionUser}-";

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Sink(new Watcher.DiagnosticSink((template, error) => Watcher.Uploader.DiagnosticSpool.Write(cfg.QueueDir, template, error)))
    .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(cfg.LogDir, $"{logFileNamePrefix}.log"),
        outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: cfg.LogRetainedDays,
        fileSizeLimitBytes: cfg.LogFileSizeLimitMb * 1024L * 1024L,
        rollOnFileSizeLimit: true,
        shared: true)
    .CreateLogger();

using var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog());
var logger = loggerFactory.CreateLogger("Watcher.Capture");

string host = Environment.MachineName;
logger.LogInformation("Watcher.Capture {Version} starting on {Host} (sessionUser={SessionUser})",
    AppVersion.Current, host, sessionUser ?? "-");
logger.LogInformation("Queue dir: {QueueDir}", cfg.QueueDir);
logger.LogInformation("FFmpeg path: {FfmpegPath}", cfg.FfmpegPath);
logger.LogInformation("Capture {Width}x{Height} @ {Fps:F1} fps grab, {OutputFps} fps out",
    cfg.DownscaleWidth, cfg.DownscaleHeight, 1000.0 / cfg.CaptureIntervalMs, cfg.OutputFps);

using var grabber = new ScreenGrabber(cfg.DownscaleWidth, cfg.DownscaleHeight);
logger.LogInformation("Screen layout {W}x{H}, monitors: {Monitors}", grabber.Layout.Width, grabber.Layout.Height,
    string.Join("; ", grabber.Layout.Monitors.Select(m => $"{m.W}x{m.H}+{m.X}+{m.Y}{(m.Primary ? " primary" : "")}")));
var detector = new MotionDetector(cfg.MotionThresholdPixelDelta);
int captureHz = Math.Max(1, 1000 / cfg.CaptureIntervalMs);
using var ring = new RingBuffer(cfg.PreRollSeconds * captureHz);
// H9: активное окно этой сессии — в sidecar каждого сегмента (режим — cfg.Activity).
// 0.14.3: у браузера — ещё домен вкладки (BrowserSite, только в режиме full).
var activeWindow = new ActiveWindow();
using var browserSite = new BrowserSite();
using var writer = new SegmentWriter(cfg, host, sessionUser, loggerFactory.CreateLogger("Watcher.Capture.SegmentWriter"),
    new ActivityLog(activeWindow.Probe, browserSite.Lookup));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    logger.LogInformation("Cancellation requested");
};

int idleTicks = 0;
int idleTicksToClose = cfg.IdleSecondsToCloseSegment * captureHz;
DateTime lastFrameTime = DateTime.MinValue;
DateTime lastConfigCheck = DateTime.MinValue;
// Фаза 5: пер-юзерный allow/exclude (см. ConfigReload.IsCaptureAllowedForUser) — считается
// в блоке проверки конфига ниже, применяется здесь на каждый тик. По умолчанию true: до
// первой проверки конфига (которая случится в самой первой итерации цикла, см.
// lastConfigCheck = DateTime.MinValue выше) ведём себя как раньше — захват разрешён всем.
bool capturingEnabled = true;
// H8: расписание и исключённые окна (RecordingPolicy) — из того же конфига,
// пересчитывается при каждой его проверке; проверяется на каждом тике.
RecordingPolicy policy = RecordingPolicy.RecordAlways;
PauseReason paused = PauseReason.None;
// Кого сверять с allowUsers/excludeUsers — то же имя, по которому служба
// решает, запускать ли запись (WTS-имя сеанса, --session-user), и под которым
// приходят сегменты. Environment.UserName у учётки Microsoft/Azure AD — имя
// профиля, другое: allowUsers [abutakov] не совпадал, запись молча не шла
// (проверка RC 0.13, ноутбук с двумя учётками).
string currentUser = string.IsNullOrWhiteSpace(sessionUser) ? Environment.UserName : sessionUser;
// Экран сейчас не захватывается (блокировка, secure desktop) — см. ScreenGrabber.Grab.
bool screenUnavailable = false;

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
                    // captureHz зависит от CaptureIntervalMs — пересчитываем, если применённый
                    // конфиг его поменял, иначе idleTicksToClose и емкость ring-буфера (pre-roll)
                    // считают по устаревшему значению и реальная длительность pre-roll в секундах
                    // начинает плыть относительно PreRollSeconds, хотя тот не менялся.
                    captureHz = Math.Max(1, 1000 / cfg.CaptureIntervalMs);
                    idleTicksToClose = cfg.IdleSecondsToCloseSegment * captureHz;
                    ring.Resize(cfg.PreRollSeconds * captureHz);

                    if (resolutionChanged)
                    {
                        // Не дать кадрам старого и нового размера оказаться в одном
                        // ffmpeg image2pipe — закрыть текущий сегмент (если открыт) и
                        // выкинуть pre-roll буфер, набранный на старом разрешении.
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
                // Файл мог оказаться в процессе перезаписи Uploader'ом — не фатально,
                // следующая проверка через ConfigCheckIntervalSeconds подхватит.
                logger.LogWarning(ex, "config cache read failed, will retry next cycle");
            }
        }

        bool recording = capturingEnabled && !Paused(policy.Check(DateTime.Now, activeWindow.Probe, browserSite.SiteFor));
        var frame = recording ? grabber.Grab() : null;
        if (recording && frame is null)
        {
            // Кадров нет — детектор и pre-roll не трогаем: после разблокировки
            // кадр сравнится с последним до блокировки, а не с чернотой.
            // Открытый сегмент закрываем сразу: иначе он висел бы открытым всю
            // блокировку (idle-тики считаются только по кадрам), а потом склеил
            // бы «до» и «после» в одно видео без разрыва по времени.
            if (!screenUnavailable)
            {
                screenUnavailable = true;
                logger.LogInformation("Screen not capturable (locked / secure desktop / blank frame), GLE={Error}, pausing", grabber.LastError);
            }
            if (writer.IsOpen)
            {
                logger.LogInformation("Segment closed (screen unavailable)");
                writer.Close();
                idleTicks = 0;
            }
        }
        else if (recording && frame is not null)
        {
            if (screenUnavailable)
            {
                screenUnavailable = false;
                logger.LogInformation("Screen capturable again, resuming");
            }
            // 0.13: монитор подключили/отключили, сменили разрешение — кадр другого
            // размера: в один файл (и в pre-roll) разные размеры не смешиваем.
            if (frameSize != (frame.Width, frame.Height))
            {
                if (frameSize != default)
                {
                    logger.LogInformation("Screen layout changed: {W}x{H}, {Count} monitor(s)", frame.Width, frame.Height, grabber.Layout.Monitors.Count);
                    if (writer.IsOpen) { writer.Close(); idleTicks = 0; }
                    ring.Clear();
                }
                frameSize = (frame.Width, frame.Height);
            }
            double change = detector.ComputeChange(frame);
            // 0.14.3: курсор — после детектора: движение мыши запись не открывает.
            if (cfg.ShowCursor) grabber.DrawCursor(frame);
            bool motion = change >= cfg.MotionThresholdPercent;

            if (motion)
            {
                if (!writer.IsOpen)
                {
                    writer.Open(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"motion {change:F2}%"), grabber.Layout);
                    logger.LogInformation("Segment opened (motion {Change:F2}%)", change);
                    foreach (var pre in ring.DrainCopies())
                    {
                        writer.WriteFrame(pre, isMotion: false);
                        pre.Dispose();
                    }
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
}
finally
{
    writer.Close();
    logger.LogInformation("Watcher.Capture stopped");
    Log.CloseAndFlush();
}
