namespace Watcher.Capture.Linux;

// Общие поля — CaptureSettings (src/Shared); здесь — значения по умолчанию Linux.
public sealed class CaptureConfig : CaptureSettings
{
    // systemd --user service — путь должен быть внутри $HOME (не /var/lib, туда
    // у обычного пользователя нет прав на запись), ровно как и Watcher.Uploader
    // настраивается при установке (install-capture.sh, см. docs/install.md).
    private static readonly string DefaultDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "watcher");

    public CaptureConfig()
    {
        QueueDir = Path.Combine(DefaultDataDir, "queue");
        LogDir = Path.Combine(DefaultDataDir, "logs");
        FfmpegPath = "ffmpeg";
        DownscaleWidth = 1920;
        DownscaleHeight = 1080;
        ConfigCachePath = Path.Combine(DefaultDataDir, "config", "capture-config.json");
    }
}
