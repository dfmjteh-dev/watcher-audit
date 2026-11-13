using System.Text;
using System.Text.Json;

namespace Watcher.TerminalAgent;

// Группирует AuditLine по ses= в "сессию" и финализирует её в JSON-файл
// очереди, когда видит USER_END. См. roadmap.md, Фаза 8 (пересмотр
// 2026-10-06) — это замена pty/asciicast-подхода: здесь только ВВОД
// (keystrokes), не полный экран, но зато без риска для login'а (ядро само
// решает писать/не писать по PAM enable=/disable=, см. /etc/pam.d/sshd).
public sealed class SessionCollector
{
    private sealed class PendingSession
    {
        public string? Acct;
        public string? Addr;
        public double StartedAtEpoch;
        public double LastSeenEpoch;
        // Задача 17 (roadmap-fixes.md): ввод — отдельно по каждому tty сессии.
        // pam_tty_audit включает аудит для всех процессов сессии, поэтому, когда
        // внутри SSH-сессии работает script(1) из watcher-term-wrap.sh (или
        // tmux/screen), каждое нажатие попадает в аудит дважды: script читает
        // внешний pty, bash — внутренний pty script'а. Склеенный по ses ввод
        // выходил задвоенным. Порядок tty — порядок первого появления.
        public readonly List<(string? Tty, StringBuilder Text)> InputByTty = new();
        // Терминал входа из USER_LOGIN — то, что набирал сам человек.
        public string? LoginTty;
        // 0.12, куски идущей сессии: сколько символов ввода уже ушло в очередь,
        // номер следующего куска и когда был последний.
        public int SentLength;
        public int Seq;
        public double LastFlushEpoch;
        // USER_END уже был, запись ждёт EndGrace (см. ниже).
        public double? EndedEpoch;
        // 0.12.2: кто открыл сессию (exe из USER_START: sshd, cron…) и запуски
        // процессов без терминала (правило auditd watcher-exec) — у сессии без
        // терминала (ssh хост 'команда', scp, автоматизация) ввода с клавиатуры
        // нет, и записью становится список запущенных команд.
        public string? StartExe;
        public readonly StringBuilder ExecLog = new();
        // Первый процесс от имени вошедшего (uid == auid) — его shell. Всё раньше —
        // подготовка сессии самим sshd/PAM (генерация MOTD и т.п.), не действия человека.
        public bool UserProcessSeen;
        // 0.14.3: в интерактивной SSH-сессии (вход с pty, LoginTty известен)
        // команды без терминала — это фоновые каналы клиента в том же подключении
        // (MobaXterm: мониторинг сервера, SFTP-браузер). Пишутся отдельной
        // записью «без терминала»: в общем вводе они подменяли собой набранное
        // человеком — первые куски уходили до ввода с терминала, а куски только
        // дописываются, и ввод терминала потом на сервер не попадал.
        public PendingSession? Background;
        public bool IsBackground;
        public bool HasTtyInput => InputByTty.Any(t => t.Text.Length > 0);
        public string Source => HasTtyInput || ExecLog.Length == 0 ? "linux-tty-audit" : "linux-exec";

        public StringBuilder InputFor(string? tty)
        {
            foreach (var (t, sb) in InputByTty)
                if (t == tty) return sb;
            var created = new StringBuilder();
            InputByTty.Add((tty, created));
            return created;
        }

        // Ввод на терминале входа, если он известен и на нём что-то есть; иначе
        // (агент стартовал посреди сессии и USER_LOGIN не видел, вход не с
        // pts/tty) — всё подряд, как до Задачи 17: лучше дубль, чем потерять
        // ввод.
        public string ResolveInput()
        {
            if (!HasTtyInput && ExecLog.Length > 0) return ExecLog.ToString();
            if (LoginTty is not null)
                foreach (var (t, sb) in InputByTty)
                    if (t == LoginTty && sb.Length > 0) return sb.ToString();
            var all = new StringBuilder();
            foreach (var (_, sb) in InputByTty) all.Append(sb);
            return all.ToString();
        }
    }

    private readonly Dictionary<int, PendingSession> _pending = new();
    private readonly string _queueDir;
    private readonly string _host;
    // Интервал кусков от сервера (UploadClient.LiveIntervalSec); null — сервер
    // кусков не принимает или выгрузка выключена: сессия уходит целиком в конце.
    private readonly Func<int?> _liveInterval;
    // Сколько ждать после USER_END, прежде чем записать сессию: при обрыве SSH
    // ядро сбрасывает буфер ввода (TTY) уже после USER_END — shell умирает от
    // SIGHUP позже, чем sshd закрывает сессию. Без ожидания такой ввод заводил
    // новую сессию без пользователя («unknown»), с задвоенным вводом (терминал
    // входа из USER_LOGIN остался у закрытой). 0 — писать сразу.
    private readonly double _endGraceSeconds;

    public SessionCollector(string queueDir, string host, Func<int?>? liveInterval = null, double endGraceSeconds = 0)
    {
        _queueDir = queueDir;
        _host = host;
        _liveInterval = liveInterval ?? (() => null);
        _endGraceSeconds = endGraceSeconds;
    }

    // 0.12: ввод идущих сессий — кусок раз в интервал, если появилось новое.
    // Вызывается из основного цикла (Program.cs) после Feed.
    public void FlushLive(double nowEpoch)
    {
        CommitExec();
        if (_liveInterval() is not { } interval) return;
        foreach (var (ses, main) in _pending)
        {
            if (main.EndedEpoch is not null) continue;
            foreach (var session in main.Background is { } bg ? [main, bg] : new[] { main })
            {
                if (nowEpoch - session.LastFlushEpoch < interval) continue;
                string input = session.ResolveInput();
                if (input.Length <= session.SentLength) continue;
                session.LastFlushEpoch = nowEpoch;
                WriteChunk(ses, session, main, input, session.LastSeenEpoch, live: true);
            }
        }
    }

    // Запуск процесса (SYSCALL по правилу watcher-exec) ждёт свои EXECVE и CWD —
    // они идут следующими строками того же события.
    private (long Serial, int Ses, double Ts, string? Cwd, IReadOnlyList<string>? Argv, bool ByUser)? _exec;
    private const int MaxExecLine = 4096;

    private void CommitExec()
    {
        if (_exec is not { } e) return;
        _exec = null;
        if (e.Argv is not { Count: > 0 } argv || !_pending.TryGetValue(e.Ses, out var session)) return;
        // Только SSH-сессии (или начатые до запуска агента, где USER_START не
        // видели): у cron и systemd-сессий тоже есть auid, но это не человек.
        if (session.StartExe is { } exe && !exe.EndsWith("/sshd", StringComparison.Ordinal)) return;
        if (e.ByUser) session.UserProcessSeen = true;
        if (!session.UserProcessSeen) return;
        var t = DateTimeOffset.FromUnixTimeMilliseconds((long)(e.Ts * 1000)).UtcDateTime;
        string line = $"{t:HH:mm:ss}Z {e.Cwd ?? "?"}$ {string.Join(' ', argv.Select(Quote))}";
        if (line.Length > MaxExecLine) line = line[..MaxExecLine] + "…";
        var target = session.LoginTty is null ? session : session.Background ??= new PendingSession
        {
            Acct = session.Acct,
            Addr = session.Addr,
            StartExe = session.StartExe,
            StartedAtEpoch = e.Ts,
            UserProcessSeen = true,
            IsBackground = true,
        };
        target.ExecLog.Append(line).Append('\n');
        target.LastSeenEpoch = Math.Max(target.LastSeenEpoch, e.Ts);
        session.LastSeenEpoch = Math.Max(session.LastSeenEpoch, e.Ts);
    }

    // Утилиты смены пользователя — их PAM-сессии вложены в сессию входа.
    private static readonly string[] NestedOpeners = ["sudo", "su", "runuser", "pkexec", "doas"];

    public static bool IsNestedOpener(string? exe) =>
        exe is not null && NestedOpeners.Contains(Path.GetFileName(exe), StringComparer.Ordinal);

    private static string Quote(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || "-_./=:,@%+".Contains(c)) ? arg : "'" + arg.Replace("'", "'\\''") + "'";

    public void Feed(AuditLine line)
    {
        if (_exec is { } pendingExec && line.Serial != pendingExec.Serial) CommitExec();
        switch (line.Type)
        {
            case AuditLineType.Syscall:
                // С терминалом — интерактивная сессия, её ввод уже пишет pam_tty_audit.
                if (line.Terminal is "(none)")
                {
                    // Агент стартовал посреди сессии и USER_START не видел — заводим её здесь.
                    if (!_pending.ContainsKey(line.Ses))
                        _pending[line.Ses] = new PendingSession { StartedAtEpoch = line.TimestampEpoch, LastSeenEpoch = line.TimestampEpoch };
                    _exec = (line.Serial, line.Ses, line.TimestampEpoch, null, null, line.Uid is { } uid && uid == line.Auid);
                }
                break;

            case AuditLineType.Execve:
                if (_exec is { } ex && ex.Serial == line.Serial) _exec = ex with { Argv = line.Argv };
                break;

            case AuditLineType.Cwd:
                if (_exec is { } ec && ec.Serial == line.Serial) _exec = ec with { Cwd = line.Cwd };
                break;
            case AuditLineType.UserStart:
                // sudo/su внутри сессии открывают свою PAM-сессию с тем же ses= —
                // это не начало входа: раньше подменялись пользователь (на root) и
                // время начала, а USER_END от sudo закрывал всю SSH-сессию.
                if (IsNestedOpener(line.Exe))
                {
                    if (_pending.TryGetValue(line.Ses, out var outer)) outer.LastSeenEpoch = line.TimestampEpoch;
                    break;
                }
                // Если сессия уже существует (TTY-событие пришло раньше, чем
                // USER_START — например, после рестарта агента с bookmark'ом
                // посреди сессии) — не затираем уже накопленный Input.
                if (!_pending.TryGetValue(line.Ses, out var existing))
                {
                    existing = new PendingSession();
                    _pending[line.Ses] = existing;
                }
                // 0.14.3: sshd на RHEL/Alma пишет второй USER_START («op=login», без
                // acct) — он не должен стирать пользователя и начало сессии.
                bool repeated = existing.StartExe is not null;
                existing.Acct = line.Acct ?? existing.Acct;
                existing.Addr = line.Addr ?? existing.Addr;
                if (!repeated)
                {
                    existing.StartedAtEpoch = line.TimestampEpoch;
                    existing.StartExe = line.Exe;
                }
                existing.LastSeenEpoch = line.TimestampEpoch;
                break;

            case AuditLineType.Tty:
                if (!_pending.TryGetValue(line.Ses, out var session))
                {
                    session = new PendingSession { StartedAtEpoch = line.TimestampEpoch };
                    _pending[line.Ses] = session;
                }
                if (line.DataHex is { Length: > 0 } hex)
                {
                    try { session.InputFor(line.Tty).Append(AuditLineParser.DecodeHex(hex)); }
                    catch (FormatException) { /* повреждённый hex — пропускаем кусок, не всю сессию */ }
                }
                session.LastSeenEpoch = line.TimestampEpoch;
                break;

            case AuditLineType.UserLogin:
                // Идёт после USER_START, до первых TTY (те сбрасываются ядром
                // при выходе процесса/заполнении буфера) — но порядок не важен:
                // выбор терминала делается только при записи (ResolveInput).
                if (!_pending.TryGetValue(line.Ses, out var loginSession))
                {
                    loginSession = new PendingSession { StartedAtEpoch = line.TimestampEpoch };
                    _pending[line.Ses] = loginSession;
                }
                loginSession.LoginTty = line.LoginTty;
                loginSession.LastSeenEpoch = line.TimestampEpoch;
                break;

            case AuditLineType.UserEnd:
                if (IsNestedOpener(line.Exe)) break;
                if (_endGraceSeconds > 0)
                {
                    if (_pending.TryGetValue(line.Ses, out var ending)) ending.EndedEpoch = line.TimestampEpoch;
                }
                else if (_pending.Remove(line.Ses, out var finished))
                {
                    WriteRecord(line.Ses, finished, line.TimestampEpoch);
                }
                break;
        }
    }

    // Сессии без USER_END (оборвались без штатного закрытия) — сбрасываем,
    // чтобы не течь памятью на долгоживущем процессе; вызывается периодически
    // из Program.cs, не на каждый Feed.
    public void SweepAbandoned(double nowEpoch, double timeoutSeconds)
    {
        CommitExec();
        var toDrop = new List<int>();
        foreach (var (ses, session) in _pending)
        {
            if (nowEpoch - session.LastSeenEpoch > timeoutSeconds
                || (session.EndedEpoch is { } ended && nowEpoch - ended >= _endGraceSeconds))
                toDrop.Add(ses);
        }
        foreach (var ses in toDrop)
        {
            _pending.Remove(ses, out var session);
            if (session is not null) WriteRecord(ses, session, session.EndedEpoch ?? nowEpoch);
        }
    }

    private void WriteRecord(int ses, PendingSession session, double endedAtEpoch)
    {
        WriteOne(ses, session, session, endedAtEpoch);
        if (session.Background is { } bg) WriteOne(ses, bg, session, endedAtEpoch);
    }

    // main — сессия входа: пользователь и адрес, если у фоновой записи их ещё
    // не было при создании (USER_START пришёл позже первых команд).
    private void WriteOne(int ses, PendingSession session, PendingSession main, double endedAtEpoch)
    {
        string input = session.ResolveInput();
        // Часть уже ушла кусками — закрываем запись последним куском.
        if (session.Seq > 0)
        {
            WriteChunk(ses, session, main, input, endedAtEpoch, live: false);
            return;
        }
        if (input.Length == 0) return; // пустая сессия (например, только MOTD, без ввода) — не пишем

        var startedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(session.StartedAtEpoch * 1000)).UtcDateTime;
        var endedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(endedAtEpoch * 1000)).UtcDateTime;

        var record = new
        {
            kind = "terminal-input",
            host = _host,
            sessionUser = session.Acct ?? main.Acct ?? "unknown",
            remoteAddr = session.Addr ?? main.Addr,
            startedAt = startedAt.ToString("o"),
            endedAt = endedAt.ToString("o"),
            durationSec = (endedAt - startedAt).TotalSeconds,
            input,
            sessionId = ses,
            auditSession = AuditSessionOf(ses, session),
            // Задача F2: источник записи — для фильтра по ОС в #/console.
            source = session.Source,
            version = AppVersion.Current,
        };

        Directory.CreateDirectory(_queueDir);
        string path = Path.Combine(_queueDir, $"{startedAt:yyyyMMdd-HHmmss}-{ses}{Suffix(session)}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
    }

    // Кусок: ввод с SentLength (если терминал входа определился позже и строка
    // стала короче — с её конца, ничего), inputOffset — для сервера. Имя файла
    // с номером куска — UploadRunner шлёт очередь по порядку имён.
    // Фоновая запись — без привязки к выводу терминала: сервер связывает вывод
    // с вводом по audit-сессии, 0 — ни с чем (у вывода такой нет).
    private static long? AuditSessionOf(int ses, PendingSession session) => session.IsBackground ? 0 : ses;

    private static string Suffix(PendingSession session) => session.IsBackground ? "-bg" : "";

    private void WriteChunk(int ses, PendingSession session, PendingSession main, string input, double endedAtEpoch, bool live)
    {
        int offset = Math.Min(session.SentLength, input.Length);
        var startedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(session.StartedAtEpoch * 1000)).UtcDateTime;
        var endedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(endedAtEpoch * 1000)).UtcDateTime;
        var record = new
        {
            kind = "terminal-input",
            host = _host,
            sessionUser = session.Acct ?? main.Acct ?? "unknown",
            remoteAddr = session.Addr ?? main.Addr,
            startedAt = startedAt.ToString("o"),
            endedAt = endedAt.ToString("o"),
            input = input[offset..],
            inputOffset = offset,
            live,
            sessionId = ses,
            auditSession = AuditSessionOf(ses, session),
            source = session.Source,
            version = AppVersion.Current,
        };
        session.Seq++;
        session.SentLength = input.Length;
        Directory.CreateDirectory(_queueDir);
        string path = Path.Combine(_queueDir, $"{startedAt:yyyyMMdd-HHmmss}-{ses}{Suffix(session)}-{session.Seq:D5}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
    }
}
