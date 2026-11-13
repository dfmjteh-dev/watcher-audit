using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using Watcher.Uploader;

namespace Watcher.TerminalAgent;

// 0.12: вывод идущей сессии кусками. watcher-term-wrap.sh пишет <id>.start в
// начале сессии и <id>.json в конце; пока .json нет, раз в интервал сервера
// (consoleLiveIntervalSec) уходит то, что script(1) дописал в .out/.timing с
// прошлого раза. Сколько отправлено — в LiveStateDir (переживает перезапуск
// агента; потеряется — сервер пропустит уже принятое, дублей не будет).
// Последний кусок — по .json, или, если shell убит и .json не появится (обрыв
// SSH), когда процесса из .start больше нет.
public sealed partial class UploadRunner
{
    // Не больше этого за один кусок: сессия, накопившая сотни мегабайт, пока
    // сервер был недоступен, уходит несколькими опросами подряд.
    private const int MaxChunkOutBytes = 8 * 1024 * 1024;
    private const int MaxChunkTimingBytes = 1024 * 1024;
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(10);
    private DateTime _lastProbe = DateTime.MinValue;

    private sealed class LiveState
    {
        public long OutOffset { get; set; }
        public long TimingOffset { get; set; }
        public DateTime LastSentUtc { get; set; }
    }

    // Принимает ли сервер куски, агент узнаёт из ответа на любую загрузку; если
    // загрузок давно не было, а сессии идут — спрашивает сам.
    private async Task ProbeLiveSupportAsync(ILogger logger, CancellationToken ct)
    {
        if (_client.LiveIntervalSec is not null || DateTime.UtcNow - _lastProbe < ProbeInterval) return;
        if (!Directory.Exists(_cfg.RawOutputDir) || Directory.GetFiles(_cfg.RawOutputDir, "*.start").Length == 0) return;
        _lastProbe = DateTime.UtcNow;
        await _client.ProbeAsync(Environment.MachineName, logger, ct);
    }

    private async Task UploadLiveOutputAsync(ILogger logger, CancellationToken ct)
    {
        if (!Directory.Exists(_cfg.RawOutputDir) || !UnixFile.IsDirectoryOwnedBySelf(_cfg.RawOutputSentDir)) return;
        foreach (string start in Directory.GetFiles(_cfg.RawOutputDir, "*.start"))
        {
            if (ct.IsCancellationRequested) break;
            string stem = Path.GetFileNameWithoutExtension(start);
            try { await LiveOneAsync(stem, start, logger, ct); }
            catch (Exception ex) { logger.Warning(ex, "live output upload failed for {Stem}, will retry", stem); }
        }
    }

    private async Task LiveOneAsync(string stem, string start, ILogger logger, CancellationToken ct)
    {
        string jsonPath = Path.Combine(_cfg.RawOutputDir, stem + ".json");
        string outPath = Path.Combine(_cfg.RawOutputDir, stem + ".out");
        string timingPath = Path.Combine(_cfg.RawOutputDir, stem + ".timing");
        var state = LoadState(stem);
        bool finished = File.Exists(jsonPath);
        if (finished && !IsQuiet(jsonPath)) return;
        if (!finished && !File.Exists(outPath)) return;   // script ещё не создал файлы

        // Без поддержки на сервере и без начатой отправки — закроет обычный путь по .json.
        if (_client.LiveIntervalSec is null && state is null) return;

        using var record = OpenOutputRecord(finished ? jsonPath : start, outPath, timingPath, stem, out string? reason);
        if (record is null)
        {
            if (_rejected.Add(start)) logger.Warning("reject {File}: {Reason}", Path.GetFileName(start), reason);
            return;
        }
        var meta = JsonNode.Parse(record.Meta) as JsonObject;
        if (meta is null) return;

        bool orphan = !finished && !ShellAlive(meta, record.Owner);
        bool closing = finished || orphan;
        state ??= new LiveState();
        int interval = _client.LiveIntervalSec ?? 60;
        if (!closing && DateTime.UtcNow - state.LastSentUtc < TimeSpan.FromSeconds(interval)) return;

        // .timing — только целыми строками; .out — до текущего конца (script
        // пишет .out раньше строки .timing о нём, вывод не отстаёт от тайминга).
        byte[] timing = ReadFrom(record.Timing, state.TimingOffset, MaxChunkTimingBytes, wholeLines: !closing);
        byte[] output = ReadFrom(record.Out, state.OutOffset, MaxChunkOutBytes, wholeLines: false);
        bool last = closing
            && state.OutOffset + output.Length >= record.Out.Length
            && state.TimingOffset + timing.Length >= record.Timing.Length;
        if (!last && !closing && output.Length == 0 && timing.Length == 0 && state.LastSentUtc != default)
        {
            state.LastSentUtc = DateTime.UtcNow;   // нового нет — не дёргаем сервер
            SaveState(stem, state);
            return;
        }

        meta["live"] = !last;
        meta["outOffset"] = state.OutOffset;
        meta["timingOffset"] = state.TimingOffset;
        if (!finished)
        {
            // Обрыв — конец по последней записи в .out; идёт — сейчас.
            meta["endedAt"] = (orphan ? File.GetLastWriteTimeUtc(outPath) : DateTime.UtcNow).ToString("o");
            meta.Remove("pid");
        }
        byte[] metaBytes = JsonSerializer.SerializeToUtf8Bytes(meta);
        var r = await _client.UploadOutputChunkAsync(metaBytes, stem + (last ? ".json" : ".start"), output, stem + ".out", timing, stem + ".timing", logger, ct);
        switch (r.Outcome)
        {
            case ChunkOutcome.Accepted when last:
            case ChunkOutcome.Closed:
                record.Dispose();
                foreach (string ext in new[] { ".json", ".out", ".timing" })
                {
                    string p = Path.Combine(_cfg.RawOutputDir, stem + ext);
                    if (File.Exists(p)) MoveToSent(p, _cfg.RawOutputSentDir);
                }
                DeleteStart(stem);
                if (orphan) logger.Information("closed {Stem}: shell is gone without writing .json (connection dropped?)", stem);
                break;
            case ChunkOutcome.Accepted:
                state.OutOffset = r.Long("outSize");
                state.TimingOffset = r.Long("timingSize");
                state.LastSentUtc = DateTime.UtcNow;
                SaveState(stem, state);
                break;
            case ChunkOutcome.Gap:
                // Сервер принял меньше — продолжаем с его размеров (файлы у нас целы).
                state.OutOffset = Math.Min(r.Long("outSize"), record.Out.Length);
                state.TimingOffset = Math.Min(r.Long("timingSize"), record.Timing.Length);
                SaveState(stem, state);
                break;
        }
    }

    private static byte[] ReadFrom(FileStream fs, long offset, int max, bool wholeLines)
    {
        long len = fs.Length;
        if (offset >= len) return Array.Empty<byte>();
        int n = (int)Math.Min(max, len - offset);
        var buf = new byte[n];
        fs.Seek(offset, SeekOrigin.Begin);
        fs.ReadExactly(buf);
        if (!wholeLines) return buf;
        int lastNl = Array.LastIndexOf(buf, (byte)'\n');
        return lastNl < 0 ? Array.Empty<byte>() : buf[..(lastNl + 1)];
    }

    // Shell сессии жив: процесс из .start есть и принадлежит владельцу записи
    // (pid могли переиспользовать — тогда скорее всего под другим uid). Нет
    // pid в .start — считаем живым, закроет .json.
    private static bool ShellAlive(JsonObject meta, uint owner)
    {
        if (meta["pid"] is not JsonValue v || !v.TryGetValue(out int pid) || pid <= 1) return true;
        try
        {
            foreach (string line in File.ReadLines($"/proc/{pid}/status"))
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                    return line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is [_, var uid, ..] && uid == owner.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return false;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private string StatePath(string stem) => Path.Combine(_cfg.LiveStateDir, stem + ".state");

    private bool HasLiveState(string stem) => File.Exists(StatePath(stem));

    private LiveState? LoadState(string stem)
    {
        try { return File.Exists(StatePath(stem)) ? JsonSerializer.Deserialize<LiveState>(File.ReadAllText(StatePath(stem))) : null; }
        catch (JsonException) { return null; }
    }

    private void SaveState(string stem, LiveState state)
    {
        Directory.CreateDirectory(_cfg.LiveStateDir);
        File.WriteAllText(StatePath(stem), JsonSerializer.Serialize(state));
    }

    private void DeleteStart(string stem)
    {
        try { File.Delete(Path.Combine(_cfg.RawOutputDir, stem + ".start")); } catch (IOException) { }
        try { File.Delete(StatePath(stem)); } catch (IOException) { }
    }
}
