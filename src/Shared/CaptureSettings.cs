namespace Watcher.Capture;

// Настройки агента захвата экрана, общие для Windows и Linux (0.14.4: были
// двумя копиями CaptureConfig). Пути и разрешение по умолчанию у ОС свои —
// задаются в конструкторе CaptureConfig каждого агента; привязка appsettings
// (Configuration.Bind) видит и унаследованные свойства.
public abstract class CaptureSettings
{
    public string QueueDir { get; set; } = "";
    public string LogDir { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public int CaptureIntervalMs { get; set; } = 500;
    public int DownscaleWidth { get; set; }
    public int DownscaleHeight { get; set; }
    public double MotionThresholdPercent { get; set; } = 0.5;
    public int MotionThresholdPixelDelta { get; set; } = 25;
    public int IdleSecondsToCloseSegment { get; set; } = 10;
    public int MaxSegmentSeconds { get; set; } = 300;
    public int PreRollSeconds { get; set; } = 10;
    public int OutputFps { get; set; } = 2;
    public int Crf { get; set; } = 30;
    public string Preset { get; set; } = "ultrafast";
    public int FfmpegWriteTimeoutMs { get; set; } = 5000;
    public int LogRetainedDays { get; set; } = 14;
    public int LogFileSizeLimitMb { get; set; } = 10;
    // Тот же файл, что пишет Watcher.Uploader после heartbeat (UploaderConfig.
    // ConfigCachePath) — одинаковый путь по умолчанию у обоих процессов, чтобы не
    // настраивать его дважды на одной станции.
    public string ConfigCachePath { get; set; } = "";
    public int ConfigCheckIntervalSeconds { get; set; } = 30;
    // H9: активное окно рядом с видео — full (процесс и заголовок), process,
    // off. Обычно приходит из конфига группы (ключ activity).
    public string Activity { get; set; } = "full";
    // 0.14.3: курсор мыши на видео.
    public bool ShowCursor { get; set; } = true;
}

// Что ConfigReload меняет на лету у детектора движения и захвата экрана.
public interface IMotionThreshold
{
    int PixelDelta { get; set; }
}

public interface IResizableGrabber
{
    int OutputWidth { get; }
    int OutputHeight { get; }
    void Resize(int outWidth, int outHeight);
}
