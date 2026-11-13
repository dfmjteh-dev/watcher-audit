using System.Globalization;

namespace Watcher.Uploader;

// R7 (docs/code-review-2026-10.md): текущее время агента на каждом запросе к
// серверу. Сервер сравнивает его со своим и видит, насколько неверны часы
// станции (Watcher.Server/StationClock.cs), — по startedAt записи этого не
// понять: запись могла пролежать в очереди неделю.
public static class AgentClock
{
    public const string Header = "X-Watcher-Agent-Time";

    public static void Stamp(HttpRequestMessage request) =>
        request.Headers.TryAddWithoutValidation(Header, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
}
