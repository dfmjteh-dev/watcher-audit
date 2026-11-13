using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Watcher.Server;

// Приём записей от агентов — /api/upload (видео) и /api/upload-terminal
// (консоль): проверка meta, запись блоба через .part (R9), строка в БД, ответ
// агенту. Без HTTP: эндпоинты (UploadEndpoints) только аутентифицируют агента,
// разбирают multipart и превращают IngestResult в ответ — код ответа и тело
// агентам важны (409 = «уже на сервере, удаляй локальную копию»), поэтому
// они здесь, а не в эндпоинте.
public sealed record IngestResult(int StatusCode, object Body);

// Часть multipart-формы: открыть поток и заявленная длина.
public sealed record UploadPart(Func<Stream> Open, long Length);

public sealed partial class RecordingIngest(Db db, ServerConfig cfg, IVideoTranscoder? videoInspector = null)
{
    public async Task<IngestResult> VideoAsync(AgentCaller caller, string filename, string metaJson, string? clientSha,
        Func<Stream> openVideo, TimeSpan? clockSkew, DateTime receivedAt, bool validateVideo = false, CancellationToken ct = default)
    {
        if (!UploadValidation.IsValidFilename(filename))
            return new IngestResult(400, new { error = "bad filename" });

        if (db.ByFilename(filename) is { } existing)
        {
            // 409 = "уже на сервере": агент на него удаляет локальную копию. Отвечать
            // им можно только на настоящий повтор — имя совпадает и у РАЗНЫХ записей
            // (одинаковые имена машин, несколько сессий на Linux, перевод часов), см.
            // docs/code-review-2026-10.md, R2. Без клиентского sha256 (старые/ручные
            // клиенты) отличить нельзя — прежнее поведение.
            string presentedSha = clientSha ?? "";
            if (!string.IsNullOrEmpty(presentedSha) && !string.Equals(presentedSha, existing.Sha256, StringComparison.OrdinalIgnoreCase))
                return new IngestResult(422, new { error = "filename collision", id = existing.Id, filename });
            return new IngestResult(409, new { id = existing.Id, filename });
        }

        // A5 (roadmap-next.md): кривой meta.json от агента (невалидный JSON, строка
        // вместо даты/числа) раньше валил JsonDocument.Parse/DateTime.Parse прямо в
        // хендлере — 500 со стектрейсом вместо 400. TryParseUploadMeta ловит это сама.
        if (!TryParseUploadMeta(metaJson, out var parsedMeta))
            return new IngestResult(400, new { error = "invalid meta" });
        var (claimedHost, stationStartedAt, stationEndedAt, dur, frames, motion, reason, sessionUser, activity, monitorsJson) = parsedMeta;
        var (startedAt, endedAt, correctedFrom) = StationClock.Correct(stationStartedAt, stationEndedAt, clockSkew, receivedAt);
        // Задача G1: у агента с учёткой host — из учётки, чужой в meta — отказ.
        if (AgentAuth.ResolveHost(caller, claimedHost) is not { } host)
            return new IngestResult(403, new { error = "host does not match agent credential" });

        // A1 (roadmap-next.md): эти поля идут в веб-UI без контроля сервера, то есть
        // от любого, кто знает ApiToken. UI экранирует на рендере (Preact, E1) — это
        // защита второго слоя, отдельно от неё, не вместо.
        if (!UploadValidation.IsSafeMetaField(host) || !UploadValidation.IsSafeMetaField(sessionUser) || !UploadValidation.IsSafeMetaField(reason))
            return new IngestResult(400, new { error = "invalid meta field" });

        string day = startedAt.ToString("yyyy-MM-dd");
        string safeHost = UploadValidation.SanitizeHost(host);
        string dir = Path.Combine(cfg.DataDir, "blobs", safeHost, day);
        Directory.CreateDirectory(dir);
        string blobPath = Path.Combine(dir, filename);
        // R9: сначала .part, итоговый путь появляется только после Insert (см.
        // BlobFiles). finally удаляет .part при любом исходе, кроме успешного Move.
        string partPath = BlobFiles.PartPath(blobPath);
        try
        {
            var (sha256, size) = await WriteBlobHashed(openVideo(), partPath);

            if (!string.IsNullOrEmpty(clientSha) && !string.Equals(clientSha, sha256, StringComparison.OrdinalIgnoreCase))
                return new IngestResult(400, new { error = "sha256 mismatch", expected = clientSha, got = sha256 });

            // Новый агент понимает типизированный отказ и сохраняет локальный оригинал.
            // Старые агенты оставляют прежний протокол, чтобы 415 не блокировал их очередь.
            if (validateVideo)
            {
                var check = videoInspector is null ? new VideoInspection(null, "inspection-unavailable")
                    : await videoInspector.InspectAsync(partPath, fullDecode: false, ct);
                if (check.Valid == false) return new IngestResult(415, new { code = "invalid-video", error = "invalid video", reason = check.Reason });
                if (check.Valid is null) return new IngestResult(503, new { error = "video inspection unavailable" });
            }

            var seg = new Segment(
                Id: 0, Host: host, Filename: filename,
                StartedAt: startedAt, EndedAt: endedAt, DurationSec: dur,
                Frames: frames, MotionEvents: motion, StartReason: reason,
                SizeBytes: size, Sha256: sha256,
                FilePath: Path.GetRelativePath(cfg.DataDir, blobPath).Replace('\\', '/'),
                UploadedAt: DateTime.UtcNow, SessionUser: sessionUser, StationStartedAt: correctedFrom, MonitorsJson: monitorsJson);
            long id;
            try { id = db.Insert(seg); }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19 && db.ByFilename(filename) is { } winner)
            {
                // Гонка двух загрузок одного имени: ByFilename выше обе прошли, Insert
                // выиграла другая. Ответ тот же, что у повтора после неё (R2).
                if (!string.Equals(sha256, winner.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new IngestResult(422, new { error = "filename collision", id = winner.Id, filename });
                return new IngestResult(409, new { id = winner.Id, filename });
            }
            try
            {
                // H9: в той же попытке, что и блоб — не вставилось, откатывается
                // и сегмент (агент повторит загрузку целиком).
                db.InsertActivity(id, host, startedAt, activity);
                BlobFiles.Commit(partPath, blobPath);
            }
            catch
            {
                db.DeleteSegmentRow(id);
                throw;
            }
            return new IngestResult(200, new { id, filename, sha256, size });
        }
        finally
        {
            BlobFiles.TryDelete(partPath);
        }
    }

    public async Task<IngestResult> TerminalAsync(AgentCaller caller, string metaJson, UploadPart? outPart, UploadPart? timingPart,
        TimeSpan? clockSkew, DateTime receivedAt)
    {
        // A5 (roadmap-next.md): тот же приём, что у /api/upload — весь разбор
        // JSON/даты/чисел под try, 400 на любую ошибку, а не необработанное
        // исключение (sessionId/exitCode не числа, outFile не строка и т.п.).
        if (!TryParseTerminalMeta(metaJson, out var tm))
            return new IngestResult(400, new { error = "invalid meta" });
        if (tm.Kind != "terminal-input" && tm.Kind != "terminal-output")
            return new IngestResult(400, new { error = "kind must be terminal-input or terminal-output" });

        // A1 (roadmap-next.md): host/sessionUser попадают в таблицу #/terminal
        // веб-UI без контроля сервера — та же защита второго слоя, что у /api/upload.
        if (!UploadValidation.IsSafeMetaField(tm.Host) || !UploadValidation.IsSafeMetaField(tm.SessionUser) ||
            !UploadValidation.IsSafeMetaField(tm.Source, 32) || !UploadValidation.IsSafeMetaField(tm.Shell, 64))
            return new IngestResult(400, new { error = "invalid meta field" });
        // Задача F2: транскрипт PowerShell — вывод без .timing (тайминга у него нет).
        bool timingOptional = tm.Source == UploadValidation.SourceWinTranscript;

        string kind = tm.Kind;
        if (AgentAuth.ResolveHost(caller, tm.Host) is not { } host)
            return new IngestResult(403, new { error = "host does not match agent credential" });
        string sessionUser = tm.SessionUser;
        // Пользователь исключён в конфиге группы/хоста — запись не сохраняем.
        // 200, а не ошибка: агент считает запись обработанной и больше её не шлёт.
        string? effectiveJson = db.GetEffectiveConfig(host)?.ConfigJson;
        // 0.13: «Снять с записи» (pauseConsole) — консоль хоста не сохраняется.
        if (TerminalUserFilter.ConsolePaused(effectiveJson, DateTime.UtcNow))
            return new IngestResult(200, new { skipped = true, reason = "console recording paused" });
        if (!TerminalUserFilter.IsAllowed(effectiveJson, sessionUser))
            return new IngestResult(200, new { skipped = true, reason = "user excluded by config" });
        string? remoteAddr = tm.RemoteAddr;
        // 0.16: происхождения транскриптов, которые группа/хост не сохраняет
        // (например, автоматизация — в её выводе бывают секреты).
        if (TerminalUserFilter.OriginSkipped(effectiveJson, tm.Origin))
            return new IngestResult(200, new { skipped = true, reason = "origin excluded by config" });
        // 0.13: служебные подключения — правило решает, скрыть запись или не сохранять.
        var serviceRules = ServiceConnections.Rules(effectiveJson);
        if (tm.Live is not null)
            return await TerminalChunkAsync(tm, host, timingOptional, clockSkew, receivedAt, outPart, timingPart, serviceRules);
        var serviceRule = kind == "terminal-input" ? ServiceConnections.Match(serviceRules, sessionUser, remoteAddr, tm.Input) : null;
        if (serviceRule is { Drop: true })
            return new IngestResult(200, new { skipped = true, reason = "service connection" });
        // R7: externalId ниже строится из времени станции, не из исправленного —
        // сдвиг меряется заново на каждом запросе, и повтор той же записи иначе
        // получал бы другой externalId и дублировался.
        DateTime stationStartedAt = tm.StartedAt;
        var (startedAt, endedAt, correctedFrom) = StationClock.Correct(tm.StartedAt, tm.EndedAt, clockSkew, receivedAt);
        // watcher-term-wrap.sh durationSec не присылает (только startedAt/endedAt)
        // — без этого у всех записей вывода в UI была длительность 0:00 (найдено
        // живой проверкой E4).
        double dur = tm.DurationSec > 0 ? tm.DurationSec : Math.Max(0, (endedAt - startedAt).TotalSeconds);
        int? exitCode = tm.ExitCode;

        string externalId;
        string? input = null;
        string? outRelPath = null, timingRelPath = null;
        long size = 0;
        string? sha256 = null;

        string? outPath = null, outPartPath = null, timingPath = null, timingPartPath = null;
        try
        {
            if (kind == "terminal-input")
            {
                // sessionId — числовой audit ses= (см. SessionCollector.cs). Сам по себе
                // НЕ уникален per-host: счётчик ядра начинается с 1 после каждой
                // загрузки, а одна сессия может прийти несколькими записями (после
                // SweepAbandoned продолжение получает тот же ses). Только по ses оба
                // случая давали 409 → агент считал запись выгруженной → ввод терялся
                // (docs/code-review-2026-10.md, R1/R3). startedAt различает и то, и
                // другое, но совпадает при повторной отправке того же файла — дедуп
                // ретраев сохраняется.
                externalId = $"{host}:in:{tm.SessionId}:{stationStartedAt:yyyyMMddTHHmmss.fffZ}";
                // input уходит в веб-UI тоже (Preact экранирует его на рендере, как и
                // всё остальное), но не ограничиваем его тем же IsSafeMetaField —
                // это текст набранный пользователем в терминале, длина/символы не
                // предсказуемы и обрезка/отказ здесь означали бы тихо терять часть
                // аудита, то есть то же R1/R3, которое только что починили.
                input = tm.Input;

                if (db.TerminalByExternalId(externalId) is { } existingIn)
                    return new IngestResult(409, new { id = existingIn.Id, externalId });
            }
            else
            {
                string outFile = tm.OutFile;
                string timingFile = tm.TimingFile;
                bool hasTimingFile = !(timingOptional && string.IsNullOrEmpty(timingFile));
                if (!UploadValidation.IsValidFilename(outFile) || (hasTimingFile && !UploadValidation.IsValidFilename(timingFile)))
                    return new IngestResult(400, new { error = "bad outFile/timingFile" });
                externalId = $"{host}:out:{outFile}";

                if (outPart is null || (hasTimingFile && timingPart is null))
                    return new IngestResult(400, new { error = "out/timing missing for terminal-output" });

                if (db.TerminalByExternalId(externalId) is { } existingOut)
                    return new IngestResult(409, new { id = existingOut.Id, externalId });

                string day = startedAt.ToString("yyyy-MM-dd");
                string safeHost = UploadValidation.SanitizeHost(host);
                string dir = Path.Combine(cfg.DataDir, "blobs", "terminal", safeHost, day);
                Directory.CreateDirectory(dir);

                // R9: как у /api/upload — .part, Insert, потом Move (см. BlobFiles).
                outPath = Path.Combine(dir, outFile);
                outPartPath = BlobFiles.PartPath(outPath);
                (sha256, size) = await WriteBlobHashed(outPart.Open(), outPartPath);
                outRelPath = Path.GetRelativePath(cfg.DataDir, outPath).Replace('\\', '/');
                if (hasTimingFile && timingPart is not null)
                {
                    timingPath = Path.Combine(dir, timingFile);
                    timingPartPath = BlobFiles.PartPath(timingPath);
                    await using (var src = timingPart.Open())
                    await using (var dst = File.Create(timingPartPath))
                    {
                        await src.CopyToAsync(dst);
                        size += timingPart.Length;
                    }
                    timingRelPath = Path.GetRelativePath(cfg.DataDir, timingPath).Replace('\\', '/');
                }
            }

            // search_text (Фаза 8 — связь с сервером, roadmap.md): для input это сам
            // input, для output — содержимое только что записанного .out (лишнее
            // чтение с диска небольшого текстового файла, не видео — приемлемо,
            // проще, чем декодировать параллельно с хешированием выше). A5
            // (roadmap-next.md): .out не ограничен по размеру отдельно от
            // MaxUploadSizeMb (512 МБ по умолчанию) — без обрезки это читалось бы
            // целиком в одну C#-строку и в колонку SQLite. ReadSearchTextTruncated
            // режет до SearchTextMaxBytes, не читая файл целиком в память, когда он
            // больше лимита.
            string? decoded = kind == "terminal-input" ? PowerShellCommands.Decode(input) : null;
            string? searchText = kind == "terminal-input"
                ? (decoded is null ? input : input + "\n" + decoded)
                : await ReadSearchTextTruncated(outPartPath!);

            db.EnsureHostExists(host);
            var row = new TerminalSession(
                Id: 0, Host: host, SessionUser: sessionUser, Kind: kind, RemoteAddr: remoteAddr,
                StartedAt: startedAt, EndedAt: endedAt, DurationSec: dur, ExitCode: exitCode,
                Input: input, OutFilePath: outRelPath, TimingFilePath: timingRelPath,
                SizeBytes: size, Sha256: sha256, ExternalId: externalId, UploadedAt: DateTime.UtcNow,
                Source: tm.Source, Shell: tm.Shell, StationStartedAt: correctedFrom,
                AuditSession: tm.AuditSession, OutSize: outPart?.Length ?? 0, TimingSize: timingPart?.Length ?? 0,
                Service: serviceRule is not null, Cols: tm.Cols, Rows: tm.Rows,
                Origin: tm.Origin, Mode: tm.Mode, InputDecoded: decoded);
            long id;
            try { id = db.InsertTerminalSession(row, searchText); }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19 && db.TerminalByExternalId(externalId) is { } winner)
            {
                // Гонка двух загрузок одной записи — ответ как у повтора после неё.
                return new IngestResult(409, new { id = winner.Id, externalId });
            }
            try
            {
                if (outPartPath is not null) BlobFiles.Commit(outPartPath, outPath!);
                if (timingPartPath is not null) BlobFiles.Commit(timingPartPath, timingPath!);
            }
            catch
            {
                db.DeleteTerminalSessionRow(id);
                // .out мог уже переехать — без строки в БД он такой же сирота.
                if (outPartPath is not null && !File.Exists(outPartPath)) BlobFiles.TryDelete(outPath);
                throw;
            }
            db.LinkTerminalSession(row with { Id = id });
            return new IngestResult(200, new { id, externalId });
        }
        finally
        {
            BlobFiles.TryDelete(outPartPath);
            BlobFiles.TryDelete(timingPartPath);
        }
    }

    // A5 (roadmap-next.md): весь JSON/дата/число разбор meta.json в одном месте,
    // под try — вызывающий код получает либо полностью валидные поля, либо false,
    // никогда необработанное исключение на пути запроса.
    private static bool TryParseUploadMeta(string metaJson, out UploadMetaFields fields)
    {
        fields = default;
        try
        {
            var metaDoc = JsonDocument.Parse(metaJson).RootElement;
            string host = metaDoc.TryGetProperty("host", out var h) ? h.GetString() ?? "unknown" : "unknown";
            DateTime startedAt = metaDoc.TryGetProperty("startedAt", out var sa) ? DateTime.Parse(sa.GetString()!).ToUniversalTime() : DateTime.UtcNow;
            DateTime endedAt = metaDoc.TryGetProperty("endedAt", out var ea) ? DateTime.Parse(ea.GetString()!).ToUniversalTime() : DateTime.UtcNow;
            double dur = metaDoc.TryGetProperty("durationSec", out var d) ? d.GetDouble() : 0;
            int frames = metaDoc.TryGetProperty("frames", out var fr) ? fr.GetInt32() : 0;
            int motion = metaDoc.TryGetProperty("motionEvents", out var me) ? me.GetInt32() : 0;
            string reason = UploadValidation.NormalizeStartReason(metaDoc.TryGetProperty("startReason", out var sr2) ? sr2.GetString() ?? "" : "");
            // Фаза 6 (roadmap.md): пусто для обычных станций (старые агенты это поле
            // не присылают вовсе — TryGetProperty просто не найдёт его, тот же null),
            // задано для RDS-хостов. Пустую строку трактуем как null — проще
            // фильтровать (WHERE session_user = $su никогда не должен сравнивать с "").
            string? sessionUser = metaDoc.TryGetProperty("sessionUser", out var su) && !string.IsNullOrEmpty(su.GetString())
                ? su.GetString() : null;
            var activity = metaDoc.TryGetProperty("activity", out var act) ? UploadValidation.ParseActivity(act, dur) : new List<ActivityEntry>();
            string? monitors = metaDoc.TryGetProperty("monitors", out var mon) ? UploadValidation.ParseMonitors(mon) : null;
            fields = new UploadMetaFields(host, startedAt, endedAt, dur, frames, motion, reason, sessionUser, activity, monitors);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // R9: копирует тело в path, попутно считая sha256 — одним проходом, без
    // повторного чтения файла.
    private static async Task<(string Sha256, long Size)> WriteBlobHashed(Stream source, string path)
    {
        await using var src = source;
        await using var dst = File.Create(path);
        using var hasher = SHA256.Create();
        var buf = new byte[81920];
        int n;
        long size = 0;
        while ((n = await src.ReadAsync(buf)) > 0)
        {
            hasher.TransformBlock(buf, 0, n, null, 0);
            await dst.WriteAsync(buf.AsMemory(0, n));
            size += n;
        }
        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return (Convert.ToHexString(hasher.Hash!).ToLowerInvariant(), size);
    }

    // A5 (roadmap-next.md) — тот же приём, что у TryParseUploadMeta выше: весь
    // разбор полей terminal-meta под try, 400 на ошибку вместо 500.
    private static bool TryParseTerminalMeta(string metaJson, out TerminalMetaFields fields)
    {
        fields = default;
        try
        {
            var meta = JsonDocument.Parse(metaJson).RootElement;
            string kind = meta.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
            string host = meta.TryGetProperty("host", out var h) ? h.GetString() ?? "unknown" : "unknown";
            string sessionUser = meta.TryGetProperty("sessionUser", out var su) ? su.GetString() ?? "unknown" : "unknown";
            string? remoteAddr = meta.TryGetProperty("remoteAddr", out var ra) && ra.ValueKind == JsonValueKind.String ? ra.GetString() : null;
            DateTime startedAt = meta.TryGetProperty("startedAt", out var sa) ? DateTime.Parse(sa.GetString()!).ToUniversalTime() : DateTime.UtcNow;
            DateTime endedAt = meta.TryGetProperty("endedAt", out var ea) ? DateTime.Parse(ea.GetString()!).ToUniversalTime() : DateTime.UtcNow;
            double dur = meta.TryGetProperty("durationSec", out var d) ? d.GetDouble() : 0;
            int? exitCode = meta.TryGetProperty("exitCode", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : null;
            long sessionId = meta.TryGetProperty("sessionId", out var sid) ? sid.GetInt64() : 0;
            string? input = meta.TryGetProperty("input", out var inp) ? inp.GetString() : null;
            string outFile = meta.TryGetProperty("outFile", out var of) ? of.GetString() ?? "" : "";
            string timingFile = meta.TryGetProperty("timingFile", out var tf) ? tf.GetString() ?? "" : "";
            string? source = meta.TryGetProperty("source", out var so) && so.ValueKind == JsonValueKind.String ? so.GetString() : null;
            string? shell = meta.TryGetProperty("shell", out var sh) && sh.ValueKind == JsonValueKind.String ? sh.GetString() : null;
            bool? live = meta.TryGetProperty("live", out var lv) && lv.ValueKind is JsonValueKind.True or JsonValueKind.False ? lv.GetBoolean() : null;
            long Offset(string name) => meta.TryGetProperty(name, out var o) && o.ValueKind == JsonValueKind.Number ? Math.Max(0, o.GetInt64()) : 0;
            long? auditSession = meta.TryGetProperty("auditSession", out var aus) && aus.ValueKind == JsonValueKind.Number ? aus.GetInt64() : null;
            if (kind == "terminal-input" && sessionId != 0) auditSession ??= sessionId;
            // Размер терминала (обёртка 0.14.4+) — только правдоподобный, иначе нет.
            int? Size(string name, int max) => meta.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt32(out int n) && n is > 0 && n <= max ? n : null;
            string? Text(string name) => meta.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            fields = new TerminalMetaFields(kind, host, sessionUser, remoteAddr, startedAt, endedAt, dur, exitCode, sessionId, input, outFile, timingFile, source, shell,
                live, Offset("inputOffset"), Offset("outOffset"), Offset("timingOffset"), auditSession, Size("cols", 1000), Size("rows", 500),
                TerminalOrigins.Origin(Text("origin")), TerminalOrigins.Mode(Text("mode")));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private const int SearchTextMaxBytes = 2 * 1024 * 1024;

    // A5 (roadmap-next.md): читает не больше SearchTextMaxBytes, не весь файл в
    // память — .out ограничен только общим MaxUploadSizeMb (512 МБ по умолчанию),
    // без этого лимита search_text мог бы стать такого же размера строкой в C# и
    // в колонке SQLite.
    private static async Task<string> ReadSearchTextTruncated(string path, int maxBytes = SearchTextMaxBytes)
    {
        using var stream = File.OpenRead(path);
        bool truncated = stream.Length > maxBytes;
        int toRead = truncated ? maxBytes : (int)stream.Length;
        var buffer = new byte[toRead];
        int read = 0;
        while (read < toRead)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, toRead - read));
            if (n == 0) break;
            read += n;
        }
        string text = Encoding.UTF8.GetString(buffer, 0, read);
        return truncated ? text + "\n...(усечено)" : text;
    }
}
