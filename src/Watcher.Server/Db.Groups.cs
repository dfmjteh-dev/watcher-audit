using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// Группы хостов и конфиги агентов: группы, override хоста, эффективный конфиг (Фаза 4).

public sealed record HostGroup(long Id, string Name, int HostCount);

// config_json хранится как есть (opaque TEXT) — сервер не парсит и не валидирует
// содержимое, это осознанно (см. roadmap.md, Фаза 4): новые поля конфига агента
// не требуют миграции схемы сервера, только schemaVersion внутри самого JSON.
public sealed record ConfigEntry(string ConfigJson, int ConfigVersion);

public sealed partial class Db
{
    // group_id не объявлен как FK — SQLite не включает их проверку по умолчанию
    // (нужен отдельный PRAGMA foreign_keys=ON на каждое соединение), поэтому отвязку
    // хостов от удаляемой группы делаем явно здесь, а не полагаемся на ON DELETE SET NULL.
    public bool DeleteGroup(long id)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var clear = c.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "UPDATE hosts SET group_id = NULL WHERE group_id = $id";
            clear.Parameters.AddWithValue("$id", id);
            clear.ExecuteNonQuery();
        }
        bool deleted;
        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM host_groups WHERE id = $id";
            del.Parameters.AddWithValue("$id", id);
            deleted = del.ExecuteNonQuery() > 0;
        }
        tx.Commit();
        return deleted;
    }

    public long CreateGroup(string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO host_groups (name) VALUES ($n) RETURNING id;";
        cmd.Parameters.AddWithValue("$n", name);
        return (long)cmd.ExecuteScalar()!;
    }

    public bool RenameGroup(long id, string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE host_groups SET name = $n WHERE id = $id";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public List<HostGroup> ListGroups()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT g.id, g.name, COUNT(h.id) AS host_count
FROM host_groups g
LEFT JOIN hosts h ON h.group_id = g.id
GROUP BY g.id, g.name
ORDER BY g.name";
        var list = new List<HostGroup>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new HostGroup(r.GetInt64(0), r.GetString(1), r.GetInt32(2)));
        return list;
    }

    public ConfigEntry? GetGroupConfig(long groupId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT config_json, config_version FROM host_groups WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", groupId);
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(0)) return null;
        return new ConfigEntry(r.GetString(0), r.GetInt32(1));
    }

    // Атомарный read-increment-write внутри одной транзакции — два параллельных
    // сохранения конфига от двух открытых вкладок оператора не должны затереть
    // версию друг друга молча (BEGIN IMMEDIATE лочит запись сразу, не на первой
    // попытке записи, SQLite busy_timeout уже выставлен в Open()).
    public int? SetGroupConfig(long groupId, string configJson)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var sel = c.CreateCommand();
        sel.Transaction = tx;
        sel.CommandText = "SELECT config_version FROM host_groups WHERE id = $id";
        sel.Parameters.AddWithValue("$id", groupId);
        object? existing = sel.ExecuteScalar();
        if (existing is null && CountGroupExists(c, tx, groupId) == 0) return null;
        int nextVersion = (existing is null or DBNull) ? 1 : Convert.ToInt32(existing) + 1;

        using var upd = c.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE host_groups SET config_json = $j, config_version = $v WHERE id = $id";
        upd.Parameters.AddWithValue("$j", configJson);
        upd.Parameters.AddWithValue("$v", nextVersion);
        upd.Parameters.AddWithValue("$id", groupId);
        upd.ExecuteNonQuery();
        tx.Commit();
        return nextVersion;
    }

    public bool ClearGroupConfig(long groupId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE host_groups SET config_json = NULL, config_version = NULL WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", groupId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public ConfigEntry? GetHostConfig(long hostId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT config_json, config_version FROM hosts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", hostId);
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(0)) return null;
        return new ConfigEntry(r.GetString(0), r.GetInt32(1));
    }

    public int? SetHostConfig(long hostId, string configJson)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var sel = c.CreateCommand();
        sel.Transaction = tx;
        sel.CommandText = "SELECT config_version FROM hosts WHERE id = $id";
        sel.Parameters.AddWithValue("$id", hostId);
        object? existing = sel.ExecuteScalar();
        if (existing is null && CountHostExists(c, tx, hostId) == 0) return null;
        int nextVersion = (existing is null or DBNull) ? 1 : Convert.ToInt32(existing) + 1;

        using var upd = c.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE hosts SET config_json = $j, config_version = $v WHERE id = $id";
        upd.Parameters.AddWithValue("$j", configJson);
        upd.Parameters.AddWithValue("$v", nextVersion);
        upd.Parameters.AddWithValue("$id", hostId);
        upd.ExecuteNonQuery();
        tx.Commit();
        return nextVersion;
    }

    public bool ClearHostConfig(long hostId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE hosts SET config_json = NULL, config_version = NULL WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", hostId);
        return cmd.ExecuteNonQuery() > 0;
    }

    // Конфиг группы, поверх него по полям — конфиг хоста (вызывается из
    // /api/heartbeat сразу после UpsertHeartbeat). Раньше конфиг хоста заменял
    // групповой целиком: поменяли у хоста одно поле — остальные групповые для
    // него переставали действовать. Ключа нет ни там, ни там — агент берёт своё
    // значение по умолчанию (ConfigDefaults у агентов захвата).
    //
    // Известный хост всегда получает объект, хотя бы {}: агент перезаписывает
    // кэш только непустым объектом, и без этого очистка конфига группы на
    // станцию не доходила бы. null — хоста ещё нет в базе.
    public ConfigEntry? GetEffectiveConfig(string hostName)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT h.config_json, h.config_version, g.config_json, g.config_version
FROM hosts h
LEFT JOIN host_groups g ON g.id = h.group_id
WHERE h.name = $name";
        cmd.Parameters.AddWithValue("$name", hostName);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        string? hostJson = r.IsDBNull(0) ? null : r.GetString(0);
        int hostVersion = r.IsDBNull(1) ? 0 : r.GetInt32(1);
        string? groupJson = r.IsDBNull(2) ? null : r.GetString(2);
        int groupVersion = r.IsDBNull(3) ? 0 : r.GetInt32(3);
        // Версия — только для лога агента; сумма растёт при любом сохранении.
        return new ConfigEntry(MergeConfig(groupJson, hostJson), groupVersion + hostVersion);
    }

    // Поля overlay перекрывают поля baseJson; null у ключа в overlay значит
    // «не задано здесь» — остаётся значение из baseJson.
    public static string MergeConfig(string? baseJson, string? overlayJson)
    {
        var merged = new System.Text.Json.Nodes.JsonObject();
        foreach (var json in new[] { baseJson, overlayJson })
        {
            if (string.IsNullOrEmpty(json)) continue;
            if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject obj) continue;
            foreach (var (key, value) in obj)
                if (value is not null) merged[key] = value.DeepClone();
        }
        return merged.ToJsonString();
    }

    // Группа хоста — для формы конфига хоста: что он наследует (0.12).
    public (long Id, string Name)? HostGroup(long hostId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT g.id, g.name FROM hosts h JOIN host_groups g ON g.id = h.group_id WHERE h.id = $id";
        cmd.Parameters.AddWithValue("$id", hostId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetString(1)) : null;
    }

    private static int CountGroupExists(SqliteConnection c, SqliteTransaction tx, long groupId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM host_groups WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", groupId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static int CountHostExists(SqliteConnection c, SqliteTransaction tx, long hostId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM hosts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", hostId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
