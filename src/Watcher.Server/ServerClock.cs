namespace Watcher.Server;

// 0.14.4: время в UI и отчётах — в часовом поясе сервера (хоста, где установлен
// Watcher; в Docker — TZ из .env, install-server.sh берёт его у хоста). Браузер
// получает IANA-имя (/api/me) и форматирует в нём; отчёты считают дни и часы в
// той же зоне, а не в зоне смотрящего.
public static class ServerClock
{
    // IANA-имя для браузера. .NET на Linux берёт его из TZ или ссылки /etc/localtime;
    // если имя не IANA (в контейнере без TZ — «UTC»/«Etc/UTC») — всё равно годится Intl.
    public static string ZoneId => TimeZoneInfo.Local.HasIanaId ? TimeZoneInfo.Local.Id
        : TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : "UTC";

    // Смещение зоны сервера в момент utc, минуты (с учётом летнего времени).
    public static int OffsetMinutes(DateTime utc) => (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).TotalMinutes;
}
