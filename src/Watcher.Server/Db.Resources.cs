using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// 0.13 (PR9б): сколько станции стоит агент — последний отчёт heartbeat.
public sealed record HostResources(double? CpuPercent, long MemBytes, int Processes, long QueueBytes, int QueueFiles, DateTime At);

public sealed partial class Db
{
    // Миграция 14: только последний отчёт — истории ресурсов агента нет (heartbeat
    // раз в минуту с 300 хостов — не та нагрузка, чтобы копить её в SQLite).
    private static void HostResourcesColumn(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "ALTER TABLE hosts ADD COLUMN resources_json TEXT;";
        cmd.ExecuteNonQuery();
    }

    private static readonly JsonSerializerOptions ResourcesJson = new(JsonSerializerDefaults.Web);

    // Числа от агента — в разумных пределах, иначе отчёт не пишем (сломанный агент
    // не должен рисовать в UI «−5 ГБ»).
    public static HostResources? Sanitize(ResourceReport? r, DateTime nowUtc)
    {
        if (r is null) return null;
        if (r.CpuPercent is < 0 or > 100 || r.MemBytes < 0 || r.Processes is < 0 or > 10_000 || r.QueueBytes < 0 || r.QueueFiles < 0) return null;
        return new HostResources(r.CpuPercent is { } c ? Math.Round(c, 1) : null, r.MemBytes, r.Processes, r.QueueBytes, r.QueueFiles, nowUtc);
    }

    public void SetHostResources(string host, HostResources r)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE hosts SET resources_json = $j WHERE name = $h";
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(r, ResourcesJson));
        cmd.ExecuteNonQuery();
    }

    internal static HostResources? ParseResources(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<HostResources>(json, ResourcesJson); }
        catch (JsonException) { return null; }
    }
}
