using System.Globalization;
using System.Text;

namespace Watcher.Server;

// Состояние сервера на момент проверки — всё, что нужно оценщику, без I/O.
public sealed record AlertSnapshot(
    DateTime NowUtc,
    IReadOnlyList<InventoryHost> Hosts,
    (long Free, long Total)? Disk,
    double MinFreeDiskPercent,
    bool RetentionAlive,
    // 0.13: новые события журнала потерь с прошлой проверки, по хостам.
    IReadOnlyList<(string Host, int Count)>? NewLosses = null);

// Kind: host-silent | clock | dropped | disk | retention. Host — null у серверных.
public sealed record Alert(string Key, string Kind, string? Host, string Text);

// Итог одной проверки. Пусто — уведомлять не о чем.
public sealed record AlertCycle(IReadOnlyList<Alert> Fired, IReadOnlyList<Alert> Resolved, IReadOnlyList<Alert> Events, IReadOnlyList<Alert> Reminder)
{
    public bool IsEmpty => Fired.Count == 0 && Resolved.Count == 0 && Events.Count == 0 && Reminder.Count == 0;
}

// H4: что сработало, что решилось. Состояния (хост молчит, часы, диск, retention)
// уведомляются при срабатывании и при решении, плюс напоминание раз в RepeatHours.
// Рост dropped_segments — событие: уведомление на каждый рост, без «решено».
// Состояние в памяти: после перезапуска сервера активные алерты придут заново.
public sealed class AlertEvaluator
{
    private readonly AlertConfig _cfg;
    private readonly Dictionary<string, Alert> _firing = new();
    private readonly Dictionary<string, int> _lastDropped = new();
    private DateTime? _lastReminderUtc;

    public AlertEvaluator(AlertConfig cfg) => _cfg = cfg;

    public IReadOnlyCollection<Alert> Firing => _firing.Values;

    public AlertCycle Evaluate(AlertSnapshot s)
    {
        var current = new Dictionary<string, Alert>();
        var events = new List<Alert>();

        foreach (var h in s.Hosts)
        {
            if (_cfg.HostSilentHours > 0 && h.LastHeartbeatAt is { } last &&
                s.NowUtc - last > TimeSpan.FromHours(_cfg.HostSilentHours))
            {
                Add(current, new Alert($"host-silent:{h.Name}", "host-silent", h.Name,
                    $"хост {h.Name} молчит {Hours(s.NowUtc - last)} (последний heartbeat {last:yyyy-MM-dd HH:mm} UTC)"));
            }

            // Тот же порог, что «неверные часы» в «Хостах» (StationClock, R7).
            if (h.ClockSkewSec is { } skew && StationClock.IsSignificant(TimeSpan.FromSeconds(skew)))
            {
                Add(current, new Alert($"clock:{h.Name}", "clock", h.Name,
                    $"часы хоста {h.Name} {(skew > 0 ? "отстают" : "спешат")} на {Hours(TimeSpan.FromSeconds(Math.Abs(skew)))}"));
            }

            // Без прошлого значения — только запоминаем: рост виден со второй проверки.
            // Уменьшение (агент перезапущен) — новая точка отсчёта. Агент с журналом
            // потерь (0.13) — по событиям журнала ниже, не по счётчику.
            if (!h.LossJournal && _lastDropped.TryGetValue(h.Name, out int prev) && h.DroppedSegments > prev)
            {
                events.Add(new Alert($"dropped:{h.Name}", "dropped", h.Name,
                    $"агент {h.Name} удалил {h.DroppedSegments - prev} сегм. по квоте диска станции (всего {h.DroppedSegments}) — записи потеряны, на станции мало места"));
            }
            if (h.LastHeartbeatAt is not null) _lastDropped[h.Name] = h.DroppedSegments;
        }

        foreach (var (host, count) in s.NewLosses ?? [])
            events.Add(new Alert($"dropped:{host}", "dropped", host,
                $"агент {host} сообщил о {count} потерянн{(count == 1 ? "ой записи" : "ых записях")} — подробности и «Принято»: хост → «Инфо» → «Потери»"));

        if (_cfg.DiskLowPercent > 0 && s.Disk is { Total: > 0 } d)
        {
            double freePct = d.Free * 100.0 / d.Total;
            if (freePct < _cfg.DiskLowPercent)
            {
                string consequence = s.MinFreeDiskPercent > 0 && freePct < s.MinFreeDiskPercent
                    ? $"ниже {Pct(s.MinFreeDiskPercent)} — retention уже удаляет записи раньше срока"
                    : $"порог предупреждения {Pct(_cfg.DiskLowPercent)}";
                Add(current, new Alert("disk", "disk", null,
                    $"мало места на сервере: свободно {Pct(freePct)} ({Gb(d.Free)} из {Gb(d.Total)}), {consequence}"));
            }
        }

        if (!s.RetentionAlive)
            Add(current, new Alert("retention", "retention", null, "retention не проходит — старые записи не удаляются, см. лог сервера"));

        var fired = current.Where(kv => !_firing.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        var resolved = _firing.Where(kv => !current.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        _firing.Clear();
        foreach (var kv in current) _firing[kv.Key] = kv.Value;

        var reminder = new List<Alert>();
        if (fired.Count > 0) _lastReminderUtc = s.NowUtc;
        else if (_cfg.RepeatHours > 0 && _firing.Count > 0 &&
                 (_lastReminderUtc is null || s.NowUtc - _lastReminderUtc >= TimeSpan.FromHours(_cfg.RepeatHours)))
        {
            reminder.AddRange(_firing.Values.OrderBy(a => a.Key, StringComparer.Ordinal));
            _lastReminderUtc = s.NowUtc;
        }
        if (_firing.Count == 0) _lastReminderUtc = null;

        return new AlertCycle(Sorted(fired), Sorted(resolved), Sorted(events), reminder);
    }

    // Текст уведомления: серверные алерты первыми, хосты одного вида — одним
    // списком с обрезкой по MaxListedHosts.
    public string Format(AlertCycle c)
    {
        var sb = new StringBuilder();
        var head = new List<string>();
        if (c.Fired.Count > 0) head.Add($"новых: {c.Fired.Count}");
        if (c.Events.Count > 0) head.Add($"событий: {c.Events.Count}");
        if (c.Resolved.Count > 0) head.Add($"решено: {c.Resolved.Count}");
        if (c.Reminder.Count > 0) head.Add($"всё ещё активно: {c.Reminder.Count}");
        sb.Append(_cfg.Title).Append(" — ").Append(string.Join(", ", head)).Append('\n');
        Section(sb, "🔴", c.Fired);
        Section(sb, "⚠️", c.Events);
        Section(sb, "🔁", c.Reminder);
        Section(sb, "✅ решено:", c.Resolved);
        return sb.ToString().TrimEnd();
    }

    private void Section(StringBuilder sb, string mark, IReadOnlyList<Alert> alerts)
    {
        foreach (var group in alerts.GroupBy(a => a.Kind))
        {
            int shown = 0;
            foreach (var a in group)
            {
                if (a.Host is not null && shown >= _cfg.MaxListedHosts) break;
                sb.Append(mark).Append(' ').Append(a.Text).Append('\n');
                shown++;
            }
            int rest = group.Count() - shown;
            if (rest > 0) sb.Append(mark).Append(" … и ещё ").Append(rest).Append(" хост(ов) с тем же\n");
        }
    }

    private static void Add(Dictionary<string, Alert> d, Alert a) => d[a.Key] = a;

    private static List<Alert> Sorted(List<Alert> l) =>
        l.OrderBy(a => a.Host is null ? 0 : 1).ThenBy(a => a.Kind, StringComparer.Ordinal).ThenBy(a => a.Host, StringComparer.Ordinal).ToList();

    private static string Hours(TimeSpan t) => t.TotalHours >= 48
        ? $"{(int)t.TotalDays} дн."
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours} ч" : $"{Math.Max(1, (int)t.TotalMinutes)} мин";

    private static string Pct(double v) => v.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    private static string Gb(long bytes) => (bytes / 1024.0 / 1024 / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " ГБ";
}
