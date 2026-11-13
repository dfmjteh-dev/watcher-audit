using System.Text;
using System.Text.Json;

namespace Watcher.Server;

public sealed record PageCursor(string At, long Id)
{
    public string Encode() => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this)));
    public static bool TryParse(string value, out PageCursor? cursor)
    {
        cursor = null;
        if (value.Length > 256) return false;
        try
        {
            cursor = JsonSerializer.Deserialize<PageCursor>(Convert.FromBase64String(value));
            return cursor is { Id: > 0 } && DateTime.TryParse(cursor.At, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out _);
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { return false; }
    }
}
