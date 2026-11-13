using Microsoft.Extensions.Logging;
using Serilog;
using Watcher.Uploader;

var cfg = UploaderConfig.Load(AppContext.BaseDirectory);

Directory.CreateDirectory(cfg.QueueDir);
Directory.CreateDirectory(cfg.SentDir);
Directory.CreateDirectory(cfg.LogDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(cfg.LogDir, "uploader-.log"),
        outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: cfg.LogRetainedDays,
        fileSizeLimitBytes: cfg.LogFileSizeLimitMb * 1024L * 1024L,
        rollOnFileSizeLimit: true,
        shared: true)
    .CreateLogger();

using var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog().AddProvider(new DiagnosticLoggerProvider(DiagnosticJournal.For(cfg.CredentialPath), "uploader")));
var logger = loggerFactory.CreateLogger("Watcher.Uploader");

logger.LogInformation("Watcher.Uploader {Version} starting", AppVersion.Current);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
// Задача G1c: на Linux это системная служба — `systemctl stop` шлёт SIGTERM,
// без обработки процесс завершался с кодом 143 и юнит уходил в failed
// (проверено на стенде). Тот же приём, что у Watcher.Capture.Linux/TerminalAgent.
using var sigTerm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });

// H2: обновление агента — только если стоит пакетом (.deb/.rpm) и запущен от root.
var installer = LinuxPackageInstaller.Detect(logger);
// 0.13: хост удалён в веб-UI — удалить пакет (только установка пакетом).
if (OperatingSystem.IsLinux() && installer is not null) AgentRemoval.Remover = AgentRemoval.RemoveLinuxPackage;
// 0.16: пакет с GuardRepair — защиту включаем сами по ключу с сервера (как Windows из MSI).
if (OperatingSystem.IsLinux() && installer is not null && File.Exists(LinuxProtection.GuardRepair)) ProtectionActivation.Activator = LinuxProtection.Start;
await UploaderLoop.RunAsync(cfg, loggerFactory, cts.Token, installer);

logger.LogInformation("Watcher.Uploader stopped");
Log.CloseAndFlush();
