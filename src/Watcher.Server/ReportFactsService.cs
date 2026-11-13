using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Watcher.Server;

// 0.14.4: заготовки отчётов (ReportFacts.cs, Db.ReportFacts.cs). Раз в
// Interval (и сразу по Wake — сменили типы программ) помечает сутки, куда пришли
// новые сегменты, и пересчитывает помеченные «хост × сутки». Сменилась зона
// сервера, таблица типов программ или формат фактов — всё заново.
public sealed class ReportFactsService : BackgroundService
{
    // Формат фактов: поменяли расчёт (ReportFacts.Compute, AppKinds.Defaults) —
    // увеличить, при старте всё пересчитается.
    public const int FactsVersion = 1;
    public const string StateKey = "report-facts-state";
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);

    private readonly Db _db;
    private readonly ILogger<ReportFactsService> _log;
    private readonly SemaphoreSlim _wake = new(0);
    // Фоновый проход и досчёт под запрос отчёта не идут одновременно.
    private readonly object _run = new();

    public ReportFactsService(Db db, ILogger<ReportFactsService> log)
    {
        _db = db;
        _log = log;
    }

    public sealed record State(int Version, string Zone, string KindsHash, long LastSegmentId, DateTime? ComputedAt);

    public void Wake() => _wake.Release();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { RunOnce(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "report facts: run failed"); }
            try { await _wake.WaitAsync(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public State? LoadState()
    {
        string? json = _db.GetSetting(StateKey);
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<State>(json); }
        catch (JsonException) { return null; }
    }

    private void SaveState(State s) => _db.SetSetting(StateKey, JsonSerializer.Serialize(s), null);

    internal static string KindsHash(Db db)
    {
        var sb = new StringBuilder(db.GetSetting(AppKinds.SettingKey) ?? "");
        foreach (var (p, k) in AppKinds.Defaults.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sb.Append('|').Append(p).Append('=').Append(k);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    public void RunOnce(CancellationToken ct = default)
    {
        lock (_run) RunOnceLocked(ct);
    }

    // Перед отчётом: пометить новые сегменты и досчитать помеченные сутки
    // периода [fromDay, toDay], не дольше budget — отчёт видит свежие записи, не
    // дожидаясь фонового прохода. Полный пересчёт (после обновления сервера) сюда
    // не входит — им занят фоновый проход, отчёт показывает pending.
    public void RefreshForReport(string fromDay, string toDay, TimeSpan budget)
    {
        if (!Monitor.TryEnter(_run, budget)) return;
        try
        {
            EnsureState();
            var map = AppKinds.Load(_db);
            var until = DateTime.UtcNow + budget;
            foreach (var (host, day) in _db.ReportDirty(10_000))
            {
                if (DateTime.UtcNow > until) break;
                if (string.CompareOrdinal(day, fromDay) < 0 || string.CompareOrdinal(day, toDay) > 0) continue;
                Recompute(host, day, map);
            }
        }
        finally { Monitor.Exit(_run); }
    }

    // Состояние заготовок: сменились зона, типы программ или формат — всё в
    // очередь заново; иначе — пометить сутки новых сегментов.
    private State EnsureState()
    {
        var state = LoadState();
        string zone = ServerClock.ZoneId, kinds = KindsHash(_db);
        if (state is null || state.Version != FactsVersion || state.Zone != zone || state.KindsHash != kinds)
        {
            _log.LogInformation("report facts: full rebuild (zone {Zone}, version {Version})", zone, FactsVersion);
            state = new State(FactsVersion, zone, kinds, _db.ResetReportFacts(), null);
        }
        else
        {
            state = state with { LastSegmentId = _db.MarkReportDirtySince(state.LastSegmentId) };
        }
        SaveState(state);
        return state;
    }

    private void RunOnceLocked(CancellationToken ct)
    {
        var state = EnsureState();

        var map = AppKinds.Load(_db);
        int done = 0;
        var started = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            var batch = _db.ReportDirty(200);
            if (batch.Count == 0) break;
            foreach (var (host, day) in batch)
            {
                if (ct.IsCancellationRequested) break;
                Recompute(host, day, map);
                done++;
            }
        }
        SaveState(state with { ComputedAt = DateTime.UtcNow });
        if (done > 0) _log.LogInformation("report facts: {Count} host-days in {Sec:F1} s", done, (DateTime.UtcNow - started).TotalSeconds);
    }

    private void Recompute(string host, string day, AppKinds.Map map)
    {
        var local = DateTime.ParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        // Смещение зоны в середине суток (переход на летнее время — ночью).
        var offset = TimeSpan.FromMinutes(ServerClock.OffsetMinutes(DateTime.SpecifyKind(local.AddHours(12), DateTimeKind.Utc)));
        var fromUtc = DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        var (segments, activity) = _db.ReportSourceForHostDay(host, fromUtc, fromUtc.AddDays(1));
        var facts = ReportFacts.Compute(segments, activity, local, local.AddDays(1), offset, map);
        _db.ReplaceReportFacts(host, day, facts);
    }
}
