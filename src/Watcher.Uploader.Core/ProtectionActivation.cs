using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Watcher.Uploader;

public sealed record MaintenanceKeyOffer(string Key, string Signature);

// 0.16: Linux-агент без защиты включает её сам, когда сервер прислал ключ
// обслуживания, заверенный ключом обновлений (у Windows тот же ключ приходит внутри
// подписанного MSI и защиту включает установщик). Нужна новая версия агента: при
// обновлении с 0.15.x пакет ставит старый агент, так что включает уже новый —
// на первом heartbeat после обновления. Базу восстановления Guard потом получает
// с сервера (заявка на текущую версию, как у Windows без .sig).
public static class ProtectionActivation
{
    // Запуск GuardRepair setup (Linux-пакет): ключ обслуживания, ключ обновлений, сервер.
    // null — эта установка защиту сама не включает (Windows, tar.gz, не root).
    public static Action<string, string, string>? Activator { get; set; }
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);

    public sealed record Attempt(DateTime At, string Key);

    public static bool ShouldActivate(MaintenanceKeyOffer offer, string? updatePublicKey, bool guardEnabled, string stateFile, DateTime now)
    {
        if (guardEnabled || string.IsNullOrWhiteSpace(updatePublicKey)) return false;
        using var key = UpdateSigning.LoadPublicKey(updatePublicKey);
        if (key is null || !UpdateSigning.VerifyMaintenanceKey(key, offer.Key, offer.Signature)) return false;
        // Неудачная попытка (нет места, нет базы прав и т.п.) не повторяется чаще раза в 6 ч.
        try
        {
            if (File.Exists(stateFile) && new FileInfo(stateFile).Length < 4096
                && JsonSerializer.Deserialize<Attempt>(File.ReadAllBytes(stateFile), GuardPaths.Json) is { } last
                && last.Key == offer.Key && now - last.At < RetryAfter) return false;
        }
        catch (JsonException) { }
        return true;
    }

    public static void Offer(MaintenanceKeyOffer offer, UploaderConfig cfg, ILogger logger, DateTime now)
    {
        if (Activator is null) return;
        string stateFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cfg.QueueDir))!, "auto-protect.json");
        if (!ShouldActivate(offer, cfg.UpdatePublicKey, GuardPaths.Enabled, stateFile, now)) return;
        try
        {
            File.WriteAllBytes(stateFile, JsonSerializer.SerializeToUtf8Bytes(new Attempt(now, offer.Key), GuardPaths.Json));
            Activator(offer.Key, cfg.UpdatePublicKey!, cfg.ServerUrl);
            logger.LogInformation("agent protection: enabling GuardRepair (maintenance key from server, signed by update key)");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning("agent protection: could not start GuardRepair setup: {Error}", e.Message);
        }
    }
}
