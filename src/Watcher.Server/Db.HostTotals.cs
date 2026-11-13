using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// 0.14.4: итоги записей по хостам (сколько сегментов, объём, последний) для
// «Хостов» и «Обзора». Полный GROUP BY по segments на каждый запрос стоил
// 0,55 с на 280 тыс. строк прода (≈60 тыс. сегментов в сутки, при 30 днях
// хранения — секунды), а страницы обновляются сами раз в 15–30 с в каждой
// открытой вкладке. Здесь — итоги на момент последнего полного пересчёта плюс
// новые строки (id больше запомненного — дёшево по первичному ключу). Удаления
// (retention, удаление записей/хоста, переименование) сбрасывают кэш; размер,
// изменённый пережатием, догоняется полным пересчётом не реже FullRecompute.
public sealed record HostRecordTotals(int Count, long Bytes, DateTime? Last);

public sealed partial class Db
{
    internal static readonly TimeSpan FullRecompute = TimeSpan.FromMinutes(10);

    private readonly object _totalsLock = new();
    private Dictionary<string, HostRecordTotals>? _segTotals;
    private long _segTotalsMaxId;
    private DateTime _segTotalsAt;

    public void InvalidateHostTotals()
    {
        lock (_totalsLock) _segTotals = null;
    }

    // using var _ = TotalsChange(); — сбросить кэш по выходе из метода, который
    // удаляет или переносит сегменты (и при исключении — часть могла пройти).
    internal TotalsInvalidation TotalsChange() => new(this);

    internal readonly struct TotalsInvalidation(Db db) : IDisposable
    {
        public void Dispose() => db.InvalidateHostTotals();
    }

    public Dictionary<string, HostRecordTotals> SegmentTotalsByHost()
    {
        using var c = Open();
        lock (_totalsLock)
        {
            if (_segTotals is null || DateTime.UtcNow - _segTotalsAt > FullRecompute)
            {
                long maxId;
                using (var q = c.CreateCommand())
                {
                    q.CommandText = "SELECT COALESCE(MAX(id), 0) FROM segments";
                    maxId = Convert.ToInt64(q.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                }
                _segTotals = Aggregate(c, maxId, 0);
                _segTotalsMaxId = maxId;
                _segTotalsAt = DateTime.UtcNow;
            }
            var result = new Dictionary<string, HostRecordTotals>(_segTotals, StringComparer.Ordinal);
            foreach (var (host, t) in Aggregate(c, long.MaxValue, _segTotalsMaxId))
                result[host] = result.TryGetValue(host, out var b)
                    ? new HostRecordTotals(b.Count + t.Count, b.Bytes + t.Bytes, Max(b.Last, t.Last))
                    : t;
            return result;
        }

        static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;
    }

    private static Dictionary<string, HostRecordTotals> Aggregate(SqliteConnection c, long toId, long afterId)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT host, COUNT(*), COALESCE(SUM(size_bytes), 0), MAX(started_at)
FROM segments WHERE id > $after AND id <= $to GROUP BY host";
        cmd.Parameters.AddWithValue("$after", afterId);
        cmd.Parameters.AddWithValue("$to", toId);
        var map = new Dictionary<string, HostRecordTotals>(StringComparer.Ordinal);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetString(0)] = new HostRecordTotals(r.GetInt32(1), r.GetInt64(2),
                r.IsDBNull(3) ? null : DateTime.Parse(r.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind));
        return map;
    }

    // Цифры «Обзора», которым не нужна точность до секунды, а считаются они полным
    // проходом по segments: статистика пережатия — раз в минуту, средний суточный
    // прирост за 7 дней — раз в 10 минут (страница обновляется раз в 15 с).
    private (DateTime At, TranscodeStats Value)? _transcodeStats;
    private (DateTime At, long Value)? _avgDaily;

    public TranscodeStats CachedTranscodeStats()
    {
        lock (_totalsLock)
            if (_transcodeStats is { } t && DateTime.UtcNow - t.At < TimeSpan.FromMinutes(1)) return t.Value;
        var v = TranscodeStats();
        lock (_totalsLock) _transcodeStats = (DateTime.UtcNow, v);
        return v;
    }

    public long CachedAvgDailyBytes()
    {
        lock (_totalsLock)
            if (_avgDaily is { } t && DateTime.UtcNow - t.At < FullRecompute) return t.Value;
        long v = BytesSince(DateTime.UtcNow.AddDays(-7)) / 7;
        lock (_totalsLock) _avgDaily = (DateTime.UtcNow, v);
        return v;
    }
}
