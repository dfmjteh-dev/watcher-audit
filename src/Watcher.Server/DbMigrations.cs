using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// R12 (docs/code-review-2026-10.md): нумерованные миграции схемы вместо одних
// CREATE TABLE IF NOT EXISTS + EnsureColumn. Так можно выразить то, что
// идемпотентным DDL не выражается (переименовать колонку, пересоздать индекс,
// переложить данные), и на десятках инсталляций разных версий видно, до какой
// версии схемы дошла каждая.
//
// Правила (docs/development.md, «Миграции схемы БД»): миграции только
// дописываются в конец Db.Migrations, номер следующий по порядку; уже
// выпущенную миграцию не менять — на части инсталляций она уже применена.
public sealed record DbMigration(int Version, string Name, Action<SqliteConnection> Apply);

public static class DbMigrations
{
    // Применяет недостающие миграции, каждую в своей транзакции вместе с
    // записью в schema_migrations: упавшая миграция откатывается целиком (DDL в
    // SQLite транзакционный), следующий старт повторит её с начала.
    public static void Apply(SqliteConnection c, IReadOnlyList<DbMigration> migrations, string dbPath)
    {
        for (int i = 0; i < migrations.Count; i++)
            if (migrations[i].Version != i + 1)
                throw new InvalidOperationException($"migrations must be numbered 1..N without gaps, got {migrations[i].Version} at position {i + 1}");

        Exec(c, @"
CREATE TABLE IF NOT EXISTS schema_migrations (
  version INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  applied_at TEXT NOT NULL,
  server_version TEXT
)");
        int current = CurrentVersion(c);
        int latest = migrations.Count;
        // БД от более нового сервера (откатили бинарник): писать в схему,
        // которую этот код не знает, опаснее, чем не стартовать.
        if (current > latest)
            throw new InvalidOperationException(
                $"Схема БД {dbPath} — версии {current}, этот сервер знает только до {latest}. " +
                "Сервер старее БД: поставьте версию сервера не ниже той, что последней работала с этой БД, " +
                "или восстановите БД из резервной копии.");
        if (current == latest) return;

        // Перед миграциями поверх уже размеченной БД — копия рядом: следующие
        // миграции могут менять и перекладывать данные. Baseline на старой,
        // ещё не размеченной БД только добавляет недостающее — без копии.
        if (current >= 1) Backup(c, dbPath, current);

        foreach (var m in migrations.Where(m => m.Version > current))
        {
            // IMMEDIATE — сразу блокировка на запись: сервер и CLI-команда,
            // стартовавшие одновременно, не применят одну миграцию дважды.
            // Сырые BEGIN/COMMIT, а не BeginTransaction: тогда команды внутри
            // миграции не обязаны получать объект транзакции.
            Exec(c, "BEGIN IMMEDIATE");
            try
            {
                if (CurrentVersion(c) >= m.Version)
                {
                    Exec(c, "ROLLBACK");
                    continue;
                }
                m.Apply(c);
                using var ins = c.CreateCommand();
                ins.CommandText = "INSERT INTO schema_migrations (version, name, applied_at, server_version) VALUES ($v, $n, $t, $s)";
                ins.Parameters.AddWithValue("$v", m.Version);
                ins.Parameters.AddWithValue("$n", m.Name);
                ins.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
                ins.Parameters.AddWithValue("$s", AppVersion.Current);
                ins.ExecuteNonQuery();
                Exec(c, "COMMIT");
            }
            catch
            {
                try { Exec(c, "ROLLBACK"); } catch (SqliteException) { /* транзакцию уже откатил сам SQLite */ }
                throw;
            }
        }
    }

    public static int CurrentVersion(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void Backup(SqliteConnection c, string dbPath, int fromVersion)
    {
        string target = $"{dbPath}.v{fromVersion}.bak";
        if (File.Exists(target)) target = $"{dbPath}.v{fromVersion}.{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
        using var cmd = c.CreateCommand();
        cmd.CommandText = "VACUUM INTO $p";
        cmd.Parameters.AddWithValue("$p", target);
        cmd.ExecuteNonQuery();
    }

    [SuppressMessage("Security", "CA2100", Justification = "Вызывается только со строковыми литералами миграций и BEGIN/COMMIT/ROLLBACK.")]
    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
