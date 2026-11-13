namespace Watcher.TerminalAgent;

// В отличие от Watcher.Capture.Linux (один процесс на залогиненного юзера,
// systemd --user, $HOME) этот агент — ОДИН процесс на весь хост, системный
// systemd-сервис от root: он читает общий /var/log/audit/audit.log (доступен
// только root/группе adm), а не что-то в контексте конкретного пользователя.
// См. roadmap.md, Фаза 8 — пересмотр механизма 2026-10-06: запись теперь
// через pam_tty_audit (ядро, fail-open), не через подмену login shell.
public sealed class TerminalAgentConfig
{
    public string AuditLogPath { get; set; } = "/var/log/audit/audit.log";

    // Куда сохраняем позицию (байтовый offset), до которой audit.log уже
    // прочитан — переживает перезапуск агента, не обрабатываем файл с начала
    // каждый раз (он может быть гигабайтным и содержать события всех сервисов
    // хоста, не только TTY).
    public string BookmarkPath { get; set; } = "/var/lib/watcher-terminal-agent/bookmark.txt";

    public string QueueDir { get; set; } = "/var/lib/watcher-terminal-agent/queue";
    public string LogDir { get; set; } = "/var/lib/watcher-terminal-agent/logs";

    // Тот же путь, что watcher-term-wrap.sh (Фаза 8b) использует для вывода
    // script(1) — этот процесс читает оттуда готовые .json+.out+.timing и
    // выгружает их на сервер, сам их не пишет (пишет непривилегированный
    // shell пользователя, см. watcher-term-wrap.sh).
    public string RawOutputDir { get; set; } = "/var/lib/watcher-terminal-agent/raw-output";

    // Загруженное не удаляется, а переносится сюда — тот же приём, что
    // SentDir у Watcher.Uploader (Windows/видео-агент), на случай если нужно
    // будет посмотреть, что реально уехало. Сами эти папки чистятся
    // TerminalRetention (см. ниже) — не растут бесконечно.
    public string QueueSentDir { get; set; } = "/var/lib/watcher-terminal-agent/queue/sent";
    // 0.12: сколько вывода идущей сессии уже отправлено (по файлу на запись,
    // root 700) — переживает перезапуск агента.
    public string LiveStateDir { get; set; } = "/var/lib/watcher-terminal-agent/live-state";
    public string RawOutputSentDir { get; set; } = "/var/lib/watcher-terminal-agent/raw-output/sent";

    // Пусто — выгрузка на сервер выключена (агент только собирает локально,
    // как раньше), ничего не ломается при обновлении без заполнения этих
    // полей. См. roadmap.md, Фаза 8 — "связать с сервером Watcher".
    public string ServerUrl { get; set; } = "";
    // Общий токен парка — переходный путь, пока агент не зарегистрирован (G1).
    public string ApiToken { get; set; } = "";
    // Задача G1c: токен регистрации из веб-UI (Настройки → Агенты) — агент меняет
    // его на свою учётку вида terminal, секрет хранит в CredentialPath (root, 600).
    public string EnrollToken { get; set; } = "";
    public string CredentialPath { get; set; } = "/var/lib/watcher-terminal-agent/agent/credential.json";

    // Файл не трогаем для аплоада/квоты, если его mtime моложе этого порога —
    // защита от гонки с ещё дописывающим его процессом (тот же приём, что
    // Watcher.Uploader/QueueQuota.cs, см. FileQuietSeconds).
    public int UploadQuietSeconds { get; set; } = 2;

    // Жёсткий порог на НЕотправленное — 0 выключает (как и у Watcher.Uploader
    // QueueMaxBytes по умолчанию): если сервер не настроен/недоступен долго,
    // queue/raw-output иначе росли бы неограниченно (см. TerminalQueueQuota.cs).
    // Разные дефолты у двух пайплайнов осознанно: input — чистый текст
    // (крошечный), output — typescript (крупнее), разная чувствительность
    // к тому, сколько держать неотправленным, оправдывает разные лимиты,
    // не общий один на оба.
    public long QueueMaxBytes { get; set; } = 0;
    public long RawOutputMaxBytes { get; set; } = 0;
    // 0.13: невыгруженное старше стольких дней удаляется (TerminalQueueQuota.ApplyAge),
    // 0 — без срока. Сервер присылает свой (queueMaxAgeDays группы/хоста) заголовком
    // ответа — он главнее и сохраняется рядом с учёткой, чтобы пережить перезапуск
    // без сервера.
    public int QueueMaxAgeDays { get; set; } = 30;

    // Ротация sent/ (TerminalRetention.cs) — тот же приём, что
    // Watcher.Uploader SentRetentionDays/SentMaxBytes, отдельная пара под
    // каждый пайплайн по той же причине, что и выше.
    public int QueueSentRetentionDays { get; set; } = 7;
    public long QueueSentMaxBytes { get; set; } = 1L * 1024 * 1024 * 1024;
    public int RawOutputSentRetentionDays { get; set; } = 7;
    public long RawOutputSentMaxBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    // Как часто гонять TerminalRetention на sent/ — не каждый опрос (это
    // сканирование директории, дешёвое, но бессмысленно часто), тот же
    // 30-минутный интервал, что у Watcher.Uploader.
    public int SentRetentionScanIntervalMinutes { get; set; } = 30;

    public int PollIntervalSeconds { get; set; } = 5;

    // Сессия, для которой за это время не появилось USER_END (например,
    // auditd не успел дописать событие закрытия, или сессия оборвалась без
    // штатного закрытия) — сбрасываем как "зависшую" и сбрасываем то, что
    // накопили, а не ждём вечно и не растим память на зависших sessionId.
    // 0.12: сутки, не час — идущая сессия уходит кусками, а час простоя в
    // открытой сессии обычное дело: иначе она разрывалась бы на две записи.
    public int AbandonedSessionTimeoutSeconds { get; set; } = 86400;

    public int LogRetainedDays { get; set; } = 14;
    public int LogFileSizeLimitMb { get; set; } = 10;
}
