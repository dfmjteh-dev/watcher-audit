using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// 0.12: настройки сервера, которые меняются в веб-UI без перезапуска (пока —
// вход через AD, ключ "ldap"). Значение — JSON; секреты в нём (пароль
// служебной учётки) наружу не отдаются, это решает эндпоинт.
public sealed partial class Db
{
    private static void ServerSettings(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE server_settings (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  updated_by TEXT
);";
        cmd.ExecuteNonQuery();
    }

    public string? GetSetting(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM server_settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value, string? updatedBy)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO server_settings (key, value, updated_at, updated_by) VALUES ($k, $v, $at, $by)
ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at, updated_by = excluded.updated_by";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$by", (object?)updatedBy ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public bool DeleteSetting(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM server_settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteNonQuery() > 0;
    }
}
