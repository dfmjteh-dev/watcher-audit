using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// Пользователи веб-UI: учётки и роли (C1/C2), группы viewer'а, LDAP (H1).

public sealed record User(
    long Id,
    string Username,
    string PasswordHash,
    string Role,
    bool Disabled,
    // H1: local — пароль в БД; ldap — вход через каталог, пароля в БД нет, роль
    // задаётся группами каталога.
    string AuthSource = "local",
    string? LdapDn = null,
    // H7: право выгружать записи (zip с манифестом) — отдельно от роли, по
    // умолчанию ни у кого, включая admin. У пользователя каталога задаётся
    // группами Server:Ldap:ExportGroups.
    bool CanExport = false,
    // Этап 11: право «управление телефонами» — редактировать политики Android
    // (блокировки/запреты). Отдельно от роли, по умолчанию ни у кого.
    bool CanManagePhones = false)
{
    public bool IsLdap => AuthSource == "ldap";
}

public sealed partial class Db
{
    public int CountUsers()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public long InsertUser(string username, string passwordHash, string role)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO users (username, password_hash, role, disabled)
VALUES ($u, $h, $r, 0)
RETURNING id;";
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$h", passwordHash);
        cmd.Parameters.AddWithValue("$r", role);
        return (long)cmd.ExecuteScalar()!;
    }

    public User? UserByUsername(string username)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, username, password_hash, role, disabled, auth_source, ldap_dn, can_export, can_manage_phones FROM users WHERE username = $u LIMIT 1";
        cmd.Parameters.AddWithValue("$u", username);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return ReadUser(r);
    }

    // Этап C2 (roadmap-next.md) — #/settings/users нужен по id, не только по
    // username (ссылки/кнопки в таблице строятся от User.Id из ListUsers).
    public User? UserById(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, username, password_hash, role, disabled, auth_source, ldap_dn, can_export, can_manage_phones FROM users WHERE id = $i LIMIT 1";
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return ReadUser(r);
    }

    private static User ReadUser(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4) != 0,
            r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt32(7) != 0, r.GetInt32(8) != 0);

    // H1: пользователь каталога по DN — имя в каталоге могли сменить, DN ищем первым.
    public User? UserByLdapDn(string dn)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, username, password_hash, role, disabled, auth_source, ldap_dn, can_export, can_manage_phones FROM users WHERE ldap_dn = $dn COLLATE NOCASE LIMIT 1";
        cmd.Parameters.AddWithValue("$dn", dn);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadUser(r) : null;
    }

    // H1: завести или обновить пользователя каталога после входа. Пароля в БД
    // нет (пустой хэш не проходит VerifyPassword). Имя занято локальным
    // пользователем — null: локальная учётка (в т.ч. аварийный admin) главнее.
    public User? UpsertLdapUser(string login, string dn, string role)
    {
        if (UserByLdapDn(dn) is { } existing)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE users SET role = $r, username = $u, ldap_dn = $dn WHERE id = $i";
            cmd.Parameters.AddWithValue("$r", role);
            cmd.Parameters.AddWithValue("$u", login);
            cmd.Parameters.AddWithValue("$dn", dn);
            cmd.Parameters.AddWithValue("$i", existing.Id);
            try { cmd.ExecuteNonQuery(); }
            catch (SqliteException e) when (e.SqliteErrorCode == 19) { return null; }   // новое имя занято другим
            return UserById(existing.Id);
        }
        if (UserByUsername(login) is not null) return null;
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO users (username, password_hash, role, disabled, auth_source, ldap_dn) VALUES ($u, '', $r, 0, 'ldap', $dn)";
            cmd.Parameters.AddWithValue("$u", login);
            cmd.Parameters.AddWithValue("$r", role);
            cmd.Parameters.AddWithValue("$dn", dn);
            cmd.ExecuteNonQuery();
        }
        return UserByLdapDn(dn);
    }

    public List<User> ListUsers()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, username, password_hash, role, disabled, auth_source, ldap_dn, can_export, can_manage_phones FROM users ORDER BY username";
        var list = new List<User>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadUser(r));
        return list;
    }

    // Используется CLI `Watcher.Server --reset-password` (Задача 10b,
    // docs/roadmap-fixes.md) — точечная смена пароля одного пользователя без
    // DELETE FROM users (сносил ВСЕХ, не только admin, старый workaround из
    // Задачи 10a). Возвращает false, если такого username нет — вызывающий
    // код должен сообщить об этом явно, не создавать пользователя неявно.
    public bool SetUserPassword(string username, string passwordHash)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE users SET password_hash = $h WHERE username = $u";
        cmd.Parameters.AddWithValue("$h", passwordHash);
        cmd.Parameters.AddWithValue("$u", username);
        return cmd.ExecuteNonQuery() > 0;
    }

    // Та же операция, что SetUserPassword, но по id — веб-UI (сброс пароля
    // админом, смена своего пароля) оперирует id из claim/маршрута, не username.
    public bool SetUserPasswordById(long id, string passwordHash)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE users SET password_hash = $h WHERE id = $i";
        cmd.Parameters.AddWithValue("$h", passwordHash);
        cmd.Parameters.AddWithValue("$i", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    // Этап C2 (roadmap-next.md): role/disabled редактируются из #/settings/users.
    // Защита от полного самоблока (нельзя разжаловать/отключить последнего
    // включённого admin'а) — в Endpoints/UserEndpoints.cs, не здесь: Db — чистый CRUD, решение
    // "можно ли" принимает вызывающий код, видящий текущего пользователя сессии.
    public bool UpdateUserRole(long id, string role)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE users SET role = $r WHERE id = $i";
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$i", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool SetUserCanExport(long id, bool canExport)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE users SET can_export = $e WHERE id = $i";
        cmd.Parameters.AddWithValue("$e", canExport ? 1 : 0);
        cmd.Parameters.AddWithValue("$i", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    // Этап 12+: логины и PBKDF2-хэши локальных включённых админов — агент хранит
    // их для офлайн-разблокировки (AdminAuth). LDAP-админы без локального пароля
    // не попадают (хэша нет).
    public List<(string Username, string Hash)> LocalAdminCredentials()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT username, password_hash FROM users WHERE role='admin' AND auth_source='local' AND disabled=0 AND password_hash <> '' ORDER BY username";
        var list = new List<(string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    public bool SetUserCanManagePhones(long id, bool value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE users SET can_manage_phones = $e WHERE id = $i";
        cmd.Parameters.AddWithValue("$e", value ? 1 : 0);
        cmd.Parameters.AddWithValue("$i", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool SetUserDisabled(long id, bool disabled)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE users SET disabled = $d WHERE id = $i";
        cmd.Parameters.AddWithValue("$d", disabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$i", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    // Lockout-guard (Endpoints/UserEndpoints.cs, PATCH /api/users/{id}): запрещает оставить
    // систему вообще без включённого admin'а.
    public int CountEnabledAdmins()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users WHERE role = 'admin' AND disabled = 0";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // Этап C1 (roadmap-next.md): пустой список = viewer без ограничений
    // (см. ViewerScope.AllowedGroupIds), не "ничего не видно".
    public List<long> UserGroupIds(long userId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT group_id FROM user_groups WHERE user_id = $u ORDER BY group_id";
        cmd.Parameters.AddWithValue("$u", userId);
        var list = new List<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    // CLI `--set-user-groups` (stopgap до UI управления пользователями, C2) —
    // полная замена набора, не инкремент: пустой groupIds снимает ограничение.
    public void SetUserGroups(long userId, IReadOnlyCollection<long> groupIds)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM user_groups WHERE user_id = $u";
            del.Parameters.AddWithValue("$u", userId);
            del.ExecuteNonQuery();
        }
        foreach (long groupId in groupIds)
        {
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO user_groups (user_id, group_id) VALUES ($u, $g)";
            ins.Parameters.AddWithValue("$u", userId);
            ins.Parameters.AddWithValue("$g", groupId);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
