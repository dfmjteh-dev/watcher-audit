using System.Text.Json;

namespace Watcher.Uploader;

// Отметки процессов записи экрана (пишет src/Shared/CaptureStatusFile.cs в
// Watcher.Capture / Watcher.Capture.Linux) — в heartbeat. Отметки старше
// StaleAfter не шлём (процесс завершился: пользователь вышел), старше
// DeleteAfter — удаляем.
public sealed record CaptureReportDto(string User, string? ConfigHash, bool? Recording, DateTime? At);

public static class CaptureStatus
{
    public const string Prefix = "capture-status_", Suffix = ".status";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DeleteAfter = TimeSpan.FromDays(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static List<CaptureReportDto> Collect(string queueDir, DateTime nowUtc)
    {
        var list = new List<CaptureReportDto>();
        if (!Directory.Exists(queueDir)) return list;
        foreach (var f in new DirectoryInfo(queueDir).GetFiles(Prefix + "*" + Suffix, SearchOption.TopDirectoryOnly))
        {
            try
            {
                var age = nowUtc - f.LastWriteTimeUtc;
                if (age > DeleteAfter) { f.Delete(); continue; }
                if (age > StaleAfter) continue;
                if (JsonSerializer.Deserialize<CaptureReportDto>(File.ReadAllText(f.FullName), Json) is { User.Length: > 0 } r) list.Add(r);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return list;
    }
}
