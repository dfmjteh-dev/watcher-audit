namespace Watcher.SessionService;

// Конфиг службы — плоский и минимальный, без конфигурации с сервера (Задача D3).
// Настройки выгрузки (сервер, токен, очередь) — отдельно, в Watcher.Uploader.json
// рядом (UploaderConfig), тот же файл, что у самостоятельного Uploader'а.
public sealed class SessionServiceConfig
{
    // Задача D2: выгрузка очереди внутри службы. false — только запуск Capture
    // (если очередь выгружает что-то другое).
    public bool RunUploader { get; set; } = true;
    // Задача D3: кэш конфига с сервера (пишет heartbeat выгрузки) — служба берёт
    // из него allowUsers/excludeUsers. При RunUploader=true используется путь из
    // Watcher.Uploader.json (ConfigCachePath), этот — только когда выгрузка
    // выключена и кэш пишет кто-то другой.
    public string ConfigCachePath { get; set; } = @"C:\ProgramData\Watcher\config\capture-config.json";
    public string CaptureExePath { get; set; } = @"C:\Watcher\Watcher.Capture.exe";
    public string LogDir { get; set; } = @"C:\ProgramData\Watcher\session-service-logs";
    public int LogRetainedDays { get; set; } = 14;
    public int LogFileSizeLimitMb { get; set; } = 10;
    // Задача F1: консольный аудит SSH-сессий (транскрипты PowerShell). Включает
    // install.ps1 -TerminalAudit 1 (политики + папка); нужен RunUploader —
    // сервер и токены берутся из Watcher.Uploader.json.
    public TerminalAuditConfig TerminalAudit { get; set; } = new();
}
