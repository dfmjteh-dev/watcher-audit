using System.Globalization;

namespace Watcher;

// 0.13 (п.15 обратной связи 2026-10-14): «Снять с записи» из веб-UI — ключи
// конфига хоста/группы pauseVideo, pauseConsole (bool), pauseUntil
// («yyyy-MM-dd», включительно, время станции; нет — бессрочно), pauseReason.
// Видео останавливает сам агент (процесс записи), консоль — сервер (не
// принимает: консольный агент конфиг не читает). Агент при паузе остаётся на
// связи (heartbeat, обновления). Один файл на сервер и оба агента захвата.
public static class RecordingPause
{
    public const int MaxReasonLength = 200;

    public static bool TryParseUntil(string? s, out DateTime date) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    public static bool IsActive(bool? paused, string? until, DateTime now)
    {
        if (paused != true) return false;
        if (string.IsNullOrEmpty(until)) return true;
        // Битая дата — пауза действует: записать лишнее хуже, чем не записать.
        return !TryParseUntil(until, out var last) || now.Date <= last.Date;
    }
}
