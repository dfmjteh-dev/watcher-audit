using Microsoft.Extensions.Configuration;

namespace Watcher.Uploader;

public sealed class UploaderConfig
{
    public string QueueDir { get; set; } = @"C:\ProgramData\Watcher\queue";
    public string SentDir { get; set; } = @"C:\ProgramData\Watcher\sent";
    public string LogDir { get; set; } = @"C:\ProgramData\Watcher\logs";
    public string ServerUrl { get; set; } = "http://localhost:5050";
    public int PollIntervalSeconds { get; set; } = 10;
    public int FileQuietSeconds { get; set; } = 5;
    public int MaxRetryDelaySeconds { get; set; } = 300;
    public bool DeleteAfterUpload { get; set; } = false;
    public int SentRetentionDays { get; set; } = 7;
    public long SentMaxBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    // Фаза 5 (roadmap): жёсткий порог на queue/ (ещё не отправленное) — 0 = без лимита
    // (дефолт, не меняет поведение существующих установок). При превышении удаляются
    // самые старые "тихие" сегменты, см. QueueQuota.cs; счётчик удалённых уходит в
    // heartbeat как droppedSegments, чтобы потеря была видна в UI, не тихой.
    public long QueueMaxBytes { get; set; } = 0;
    // 0.13: сколько дней держать невыгруженное (QueueAge), 0 — без срока. Конфиг
    // группы/хоста (queueMaxAgeDays) главнее.
    public int QueueMaxAgeDays { get; set; } = QueueAge.DefaultDays;
    // Задача G1: токен регистрации из веб-UI (Настройки → Агенты), агент меняет
    // его на свою учётку (POST /api/enroll) и сохраняет секрет в CredentialPath.
    public string EnrollToken { get; set; } = "";
    public string CredentialPath { get; set; } = @"C:\ProgramData\Watcher\agent\credential.json";
    // Задача 18 (roadmap-fixes): сегмент без .json (Capture убит) старше
    // OrphanMinAgeMinutes разбирается этим ffmpeg — .rec.mp4 перепаковывается, обычный
    // mp4 проверяется; удачные уходят со startReason "recovered", невосстановимые
    // удаляются (droppedSegments). Пусто — разбор выключен, файлы лежат в очереди.
    public string FfmpegPath { get; set; } = "";
    public int OrphanMinAgeMinutes { get; set; } = 30;
    // Фаза 3 (roadmap): /api/heartbeat должен быть дешёвым — раз в минуту на агента
    // при целевых ~300 агентах это 5 запросов/сек на сервер, не тяжелее /api/upload.
    public int HeartbeatIntervalSeconds { get; set; } = 60;
    // Фаза 4 (roadmap): куда кэшировать последний config_json, полученный в ответе
    // heartbeat (хост-override либо конфиг группы хоста). Применение самого конфига
    // Capture'ом — отдельная работа (Фаза 5), здесь только пишется файл.
    public string ConfigCachePath { get; set; } = @"C:\ProgramData\Watcher\config\capture-config.json";
    // H2: открытый ключ обновлений владельца (строка base64 из
    // tools/update-signing/new-update-key.sh). Пусто — станция автоматически не
    // обновляется (и не сообщает серверу платформу).
    public string UpdatePublicKey { get; set; } = "";
    // Куда скачиваются пакеты и пишется last-result.json. Короткий путь — на
    // Windows он уходит в командную строку задачи Планировщика (лимит 261 символ).
    public string UpdateDir { get; set; } = OperatingSystem.IsWindows() ? @"C:\ProgramData\Watcher\upd" : "/var/lib/watcher/updates";
    public int LogRetainedDays { get; set; } = 14;
    public int LogFileSizeLimitMb { get; set; } = 10;

    // appsettings.json (build-time дефолты) + Watcher.Uploader.json (пишет
    // install.ps1 / install-capture-linux.sh) из каталога exe. Общий для
    // самостоятельного Uploader'а и службы WatcherSessionService (Задача D2),
    // которая лежит в том же каталоге и читает тот же файл.
    public static UploaderConfig Load(string baseDir)
    {
        var root = new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("Watcher.Uploader.json", optional: true)
            .Build();
        var cfg = new UploaderConfig();
        root.GetSection("Uploader").Bind(cfg);
        return cfg;
    }
}
