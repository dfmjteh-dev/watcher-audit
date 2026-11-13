using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Watcher.SessionService;
using UploaderConfig = Watcher.Uploader.UploaderConfig;

[assembly: SupportedOSPlatform("windows")]

// Установщик запрашивает политику через уже собранный агент: PowerShell
// Add-Type запускает csc.exe из временного каталога и может быть запрещён.
if (args.Length == 1 && args[0] == "--process-creation-audit")
{
    try { Console.WriteLine(WindowsAuditPolicy.ProcessCreationEnabled() ? "enabled" : "disabled"); }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
    return;
}

// 0.16: установка обновления MSI одноразовой SYSTEM-задачей (без PowerShell).
if (args.Length == 2 && args[0] == "--apply-msi-update")
{
    Environment.ExitCode = MsiUpdateHelper.Run(args[1]);
    return;
}

// Watcher.SessionService.json — установщик пишет сюда, тот же приём,
// что Watcher.Capture.json/Watcher.Uploader.json: "appsettings.json" у каждого
// из трёх проектов свой build-time файл, который установщик сознательно НЕ
// копирует в общий $InstallDir (иначе три одинаково названных файла от разных
// exe конфликтовали бы в одной папке) — только это именованное переопределение.
var cfgRoot = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("Watcher.SessionService.json", optional: true)
    .Build();

var cfg = new SessionServiceConfig();
cfgRoot.GetSection("SessionService").Bind(cfg);

Directory.CreateDirectory(cfg.LogDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(cfg.LogDir, "watcher-session-service-.log"),
        outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: cfg.LogRetainedDays,
        fileSizeLimitBytes: cfg.LogFileSizeLimitMb * 1024L * 1024L,
        rollOnFileSizeLimit: true,
        shared: true)
    .CreateLogger();

var diagnosticsCfg = UploaderConfig.Load(AppContext.BaseDirectory);
using var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog()
    .AddProvider(new Watcher.Uploader.DiagnosticLoggerProvider(Watcher.Uploader.DiagnosticJournal.For(diagnosticsCfg.CredentialPath), "service")));
var logger = loggerFactory.CreateLogger("Watcher.SessionService");

logger.LogInformation("Watcher.SessionService {Version} assembly starting, CaptureExePath={Path}",
    AppVersion.Current, cfg.CaptureExePath);

// Задача D2: выгрузка — внутри службы. Конфиг — тот же Watcher.Uploader.json
// рядом с exe, лог — свой файл uploader-*.log в каталоге логов Uploader'а
// (как у отдельного Watcher.Uploader.exe до D2), не в лог службы.
UploaderConfig? uploaderCfg = null;
Serilog.Core.Logger? uploaderSerilog = null;
ILoggerFactory? uploaderLoggerFactory = null;
if (cfg.RunUploader)
{
    uploaderCfg = UploaderConfig.Load(AppContext.BaseDirectory);
    Directory.CreateDirectory(uploaderCfg.QueueDir);
    Directory.CreateDirectory(uploaderCfg.SentDir);
    Directory.CreateDirectory(uploaderCfg.LogDir);
    uploaderSerilog = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.File(
            Path.Combine(uploaderCfg.LogDir, "uploader-.log"),
            outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: uploaderCfg.LogRetainedDays,
            fileSizeLimitBytes: uploaderCfg.LogFileSizeLimitMb * 1024L * 1024L,
            rollOnFileSizeLimit: true,
            shared: true)
        .CreateLogger();
    uploaderLoggerFactory = LoggerFactory.Create(builder => builder.AddSerilog(uploaderSerilog).AddProvider(new Watcher.Uploader.DiagnosticLoggerProvider(Watcher.Uploader.DiagnosticJournal.For(uploaderCfg.CredentialPath), "uploader")));
}

// Служба под SCM (sc.exe create ... start= auto obj= LocalSystem, см.
// install.ps1), не generic host — OnSessionChange/OnStart приходят от SCM,
// без него (запуск .exe напрямую из консоли) события сессий не доставляются.
ServiceBase.Run(new SessionWatcherService(cfg, logger, uploaderCfg, uploaderLoggerFactory));

uploaderLoggerFactory?.Dispose();
uploaderSerilog?.Dispose();
Log.CloseAndFlush();
