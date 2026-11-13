using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace Watcher.Server;

// Пережатие записей на сервере — см. Db.Transcode.cs. Пока есть очередь —
// без пауз, Workers файлов параллельно по Threads потоков, ffmpeg с
// наименьшим приоритетом (nice 19: веб и приём записей берут CPU первыми);
// очередь пуста — проверка раз в IntervalMinutes.
//
// Настройки по умолчанию — по замеру на 120 сегментах прода (2026-10-19,
// средний ~1,2 МБ): SVT-AV1 4.2 (статический ffmpeg, Dockerfile) пресет 7 crf 41
// — ×4,8 к x264 агента при SSIM 0,991 (прежние 1.4.1/8/35: ×3,5, 0,994; мелкий
// текст 1С читается так же) и в 2,5 раза меньше CPU; один поток на файл и файлы параллельно — быстрее, чем
// несколько потоков на один короткий файл.
public sealed class TranscodeConfig
{
    public bool Enabled { get; set; } = true;
    public string FfmpegPath { get; set; } = "ffmpeg";
    public int IntervalMinutes { get; set; } = 60;
    // Один прогон — не дольше (итог в журнал, «Обзор»); очередь осталась —
    // следующий сразу.
    public int MaxRunMinutes { get; set; } = 50;
    // Не раньше, чем через столько минут после приёма: свежую запись чаще
    // всего смотрят сразу, пусть смотрят исходник.
    public int MinAgeMinutes { get; set; } = 30;
    // SVT-AV1: CRF (выше — меньше файл), Preset (0–13, выше — быстрее).
    public int Crf { get; set; } = 41;
    public int Preset { get; set; } = 7;
    // Потоков SVT-AV1 на один файл.
    public int Threads { get; set; } = 1;
    // Верхняя граница; фактическая выдача дополнительно ограничена TranscodeMemoryGuard.
    // Файлов одновременно; 0 — по серверу: ядер − 1 (одно — вебу и приёму,
    // хотя nice 19 и так уступает), не больше, чем влезает в память
    // (MemoryPerWorkerMb на процесс ffmpeg: 720p — ~430 МБ, два монитора
    // больше). 0.14.0 брал один файл за раз — на 8 ядрах было занято ~2,5.
    public int Workers { get; set; } = 0;
    public int MemoryPerWorkerMb { get; set; } = 600;
    public int EffectiveWorkers => Workers > 0 ? Workers : AutoWorkers(Environment.ProcessorCount,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Threads, MemoryPerWorkerMb);

    // Ядра — с учётом потоков на файл; память — доступная процессу (в Docker —
    // лимит контейнера), четверть — серверу и кэшу.
    public static int AutoWorkers(int cores, long availableBytes, int threads, int memoryPerWorkerMb)
    {
        int byCpu = Math.Max(1, (cores - 1) / Math.Max(1, threads));
        long byMem = availableBytes > 0 ? availableBytes * 3 / 4 / (Math.Max(64, memoryPerWorkerMb) * 1048576L) : byCpu;
        return (int)Math.Max(1, Math.Min(byCpu, byMem));
    }
    // Карантин удаляется только отдельным admin-действием после истечения срока.
    public int QuarantineDays { get; set; } = 7;
    public long QuarantineMaxBytes { get; set; } = 10L * 1024 * 1024 * 1024;
    // Пережатие с меньшей экономией оставляет исходник.
    public int MinSavingPercent { get; set; } = 10;
}

// Что делает служба сейчас — для «Обзора» (InventoryEndpoints, /api/overview).
// Счётчики по записям — из БД (Db.TranscodeStats), здесь только то, чего в БД нет.
public sealed class TranscodeState
{
    private readonly object _lock = new();
    private string? _disabledReason;
    private DateTime? _runStartedAt, _nextRunAt, _lastRunAt;
    private int _runDone;
    private long _runBefore, _runAfter;
    private TranscodeService.RunResult? _lastRun;
    private TranscodeMemoryStatus? _memory;
    public void Memory(TranscodeMemoryStatus memory) { lock (_lock) _memory = memory; }

    public void Disabled(string reason) { lock (_lock) _disabledReason = reason; }
    public void Ready() { lock (_lock) _disabledReason = null; }
    // Первый запуск — не сразу после старта сервера (см. ExecuteAsync): «ждёт до…».
    public void Waiting(DateTime next) { lock (_lock) _nextRunAt = next; }
    public void RunStarted(DateTime now) { lock (_lock) { _runStartedAt = now; _runDone = 0; _runBefore = 0; _runAfter = 0; } }
    public void SegmentDone(long before, long after) { lock (_lock) { _runDone++; _runBefore += before; _runAfter += after; } }
    public void RunFinished(TranscodeService.RunResult r, DateTime now, DateTime next)
    {
        lock (_lock) { _runStartedAt = null; _lastRun = r; _lastRunAt = now; _nextRunAt = next; }
    }

    public object Snapshot()
    {
        lock (_lock)
            return new
            {
                disabledReason = _disabledReason,
                memory = _memory,
                running = _runStartedAt is not null,
                runStartedAt = _runStartedAt?.ToString("o"),
                runDone = _runDone,
                runSavedBytes = _runBefore - _runAfter,
                lastRunAt = _lastRunAt?.ToString("o"),
                lastRun = _lastRun is null ? null : new
                {
                    done = _lastRun.Done,
                    kept = _lastRun.Kept,
                    failed = _lastRun.Failed,
                    savedBytes = _lastRun.BytesBefore - _lastRun.BytesAfter,
                },
                nextRunAt = _nextRunAt?.ToString("o"),
            };
    }
}

// Шов для тестов: настоящий ffmpeg в CI не нужен.
public interface IVideoTranscoder
{
    // null — можно работать; иначе причина, почему нельзя (нет ffmpeg/AV1).
    Task<string?> CheckAsync(CancellationToken ct);
    Task<bool> EncodeAsync(string input, string output, CancellationToken ct);
    Task<double?> DurationAsync(string path, CancellationToken ct);
    Task<VideoInspection> InspectAsync(string path, bool fullDecode, CancellationToken ct) => Task.FromResult(new VideoInspection(null, "inspection-unavailable"));
    Task<bool> RemuxAsync(string input, string output, CancellationToken ct) => Task.FromResult(false);
    Task<EncodeResult> EncodeCheckedAsync(string input, string output, CancellationToken ct) => EncodeCompatAsync(this, input, output, ct);
    private static async Task<EncodeResult> EncodeCompatAsync(IVideoTranscoder self, string input, string output, CancellationToken ct) =>
        new(await self.EncodeAsync(input, output, ct), "encoder", null);

}

public sealed record VideoInspection(bool? Valid, string Reason, string? Error = null, double? Duration = null);
public sealed record EncodeResult(bool Ok, string Reason, string? Error);

public sealed partial class TranscodeService : BackgroundService
{
    private readonly Db _db;
    private readonly ServerConfig _cfg;
    private readonly IVideoTranscoder _transcoder;
    private readonly ILogger<TranscodeService> _log;
    private readonly TranscodeState _state;
    private readonly TranscodeMemoryGuard _memory;

    public TranscodeService(Db db, ServerConfig cfg, IVideoTranscoder transcoder, ILogger<TranscodeService> log, TranscodeState? state = null, ITranscodeMemorySource? memory = null)
    {
        _db = db;
        _cfg = cfg;
        _transcoder = transcoder;
        _log = log;
        _state = state ?? new TranscodeState();
        _memory = new(memory ?? new LinuxTranscodeMemorySource(), cfg.Transcode.MemoryPerWorkerMb);
    }

    private TranscodeConfig T => _cfg.Transcode;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!T.Enabled) { _state.Disabled("выключено в настройках (Server:Transcode:Enabled)"); return; }
        // Не сразу после старта: обновление из веб-UI (ServerUpdate.cs) до 5 минут
        // проверяет новую версию и может откатить БД из снимка — пережатое за это
        // время (исходники удалены) осталось бы в старой БД без файлов.
        _state.Waiting(DateTime.UtcNow.AddMinutes(15));
        try { await Task.Delay(TimeSpan.FromMinutes(15), ct); }
        catch (OperationCanceledException) { return; }
        if (await _transcoder.CheckAsync(ct) is { } reason)
        {
            _log.LogWarning("transcode: disabled — {Reason}", reason);
            _state.Disabled(reason);
            return;
        }
        _state.Ready();
        try
        {
            int parts = SweepStaleParts();
            if (parts > 0) _log.LogInformation("transcode: removed {Count} stale .part files", parts);
            ReconcileOriginals(null);
        }
        catch (Exception ex) { _log.LogError(ex, "transcode startup cleanup failed"); }
        var interval = TimeSpan.FromMinutes(Math.Max(5, T.IntervalMinutes));
        while (!ct.IsCancellationRequested)
        {
            bool backlog = false;
            try
            {
                var r = await RunOnceAsync(ct);
                // Прогон кончился по MaxRunMinutes, а очередь есть — сразу следующий:
                // пауза только когда пережимать нечего (nice 19 и так отдаёт CPU всем).
                backlog = r.Done + r.Kept + r.Failed > 0 && _db.TranscodePendingCount() > 0;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "transcode run failed"); }
            if (_memory.Status.Reason != "ready" && _db.TranscodePendingCount() > 0)
            {
                _state.Waiting(DateTime.UtcNow.AddSeconds(5));
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }
            if (backlog) continue;
            var retryAt = _db.NextTranscodeRetry();
            var delay = retryAt is { } at ? TimeSpan.FromSeconds(Math.Max(5, Math.Min(interval.TotalSeconds, (at - DateTime.UtcNow).TotalSeconds))) : interval;
            _state.Waiting(DateTime.UtcNow.Add(delay));
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public sealed record RunResult(int Done, int Kept, int Failed, long BytesBefore, long BytesAfter);

    public async Task<RunResult> RunOnceAsync(CancellationToken ct, DateTime? nowUtc = null)
    {
        DateTime now = nowUtc ?? DateTime.UtcNow;
        DateTime runStarted = DateTime.UtcNow;
        _state.RunStarted(runStarted);

        var budget = Stopwatch.StartNew();
        var maxRun = TimeSpan.FromMinutes(Math.Max(1, T.MaxRunMinutes));
        int done = 0, kept = 0, failed = 0;
        long before = 0, after = 0;
        var sync = new object();
        int workers = T.EffectiveWorkers;
        DateTime cut = now.AddMinutes(-Math.Max(0, T.MinAgeMinutes));
        int page = 10 * workers;
        // Непрерывный пул (0.14.3, bug-watcher.md): раньше пачка из 10×workers
        // шла через Parallel.ForEachAsync, и следующая выбиралась только после
        // самого долгого файла пачки — на проде ~3 мин из 5 ядра простаивали.
        // Теперь продюсер подаёт кандидатов в ограниченный канал, воркеры берут
        // следующий, как только освободились. Файлы в работе ещё «в очереди» в
        // БД — повторно не выдаются (seen); в работе и в канале их не больше
        // 3×workers, страница — 10×workers, так что свежие на ней всегда есть.
        // По бюджету продюсер перестаёт выдавать, а уже начатые дорабатываются.
        var queue = Channel.CreateBounded<TranscodeCandidate>(new BoundedChannelOptions(2 * workers) { SingleWriter = true });
        // Воркер упал — продюсер не должен навсегда встать на полном канале.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producer = Task.Run(async () =>
        {
            var seen = new HashSet<long>();
            try
            {
                while (budget.Elapsed < maxRun && !stop.IsCancellationRequested)
                {
                    var raw = _db.TranscodeCandidates(cut, page, now);
                    var fresh = raw.Where(c => seen.Add(c.Id)).ToList();
                    if (fresh.Count == 0)
                    {
                        if (raw.Count < page) break;   // остались только те, что в работе
                        await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
                        continue;
                    }
                    foreach (var c in fresh)
                    {
                        if (budget.Elapsed >= maxRun) break;
                        await queue.Writer.WriteAsync(c, stop.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested && !ct.IsCancellationRequested) { }
            finally { queue.Writer.TryComplete(); }
        }, ct);
        var pool = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            try
            {
                await foreach (var cand in queue.Reader.ReadAllAsync(stop.Token))
                {
                    if (budget.Elapsed >= maxRun) continue;   // бюджет вышел — остаток канала не берём
                    TranscodeMemoryGuard.Lease? slot;
                    while ((slot = _memory.TryAcquire(workers, ct)) is null)
                    {
                        if (budget.Elapsed >= maxRun) break;
                        await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
                    }
                    if (slot is null) continue;
                    using var memorySlot = slot;
                    if (budget.Elapsed >= maxRun) continue;
                    (Outcome, long) r;
                    try { r = await TranscodeOneAsync(cand, slot.Token, now); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && slot.Token.IsCancellationRequested)
                    {
                        _log.LogInformation("transcode: segment {Id} deferred due to memory pressure", cand.Id);
                        continue;
                    }
                    lock (sync)
                    {
                        switch (r)
                        {
                            case (Outcome.Done, long size):
                                done++; before += cand.SizeBytes; after += size;
                                _state.SegmentDone(cand.SizeBytes, size);
                                break;
                            case (Outcome.Kept, _): kept++; break;
                            case (Outcome.Failed, _): failed++; break;
                        }
                    }
                }
            }
            catch
            {
                stop.Cancel();
                throw;
            }
        }, ct)).ToList();
        using var monitoring = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = MonitorMemoryAsync(workers, monitoring.Token);
        try { await Task.WhenAll(pool.Append(producer)); }
        finally
        {
            monitoring.Cancel();
            await monitor;
            _memory.CheckPressure(workers);
            _state.Memory(_memory.Status);
        }
        if (done + kept + failed > 0)
            _log.LogInformation("transcode: {Done} done ({BeforeMb:F1} MB → {AfterMb:F1} MB), {Kept} kept as is, {Failed} failed, {Pending} pending",
                done, before / 1048576.0, after / 1048576.0, kept, failed, _db.TranscodePendingCount());
        // Исходники, которые этот прогон не смог удалить (IOException), — ещё раз.
        if (done > 0) ReconcileOriginals(runStarted.AddMinutes(-1));
        var result = new RunResult(done, kept, failed, before, after);
        _state.RunFinished(result, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(Math.Max(5, T.IntervalMinutes)));
        return result;
    }

    private async Task MonitorMemoryAsync(int workers, CancellationToken ct)
    {
        TranscodeMemoryStatus? previous = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _memory.CheckPressure(workers);
                var status = _memory.Status;
                _state.Memory(status);
                if (previous?.Reason != status.Reason || previous?.EffectiveWorkers != status.EffectiveWorkers)
                    _log.LogInformation("transcode memory: {Reason}, available={Available}, total={Total}, reserve={Reserve}, childRss={ChildRss}, workerEstimate={Estimate}, active={Active}, effective={Effective}",
                        status.Reason, status.AvailableBytes, status.TotalBytes, status.ReserveBytes,
                        status.ChildRssBytes, status.WorkerEstimateBytes, status.ActiveWorkers, status.EffectiveWorkers);
                previous = status;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private enum Outcome { Done, Kept, Failed, Skipped }

    private async Task<(Outcome, long)> TranscodeOneAsync(TranscodeCandidate cand, CancellationToken ct, DateTime now)
    {
        using var memoryScope = LinuxTranscodeMemorySource.EnterWorker();
        var lease = _db.ClaimTranscode(cand, now);
        if (lease is null) return (Outcome.Skipped, 0);
        string? part = null;
        try
        {
            string src = DataPath(cand.FilePath);
            string newRel = NewRelPath(cand.FilePath);
            string dst = DataPath(newRel);
            part = BlobFiles.PartPath(dst);
            if (!File.Exists(src)) return await HandleFailureAsync(cand, lease, "file-missing", null, now, ct);
            var encoded = await _transcoder.EncodeCheckedAsync(src, part, ct);
            ct.ThrowIfCancellationRequested();
            if (!encoded.Ok || !File.Exists(part))
                return await HandleFailureAsync(cand, lease, encoded.Reason, encoded.Error, now, ct);
            double? dIn = await DurationAsync(src, ct), dOut = await DurationAsync(part, ct);
            if (dIn is null || dOut is null || Math.Abs(dIn.Value - dOut.Value) > Math.Max(1.0, dIn.Value * 0.02))
                return await HandleFailureAsync(cand, lease, "duration-mismatch", $"input={dIn}, output={dOut}", now, ct);
            long size = new FileInfo(part).Length;
            if (size <= 0 || size > cand.SizeBytes * (100 - Math.Clamp(T.MinSavingPercent, 0, 99)) / 100)
            {
                return _db.FinishTranscodeIssue(cand, lease, "kept", "no-saving", null, now, failure: false) ? (Outcome.Kept, 0) : (Outcome.Skipped, 0);
            }
            string sha = await Sha256Async(part, ct);
            BlobFiles.Commit(part, dst);
            if (!_db.CommitTranscode(cand.Id, cand.FilePath, new TranscodedFile(newRel, sha, size), now))
            {
                BlobFiles.TryDelete(dst);
                return (Outcome.Skipped, 0);
            }
            _db.ReleaseTranscode(cand.Id, lease.Token, done: true);
            BlobFiles.TryDelete(src);
            return (Outcome.Done, size);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ScheduleFailure(cand, lease, "storage", ex.Message, now);
        }
        finally
        {
            BlobFiles.TryDelete(part);
            _db.PauseHeldTranscode(cand.Id, lease.Token, now);
            _db.ReleaseTranscode(cand.Id, lease.Token);
        }
    }

    private Task<double?> DurationAsync(string path, CancellationToken ct) => _transcoder.DurationAsync(path, ct);

    // Оба варианта файла записи: исходный и пережатый рядом. Удалять запись —
    // оба (после падения посреди пережатия на диске может остаться любой).
    public static IEnumerable<string> FileVariants(string relPath)
    {
        yield return relPath;
        yield return relPath.EndsWith(".av1.mp4", StringComparison.Ordinal) ? OriginalRelPath(relPath) : NewRelPath(relPath);
    }

    public static string OriginalRelPath(string av1RelPath) => av1RelPath[..^".av1.mp4".Length] + ".mp4";

    // Сверка после падения: строка уже «пережата», а исходник ещё лежит (сервер
    // упал между записью в БД и удалением, или удалить не удалось) — удаляем.
    // full — все пережатые (при старте службы), иначе — пережатые с since.
    public int ReconcileOriginals(DateTime? since)
    {
        int removed = 0;
        foreach (string rel in _db.TranscodedFilePaths(since))
        {
            if (!rel.EndsWith(".av1.mp4", StringComparison.Ordinal)) continue;
            string orig;
            try
            {
                orig = DataPath(OriginalRelPath(rel));
            }
            catch (IOException) { continue; }
            try
            {
                if (!File.Exists(orig)) continue;
                File.Delete(orig);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning("transcode: failed to delete original {File}: {Err}", orig, ex.Message);
            }
        }
        if (removed > 0) _log.LogInformation("transcode: removed {Count} originals left after interrupted runs", removed);
        return removed;
    }

    // .part от ffmpeg, убитого вместе с сервером, — при старте службы, когда
    // своих пережатий в работе ещё нет (загрузки агентов пишут другие .part).
    public int SweepStaleParts()
    {
        string blobs = Path.Combine(_cfg.DataDir, "blobs");
        if (!Directory.Exists(blobs)) return 0;
        int n = 0;
        foreach (string f in Directory.EnumerateFiles(blobs, "*.av1.mp4.*" + BlobFiles.PartSuffix, SearchOption.AllDirectories))
        {
            try { File.Delete(f); n++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return n;
    }

    // blobs/host/day/name.mp4 → blobs/host/day/name.av1.mp4: рядом с исходным,
    // та же раскладка по дням (retention, бэкап, удаление хоста не меняются).
    public static string NewRelPath(string relPath)
    {
        string dir = Path.GetDirectoryName(relPath)?.Replace('\\', '/') ?? "";
        string name = Path.GetFileNameWithoutExtension(relPath) + ".av1.mp4";
        return dir.Length == 0 ? name : dir + "/" + name;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
    }
}

public sealed class FfmpegTranscoder : IVideoTranscoder
{
    private readonly ServerConfig _cfg;
    private readonly ILogger<FfmpegTranscoder> _log;

    public FfmpegTranscoder(ServerConfig cfg, ILogger<FfmpegTranscoder> log)
    {
        _cfg = cfg;
        _log = log;
    }

    private readonly SemaphoreSlim _inspectionSlots = new(2, 2);
    private TranscodeConfig T => _cfg.Transcode;

    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        var (code, stdout, _) = await RunAsync(T.FfmpegPath, ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(30), ct);
        if (code is null) return $"ffmpeg not found ({T.FfmpegPath})";
        return stdout.Contains("libsvtav1", StringComparison.Ordinal) ? null : "ffmpeg has no libsvtav1 encoder";
    }

    // Тот же mp4 с индексом в начале (faststart), что принимает сервер от
    // агента, — плеер (<video> с Range) работает с ним так же. -f mp4 — выход
    // во временный .part, по расширению ffmpeg формат не угадает.
    public static string[] EncodeArgs(string input, string output, TranscodeConfig t) =>
    [
        "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
        "-i", input,
        "-map", "0:v:0", "-an",
        "-c:v", "libsvtav1",
        "-preset", t.Preset.ToString(CultureInfo.InvariantCulture),
        "-crf", t.Crf.ToString(CultureInfo.InvariantCulture),
        // scm — режим экранного контента (текст, окна), lp — потоков на файл.
        "-svtav1-params", $"lp={Math.Max(1, t.Threads)}:scm=1",
        "-pix_fmt", "yuv420p",
        "-movflags", "+faststart",
        "-f", "mp4", output,
    ];

    public async Task<bool> EncodeAsync(string input, string output, CancellationToken ct) => (await EncodeCheckedAsync(input, output, ct)).Ok;

    public async Task<EncodeResult> EncodeCheckedAsync(string input, string output, CancellationToken ct)
    {
        var (code, _, error) = await RunAsync(T.FfmpegPath, EncodeArgs(input, output, T), TimeSpan.FromMinutes(60), ct, lowPriority: true);
        return new(code == 0, code is null ? (error == "timeout" ? "timeout" : "encoder-unavailable") : "encoder", Db.ClipTranscodeError(error));
    }

    public async Task<VideoInspection> InspectAsync(string path, bool fullDecode, CancellationToken ct)
    {
        if (!File.Exists(path)) return new(null, "file-missing");
        await _inspectionSlots.WaitAsync(ct);
        try
        {
            var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-xerror", "-i", path, "-map", "0:v:0", "-an" };
            if (!fullDecode) args.AddRange(["-frames:v", "1"]);
            args.AddRange(["-f", "null", "-"]);
            var (code, _, error) = await RunAsync(T.FfmpegPath, args, TimeSpan.FromMinutes(fullDecode ? 60 : 1), ct, lowPriority: true);
            if (code is null) return new(null, error == "timeout" ? "inspection-timeout" : "inspection-unavailable", Db.ClipTranscodeError(error));
            if (code != 0)
            {
                // Ошибки доступа и ресурсов не доказывают повреждение содержимого.
                bool unavailable = error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
                    || error.Contains("No such file", StringComparison.OrdinalIgnoreCase) || error.Contains("Cannot allocate memory", StringComparison.OrdinalIgnoreCase)
                    || error.Contains("Resource temporarily unavailable", StringComparison.OrdinalIgnoreCase);
                // Неизвестный кодек/декодер и прочие ошибки среды не являются доказательством порчи.
                bool corrupt = new[] { "moov atom not found", "Invalid data found when processing input", "does not contain any stream", "matches no streams", "corrupt", "Invalid NAL", "Error while decoding", "Invalid packet", "partial file" }
                    .Any(value => error.Contains(value, StringComparison.OrdinalIgnoreCase));
                return new(!unavailable && corrupt ? false : null, !unavailable && corrupt ? "invalid-source" : "inspection-unavailable", Db.ClipTranscodeError(error));
            }
            double? duration = Mp4Duration.Read(path);
            return duration is > 0 ? new(true, "valid", null, duration) : new(false, "invalid-duration");
        }
        finally { _inspectionSlots.Release(); }
    }

    public async Task<bool> RemuxAsync(string input, string output, CancellationToken ct)
    {
        var (code, _, _) = await RunAsync(T.FfmpegPath,
            ["-nostdin", "-y", "-hide_banner", "-loglevel", "error", "-i", input, "-map", "0:v:0", "-c", "copy", "-an", "-movflags", "+faststart", "-f", "mp4", output],
            TimeSpan.FromMinutes(10), ct, lowPriority: true);
        return code == 0;
    }

    // Длительность — из заголовка mp4 (Mp4Duration), без процесса ffprobe (~5%
    // времени на файл, лишний бинарник в образе). Не разобрался —
    // null — независимая проверка источника и ограниченные повторы.
    public Task<double?> DurationAsync(string path, CancellationToken ct) => Task.FromResult(Mp4Duration.Read(path));

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int capacity)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
            if (result.Length < capacity) result.Append(buffer, 0, Math.Min(read, capacity - result.Length));
        return result.ToString();
    }

    // null в коде — процесс не запустился (нет файла) или убит по таймауту.
    private static async Task<(int? Code, string Stdout, string Stderr)> RunAsync(
        string file, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct, bool lowPriority = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (null, "", ""); }
        using (p)
        {
            LinuxTranscodeMemorySource.Track(p);
            try
            {
                if (lowPriority)
                {
                    // nice 19: веб и приём записей важнее, ffmpeg берёт только простой CPU.
                    try { p.PriorityClass = ProcessPriorityClass.Idle; }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException) { }
                }
                var stdout = ReadBoundedAsync(p.StandardOutput, 65536);
                var stderr = ReadBoundedAsync(p.StandardError, 4096);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                try { await p.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    await p.WaitForExitAsync(CancellationToken.None);
                    await Task.WhenAll(stdout, stderr);
                    ct.ThrowIfCancellationRequested();
                    return (null, "", "timeout");
                }
                return (p.ExitCode, await stdout, await stderr);
            }
            finally { LinuxTranscodeMemorySource.Untrack(p); }
        }
    }
}
