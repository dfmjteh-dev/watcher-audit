using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Serilog;
using Watcher.TerminalAgent;

// СПАЙК Фазы 8 (roadmap.md), пересмотрен 2026-10-06 — первая версия (login
// shell + pty + asciicast) заменена на этот подход после ревью: подмена
// login shell'а пользователя — риск полного lockout при любом баге агента,
// слишком высокая цена за функциональность. Сейчас: `pam_tty_audit.so` в
// PAM session-стеке (`session optional pam_tty_audit.so enable=<юзер>`,
// см. install-заметку в roadmap.md) — ядро само тэгает сессию и пишет
// нажатия клавиш через auditd, НИЧЕГО не подменяя в пути exec. Fail-open
// проверено живьём: `session optional` + сбой/отсутствие auditd не мешает
// логину вообще. Эта программа — отдельный системный (не per-user, в
// отличие от Watcher.Capture.Linux) сервис, который читает общий
// /var/log/audit/audit.log и складывает законченные сессии в очередь.
//
// Ограничение, принятое осознанно (см. roadmap.md): это только ВВОД
// (keystrokes), не то, что пользователь видел на экране — vim/htop/вывод
// команд сюда не попадают. Если понадобится полный replay экрана — это
// отдельная, более рискованная задача (см. отвергнутый вариант с pty в
// истории git этого файла), не блокирует этот, более безопасный путь.
internal static class Program
{
    private static async Task<int> Main()
    {
        var cfgRoot = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("Watcher.TerminalAgent.json", optional: true)
            .Build();
        var cfg = new TerminalAgentConfig();
        cfgRoot.GetSection("TerminalAgent").Bind(cfg);

        Directory.CreateDirectory(cfg.QueueDir);
        Directory.CreateDirectory(cfg.LogDir);

        var diagnosticsJournal = Watcher.Uploader.DiagnosticJournal.For(cfg.CredentialPath);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(new Watcher.DiagnosticSink((template, error) => diagnosticsJournal.Capture("terminal", error, template)))
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}]: {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(cfg.LogDir, "watcher-term-.log"),
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}]: {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: cfg.LogRetainedDays,
                fileSizeLimitBytes: cfg.LogFileSizeLimitMb * 1024L * 1024L,
                rollOnFileSizeLimit: true,
                shared: true)
            .CreateLogger();
        var logger = Log.Logger;

        string host = Environment.MachineName;
        logger.Information("Watcher.TerminalAgent {Version} starting on {Host}, auditLog={AuditLog}",
            AppVersion.Current, host, cfg.AuditLogPath);

        var tailer = new AuditLogTailer(cfg.AuditLogPath, cfg.BookmarkPath);

        // Пусто — выгрузка выключена, агент работает как раньше (только
        // локальный сбор), см. Config.cs.
        UploadRunner? uploadRunner = null;
        UploadClient? uploadClient = null;
        if (!string.IsNullOrWhiteSpace(cfg.ServerUrl))
        {
            var http = UploadClient.NewHttp();
            var identity = new Watcher.Uploader.AgentIdentity(
                new Watcher.Uploader.AgentIdentityOptions(cfg.ServerUrl, cfg.EnrollToken, cfg.ApiToken, cfg.CredentialPath), http,
                new Serilog.Extensions.Logging.SerilogLoggerFactory(logger).CreateLogger("Watcher.TerminalAgent.Identity"),
                host, Watcher.Uploader.AgentIdentity.KindTerminal);
            uploadClient = new UploadClient(cfg.ServerUrl, http, identity);
            uploadRunner = new UploadRunner(cfg, uploadClient);
            logger.Information("upload to server enabled: {ServerUrl}", cfg.ServerUrl);
            // B1 (roadmap-next.md): ввод/вывод консоли и ApiToken по http на
            // не-localhost — открытым текстом по сети, на каждом старте, не
            // только один раз при установке (install-terminal-agent.sh).
            if (Uri.TryCreate(cfg.ServerUrl, UriKind.Absolute, out var serverUri) &&
                serverUri.Scheme == "http" && serverUri.Host is not ("localhost" or "127.0.0.1"))
            {
                logger.Warning(
                    "ServerUrl использует http:// на не-localhost адрес ({Host}) — консольный ввод/вывод и ApiToken идут по сети открытым текстом",
                    serverUri.Host);
            }
        }

        // 0.13: хост удалён в веб-UI — удалить пакет агента (только установка пакетом).
        if (Watcher.Uploader.AgentRemoval.IsLinuxPackageInstalled())
            Watcher.Uploader.AgentRemoval.Remover = Watcher.Uploader.AgentRemoval.RemoveLinuxPackage;
        var collector = new SessionCollector(cfg.QueueDir, host, () => uploadClient?.LiveIntervalSec, endGraceSeconds: 10);

        using var cts = new CancellationTokenSource();
        void RequestShutdown(string reason)
        {
            cts.Cancel();
            logger.Information("shutdown requested ({Reason})", reason);
        }
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; RequestShutdown("SIGINT"); });
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; RequestShutdown("SIGTERM"); });

        DateTime lastSentRetention = DateTime.MinValue;
        using var diagnosticsHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var diagnostics = uploadClient is null ? null : new Watcher.Uploader.DiagnosticClient(diagnosticsHttp, uploadClient.Identity, cfg.ServerUrl, diagnosticsJournal, cfg.LogDir);
        var diagnosticResources = new Watcher.Uploader.AgentResources(new[] { cfg.QueueDir, cfg.RawOutputDir });
        DateTime lastAgeSweep = DateTime.MinValue;
        // Срок, присланный сервером, — в файл рядом с учёткой: без сервера после
        // перезапуска действует он, а не appsettings.
        string queueAgePath = Path.Combine(Path.GetDirectoryName(cfg.CredentialPath) ?? cfg.QueueDir, "queue-max-age-days");
        int queueMaxAgeDays = QueueAgeState.Load(queueAgePath) ?? cfg.QueueMaxAgeDays;

        while (!cts.IsCancellationRequested)
        {
            try
            {
                var lines = tailer.ReadNewLines();
                int parsed = 0;
                foreach (var raw in lines)
                {
                    var parsedLine = AuditLineParser.TryParse(raw);
                    if (parsedLine is null) continue;
                    collector.Feed(parsedLine);
                    parsed++;
                }
                if (parsed > 0) logger.Information("processed {Count} audit lines", parsed);

                collector.SweepAbandoned(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), cfg.AbandonedSessionTimeoutSeconds);
                collector.FlushLive(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                // Квота на неотправленное — независимо от того, включена ли
                // выгрузка: без сервера (или пока он недоступен) это
                // единственное, что не даёт queue/raw-output расти
                // неограниченно (см. Config.cs, TerminalQueueQuota.cs).
                TerminalQueueQuota.ApplyInput(cfg.QueueDir, cfg.QueueMaxBytes, cfg.UploadQuietSeconds, logger);
                TerminalQueueQuota.ApplyOutput(cfg.RawOutputDir, cfg.RawOutputMaxBytes, cfg.UploadQuietSeconds, logger);
                if (uploadClient?.QueueMaxAgeDays is { } serverDays && serverDays != queueMaxAgeDays)
                {
                    queueMaxAgeDays = serverDays;
                    QueueAgeState.Save(queueAgePath, serverDays, logger);
                }
                if ((DateTime.UtcNow - lastAgeSweep).TotalMinutes >= 10)
                {
                    lastAgeSweep = DateTime.UtcNow;
                    foreach (var dir in new[] { cfg.QueueDir, cfg.RawOutputDir, cfg.LiveStateDir })
                        TerminalQueueQuota.ApplyAge(dir, queueMaxAgeDays, DateTime.UtcNow, logger);
                }

                if (uploadClient is not null) await uploadClient.Identity.EnsureEnrolledAsync(cts.Token);
                if (uploadRunner is not null) await uploadRunner.RunOnceAsync(logger, cts.Token);
                if (diagnostics is not null) await diagnostics.SendAsync(AppVersion.Current, () => diagnosticResources.Collect(DateTime.UtcNow), cfg.QueueDir, cts.Token);

                if ((DateTime.UtcNow - lastSentRetention).TotalMinutes >= Math.Max(5, cfg.SentRetentionScanIntervalMinutes))
                {
                    TerminalRetention.Apply(cfg.QueueSentDir, cfg.QueueSentRetentionDays, cfg.QueueSentMaxBytes, logger);
                    TerminalRetention.Apply(cfg.RawOutputSentDir, cfg.RawOutputSentRetentionDays, cfg.RawOutputSentMaxBytes, logger);
                    lastSentRetention = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                // Системный сервис — одна плохая строка/временная недоступность
                // audit.log не должна убивать весь процесс, см. Watcher.Uploader
                // (тот же принцип "лог и продолжить", не crash-loop).
                logger.Warning(ex, "poll iteration failed, continuing");
            }

            try { Task.Delay(TimeSpan.FromSeconds(cfg.PollIntervalSeconds), cts.Token).Wait(cts.Token); }
            catch (OperationCanceledException) { break; }
        }

        logger.Information("Watcher.TerminalAgent stopped");
        Log.CloseAndFlush();
        return 0;
    }
}
