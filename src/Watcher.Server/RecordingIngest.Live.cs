using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Watcher.Server;

// 0.12: консольная сессия кусками, пока она идёт (раньше — только целиком
// после закрытия: сессию, открытую неделю или зависшую из-за сети, всё это
// время не было видно). Агент шлёт кусок раз в consoleLiveIntervalSec и
// последний — при закрытии сессии:
//   ввод  — inputOffset (сколько символов ввода агент уже отправлял) + input
//           с этого места;
//   вывод — outOffset/timingOffset (сколько байт .out/.timing уже отправлено)
//           + файлы с этого места.
// Смещение меньше уже принятого — повтор (агент перезапустился или не
// дождался ответа): уже принятая часть пропускается, дублей нет. Больше
// принятого — кусок потерян: 416 с принятыми размерами, агент продолжает с
// них. Запись, закрытая последним куском, дальше не меняется (409).
public sealed partial class RecordingIngest
{
    // Паузы между выводом длиннее этого не считаются активным временем.
    public const double ActiveGapSec = 60;

    // Как часто агент шлёт незакрытую сессию — consoleLiveIntervalSec из конфига
    // группы/хоста. Уходит агенту заголовком каждого ответа /api/upload-terminal
    // (у консольного агента нет heartbeat); заголовок же говорит агенту, что
    // сервер принимает куски — старому серверу агент шлёт сессию целиком.
    public const string LiveIntervalHeader = "X-Watcher-Live-Interval";
    public const int DefaultLiveIntervalSec = 60;

    public static int LiveIntervalFor(string? effectiveConfigJson)
    {
        if (string.IsNullOrEmpty(effectiveConfigJson)) return DefaultLiveIntervalSec;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(effectiveConfigJson);
            if (doc.RootElement.TryGetProperty("consoleLiveIntervalSec", out var v) && v.TryGetInt32(out int sec)
                && sec >= AgentConfigLimits.ConsoleLiveIntervalSec.Min && sec <= AgentConfigLimits.ConsoleLiveIntervalSec.Max)
                return sec;
        }
        catch (System.Text.Json.JsonException) { }
        return DefaultLiveIntervalSec;
    }

    // 0.13: срок хранения невыгруженного на станции (queueMaxAgeDays группы/хоста,
    // иначе 30) — консольному агенту тем же способом, заголовком ответа (heartbeat
    // у него нет; видео-агент берёт его из кэша конфига heartbeat).
    public const string QueueMaxAgeHeader = "X-Watcher-Queue-Max-Age";

    public static int QueueMaxAgeFor(string? effectiveConfigJson)
    {
        if (string.IsNullOrEmpty(effectiveConfigJson)) return AgentConfigLimits.DefaultQueueMaxAgeDays;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(effectiveConfigJson);
            if (doc.RootElement.TryGetProperty("queueMaxAgeDays", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
                && v.TryGetInt32(out int d) && AgentConfigLimits.QueueMaxAgeDays.Contains(d))
                return d;
        }
        catch (System.Text.Json.JsonException) { }
        return AgentConfigLimits.DefaultQueueMaxAgeDays;
    }

    // Куски одной записи — по очереди (дописывание файла + строка БД);
    // разные записи не ждут друг друга.
    private static readonly SemaphoreSlim[] ChunkLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private async Task<IngestResult> TerminalChunkAsync(TerminalMetaFields tm, string host, bool timingOptional,
        TimeSpan? clockSkew, DateTime receivedAt, UploadPart? outPart, UploadPart? timingPart, IReadOnlyList<ServiceRule> serviceRules)
    {
        bool live = tm.Live!.Value;
        var (startedAt, endedAt, correctedFrom) = StationClock.Correct(tm.StartedAt, tm.EndedAt, clockSkew, receivedAt);
        string externalId;
        if (tm.Kind == "terminal-input")
        {
            externalId = $"{host}:in:{tm.SessionId}:{tm.StartedAt:yyyyMMddTHHmmss.fffZ}";
        }
        else
        {
            bool hasTiming = !(timingOptional && string.IsNullOrEmpty(tm.TimingFile));
            if (!UploadValidation.IsValidFilename(tm.OutFile) || (hasTiming && !UploadValidation.IsValidFilename(tm.TimingFile)))
                return new IngestResult(400, new { error = "bad outFile/timingFile" });
            if (outPart is null || (hasTiming && timingPart is null))
                return new IngestResult(400, new { error = "out/timing missing for terminal-output" });
            externalId = $"{host}:out:{tm.OutFile}";
        }

        var gate = ChunkLocks[(externalId.GetHashCode() & 0x7fffffff) % ChunkLocks.Length];
        await gate.WaitAsync();
        try
        {
            var row = db.TerminalByExternalId(externalId);
            if (row is { Live: false, Stale: false })
                return new IngestResult(409, new { id = row.Id, externalId });
            var baseRow = row ?? new TerminalSession(
                Id: 0, Host: host, SessionUser: tm.SessionUser, Kind: tm.Kind, RemoteAddr: tm.RemoteAddr,
                StartedAt: startedAt, EndedAt: endedAt, DurationSec: 0, ExitCode: null, Input: null,
                OutFilePath: null, TimingFilePath: null, SizeBytes: 0, Sha256: null, ExternalId: externalId,
                UploadedAt: DateTime.UtcNow, Source: tm.Source, Shell: tm.Shell, StationStartedAt: correctedFrom,
                Live: true, AuditSession: tm.AuditSession, Cols: tm.Cols, Rows: tm.Rows, Origin: tm.Origin, Mode: tm.Mode);
            // Конец — время станции из куска (для идущей сессии — момент отправки),
            // исправленное на сдвиг часов; начало — как при создании записи.
            var updated = baseRow with
            {
                EndedAt = endedAt < baseRow.StartedAt ? baseRow.StartedAt : endedAt,
                Live = live,
                RemoteAddr = baseRow.RemoteAddr ?? tm.RemoteAddr,
            };
            updated = updated with { DurationSec = (updated.EndedAt - updated.StartedAt).TotalSeconds };

            return tm.Kind == "terminal-input"
                ? InputChunk(tm, row, updated, serviceRules)
                : await OutputChunkAsync(tm, row, updated, outPart!, timingPart);
        }
        finally
        {
            gate.Release();
        }
    }

    // Поиск по вводу — и по расшифрованному -EncodedCommand.
    private static string SearchOf(TerminalSession s) => s.InputDecoded is null ? s.Input ?? "" : s.Input + "\n" + s.InputDecoded;

    private IngestResult InputChunk(TerminalMetaFields tm, TerminalSession? row, TerminalSession updated, IReadOnlyList<ServiceRule> serviceRules)
    {
        string have = row?.Input ?? "";
        if (tm.InputOffset > have.Length)
            return new IngestResult(416, new { error = "offset beyond received data", inputLength = have.Length });
        string text = tm.Input ?? "";
        int skip = have.Length - (int)tm.InputOffset;
        string input = have + (skip < text.Length ? text[skip..] : "");
        updated = updated with { Input = input, InputDecoded = PowerShellCommands.Decode(input), LastActivityAt = updated.EndedAt };
        // 0.13: служебное подключение — по всему вводу на сейчас: команда не по
        // правилу в следующем куске снимает пометку. drop — только когда сессия
        // закрыта (пока идёт, следующая команда может оказаться чужой) и у неё
        // нет вывода; до того запись просто скрыта.
        var serviceRule = ServiceConnections.Match(serviceRules, updated.SessionUser, updated.RemoteAddr, input);
        if (serviceRule is { Drop: true } && !updated.Live && (row is null || db.TerminalChildren([row.Id]).Count == 0))
        {
            if (row is not null) db.DeleteTerminalSessionRow(row.Id);
            return new IngestResult(200, new { skipped = true, reason = "service connection", externalId = updated.ExternalId, inputLength = input.Length });
        }
        updated = updated with { Service = serviceRule is not null };
        if (row is null)
        {
            // Пустой ввод не заводим — как и агент не пишет сессию без ввода.
            if (input.Length == 0) return new IngestResult(200, new { externalId = updated.ExternalId, inputLength = 0 });
            db.EnsureHostExists(updated.Host);
            long id = db.InsertTerminalSession(updated, SearchOf(updated));
            updated = updated with { Id = id };
        }
        else
        {
            db.UpdateTerminalLive(updated, SearchOf(updated));
        }
        db.LinkTerminalSession(updated);
        return new IngestResult(200, new { id = updated.Id, externalId = updated.ExternalId, inputLength = input.Length });
    }

    private async Task<IngestResult> OutputChunkAsync(TerminalMetaFields tm, TerminalSession? row, TerminalSession updated,
        UploadPart outPart, UploadPart? timingPart)
    {
        if (tm.OutOffset > updated.OutSize || tm.TimingOffset > updated.TimingSize)
            return new IngestResult(416, new { error = "offset beyond received data", outSize = updated.OutSize, timingSize = updated.TimingSize });

        bool created = row is null;
        if (created)
        {
            string day = updated.StartedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string dir = Path.Combine(cfg.DataDir, "blobs", "terminal", UploadValidation.SanitizeHost(updated.Host), day);
            Directory.CreateDirectory(dir);
            updated = updated with
            {
                OutFilePath = Rel(Path.Combine(dir, tm.OutFile)),
                TimingFilePath = timingPart is null ? null : Rel(Path.Combine(dir, tm.TimingFile)),
            };
        }

        string outPath = Abs(updated.OutFilePath!);
        long outSize = await AppendFrom(outPart, tm.OutOffset, outPath, updated.OutSize);
        long timingSize = updated.TimingSize;
        string? timingPath = updated.TimingFilePath is null ? null : Abs(updated.TimingFilePath);
        if (timingPart is not null && timingPath is not null)
            timingSize = await AppendFrom(timingPart, tm.TimingOffset, timingPath, updated.TimingSize);

        var (active, elapsed) = timingPath is null ? ((double?)null, (double?)null) : ActiveTime(timingPath);
        updated = updated with
        {
            OutSize = outSize,
            TimingSize = timingSize,
            SizeBytes = outSize + timingSize,
            ActiveSec = active,
            // По .timing — момент последнего вывода; без него (транскрипт) — время куска.
            LastActivityAt = elapsed is { } e ? updated.StartedAt.AddSeconds(e) : updated.EndedAt,
            ExitCode = updated.Live ? null : tm.ExitCode,
            Sha256 = updated.Live ? null : await Sha256Of(outPath),
        };
        string search = await ReadSearchTextTruncated(outPath);
        if (created)
        {
            db.EnsureHostExists(updated.Host);
            updated = updated with { Id = db.InsertTerminalSession(updated, search) };
            db.LinkTerminalSession(updated);
        }
        else
        {
            db.UpdateTerminalLive(updated, search);
        }
        return new IngestResult(200, new { id = updated.Id, externalId = updated.ExternalId, outSize, timingSize });
    }

    // Дописывает в файл часть куска после уже принятых `have` байт. Хвост файла
    // сверх `have` (прошлое дописывание не дошло до записи в БД) отрезается —
    // иначе повтор куска задвоил бы его.
    private static async Task<long> AppendFrom(UploadPart part, long offset, string path, long have)
    {
        await using var src = part.Open();
        long skip = have - offset;
        var buf = new byte[81920];
        while (skip > 0)
        {
            int n = await src.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, skip)));
            if (n == 0) return have;
            skip -= n;
        }
        await using var dst = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        dst.SetLength(have);
        dst.Seek(have, SeekOrigin.Begin);
        await src.CopyToAsync(dst);
        return dst.Length;
    }

    // Активное время и сколько всего прошло от начала до последнего вывода — по
    // .timing script(1): классический формат «<пауза> <байт>» или расширенный
    // «O|I|S <пауза> …» (H — заголовки, без паузы).
    public static (double Active, double Elapsed) ActiveTime(string timingPath)
    {
        double active = 0, elapsed = 0;
        foreach (var line in File.ReadLines(timingPath))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            string delay = p[0] is "O" or "I" or "S" && p.Length > 1 ? p[1] : p[0];
            if (!double.TryParse(delay, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || d < 0) continue;
            elapsed += d;
            if (d <= ActiveGapSec) active += d;
        }
        return (active, elapsed);
    }

    private static async Task<string> Sha256Of(string path)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
    }

    private string Abs(string rel) => Path.GetFullPath(Path.Combine(cfg.DataDir, rel));
    private string Rel(string full) => Path.GetRelativePath(cfg.DataDir, full).Replace('\\', '/');
}
