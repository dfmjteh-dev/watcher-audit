namespace Watcher;

// Аргументы ffmpeg, общие для агента захвата и выгрузки (src/Shared,
// подключается ссылкой). Запуск процесса у каждого свой — разные логирование и
// таймауты, — а команда одна.
public static class FfmpegArgs
{
    // Фрагментированный .rec.mp4 (переживает обрыв записи) → обычный faststart
    // mp4 без перекодирования: Capture при закрытии сегмента, Uploader для
    // осиротевших сегментов (Задача 18).
    public static string[] Remux(string recPath, string outPath) =>
        ["-y", "-hide_banner", "-loglevel", "error", "-i", recPath, "-c", "copy", "-movflags", "+faststart", outPath];
}
