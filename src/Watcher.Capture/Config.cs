namespace Watcher.Capture;

// Общие поля — CaptureSettings (src/Shared); здесь — значения по умолчанию Windows.
public sealed class CaptureConfig : CaptureSettings
{
    public CaptureConfig()
    {
        QueueDir = @"C:\ProgramData\Watcher\queue";
        LogDir = @"C:\ProgramData\Watcher\logs";
        FfmpegPath = "ffmpeg.exe";
        DownscaleWidth = 640;
        DownscaleHeight = 480;
        ConfigCachePath = @"C:\ProgramData\Watcher\config\capture-config.json";
    }
}
