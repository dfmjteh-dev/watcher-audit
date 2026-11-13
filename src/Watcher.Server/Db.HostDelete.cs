using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// 0.13 (п.10 и п.14 обратной связи 2026-10-14): удаление хоста из веб-UI вместе
// с записями (решение владельца: только admin, в журнал доступа). Учётки агента
// не стираются, а помечаются deleted_host: агент с такой учёткой — тот, что
// стоит на удалённой машине, — получает в ответ «удалён, деинсталлируйся»
// (AgentAccess, 410) сразу, если он на связи, или когда вернётся; иначе он бы
// перерегистрировался и хост появился заново.
public enum HostDeleteOutcome { Deleted, NotFound, LegalHold, MaintenanceRequired }

public sealed record HostDeleteResult(HostDeleteOutcome Outcome, string? Name = null, int Segments = 0, int TerminalSessions = 0,
    int Credentials = 0, IReadOnlyList<string>? Files = null);

public sealed partial class Db
{
    // Миграция 11.
    private static void HostDelete(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
ALTER TABLE agent_credentials ADD COLUMN deleted_host TEXT;
ALTER TABLE agent_credentials ADD COLUMN uninstall_sent_at TEXT;";
        cmd.ExecuteNonQuery();
    }

    // Строки БД — одной транзакцией; файлы записей (относительные пути в Files)
    // удаляет вызывающий после неё: файл без строки — мусор, строка без файла —
    // битая запись в UI. Действующий legal hold — отказ: записи под ним держат
    // по юридической причине, снять hold — отдельное осознанное действие.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Тексты SQL — константы кода; имя и id — только через Parameters.")]
    public HostDeleteResult DeleteHost(long hostId, bool forgetProtected = false)
    {
        using var totals = TotalsChange();
        using var c = Open();
        using var tx = c.BeginTransaction();
        string? name;
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT name FROM hosts WHERE id = $id";
            q.Parameters.AddWithValue("$id", hostId);
            name = q.ExecuteScalar() as string;
        }
        if (name is null) return new(HostDeleteOutcome.NotFound);
        if (!forgetProtected && ProtectedUninstallRequired(hostId)) return new(HostDeleteOutcome.MaintenanceRequired, name);

        long Scalar(string sql)
        {
            using var q = c.CreateCommand();
            q.Transaction = tx;
            q.CommandText = sql;
            q.Parameters.AddWithValue("$n", name);
            q.Parameters.AddWithValue("$id", hostId);
            return Convert.ToInt64(q.ExecuteScalar());
        }
        if (Scalar("SELECT COUNT(*) FROM legal_holds WHERE host = $n AND released_at IS NULL") > 0)
            return new(HostDeleteOutcome.LegalHold, name);

        var files = new List<string>();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = @"
SELECT file_path FROM segments WHERE host = $n
UNION ALL SELECT out_file_path FROM terminal_sessions WHERE host = $n AND out_file_path IS NOT NULL
UNION ALL SELECT timing_file_path FROM terminal_sessions WHERE host = $n AND timing_file_path IS NOT NULL";
            q.Parameters.AddWithValue("$n", name);
            using var r = q.ExecuteReader();
            while (r.Read()) files.Add(r.GetString(0));
        }
        int segments = (int)Scalar("SELECT COUNT(*) FROM segments WHERE host = $n");
        int terminal = (int)Scalar("SELECT COUNT(*) FROM terminal_sessions WHERE host = $n");
        int creds = (int)Scalar("SELECT COUNT(*) FROM agent_credentials WHERE host_id = $id");

        foreach (var sql in new[]
        {
            "UPDATE agent_credentials SET deleted_host = $n, revoked_at = COALESCE(revoked_at, $now) WHERE host_id = $id",
            "UPDATE terminal_sessions SET parent_id = NULL WHERE parent_id IN (SELECT id FROM terminal_sessions WHERE host = $n)",
            "DELETE FROM segments WHERE host = $n",            // segment_activity — триггером
            "DELETE FROM report_host_hour WHERE host = $n",    // заготовки отчётов (Db.ReportFacts.cs)
            "DELETE FROM report_user_hour WHERE host = $n",
            "DELETE FROM report_app_day WHERE host = $n",
            "DELETE FROM report_subject_day WHERE host = $n",
            "DELETE FROM report_dirty WHERE host = $n",
            "DELETE FROM segment_activity WHERE host = $n",    // на всякий случай: строки без сегмента
            "DELETE FROM terminal_sessions WHERE host = $n",
            "DELETE FROM host_presence WHERE host = $n",
            "DELETE FROM host_losses WHERE host = $n",
            "DELETE FROM android_events WHERE host = $n",       // Этап 3: события телефона
            "DELETE FROM legal_holds WHERE host = $n",          // только снятые — действующие отсекли выше
            "DELETE FROM hosts WHERE id = $id",
        })
        {
            using var u = c.CreateCommand();
            u.Transaction = tx;
            u.CommandText = sql;
            u.Parameters.AddWithValue("$n", name);
            u.Parameters.AddWithValue("$id", hostId);
            u.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            u.ExecuteNonQuery();
        }
        tx.Commit();
        return new(HostDeleteOutcome.Deleted, name, segments, terminal, creds, files);
    }

    // Учётка удалённого хоста: имя хоста и впервые ли ей отвечаем «деинсталлируйся»
    // (в журнал доступа — один раз). null — учётка не такая (или нет такой).
    public (string Host, bool First)? DeletedHostCredential(string secretHash)
    {
        using var c = Open();
        string? host;
        bool first;
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT deleted_host, uninstall_sent_at FROM agent_credentials WHERE secret_hash = $s AND deleted_host IS NOT NULL";
            q.Parameters.AddWithValue("$s", secretHash);
            using var r = q.ExecuteReader();
            if (!r.Read()) return null;
            host = r.GetString(0);
            first = r.IsDBNull(1);
        }
        if (first)
        {
            using var u = c.CreateCommand();
            u.CommandText = "UPDATE agent_credentials SET uninstall_sent_at = $now WHERE secret_hash = $s";
            u.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            u.Parameters.AddWithValue("$s", secretHash);
            u.ExecuteNonQuery();
        }
        return (host, first);
    }

    // Отозванная учётка живого хоста — мусор в «Настройки → Агенты», её можно
    // удалить. Учётку удалённого хоста (deleted_host) — нет: по ней вернувшийся
    // агент узнаёт, что должен удалить себя.
    public bool DeleteRevokedCredential(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_credentials WHERE id = $id AND revoked_at IS NOT NULL AND deleted_host IS NULL";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }
}
