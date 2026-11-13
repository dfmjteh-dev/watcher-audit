using System.Globalization;

namespace Watcher.Server;

// R7 (docs/code-review-2026-10.md): startedAt — это часы станции, а по
// started_at работают retention и раскладка блобов по дням. Станция со
// сброшенной батарейкой BIOS (год 2000) теряла записи при первом же проходе
// retention, станция с датой в будущем не чистилась никогда.
//
// Сдвиг меряется не по startedAt против времени сервера — станция, неделю
// бывшая без сети, честно присылает недельные записи. Агент кладёт в каждый
// запрос своё текущее время (заголовок, см. Watcher.Uploader.AgentClock), и
// разница с сервером в этот момент и есть ошибка его часов. Если часы
// станции ошибаются больше чем на Tolerance, время записи сдвигается на эту
// разницу (в предположении, что ошибка та же, что и в момент записи), а
// исходное время станции сохраняется отдельно.
public static class StationClock
{
    public const string AgentTimeHeader = "X-Watcher-Agent-Time";

    // NTP-дрейф и задержка выгрузки — не повод переписывать время записи.
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(10);

    // serverNow − agentNow; null — заголовка нет (агент до R7) или он нечитаем.
    public static TimeSpan? MeasureSkew(HttpRequest request, DateTime serverNowUtc)
    {
        if (!request.Headers.TryGetValue(AgentTimeHeader, out var value)) return null;
        if (!DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var agentNow))
            return null;
        return serverNowUtc - agentNow;
    }

    public static bool IsSignificant(TimeSpan? skew) => skew is { } k && k.Duration() > Tolerance;

    // StationStartedAt != null — время записи исправлено, это исходное время станции.
    public static (DateTime StartedAt, DateTime EndedAt, DateTime? StationStartedAt) Correct(
        DateTime startedAt, DateTime endedAt, TimeSpan? skew, DateTime serverNowUtc)
    {
        DateTime started = startedAt, ended = endedAt;
        if (IsSignificant(skew))
        {
            started += skew!.Value;
            ended += skew.Value;
        }
        // Запись из будущего невозможна при любом сдвиге: часы были неверны в
        // момент записи и успели исправиться до выгрузки (или агент до R7 без
        // заголовка). Лучшее, что известно, — время выгрузки.
        if (started > serverNowUtc + Tolerance)
        {
            TimeSpan duration = ended > started ? ended - started : TimeSpan.Zero;
            ended = serverNowUtc;
            started = serverNowUtc - duration;
        }
        return started == startedAt ? (startedAt, endedAt, null) : (started, ended, startedAt);
    }
}
