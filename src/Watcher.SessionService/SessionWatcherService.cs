using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using UploaderConfig = Watcher.Uploader.UploaderConfig;
using UploaderLoop = Watcher.Uploader.UploaderLoop;

namespace Watcher.SessionService;

// Session-0 служба, запускающая Watcher.Capture.exe в сессии каждого
// залогиненного пользователя через CreateProcessAsUser. Появилась в Фазе 6
// (roadmap.md) для RDS — вместо Scheduled Task на BUILTIN\Users, который не
// давал процесса второму одновременному пользователю (MultipleInstances=
// IgnoreNew работает на уровне задачи, не сессии). С Задачи D1
// (roadmap-next.md) — единственный способ запуска Capture на любой станции:
// обычная станция — та же схема с одной сессией (вход после загрузки,
// блокировка, быстрое переключение пользователей проверены на Win10/11).
//
// Находка относительно roadmap: там предлагался ручной P/Invoke
// WTSRegisterSessionNotification — ServiceBase.OnSessionChange даёт те же
// события через SCM без этого, проще. См. план спайка.
//
// Фаза 6 — полная реализация (roadmap.md): sessionUser передаётся в Capture
// через --session-user (используется в имени файла сегмента/sidecar-метаданных,
// см. Watcher.Capture/SegmentWriter.cs), watchdog-таймер ниже закрывает пробел
// tamper protection из docs/architecture.md («кассир с правами админа может
// убить процесс, поднимется только через минуту») — для хостов под этой
// службой респаун почти мгновенный, не раз в минуту по Scheduled Task.
// install.ps1 регистрирует эту службу всегда (с D1).
[SupportedOSPlatform("windows")]
public sealed class SessionWatcherService : ServiceBase
{
    private const int WatchdogIntervalMs = 15_000;

    private readonly SessionServiceConfig _cfg;
    private readonly ILogger _logger;
    // R15 (code-review-2026-10.md): _runningBySession и _userFilter трогают поток
    // SCM (OnStart/OnSessionChange/OnStop) и поток таймера watchdog — под одной
    // блокировкой. Держим её на всё решение «запускать ли Capture в сессии»
    // (TryLaunchForSession целиком), а не на отдельные обращения к словарю:
    // иначе SessionLogon и тик watchdog могут оба увидеть «не запущен» и поднять
    // два Capture в одной сессии. Внутри — только WTS/CreateProcessAsUser, это
    // миллисекунды; тики таймера, если вдруг затянутся, просто встают в очередь.
    private readonly object _sync = new();
    private readonly Dictionary<int, int> _runningBySession = new(); // sessionId -> pid
    private System.Threading.Timer? _watchdog;
    private bool _enumerateOnStart = true, _stopping;

    // Задача D2: выгрузка очереди — фоновая задача этой же службы (null — выключена,
    // SessionService:RunUploader=false). Capture от имени пользователя пишет в
    // ProgramData\Watcher\queue, служба от LocalSystem читает и удаляет — выгрузка
    // идёт и когда никто не залогинен, ApiToken не нужно давать читать пользователям.
    private readonly UploaderConfig? _uploaderCfg;
    private readonly ILoggerFactory? _uploaderLoggerFactory;
    private CancellationTokenSource? _uploaderCts;
    private Task? _uploaderTask;
    // Задача F1: консольный аудит SSH — на том же токене отмены, что и выгрузка.
    private Task? _terminalAuditTask;

    // Задача D3: кого писать — решает служба (allowUsers/excludeUsers из конфига
    // хоста/группы на сервере): в сессии исключённого пользователя Capture не
    // запускается и не перезапускается. Файл — кэш, который пишет heartbeat;
    // перечитывается в тике watchdog, если поменялось время записи.
    private readonly string _configCachePath;
    private UserFilter? _userFilter;
    private DateTime _userFilterStamp = DateTime.MinValue;

    public SessionWatcherService(SessionServiceConfig cfg, ILogger logger, UploaderConfig? uploaderCfg = null, ILoggerFactory? uploaderLoggerFactory = null)
    {
        _cfg = cfg;
        _logger = logger;
        _configCachePath = uploaderCfg?.ConfigCachePath is { Length: > 0 } p ? p : cfg.ConfigCachePath;
        _uploaderCfg = uploaderCfg;
        _uploaderLoggerFactory = uploaderLoggerFactory;
        ServiceName = "WatcherSessionService";
        CanHandleSessionChangeEvent = true;
    }

    protected override void OnStart(string[] args)
    {
        _logger.LogInformation("Watcher.SessionService {Version} starting", AppVersion.Current);
        // WTS enumeration and process launch must not block the SCM start callback.
        // The watchdog reconciles existing sessions immediately, then every 15 s.
        _watchdog = new System.Threading.Timer(_ => WatchdogTick(), null, 0, WatchdogIntervalMs);

        if (_uploaderCfg is not null && _uploaderLoggerFactory is not null)
        {
            _uploaderCts = new CancellationTokenSource();
            var ct = _uploaderCts.Token;
            // 0.13: хост удалён в веб-UI — служба удаляет агента со станции.
            Watcher.Uploader.AgentRemoval.Remover = WindowsSelfRemover.Remove;
            _uploaderTask = Task.Run(async () =>
            {
                // Цикл сам ловит ошибки отдельных проходов; сюда долетает только то,
                // что его уронило целиком — тогда перезапуск через минуту, а не
                // тихо мёртвая выгрузка до перезапуска службы.
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        // H2: обновление агента — замена файлов в каталоге этой службы,
                        // 0.13: у установленного из MSI — новым MSI (WindowsMsiInstaller).
                        await UploaderLoop.RunAsync(_uploaderCfg, _uploaderLoggerFactory, ct,
                            new WindowsUpdateInstaller(
                                new WindowsMsiInstaller(AppContext.BaseDirectory, _uploaderCfg.ServerUrl, _uploaderCfg.UpdatePublicKey ?? "",
                                    _cfg.TerminalAudit.Enabled, _logger),
                                _uploaderCfg.CredentialPath));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Uploader loop crashed, restarting in 60 s");
                        try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch (OperationCanceledException) { break; }
                    }
                }
            });
            _logger.LogInformation("Uploader started in-process (server {ServerUrl})", _uploaderCfg.ServerUrl);

            if (_cfg.TerminalAudit.Enabled)
            {
                var audit = new TerminalAudit(_cfg.TerminalAudit, _uploaderCfg, _uploaderLoggerFactory.CreateLogger("Watcher.TerminalAudit"));
                _terminalAuditTask = Task.Run(() => audit.RunAsync(ct));
                _logger.LogInformation("Terminal audit (SSH transcripts) started");
            }
        }
        else if (_cfg.TerminalAudit.Enabled)
        {
            _logger.LogWarning("TerminalAudit.Enabled, but RunUploader=false — nothing to upload with, terminal audit not started");
        }
    }

    protected override void OnStop()
    {
        int tracked;
        lock (_sync) { _stopping = true; tracked = _runningBySession.Count; }
        _logger.LogInformation("Watcher.SessionService stopping ({Count} tracked sessions)", tracked);
        _watchdog?.Dispose();
        _watchdog = null;

        if (_uploaderCts is not null)
        {
            _uploaderCts.Cancel();
            // Недогруженный файл не теряется — останется в очереди до следующего
            // старта; ждём недолго, чтобы не задерживать остановку службы.
            try { _uploaderTask?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
            try { _terminalAuditTask?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            _uploaderCts.Dispose();
            _uploaderCts = null;
        }
    }

    // Tamper protection: только для сессий, которые ещё легитимно залогинены
    // (запись убирается из _runningBySession на SessionLogoff, не на disconnect —
    // там процесс продолжает легитимно работать, просто бездействует по уже
    // разобранной motion-gating логике, см. docs/roadmap.md, Фаза 6, спайк).
    // Тик идёт на ThreadPool параллельно с OnSessionChange — см. _sync.
    private void WatchdogTick()
    {
        lock (_sync) { if (!_stopping) WatchdogTickLocked(); }
    }

    private void WatchdogTickLocked()
    {
        // Правила поменялись — запускаем Capture тем, кого теперь можно писать
        // (вход пользователя уже был, SessionLogon не повторится). Исключённым
        // теперь — работающий Capture не трогаем: он сам перестанет снимать
        // (ConfigReload в Capture, те же правила) и штатно закроет сегмент;
        // убитый сегмент восстанавливается только до последнего фрагмента и позже
        // (roadmap-fixes, Задача 18).
        if (RefreshUserFilter() || _enumerateOnStart)
        {
            foreach (var (sessionId, state) in EnumerateSessions())
            {
                if (state is SessionNative.WTS_CONNECTSTATE_CLASS.Active or SessionNative.WTS_CONNECTSTATE_CLASS.Connected)
                    TryLaunchForSession(sessionId, "config-change");
            }
            _enumerateOnStart = false;
        }

        var toRespawn = SessionLogic.SessionsNeedingRespawn(_runningBySession, IsAlive);
        foreach (var sessionId in toRespawn)
        {
            if (_runningBySession.TryGetValue(sessionId, out int pid))
            {
                _logger.LogWarning("Session {SessionId}: tracked Capture pid={Pid} is gone, respawning (watchdog)", sessionId, pid);
            }
            TryLaunchForSession(sessionId, "watchdog");
        }
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        int sessionId = changeDescription.SessionId;
        var reason = changeDescription.Reason;
        _logger.LogInformation("OnSessionChange: session={SessionId} reason={Reason}", sessionId, reason);

        lock (_sync)
        {
            switch (SessionLogic.ClassifyChange(reason))
            {
                case SessionChangeAction.Launch:
                    TryLaunchForSession(sessionId, reason.ToString());
                    break;

                case SessionChangeAction.StopTracking:
                    _runningBySession.Remove(sessionId);
                    _logger.LogInformation("Session {SessionId}: SessionLogoff, stopped tracking for watchdog", sessionId);
                    break;

                case SessionChangeAction.LogDisconnect:
                    LogDisconnectObservation(sessionId, reason);
                    break;

                case SessionChangeAction.Ignore:
                    break;
            }
        }
    }

    // Вызывать только под _sync (см. комментарий к полю).

    private void TryLaunchForSession(int sessionId, string trigger)
    {
        if (_runningBySession.TryGetValue(sessionId, out int existingPid) && IsAlive(existingPid))
        {
            _logger.LogInformation("Session {SessionId}: Capture already running (pid={Pid}), skip ({Trigger})",
                sessionId, existingPid, trigger);
            return;
        }

        if (SessionLogic.ExistingCaptureInSession(RunningCaptures(), sessionId) is int adoptedPid)
        {
            _runningBySession[sessionId] = adoptedPid;
            _logger.LogInformation("Session {SessionId}: Capture already running (pid={Pid}, not started by this service instance), adopting ({Trigger})",
                sessionId, adoptedPid, trigger);
            return;
        }

        string? queried = QueryUserName(sessionId);
        if (!SessionLogic.SessionHasUser(queried))
        {
            // Не ошибка: SessionLogon придёт отдельным событием после входа.
            _logger.LogInformation("Session {SessionId}: no user signed in yet, skip ({Trigger})", sessionId, trigger);
            return;
        }
        string userName = queried ?? "?";
        if (queried is not null && !SessionLogic.IsUserAllowed(_userFilter, queried))
        {
            // Снимаем с учёта, иначе watchdog пытался бы «перезапустить» каждые 15 сек.
            _runningBySession.Remove(sessionId);
            _logger.LogInformation("Session {SessionId} (user={User}): excluded by server config (allowUsers/excludeUsers), Capture not launched ({Trigger})",
                sessionId, userName, trigger);
            return;
        }
        _logger.LogInformation("Session {SessionId} (user={User}): launching Capture ({Trigger})", sessionId, userName, trigger);

        IntPtr hUserToken = IntPtr.Zero;
        IntPtr hDupToken = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;
        try
        {
            if (!SessionNative.WTSQueryUserToken(sessionId, out hUserToken))
            {
                _logger.LogWarning("Session {SessionId}: WTSQueryUserToken failed, GLE={Error}",
                    sessionId, Marshal.GetLastWin32Error());
                return;
            }

            if (!SessionNative.DuplicateTokenEx(hUserToken, SessionNative.TOKEN_ALL_ACCESS, IntPtr.Zero,
                    SessionNative.SECURITY_IMPERSONATION_LEVEL.Impersonation, SessionNative.TOKEN_TYPE.TokenPrimary, out hDupToken))
            {
                _logger.LogWarning("Session {SessionId}: DuplicateTokenEx failed, GLE={Error}",
                    sessionId, Marshal.GetLastWin32Error());
                return;
            }

            if (!SessionNative.CreateEnvironmentBlock(out envBlock, hDupToken, false))
            {
                _logger.LogWarning("Session {SessionId}: CreateEnvironmentBlock failed, GLE={Error}",
                    sessionId, Marshal.GetLastWin32Error());
                return;
            }

            var si = new SessionNative.STARTUPINFO
            {
                cb = Marshal.SizeOf<SessionNative.STARTUPINFO>(),
                lpDesktop = "winsta0\\default",
            };

            string workingDir = Path.GetDirectoryName(_cfg.CaptureExePath) ?? @"C:\Watcher";
            // --session-user прокидывается в Capture (см. Watcher.Capture/Program.cs,
            // SegmentWriter.cs) — различает имена файлов сегментов/лог-файлов между
            // одновременными сессиями, без этого они бы коллизили в общем queue/logs
            // (найдено спайком Фазы 6, см. docs/roadmap.md).
            string cmdLine = SessionLogic.BuildCaptureCommandLine(_cfg.CaptureExePath, userName);
            var cmdLineBuilder = new System.Text.StringBuilder(cmdLine);
            bool ok = SessionNative.CreateProcessAsUser(
                hDupToken,
                _cfg.CaptureExePath,
                cmdLineBuilder,
                IntPtr.Zero, IntPtr.Zero,
                false,
                SessionNative.CREATE_UNICODE_ENVIRONMENT | SessionNative.CREATE_NEW_CONSOLE,
                envBlock,
                workingDir,
                ref si,
                out var pi);

            if (!ok)
            {
                _logger.LogWarning("Session {SessionId} (user={User}): CreateProcessAsUser failed, GLE={Error}",
                    sessionId, userName, Marshal.GetLastWin32Error());
                return;
            }

            _logger.LogInformation("Session {SessionId} (user={User}): Capture started, pid={Pid}",
                sessionId, userName, pi.dwProcessId);
            _runningBySession[sessionId] = pi.dwProcessId;

            SessionNative.CloseHandle(pi.hThread);
            SessionNative.CloseHandle(pi.hProcess);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session {SessionId} (user={User}): unexpected failure launching Capture", sessionId, userName);
        }
        finally
        {
            if (envBlock != IntPtr.Zero) SessionNative.DestroyEnvironmentBlock(envBlock);
            if (hDupToken != IntPtr.Zero) SessionNative.CloseHandle(hDupToken);
            if (hUserToken != IntPtr.Zero) SessionNative.CloseHandle(hUserToken);
        }
    }

    // true — правила изменились (или прочитаны впервые с непустыми списками).
    private bool RefreshUserFilter()
    {
        DateTime stamp;
        try
        {
            stamp = File.Exists(_configCachePath) ? File.GetLastWriteTimeUtc(_configCachePath) : DateTime.MinValue;
        }
        catch (IOException) { return false; }
        if (stamp == _userFilterStamp) return false;
        _userFilterStamp = stamp;

        UserFilter? next = null;
        if (stamp != DateTime.MinValue)
        {
            try { next = SessionLogic.ParseUserFilter(File.ReadAllText(_configCachePath)); }
            catch (IOException ex)
            {
                // Файл как раз переписывается — прочитаем на следующем тике.
                _logger.LogWarning("Config cache {Path} not readable yet: {Error}", _configCachePath, ex.Message);
                _userFilterStamp = DateTime.MinValue;
                return false;
            }
        }
        string Describe(UserFilter? f) =>
            f?.AllowUsers is { Count: > 0 } a ? "only: " + string.Join(", ", a)
            : f?.ExcludeUsers is { Count: > 0 } e ? "all except: " + string.Join(", ", e)
            : "all users";
        bool changed = Describe(next) != Describe(_userFilter);
        _userFilter = next;
        if (changed) _logger.LogInformation("Capture user filter from server config: {Filter}", Describe(next));
        return changed;
    }

    private void LogDisconnectObservation(int sessionId, SessionChangeReason reason)
    {
        // Сознательно не трогаем процесс здесь — это и есть открытый вопрос спайка
        // (что происходит с BitBlt-захватом в disconnected-сессии), см. план теста
        // в docs/roadmap.md, Фаза 6. Логируем фактическое состояние для разбора.
        if (_runningBySession.TryGetValue(sessionId, out int pid))
        {
            bool alive = IsAlive(pid);
            _logger.LogInformation("Session {SessionId}: {Reason}, tracked Capture pid={Pid} alive={Alive}",
                sessionId, reason, pid, alive);
        }
        else
        {
            _logger.LogInformation("Session {SessionId}: {Reason}, no tracked Capture process", sessionId, reason);
        }
    }

    // Процессы Capture (по имени exe из конфига) с их сессиями.
    private IEnumerable<(int Pid, int SessionId)> RunningCaptures()
    {
        string name = Path.GetFileNameWithoutExtension(_cfg.CaptureExePath);
        var result = new List<(int, int)>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
        {
            using (p)
            {
                try
                {
                    if (!p.HasExited) result.Add((p.Id, p.SessionId));
                }
                catch (InvalidOperationException) { /* завершился между перечислением и проверкой */ }
                catch (System.ComponentModel.Win32Exception) { /* нет доступа — не наш случай под LocalSystem */ }
            }
        }
        return result;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? QueryUserName(int sessionId)
    {
        if (!SessionNative.WTSQuerySessionInformationW(IntPtr.Zero, sessionId, SessionNative.WTS_INFO_CLASS.WTSUserName,
                out IntPtr buffer, out _))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            SessionNative.WTSFreeMemory(buffer);
        }
    }

    private static IEnumerable<(int SessionId, SessionNative.WTS_CONNECTSTATE_CLASS State)> EnumerateSessions()
    {
        if (!SessionNative.WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out IntPtr pSessionInfo, out int count))
        {
            yield break;
        }

        try
        {
            int structSize = Marshal.SizeOf<SessionNative.WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<SessionNative.WTS_SESSION_INFO>(IntPtr.Add(pSessionInfo, i * structSize));
                yield return (info.SessionId, info.State);
            }
        }
        finally
        {
            SessionNative.WTSFreeMemory(pSessionInfo);
        }
    }
}
