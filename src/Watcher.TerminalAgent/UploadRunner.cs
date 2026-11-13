using System.Text.Json;
using Serilog;
using Watcher.Uploader;

namespace Watcher.TerminalAgent;

// Сканирует queue/ (8a, terminal-input) и raw-output/ (8b, terminal-output)
// на готовые к выгрузке записи и грузит их через UploadClient. "Готова" —
// присутствует .json и (для 8b) оба .out/.timing, и файл не трогали минимум
// UploadQuietSeconds (защита от гонки с ещё дописывающим процессом — тем же
// приёмом, что Watcher.Uploader/QueueQuota.cs). Успешно выгруженное
// переносится в sent/, не удаляется — см. Config.cs.
public sealed partial class UploadRunner
{
    private readonly TerminalAgentConfig _cfg;
    private readonly UploadClient _client;
    private readonly HashSet<string> _rejected = new();
    private bool _sentDirWarned;

    public UploadRunner(TerminalAgentConfig cfg, UploadClient client)
    {
        _cfg = cfg;
        _client = client;
        Directory.CreateDirectory(_cfg.QueueSentDir);
        Directory.CreateDirectory(_cfg.RawOutputSentDir);
    }

    public async Task RunOnceAsync(ILogger logger, CancellationToken ct)
    {
        await ProbeLiveSupportAsync(logger, ct);
        await UploadInputRecordsAsync(logger, ct);
        await UploadLiveOutputAsync(logger, ct);
        await UploadOutputRecordsAsync(logger, ct);
    }

    private async Task UploadInputRecordsAsync(ILogger logger, CancellationToken ct)
    {
        foreach (string json in EnumerateJsonFiles(_cfg.QueueDir))
        {
            if (ct.IsCancellationRequested) break;
            if (!IsQuiet(json)) continue;
            try
            {
                // 0.12: кусок идущей сессии (SessionCollector, inputOffset/live) —
                // 416 значит, что предыдущий кусок ещё не дошёл: оставляем до
                // следующего опроса, он пойдёт раньше этого (порядок имён).
                var r = await _client.UploadInputChunkAsync(json, logger, ct);
                if (r.Outcome is ChunkOutcome.Accepted or ChunkOutcome.Closed)
                    MoveToSent(json, _cfg.QueueSentDir);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "input upload failed for {File}, will retry next poll", Path.GetFileName(json));
            }
        }
    }

    private async Task UploadOutputRecordsAsync(ILogger logger, CancellationToken ct)
    {
        if (!UnixFile.IsDirectoryOwnedBySelf(_cfg.RawOutputSentDir))
        {
            if (!_sentDirWarned)
                logger.Error("{Dir} is not a directory owned by the agent (pre-created by a user or a symlink?) — "
                    + "terminal-output upload is paused; re-run install-terminal-agent.sh", _cfg.RawOutputSentDir);
            _sentDirWarned = true;
            return;
        }
        _sentDirWarned = false;

        foreach (string json in EnumerateJsonFiles(_cfg.RawOutputDir))
        {
            if (ct.IsCancellationRequested) break;
            if (!IsQuiet(json)) continue;
            try
            {
                // Имена .out/.timing — из имени самого .json (как в
                // TerminalQueueQuota), не из его содержимого: .json пишет
                // пользователь, и "outFile":"/etc/shadow" или "../.." раньше
                // уходил прямо в Path.Combine — root читал и выгружал этот файл,
                // а потом File.Move уносил его в sent/ (R5).
                string stem = Path.GetFileNameWithoutExtension(json);
                // Запись, которую уже начали слать кусками (или сервер их
                // принимает), закрывает UploadLiveOutputAsync.
                if (File.Exists(Path.Combine(_cfg.RawOutputDir, stem + ".start")) && (_client.LiveIntervalSec is not null || HasLiveState(stem)))
                    continue;
                string outPath = Path.Combine(_cfg.RawOutputDir, stem + ".out");
                string timingPath = Path.Combine(_cfg.RawOutputDir, stem + ".timing");
                if (!File.Exists(outPath) || !File.Exists(timingPath))
                {
                    logger.Warning("skip {File} — .out/.timing sibling missing", Path.GetFileName(json));
                    continue;
                }

                using var record = OpenOutputRecord(json, outPath, timingPath, stem, out string? rejectReason);
                if (record is null)
                {
                    // Один раз на запись за время жизни процесса — иначе warning на
                    // каждом опросе. Запись остаётся на месте (её разберёт человек или
                    // удалит квота RawOutputMaxBytes), агент её не трогает.
                    if (_rejected.Add(json))
                        logger.Warning("reject {File}: {Reason}", Path.GetFileName(json), rejectReason);
                    continue;
                }

                if (await _client.UploadOutputAsync(record.Meta, Path.GetFileName(json), record.Out, Path.GetFileName(outPath),
                        record.Timing, Path.GetFileName(timingPath), logger, ct))
                {
                    record.Dispose();
                    MoveToSent(json, _cfg.RawOutputSentDir);
                    MoveToSent(outPath, _cfg.RawOutputSentDir);
                    MoveToSent(timingPath, _cfg.RawOutputSentDir);
                    DeleteStart(stem);
                }
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "output upload failed for {File}, will retry next poll", Path.GetFileName(json));
            }
        }
    }

    private const int MaxMetaBytes = 64 * 1024;

    private sealed class OutputRecord(byte[] meta, FileStream @out, FileStream timing, uint owner) : IDisposable
    {
        public byte[] Meta { get; } = meta;
        public uint Owner { get; } = owner;
        public FileStream Out { get; } = @out;
        public FileStream Timing { get; } = timing;

        public void Dispose()
        {
            Out.Dispose();
            Timing.Dispose();
        }
    }

    // R5: все три файла — обычные файлы (не симлинк, не FIFO), открыты без
    // прохода по симлинку, у всех один владелец, и это тот пользователь, что
    // указан в sessionUser. Без последнего пользователь мог бы подложить в
    // raw-output запись от имени другого (R10). Проверки — по открытым
    // дескрипторам, выгружаются ровно эти дескрипторы.
    private static OutputRecord? OpenOutputRecord(string jsonPath, string outPath, string timingPath, string stem, out string? reason)
    {
        byte[] meta;
        uint owner;
        using (var js = UnixFile.OpenRegularNoFollow(jsonPath, out owner, out reason))
        {
            if (js is null) { reason = ".json: " + reason; return null; }
            if (js.Length > MaxMetaBytes) { reason = ".json is too large"; return null; }
            meta = new byte[js.Length];
            js.ReadExactly(meta);
        }

        string? sessionUser;
        try
        {
            using var doc = JsonDocument.Parse(meta);
            var root = doc.RootElement;
            sessionUser = root.TryGetProperty("sessionUser", out var su) && su.ValueKind == JsonValueKind.String ? su.GetString() : null;
            // Поля outFile/timingFile пишет watcher-term-wrap.sh — они обязаны
            // совпадать с тем, что реально выгружается.
            if (!MatchesOrAbsent(root, "outFile", stem + ".out") || !MatchesOrAbsent(root, "timingFile", stem + ".timing"))
            {
                reason = "outFile/timingFile do not match the record name";
                return null;
            }
        }
        catch (JsonException)
        {
            reason = ".json is not valid JSON";
            return null;
        }
        if (string.IsNullOrEmpty(sessionUser))
        {
            reason = "sessionUser missing";
            return null;
        }
        if (UnixFile.LookupUid(sessionUser) != owner)
        {
            reason = $"sessionUser '{sessionUser}' is not the owner (uid {owner}) of the record";
            return null;
        }

        var outStream = OpenOwnedBy(outPath, owner, ".out", ref reason);
        if (outStream is null) return null;
        var timingStream = OpenOwnedBy(timingPath, owner, ".timing", ref reason);
        if (timingStream is null)
        {
            outStream.Dispose();
            return null;
        }
        return new OutputRecord(meta, outStream, timingStream, owner);
    }

    private static FileStream? OpenOwnedBy(string path, uint expectedOwner, string label, ref string? reason)
    {
        var fs = UnixFile.OpenRegularNoFollow(path, out uint owner, out string? error);
        if (fs is null)
        {
            reason = $"{label}: {error}";
            return null;
        }
        if (owner != expectedOwner)
        {
            fs.Dispose();
            reason = $"{label}: owner uid {owner} differs from .json owner uid {expectedOwner}";
            return null;
        }
        return fs;
    }

    private static bool MatchesOrAbsent(JsonElement root, string property, string expected) =>
        !root.TryGetProperty(property, out var v) || (v.ValueKind == JsonValueKind.String && v.GetString() == expected);

    private IEnumerable<string> EnumerateJsonFiles(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json") : Array.Empty<string>();

    private bool IsQuiet(string path) =>
        (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds >= _cfg.UploadQuietSeconds;

    private static void MoveToSent(string path, string sentDir) =>
        File.Move(path, Path.Combine(sentDir, Path.GetFileName(path)), overwrite: true);
}
