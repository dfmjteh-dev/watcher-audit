using Microsoft.Extensions.Logging;

namespace Watcher.Uploader;

// Цикл выгрузки очереди: квота, аплоад, ротация sent/, heartbeat. Вынесен из
// Program.cs в Задаче D2 (roadmap-next.md): на Windows его крутит служба
// WatcherSessionService внутри своего процесса (от LocalSystem, независимо от
// того, залогинен ли кто-то), на Linux — по-прежнему отдельный Watcher.Uploader.
public static class UploaderLoop
{
    // installer (H2) — как ставить обновления на этой ОС; null — станция
    // автоматически не обновляется.
    public static async Task RunAsync(UploaderConfig cfg, ILoggerFactory loggerFactory, CancellationToken ct, IUpdateInstaller? installer = null)
    {
        var logger = loggerFactory.CreateLogger("Watcher.Uploader");
        logger.LogInformation("Queue: {QueueDir}", cfg.QueueDir);
        logger.LogInformation("Sent:  {SentDir}", cfg.SentDir);
        logger.LogInformation("Server: {ServerUrl}", cfg.ServerUrl);
        // B1 (roadmap-next.md): видео и ApiToken по http на не-localhost — открытым
        // текстом по сети. install.ps1/install-capture-linux.sh уже предупреждают
        // об этом на установке — здесь то же самое на каждом старте, не только один раз.
        if (Uri.TryCreate(cfg.ServerUrl, UriKind.Absolute, out var serverUri) &&
            serverUri.Scheme == "http" && serverUri.Host is not ("localhost" or "127.0.0.1"))
        {
            logger.LogWarning(
                "ServerUrl использует http:// на не-localhost адрес ({Host}) — видео и ApiToken идут по сети открытым текстом",
                serverUri.Host);
        }

        var uploader = new Uploader(cfg, loggerFactory.CreateLogger("Watcher.Uploader.Uploader"));
        using var diagnosticsHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var diagnostics = new DiagnosticClient(diagnosticsHttp, uploader.Identity, cfg.ServerUrl, DiagnosticJournal.For(cfg.CredentialPath), cfg.LogDir, cfg.UpdateDir);
        var updater = new AgentUpdater(cfg, installer, loggerFactory.CreateLogger("Watcher.Uploader.Update"));
        logger.LogInformation("Auto-update: {State}", updater.Platform is { } p ? p : "off (no UpdatePublicKey or unsupported install)");

        int retryDelay = cfg.PollIntervalSeconds;
        DateTime lastRetention = DateTime.MinValue;
        DateTime lastHeartbeat = DateTime.MinValue;
        string heartbeatHost = Environment.MachineName;
        // Монотонный счётчик с момента старта процесса (не за интервал) — так админ видит
        // "сколько всего потеряно", а не только последний замеченный всплеск между опросами.
        // Сбрасывается на 0 при перезапуске агента, как и версия — это ожидаемо, не баг.
        int totalDroppedSegments = 0;
        DateTime lastOrphanSweep = DateTime.MinValue;
        DateTime lastAgeSweep = DateTime.MinValue;
        var losses = LossJournal.For(cfg);
        var resources = AgentResources.For(cfg);
        Action<string, long> Lost(string reason) => (file, size) => losses.Add(file, size, reason, DateTime.UtcNow);
        if (string.IsNullOrWhiteSpace(cfg.FfmpegPath))
            logger.LogWarning("FfmpegPath not set — mp4 without .json (capture killed mid-segment) will stay in queue");

        while (!ct.IsCancellationRequested)
        {
            bool anyFailure = false;
            bool anyWork = false;

            // Задача G1: регистрация по EnrollToken (раз, потом мгновенно выходит).
            await uploader.Identity.EnsureEnrolledAsync(ct);

            // Задача 18: раз в 10 минут, не каждый проход — файл всё равно ждёт
            // OrphanMinAgeMinutes, а ffmpeg на каждый файл не бесплатен.
            if (!string.IsNullOrWhiteSpace(cfg.FfmpegPath) && (DateTime.UtcNow - lastOrphanSweep).TotalMinutes >= 10)
            {
                lastOrphanSweep = DateTime.UtcNow;
                try
                {
                    totalDroppedSegments += OrphanSegments.Apply(cfg.QueueDir,
                        TimeSpan.FromMinutes(cfg.OrphanMinAgeMinutes), heartbeatHost, AppVersion.Current,
                        (rec, mp4) => OrphanSegments.RemuxWithFfmpeg(cfg.FfmpegPath, rec, mp4, TimeSpan.FromMinutes(2)),
                        f => OrphanSegments.ProbeWithFfmpeg(cfg.FfmpegPath, f, TimeSpan.FromMinutes(2)),
                        msg => logger.LogWarning("{Message}", msg), Lost(LossJournal.ReasonBroken));
                }
                catch (Exception ex) { logger.LogWarning(ex, "orphan sweep failed"); }
            }

            // Жёсткая квота на queue/ — до попытки аплоада, не после: защита диска важнее
            // отдельной попытки выгрузить файл, который всё равно над лимитом. Раз в цикл,
            // не по отдельному таймеру — тот же проход, что ниже листит директорию для
            // аплоада, лишняя директория не нужна.
            totalDroppedSegments += QueueQuota.Apply(cfg.QueueDir, cfg.QueueMaxBytes, cfg.FileQuietSeconds,
                msg => logger.LogWarning("{Message}", msg), Lost(LossJournal.ReasonQuota));
            // Срок хранения невыгруженного — раз в 10 минут, речь о днях.
            if ((DateTime.UtcNow - lastAgeSweep).TotalMinutes >= 10)
            {
                lastAgeSweep = DateTime.UtcNow;
                try
                {
                    totalDroppedSegments += QueueAge.Apply(cfg.QueueDir, QueueAge.Effective(cfg.ConfigCachePath, cfg.QueueMaxAgeDays),
                        DateTime.UtcNow, msg => logger.LogWarning("{Message}", msg), Lost(LossJournal.ReasonAge));
                }
                catch (Exception ex) { logger.LogWarning(ex, "queue max age sweep failed"); }
            }

            // 0.14.2: первый heartbeat — до первой выгрузки: в ответе предел сервера
            // (ServerMaxUploadBytes), без него слишком большой файл ушёл бы по сети зря.
            if (lastHeartbeat == DateTime.MinValue)
            {
                await uploader.SendHeartbeatAsync(heartbeatHost, AppVersion.Current, totalDroppedSegments, ct, updater, losses, resources);
                lastHeartbeat = DateTime.UtcNow;
            }

            try
            {
                var mp4s = Directory.GetFiles(cfg.QueueDir, "*.mp4")
                    .OrderBy(f => File.GetLastWriteTimeUtc(f))
                    .ToArray();

                foreach (var mp4 in mp4s)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!IsQuiet(mp4, cfg.FileQuietSeconds)) continue;
                    string json = mp4 + ".json";
                    if (!File.Exists(json) || !IsQuiet(json, cfg.FileQuietSeconds)) continue;

                    anyWork = true;
                    var outcome = await uploader.UploadAsync(mp4, json, ct);
                    if (outcome == UploadOutcome.Uploaded)
                    {
                        MoveOrDelete(mp4, json, cfg, msg => logger.LogWarning("{Message}", msg));
                    }
                    else if (outcome == UploadOutcome.Rejected)
                    {
                        if (RetainRejected(mp4, json, cfg.SentDir, losses, msg => logger.LogWarning("{Message}", msg))) totalDroppedSegments++;
                        else anyFailure = true;
                    }
                    else if (outcome == UploadOutcome.NameCollision)
                    {
                        // Отправится под новым именем на следующем проходе (уже "тихий").
                        string renamed = Uploader.RenameForCollision(mp4);
                        logger.LogInformation("renamed {Old} -> {New}", Path.GetFileName(mp4), Path.GetFileName(renamed));
                    }
                    else if (outcome == UploadOutcome.TooLarge)
                    {
                        // Сервер такой файл не примет: удаляем и пишем потерю, очередь идёт
                        // дальше. Раньше он стоял первым в очереди и уходил по сети заново
                        // на каждом проходе, а всё записанное после него не выгружалось.
                        if (DropTooLarge(mp4, json, msg => logger.LogWarning("{Message}", msg), Lost(LossJournal.ReasonTooLarge)))
                            totalDroppedSegments++;
                    }
                    else
                    {
                        anyFailure = true;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "loop error");
                anyFailure = true;
            }

            if ((DateTime.UtcNow - lastRetention).TotalMinutes > 30)
            {
                Retention.Apply(Path.Combine(cfg.SentDir, "rejected"), cfg.SentRetentionDays, cfg.SentMaxBytes,
                    msg => logger.LogInformation("{Message}", msg));
                Retention.Apply(cfg.SentDir, cfg.SentRetentionDays, cfg.SentMaxBytes,
                    msg => logger.LogInformation("{Message}", msg));
                lastRetention = DateTime.UtcNow;
            }

            if ((DateTime.UtcNow - lastHeartbeat).TotalSeconds >= cfg.HeartbeatIntervalSeconds)
            {
                await uploader.SendHeartbeatAsync(heartbeatHost, AppVersion.Current, totalDroppedSegments, ct, updater, losses, resources);
                lastHeartbeat = DateTime.UtcNow;
            }

            await diagnostics.SendAsync(AppVersion.Current, () => resources.Collect(DateTime.UtcNow), cfg.QueueDir, ct);

            int wait = anyFailure
                ? Math.Min(cfg.MaxRetryDelaySeconds, retryDelay * 2)
                : cfg.PollIntervalSeconds;
            retryDelay = anyFailure ? wait : cfg.PollIntervalSeconds;

            if (!anyWork && !anyFailure) wait = cfg.PollIntervalSeconds;

            try { await Task.Delay(TimeSpan.FromSeconds(wait), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static bool IsQuiet(string path, int quietSeconds)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length == 0) return false;
            if ((DateTime.UtcNow - fi.LastWriteTimeUtc).TotalSeconds < quietSeconds) return false;
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (IOException) { return false; }
    }

    public static bool RetainRejected(string mp4, string json, string sentDir, LossJournal losses, Action<string> log)
    {
        try
        {
            long size = new FileInfo(mp4).Length;
            string folder = Path.Combine(sentDir, "rejected"); Directory.CreateDirectory(folder);
            if (!losses.TryAdd(Path.GetFileName(mp4), size, LossJournal.ReasonBroken, DateTime.UtcNow)) return false;
            string dest = Path.Combine(folder, Path.GetFileNameWithoutExtension(mp4) + "-" + Guid.NewGuid().ToString("N") + ".mp4");
            // Сначала копируем пару; при ошибке исходник очереди остаётся для повтора.
            File.Copy(mp4, dest);
            if (File.Exists(json)) File.Copy(json, dest + ".json");
            foreach (string copy in new[] { dest, dest + ".json" })
                if (File.Exists(copy))
                {
                    using var durable = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite, FileShare.Read); durable.Flush(true);
                    File.SetLastWriteTimeUtc(copy, DateTime.UtcNow);
                }
            File.Delete(mp4);
            if (File.Exists(json)) File.Delete(json);
            log($"server rejected invalid video; retained locally: {Path.GetFileName(dest)}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { log($"could not retain rejected video: {ex.Message}"); return false; }
    }

    public static bool DropTooLarge(string mp4, string json, Action<string> log, Action<string, long> lost)
    {
        try
        {
            long size = new FileInfo(mp4).Length;
            File.Delete(mp4);
            if (File.Exists(json)) File.Delete(json);
            lost(Path.GetFileName(mp4), size);
            log($"deleted {Path.GetFileName(mp4)} ({size:N0} B): larger than the server accepts");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"could not delete oversized {Path.GetFileName(mp4)}: {ex.Message}");
            return false;
        }
    }

    private static void MoveOrDelete(string mp4, string json, UploaderConfig cfg, Action<string> log)
    {
        try
        {
            if (cfg.DeleteAfterUpload)
            {
                File.Delete(mp4);
                if (File.Exists(json)) File.Delete(json);
                return;
            }
            string destMp4 = Path.Combine(cfg.SentDir, Path.GetFileName(mp4));
            if (File.Exists(destMp4)) File.Delete(destMp4);
            File.Move(mp4, destMp4);
            if (File.Exists(json))
            {
                string destJson = Path.Combine(cfg.SentDir, Path.GetFileName(json));
                if (File.Exists(destJson)) File.Delete(destJson);
                File.Move(json, destJson);
            }
        }
        catch (Exception ex)
        {
            log($"move failed {Path.GetFileName(mp4)}: {ex.Message}");
        }
    }
}
