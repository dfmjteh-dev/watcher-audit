using System.Text.Json;

namespace Watcher.Uploader;

// 0.13 (п.11 обратной связи 2026-10-14): сервер недоступен неделями — невыгруженное
// на станции не должно копиться бесконечно. Всё в очереди старше QueueMaxAgeDays
// удаляется и считается потерей (droppedSegments): это настоящая потеря, в отличие
// от пустых сегментов (OrphanSegments). Срок — queueMaxAgeDays из конфига группы/
// хоста (кэш ответа heartbeat, переживает месяцы без сервера), иначе из
// appsettings (по умолчанию 30). Квота по размеру (QueueQuota) — отдельно.
public static class QueueAge
{
    // Те же пределы, что AgentConfigLimits.QueueMaxAgeDays на сервере.
    public const int MinDays = 0, MaxDays = 3650, DefaultDays = 30;
    public const string ConfigKey = "queueMaxAgeDays";

    // Срок из кэша конфига сервера, иначе fallback. Битый кэш или значение вне
    // пределов — fallback (как с остальными ключами: агент не верит серверу вслепую).
    public static int Effective(string? configCachePath, int fallback)
    {
        if (string.IsNullOrWhiteSpace(configCachePath) || !File.Exists(configCachePath)) return fallback;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configCachePath));
            return FromConfig(doc.RootElement) ?? fallback;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return fallback; }
    }

    public static int? FromConfig(JsonElement config) =>
        config.ValueKind == JsonValueKind.Object && config.TryGetProperty(ConfigKey, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int d)
            && d >= MinDays && d <= MaxDays ? d : null;

    // Удаляет из queueDir видео-сегменты (mp4 с .json, .rec.mp4, mp4 без .json)
    // старше maxAgeDays по времени изменения. Возвращает число удалённых сегментов.
    public static int Apply(string queueDir, int maxAgeDays, DateTime nowUtc, Action<string> log, Action<string, long>? onLoss = null)
    {
        if (maxAgeDays <= 0 || !Directory.Exists(queueDir)) return 0;
        DateTime cutoff = nowUtc.AddDays(-maxAgeDays);
        int dropped = 0;
        foreach (var f in new DirectoryInfo(queueDir).GetFiles("*.mp4", SearchOption.TopDirectoryOnly))
        {
            if (f.LastWriteTimeUtc >= cutoff) continue;
            var json = new FileInfo(f.FullName + ".json");
            long size = f.Length;
            try
            {
                f.Delete();
                if (json.Exists) json.Delete();
                dropped++;
                onLoss?.Invoke(f.Name, size);
                log($"queue max age: dropped {f.Name} ({size:N0} B, not uploaded for more than {maxAgeDays} d)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"queue max age: delete failed {f.Name}: {ex.Message}");
            }
        }
        // .json, чей mp4 уже удалён (квотой, вручную) — мусор, не потеря.
        foreach (var j in new DirectoryInfo(queueDir).GetFiles("*.mp4.json", SearchOption.TopDirectoryOnly))
            if (j.LastWriteTimeUtc < cutoff && !File.Exists(j.FullName[..^".json".Length]))
                try { j.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return dropped;
    }
}
