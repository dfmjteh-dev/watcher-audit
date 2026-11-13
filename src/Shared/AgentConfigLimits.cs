namespace Watcher;

// R4: допустимые значения конфига захвата из веб-UI. Проверяют обе стороны —
// сервер при сохранении (AgentConfigValidation) и агент при применении
// (ConfigReload в обоих агентах захвата): агент не доверяет серверу вслепую.
// Один файл на всех (src/Shared, подключается ссылкой) — пределы не разойдутся.
public readonly record struct ConfigRange(double Min, double Max)
{
    public bool Contains(double value) => value >= Min && value <= Max;
}

public static class AgentConfigLimits
{
    public static readonly string[] Presets =
        ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];

    public static readonly ConfigRange CaptureIntervalMs = new(100, 10000);
    public static readonly ConfigRange DownscaleWidth = new(320, 7680);
    public static readonly ConfigRange DownscaleHeight = new(240, 4320);
    public static readonly ConfigRange MotionThresholdPercent = new(0, 100);
    public static readonly ConfigRange MotionThresholdPixelDelta = new(1, 255);
    public static readonly ConfigRange OutputFps = new(1, 60);
    public static readonly ConfigRange Crf = new(0, 51);
    // 0.12: как часто консольный агент шлёт незакрытую сессию, секунды.
    public static readonly ConfigRange ConsoleLiveIntervalSec = new(10, 3600);
    // 0.13: сколько дней станция держит невыгруженные записи (сервер недоступен),
    // 0 — без срока. Те же числа — QueueAge.Range в Watcher.Uploader.Core.
    public static readonly ConfigRange QueueMaxAgeDays = new(0, 3650);
    public const int DefaultQueueMaxAgeDays = 30;
}
