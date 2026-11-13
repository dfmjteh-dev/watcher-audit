using System.Collections.Concurrent;
using Watcher.Shared;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Watcher.Uploader;

namespace Watcher.SessionService;

// Задача F1 (roadmap-next.md): консольный аудит SSH-сессий Windows в той же
// службе. Транскрипты пишет сам PowerShell по машинной политике
// (install.ps1 -TerminalAudit 1) в TranscriptDir, куда пользователь может
// только создавать файлы (F0, п. 5). Служба:
//   - берёт закрытые транскрипты (файл не держит PowerShell);
//   - выгружает КАЖДЫЙ двумя записями, как Linux: terminal-input (команды) и
//     terminal-output (текст транскрипта, без .timing — F2), source=win-transcript,
//     с метками origin (SSH, автоматизация, локальный вход… — TranscriptKind,
//     по 4688 и учётке процесса) и mode (интерактив/команда). Что из этого
//     хранить, решает сервер по настройке группы (0.16; раньше выгружался только
//     SSH, остальное удалялось, а неопознанное через сутки становилось потерей);
//   - происхождение не определилось за UnknownGraceMinutes после закрытия файла
//     (аудит 4688 выключен, журнал перезаписан) — выгружает с origin=unknown;
//   - идущую SSH-сессию шлёт кусками, остальные — после закрытия.
public sealed class TerminalAuditConfig
{
    public bool Enabled { get; set; } = false;
    public string TranscriptDir { get; set; } = @"C:\ProgramData\Watcher\transcripts";
    // Своя учётка вида terminal — отдельный файл от учётки видео (G1).
    public string CredentialPath { get; set; } = @"C:\ProgramData\Watcher\agent\terminal-credential.json";
    public int ScanIntervalSeconds { get; set; } = 15;
    // Сколько ждать 4688 для закрытого транскрипта, прежде чем выгрузить его как unknown.
    public int UnknownGraceMinutes { get; set; } = 10;
    // 0.15.0: чья сессия — решённое при первом взгляде на транскрипт (TranscriptOrigins).
    public string OriginsPath { get; set; } = @"C:\ProgramData\Watcher\agent\transcript-origins.json";
    public string ProcessHistoryPath { get; set; } = @"C:\ProgramData\Watcher\agent\process-starts.json";
    public string DiagnosticsPath { get; set; } = @"C:\ProgramData\Watcher\agent\transcript-diagnostics.json";
    public string QuarantineDir { get; set; } = @"C:\ProgramData\Watcher\agent\transcript-quarantine";
    public int QuarantineDays { get; set; } = 7;
    public long QuarantineMaxBytes { get; set; } = 256 * 1024 * 1024;
}

[SupportedOSPlatform("windows")]
public sealed class TerminalAudit
{
    private readonly TerminalAuditConfig _cfg;
    private readonly TerminalUploadClient _client;
    private readonly ILogger _logger;
    private readonly string _host = Environment.MachineName;
    private DateTime _lastUnknownWarning = DateTime.MinValue;
    // 0.12: идущие SSH-сессии — что уже отправлено (по пути транскрипта) и чья
    // это сессия (4688 читаем один раз на файл, не каждый опрос).
    private sealed class LiveTranscript
    {
        public SessionOrigin Origin;
        public string? Remote;
        public string Label = TranscriptKind.Unknown;
        public long OutSent;
        public long InputSent;
        public DateTime LastSentUtc;
    }
    private readonly Dictionary<string, LiveTranscript> _live = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastProbe = DateTime.MinValue;

    private readonly LossJournal _losses;
    private readonly TranscriptOrigins _origins;
    private readonly SecurityProcessHistory _history;
    private readonly TranscriptQuarantine _quarantine;
    private readonly ConcurrentDictionary<string, string> _issues = new(StringComparer.OrdinalIgnoreCase);

    public TerminalAudit(TerminalAuditConfig cfg, UploaderConfig uploaderCfg, ILogger logger)
    {
        _cfg = cfg;
        _logger = logger;
        _losses = LossJournal.For(uploaderCfg);   // тот же файл, что у выгрузки видео
        _origins = new TranscriptOrigins(cfg.OriginsPath);
        _history = new SecurityProcessHistory(cfg.ProcessHistoryPath);
        _quarantine = new TranscriptQuarantine(cfg.QuarantineDir,
            Math.Clamp(cfg.QuarantineMaxBytes, 1024 * 1024, 1024L * 1024 * 1024), TimeSpan.FromDays(Math.Clamp(cfg.QuarantineDays, 1, 30)));
        _quarantine.ValidateSourceDirectory(cfg.TranscriptDir);
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var identity = new AgentIdentity(
            new AgentIdentityOptions(uploaderCfg.ServerUrl, uploaderCfg.EnrollToken, cfg.CredentialPath),
            http, logger, _host, AgentIdentity.KindTerminal);
        _client = new TerminalUploadClient(uploaderCfg.ServerUrl, http, identity, logger);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("Terminal audit: transcripts in {Dir}, all sessions with origin labels", _cfg.TranscriptDir);
        // Сбор метаданных не зависит от сети/регистрации/долгой загрузки записи.
        var collector = Task.Run(async () =>
        {
            var lastCollectorWarning = DateTime.MinValue;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _history.Refresh();
                    if (Directory.Exists(_cfg.TranscriptDir))
                        CaptureOrigins(new DirectoryInfo(_cfg.TranscriptDir).EnumerateFiles("*.txt", SearchOption.AllDirectories), ct);
                    _origins.Save();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    if (DateTime.UtcNow - lastCollectorWarning > TimeSpan.FromMinutes(5))
                    {
                        lastCollectorWarning = DateTime.UtcNow;
                        _logger.LogWarning("Process metadata collection failed: {Type}; retrying", ex.GetType().Name);
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(_history.CatchingUp ? 1 : Math.Max(5, _cfg.ScanIntervalSeconds)), ct);
            }
        }, ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await ScanOnceAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogWarning(ex, "Terminal audit scan failed"); }
                finally { _origins.Save(); }
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _cfg.ScanIntervalSeconds)), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            try { await collector; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
    }

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        try { _quarantine.Sweep(DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ReportMaintenanceError("quarantine-cleanup", ex); }
        if (!Directory.Exists(_cfg.TranscriptDir))
        {
            _issues.Clear();
            WriteDiagnostics();
            return;
        }
        var files = new DirectoryInfo(_cfg.TranscriptDir)
            .EnumerateFiles("*.txt", SearchOption.AllDirectories)
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();
        _issues.Clear();
        // Сначала классифицируем все заголовки и сохраняем решения, даже если
        // сеть недоступна или загрузка первого файла займёт несколько минут.
        CaptureOrigins(files, ct);
        _origins.Save();
        WriteDiagnostics();
        bool canUpload = true;
        try { await _client.Identity.EnsureEnrolledAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { canUpload = false; _logger.LogDebug("Terminal enrollment unavailable: {Type}", ex.GetType().Name); }
        if (canUpload && _client.LiveIntervalSec is null && files.Count > 0 && DateTime.UtcNow - _lastProbe > TimeSpan.FromMinutes(10))
        {
            _lastProbe = DateTime.UtcNow;
            await _client.ProbeAsync(_host, ct);
        }
        foreach (var path in _live.Keys.Where(p => !File.Exists(p)).ToList()) _live.Remove(path);
        foreach (var f in files)
        {
            if (ct.IsCancellationRequested) break;
            // Пока сессия жива, файл держит PowerShell (F0, п. 1). С 0.12, если
            // сервер принимает куски, читаем его и так (PowerShell пишет с
            // FileShare.Read) и шлём SSH-сессию кусками, не дожидаясь конца.
            string? text = ReadIfClosed(f.FullName, headerOnly: true);
            if (text is null)
            {
                if (canUpload) await LiveAsync(f, ct);
                continue;
            }

            var info = ParseTranscript(text);
            if (info is null)
            {
                _issues[f.Name] = TranscriptIssue.InvalidTranscript;
                if (DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromHours(1))
                {
                    try
                    {
                        _quarantine.Store(f.FullName, TranscriptIssue.InvalidTranscript, DateTime.UtcNow,
                            entry => _losses.TryAdd(entry.File, entry.Size, LossJournal.ReasonTranscript, entry.At, entry.Reason));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { ReportMaintenanceError("quarantine-invalid", ex); }
                }
                continue;
            }

            DateTime startUtc = info.StartedLocal.ToUniversalTime();
            // 0.15.0: решённое, пока файл был открыт (или до перезапуска службы), —
            // не переспрашиваем 4688: к закрытию долгой консоли его уже может не быть.
            var known = OriginOf(f, info);
            if (known is null)
            {
                string reason = _issues.GetValueOrDefault(f.Name) ?? TranscriptIssue.EventMissing;
                // Пока читатель ещё догоняет Security, или файл закрыт недавно, — ждём 4688.
                if (reason == TranscriptIssue.EventPending
                    || DateTime.UtcNow - f.LastWriteTimeUtc < TimeSpan.FromMinutes(Math.Max(1, _cfg.UnknownGraceMinutes))) continue;
                known = new TranscriptOrigins.Entry(SessionOrigin.Unknown, null, TranscriptKind.Unknown);
            }

            if (!canUpload) continue;
            text = ReadIfClosed(f.FullName);
            if (text is null || ParseTranscript(text) is not { } fullInfo) continue;
            info = fullInfo;
            DateTime endUtc = (info.EndedLocal ?? f.LastWriteTime).ToUniversalTime();
            // Часть уже ушла кусками — закрываем записи последним куском.
            bool sent = _live.TryGetValue(f.FullName, out var liveState)
                ? await SendChunksAsync(f, text, info, startUtc, endUtc, known.Remote, liveState, live: false, ct)
                : await UploadAsync(f.Name, text, info, startUtc, endUtc, known.Remote, known.Label ?? TranscriptKind.Unknown, ct);
            if (sent)
            {
                _live.Remove(f.FullName);
                _origins.Remove(f.Name);
                TryDelete(f.FullName, null);
            }
        }
        if (canUpload) await DrainQuarantineAsync(ct);
        _origins.Retain(Directory.EnumerateFiles(_cfg.TranscriptDir, "*.txt", SearchOption.AllDirectories).Select(Path.GetFileName).OfType<string>());
        _origins.Save();
        WriteDiagnostics();
    }

    // До 0.16 неопознанные транскрипты уходили в карантин невыгруженными.
    // Теперь их выгружаем с origin=unknown (повреждённые — остаются потерями).
    private async Task DrainQuarantineAsync(CancellationToken ct)
    {
        IReadOnlyList<(TranscriptQuarantine.Entry Entry, string Path)> pending;
        try { pending = _quarantine.Pending(e => e.Reason != TranscriptIssue.InvalidTranscript, 50); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ReportMaintenanceError("quarantine-drain", ex); return; }
        foreach (var (entry, path) in pending)
        {
            if (ct.IsCancellationRequested) return;
            string? text = ReadIfClosed(path);
            if (text is null || ParseTranscript(text) is not { } info) continue;
            DateTime startUtc = info.StartedLocal.ToUniversalTime();
            DateTime endUtc = (info.EndedLocal ?? info.StartedLocal).ToUniversalTime();
            string label = TranscriptKind.IsServiceAccount(info.RunAsUser, SidOf) ? TranscriptKind.Automation : TranscriptKind.Unknown;
            if (!await UploadAsync(entry.File, text, info, startUtc, endUtc, null, label, ct)) return;
            try { _quarantine.Release(entry); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { ReportMaintenanceError("quarantine-release", ex); }
        }
    }

    private void CaptureOrigins(IEnumerable<FileInfo> files, CancellationToken ct)
    {
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (_origins.Get(file.Name) is not null) continue;
            var header = ReadHeader(file.FullName);
            if (header is not null && ParseTranscript(header) is { } info) _ = OriginOf(file, info);
        }
    }

    private DateTime _lastMaintenanceWarning;
    private void ReportMaintenanceError(string operation, Exception ex)
    {
        if (DateTime.UtcNow - _lastMaintenanceWarning < TimeSpan.FromMinutes(5)) return;
        _lastMaintenanceWarning = DateTime.UtcNow;
        _logger.LogWarning("Transcript maintenance failed: {Operation}, {Type}; original retained where possible", operation, ex.GetType().Name);
    }

    private void WriteDiagnostics()
    {
        var groups = _issues.Values.GroupBy(x => x).OrderBy(g => g.Key)
            .Select(g => new { reason = g.Key, count = g.Count() }).ToList();
        try
        {
            CredentialStore.CreateProtectedDirectory(Path.GetDirectoryName(Path.GetFullPath(_cfg.DiagnosticsPath))!);
            File.WriteAllText(_cfg.DiagnosticsPath + ".tmp", JsonSerializer.Serialize(new
            {
                at = DateTime.UtcNow,
                groups,
                history = _history.Status,
            }));
            File.Move(_cfg.DiagnosticsPath + ".tmp", _cfg.DiagnosticsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ReportMaintenanceError("diagnostics-save", ex); }
        if (groups.Count == 0 && !_history.ReadFailed && !_history.SaveFailed) return;
        if (DateTime.UtcNow - _lastUnknownWarning < TimeSpan.FromHours(1)) return;
        _lastUnknownWarning = DateTime.UtcNow;
        foreach (var group in groups)
            _logger.LogWarning("Unclassified transcripts: {Reason}, count={Count}; uploaded as origin=unknown after {Grace} min", group.reason, group.count, _cfg.UnknownGraceMinutes);
        if (_history.ReadFailed || _history.SaveFailed)
            _logger.LogWarning("Process history health: readFailed={ReadFailed}, saveFailed={SaveFailed}; collection retries automatically", _history.ReadFailed, _history.SaveFailed);
    }

    private static TranscriptInfo? ParseTranscript(string text)
    {
        try { return TranscriptParser.Parse(text); }
        catch (FormatException) { return null; }
    }

    private static string? ReadHeader(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return ReadStart(reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private async Task<bool> UploadAsync(string fileName, string text, TranscriptInfo info, DateTime startUtc, DateTime endUtc, string? remote, string origin, CancellationToken ct)
    {
        var common = new Dictionary<string, object?>
        {
            ["origin"] = origin,
            ["mode"] = TranscriptKind.Mode(info.HostApplication),
            ["host"] = _host,
            ["sessionUser"] = info.SessionUser,
            ["remoteAddr"] = remote,
            ["startedAt"] = startUtc.ToString("o"),
            ["endedAt"] = endUtc.ToString("o"),
            ["durationSec"] = Math.Max(0, (endUtc - startUtc).TotalSeconds),
            ["source"] = "win-transcript",
            ["shell"] = info.Shell,
            ["version"] = AppVersion.Current,
        };

        if (info.Commands.Count > 0)
        {
            var input = new Dictionary<string, object?>(common)
            {
                ["kind"] = "terminal-input",
                ["sessionId"] = info.ProcessId,
                ["input"] = string.Join("\n", info.Commands),
            };
            if (!await _client.UploadAsync(JsonSerializer.Serialize(input), fileName + " (input)", Array.Empty<(string, string, byte[])>(), ct))
                return false;
        }

        var output = new Dictionary<string, object?>(common)
        {
            ["kind"] = "terminal-output",
            ["outFile"] = fileName,
            ["timingFile"] = "",
            ["auditSession"] = info.ProcessId,
        };
        return await _client.UploadAsync(JsonSerializer.Serialize(output), fileName,
            new[] { ("out", fileName, Encoding.UTF8.GetBytes(text)) }, ct);
    }

    // Открытый транскрипт: сразу решаем, чья сессия (пока 4688 о старте ещё в
    // журнале), — даже если сервер не принимает куски; SSH-сессию шлём кусками.
    private async Task LiveAsync(FileInfo f, CancellationToken ct)
    {
        if (_origins.Get(f.Name)?.Origin == SessionOrigin.NotSsh) return;
        int? interval = _client.LiveIntervalSec;
        if (interval is null && _origins.Get(f.Name) is not null) return;
        if (_live.TryGetValue(f.FullName, out var st) && interval is { } iv && DateTime.UtcNow - st.LastSentUtc < TimeSpan.FromSeconds(iv)) return;
        string? header = ReadHeader(f.FullName);
        if (header is null || ParseTranscript(header) is not { } info) return;
        DateTime startUtc = info.StartedLocal.ToUniversalTime();
        if (st is null)
        {
            // Unknown — событий 4688 ещё нет (журнал не дописан): спросим в следующий раз.
            if (OriginOf(f, info) is not { } known) return;
            st = new LiveTranscript { Origin = known.Origin, Remote = known.Remote, Label = known.Label ?? TranscriptKind.Unknown };
            _live[f.FullName] = st;
        }
        if (interval is null || st.Origin != SessionOrigin.Ssh) return;
        string? text = ReadShared(f.FullName);
        if (text is null || ParseTranscript(text) is not { } fullInfo) return;
        await SendChunksAsync(f, text, fullInfo, startUtc, DateTime.UtcNow, st.Remote, st, live: true, ct);
    }

    // Чья сессия: запомненное (TranscriptOrigins), иначе по учётке процесса или
    // по 4688 — и запомнить. null — событий нет (Unknown), спросим при следующем опросе.
    private TranscriptOrigins.Entry? OriginOf(FileInfo f, TranscriptInfo info)
    {
        var saved = _origins.Get(f.Name);
        if (saved is { Label: not null }) return saved;
        // Решения до 0.16 — без метки: SSH однозначен, «не SSH» уточняем ниже.
        if (saved is { Origin: SessionOrigin.Ssh }) return Remember(f, saved with { Label = TranscriptKind.Ssh });
        // SYSTEM/служба/машинная учётка — не SSH-вход, 4688 не нужен.
        if (TranscriptKind.IsServiceAccount(info.RunAsUser, SidOf))
        {
            _issues.TryRemove(f.Name, out _);
            return Remember(f, new TranscriptOrigins.Entry(SessionOrigin.NotSsh, null, TranscriptKind.Automation));
        }
        DateTime startUtc = info.StartedLocal.ToUniversalTime();
        var decision = SshAncestry.ClassifyDetailed(info.ProcessId, startUtc, _history.Index, info.HostApplication);
        if (decision.Origin == SessionOrigin.Unknown)
        {
            if (saved is not null) return Remember(f, saved with { Label = TranscriptKind.Unknown });
            _issues[f.Name] = decision.Reason == TranscriptIssue.EventMissing
                ? _history.MissingReason(startUtc, DateTime.UtcNow) : decision.Reason ?? TranscriptIssue.EventMissing;
            return null;
        }
        _issues.TryRemove(f.Name, out _);
        string? remote = decision.Origin == SessionOrigin.Ssh
            ? SshAncestry.RemoteAddress(info.SessionUser, decision.SshdPid, startUtc, ReadSshAccepts(startUtc - TimeSpan.FromMinutes(2), startUtc + SshAncestry.After))
            : null;
        return Remember(f, new TranscriptOrigins.Entry(decision.Origin, remote, decision.Label));
    }

    // Имя учётки → SID локальными средствами Windows (LSA); не разрешилось — null.
    private static string? SidOf(string account)
    {
        try { return ((System.Security.Principal.SecurityIdentifier)new System.Security.Principal.NTAccount(account).Translate(typeof(System.Security.Principal.SecurityIdentifier))).Value; }
        catch (Exception ex) when (ex is System.Security.Principal.IdentityNotMappedException or SystemException) { return null; }
    }

    private TranscriptOrigins.Entry Remember(FileInfo f, TranscriptOrigins.Entry entry)
    {
        _origins.Set(f.Name, entry.Origin, entry.Remote, entry.Label);
        return entry;
    }

    // Куски ввода (команды) и вывода (текст транскрипта) с того места, до
    // которого сервер уже принял. Идущая сессия — текст до последней целой
    // строки. true — обе записи на сервере закрыты (live=false принят).
    private async Task<bool> SendChunksAsync(FileInfo f, string text, TranscriptInfo info, DateTime startUtc, DateTime endUtc,
        string? remote, LiveTranscript st, bool live, CancellationToken ct)
    {
        var common = new Dictionary<string, object?>
        {
            ["host"] = _host,
            ["sessionUser"] = info.SessionUser,
            ["remoteAddr"] = remote,
            ["startedAt"] = startUtc.ToString("o"),
            ["endedAt"] = endUtc.ToString("o"),
            ["source"] = "win-transcript",
            ["shell"] = info.Shell,
            ["version"] = AppVersion.Current,
            ["live"] = live,
            ["origin"] = st.Label,
            ["mode"] = TranscriptKind.Mode(info.HostApplication),
        };
        bool ok = true;
        string input = string.Join("\n", info.Commands);
        if (input.Length > st.InputSent || (!live && st.InputSent > 0))
        {
            int from = (int)Math.Min(st.InputSent, input.Length);
            var meta = new Dictionary<string, object?>(common)
            {
                ["kind"] = "terminal-input",
                ["sessionId"] = info.ProcessId,
                ["input"] = input[from..],
                ["inputOffset"] = from,
            };
            var r = await _client.UploadChunkAsync(JsonSerializer.Serialize(meta), f.Name + " (input)", [], ct);
            if (r.Outcome is ChunkOutcome.Accepted or ChunkOutcome.Gap) st.InputSent = r.Long("inputLength");
            ok &= r.Outcome is ChunkOutcome.Accepted or ChunkOutcome.Closed && !(r.Outcome == ChunkOutcome.Accepted && st.InputSent < input.Length);
        }

        string body = live ? text[..(text.LastIndexOf('\n') + 1)] : text;
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        if (bytes.Length > st.OutSent || !live)
        {
            long from = Math.Min(st.OutSent, bytes.Length);
            var meta = new Dictionary<string, object?>(common)
            {
                ["kind"] = "terminal-output",
                ["outFile"] = f.Name,
                ["timingFile"] = "",
                ["auditSession"] = info.ProcessId,
                ["outOffset"] = from,
                ["timingOffset"] = 0,
            };
            var r = await _client.UploadChunkAsync(JsonSerializer.Serialize(meta), f.Name, [("out", f.Name, bytes[(int)from..])], ct);
            if (r.Outcome is ChunkOutcome.Accepted or ChunkOutcome.Gap) st.OutSent = r.Long("outSize");
            ok &= r.Outcome is ChunkOutcome.Accepted or ChunkOutcome.Closed;
        }
        st.LastSentUtc = DateTime.UtcNow;
        return ok;
    }

    // Транскрипт идущей сессии: PowerShell держит его открытым на запись.
    private static string? ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return sr.ReadToEnd();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? ReadIfClosed(string path, bool headerOnly = false)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return headerOnly ? ReadStart(sr) : sr.ReadToEnd();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string ReadStart(StreamReader reader)
    {
        var buffer = new char[65536];
        int read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }

    private void TryDelete(string path, string? reason)
    {
        try
        {
            File.Delete(path);
            if (reason is not null) _logger.LogWarning("Transcript {File} deleted: {Reason}", Path.GetFileName(path), reason);
        }
        catch (Exception ex) { _logger.LogWarning("Transcript {File} delete failed: {Error}", Path.GetFileName(path), ex.Message); }
    }

    private List<SshAccept> ReadSshAccepts(DateTime fromUtc, DateTime toUtc)
    {
        var list = new List<SshAccept>();
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("OpenSSH/Operational", PathType.LogName, TimeQuery(null, fromUtc, toUtc)));
            for (var r = reader.ReadEvent(); r is not null; r = reader.ReadEvent())
            {
                using (r)
                {
                    if (r.TimeCreated is null) continue;
                    string message;
                    try { message = r.FormatDescription() ?? ""; }
                    catch (EventLogException) { message = ""; }
                    if (message.Length == 0) message = string.Join(" ", r.Properties.Select(x => x.Value?.ToString()));
                    if (SshAncestry.ParseAccept(r.TimeCreated.Value.ToUniversalTime(), r.ProcessId ?? 0, message) is { } a) list.Add(a);
                }
            }
        }
        catch (EventLogException) { /* нет журнала OpenSSH — адрес останется пустым */ }
        return list;
    }

    private static string TimeQuery(int? eventId, DateTime fromUtc, DateTime toUtc)
    {
        string id = eventId is null ? "" : $"(EventID={eventId}) and ";
        return $"*[System[{id}TimeCreated[@SystemTime>='{fromUtc:yyyy-MM-ddTHH:mm:ss.fffZ}' and @SystemTime<='{toUtc:yyyy-MM-ddTHH:mm:ss.fffZ}']]]";
    }

}
