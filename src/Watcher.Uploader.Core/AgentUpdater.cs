using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Watcher.Uploader;

// H2 (roadmap-next.md): обновление агента с сервера. Сервер в ответе heartbeat
// предлагает пакет целевой версии группы; здесь он скачивается, сверяется по
// SHA-256 и ПОДПИСИ открытым ключом владельца (UpdatePublicKey) — это граница
// безопасности: взломанный сервер без закрытого ключа ничего не поставит — и
// передаётся установщику платформы. Установка идёт отдельным процессом (Windows:
// задача Планировщика от SYSTEM, Linux: systemd-run): служба, которая скачала
// пакет, сама будет остановлена и перезапущена. Итог установщик пишет в
// last-result.json, следующий heartbeat отправляет его серверу.
public sealed record UpdateOffer(long Id, string Version, string Platform, string Sha256, long Size, string Signature, string Url);

public sealed record UpdateResult(string Version, bool Ok, string Message, DateTime At);

public interface IUpdateInstaller
{
    // windows-msi / linux-deb / linux-rpm; null — станция обновляться не умеет.
    string? Platform { get; }
    // Установка этой (или любой) версии уже идёт — не начинать вторую.
    bool InProgress();
    // Запустить установку проверенного пакета и вернуться (не ждать). Итог
    // установщик пишет в resultFile сам.
    void Start(string packagePath, string version, string workDir, string resultFile);
}

public sealed class AgentUpdater
{
    public const long MaxPackageSize = 2L * 1024 * 1024 * 1024;
    public const string ResultFileName = "last-result.json";
    // Неудачную версию не пробуем чаще раза в час: битый пакет или пакет,
    // который не встаёт, иначе качался бы каждую минуту с каждой станции.
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private readonly UploaderConfig _cfg;
    private readonly IUpdateInstaller? _installer;
    private readonly ILogger _logger;
    private readonly Func<DateTime> _clock;
    private readonly ECDsa? _key;
    private readonly Func<string, long> _availableSpace;
    private int _busy;
    // Скачивание/передача установщику или установка ещё идёт — защиту не включаем посреди.
    public bool Busy => Volatile.Read(ref _busy) != 0 || (_installer?.InProgress() ?? false);

    public AgentUpdater(UploaderConfig cfg, IUpdateInstaller? installer, ILogger logger, Func<DateTime>? clock = null, Func<string, long>? availableSpace = null)
    {
        _cfg = cfg;
        _installer = installer;
        _logger = logger;
        _clock = clock ?? (() => DateTime.UtcNow);
        _availableSpace = availableSpace ?? (path => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace);
        _key = UpdateSigning.LoadPublicKey(cfg.UpdatePublicKey);
        if (!string.IsNullOrWhiteSpace(cfg.UpdatePublicKey) && _key is null)
            logger.LogWarning("UpdatePublicKey не разобран — автообновление выключено");
    }

    // Платформа для heartbeat: null — эта станция автоматически не обновляется
    // (нет ключа или установщика — например, Linux поставлен tar.gz-скриптами).
    public string? Platform => _key is not null ? _installer?.Platform : null;

    private string ResultPath => Path.Combine(_cfg.UpdateDir, ResultFileName);

    public UpdateResult? LastResult()
    {
        try
        {
            return File.Exists(ResultPath) ? JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(ResultPath), Json) : null;
        }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    // Для heartbeat: «ok 0.10.0» / «failed 0.10.0: причина».
    public string? StatusForHeartbeat() =>
        LastResult() is { } r ? (r.Ok ? $"ok {r.Version}" : $"failed {r.Version}: {r.Message}").Truncate(300) : null;

    public void WriteResult(UpdateResult r)
    {
        Directory.CreateDirectory(_cfg.UpdateDir);
        string temporary = ResultPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(r, Json));
            File.Move(temporary, ResultPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Решение «браться ли за предложение» — без сети и файлов пакета, для тестов.
    public string? WhyNot(UpdateOffer offer)
    {
        if (GuardPaths.Enabled) return "protected updates require signed maintenance authorization";
        if (Platform is not { } platform) return "updates disabled on this station";
        if (offer.Platform != platform) return $"offer for {offer.Platform}, station is {platform}";
        if (!UpdateSigning.IsValidVersion(offer.Version)) return "invalid version";
        if (offer.Size is <= 0 or > MaxPackageSize) return "invalid package size";
        if (_installer!.InProgress()) return "update already in progress";
        if (LastResult() is { Ok: false } last && last.Version == offer.Version && _clock() - last.At < RetryFailedAfter)
            return $"version {offer.Version} failed less than {RetryFailedAfter.TotalMinutes:0} min ago";
        return null;
    }

    // Из heartbeat: в фоне, чтобы скачивание сотен мегабайт не держало цикл
    // выгрузки видео. Одновременно — одна попытка.
    public void OnOffer(UpdateOffer offer, Func<string, string, CancellationToken, Task<string>> download, CancellationToken ct)
    {
        if (WhyNot(offer) is { } reason)
        {
            _logger.LogDebug("update {Version} skipped: {Reason}", offer.Version, reason);
            return;
        }
        _ = Task.Run(async () =>
        {
            try { await ApplyAsync(offer, download, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }, CancellationToken.None);
    }

    // download(url, путь) скачивает файл и возвращает его SHA-256 (hex).
    public async Task<bool> ApplyAsync(UpdateOffer offer, Func<string, string, CancellationToken, Task<string>> download, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
        string? package = null;
        bool handedOff = false;
        try
        {
            if (!UpdateSigning.IsValidVersion(offer.Version) || offer.Size is <= 0 or > MaxPackageSize)
                return Fail(offer.Version, "invalid version or package size");
            if (_installer?.Platform is null || _key is null || offer.Platform != _installer.Platform || GuardPaths.Enabled) return false;
            if (_installer.InProgress()) return false;
            ct.ThrowIfCancellationRequested();
            if (_availableSpace(_cfg.UpdateDir) < offer.Size * 3 + 128L * 1024 * 1024)
                return Fail(offer.Version, "insufficient disk space for package and installation; installation was not started");
            string workDir = Path.Combine(_cfg.UpdateDir, offer.Version);
            _logger.LogInformation("update {Version} ({Platform}) offered — downloading", offer.Version, offer.Platform);
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
            Directory.CreateDirectory(workDir);
            string ext = UpdateSigning.PackageExtension(offer.Platform);
            package = Path.Combine(workDir, "package" + ext);
            _ = await download(offer.Url, package, ct);
            ct.ThrowIfCancellationRequested();
            string sha;
            await using (var stream = File.OpenRead(package))
                sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();

            // Хеш — тот, что посчитан здесь по скачанному, а не присланный
            // сервером: подпись проверяется именно по нему.
            if (!string.Equals(sha, offer.Sha256, StringComparison.OrdinalIgnoreCase))
                return Fail(offer.Version, "sha256 of downloaded file differs from offer");
            if (!UpdateSigning.Verify(_key!, offer.Platform, offer.Version, sha, offer.Signature))
                return Fail(offer.Version, "signature does not match update key");
            if (new FileInfo(package).Length != offer.Size)
                return Fail(offer.Version, "downloaded package size differs from offer");

            // Подпись рядом с пакетом: установщик (GuardRepair agent-install) по ней
            // закрепляет этот пакет базой восстановления Guard (0.16, без копии MSI в MSI).
            File.WriteAllText(package + ".sig", offer.Signature);
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("update {Version}: signature ok, starting installer", offer.Version);
            _installer!.Start(package, offer.Version, workDir, ResultPath);
            handedOff = true;
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(offer.Version, "update download timed out; installation was not started");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Fail(offer.Version, e.Message);
        }
        finally
        {
            if (!handedOff && package is not null)
                foreach (string path in new[] { package, package + ".part", package + ".sig" })
                    try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _logger.LogWarning("Cannot remove incomplete update package"); }
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private bool Fail(string version, string message)
    {
        _logger.LogWarning("update {Version} failed: {Message}", version, message);
        try { WriteResult(new UpdateResult(version, false, message, _clock())); }
        catch (IOException e) { _logger.LogWarning(e, "cannot write update result"); }
        return false;
    }
}
