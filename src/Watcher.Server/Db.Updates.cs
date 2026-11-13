using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// H2 (roadmap-next.md): обновление агентов с сервера. Сервер только хранит
// подписанные пакеты и говорит станции «для тебя есть версия X» — подпись
// проверяет сама станция своим открытым ключом (UpdateSigning). Взломанный
// сервер без закрытого ключа владельца подсунуть свой код не может.
public sealed record AgentPackage(
    long Id, string Platform, string Version, string FilePath, string Sha256, long Size,
    string Signature, string? UploadedBy, DateTime UploadedAt);

public sealed partial class Db
{
    // Миграция 6 (H2). agent_target_version у группы — какую версию ставить её
    // хостам (NULL — не обновлять). У хоста — платформа агента (как обновлять:
    // windows-zip / linux-deb / linux-rpm, NULL — не умеет) и последний
    // результат обновления, который агент прислал в heartbeat.
    private static void AgentUpdates(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE agent_packages (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  platform TEXT NOT NULL,
  version TEXT NOT NULL,
  file_path TEXT NOT NULL,
  sha256 TEXT NOT NULL,
  size INTEGER NOT NULL,
  signature TEXT NOT NULL,
  uploaded_by TEXT,
  uploaded_at TEXT NOT NULL,
  UNIQUE (platform, version)
);
ALTER TABLE host_groups ADD COLUMN agent_target_version TEXT;
ALTER TABLE hosts ADD COLUMN agent_platform TEXT;
ALTER TABLE hosts ADD COLUMN update_status TEXT;
ALTER TABLE hosts ADD COLUMN update_status_at TEXT;";
        cmd.ExecuteNonQuery();
    }

    public long InsertAgentPackage(AgentPackage p)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO agent_packages (platform, version, file_path, sha256, size, signature, uploaded_by, uploaded_at)
VALUES ($p, $v, $f, $sha, $size, $sig, $u, $at) RETURNING id";
        cmd.Parameters.AddWithValue("$p", p.Platform);
        cmd.Parameters.AddWithValue("$v", p.Version);
        cmd.Parameters.AddWithValue("$f", p.FilePath);
        cmd.Parameters.AddWithValue("$sha", p.Sha256);
        cmd.Parameters.AddWithValue("$size", p.Size);
        cmd.Parameters.AddWithValue("$sig", p.Signature);
        cmd.Parameters.AddWithValue("$u", (object?)p.UploadedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", p.UploadedAt.ToString("o"));
        return (long)cmd.ExecuteScalar()!;
    }

    public List<AgentPackage> ListAgentPackages() => QueryPackages("ORDER BY uploaded_at DESC, id DESC");

    public AgentPackage? AgentPackageById(long id) => QueryPackages("WHERE id = $id", ("$id", id)).FirstOrDefault();

    public AgentPackage? AgentPackageFor(string platform, string version) =>
        QueryPackages("WHERE platform = $p AND version = $v", ("$p", platform), ("$v", version)).FirstOrDefault();

    public bool DeleteAgentPackage(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_packages WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "tail — только константы из методов выше, значения — через Parameters.")]
    private List<AgentPackage> QueryPackages(string tail, params (string Name, object Value)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id, platform, version, file_path, sha256, size, signature, uploaded_by, uploaded_at FROM agent_packages {tail}";
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        var list = new List<AgentPackage>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new AgentPackage(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5),
                r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                DateTime.Parse(r.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return list;
    }

    // Группы с целевой версией: (id, имя, версия). NULL-версии — тоже, для UI.
    public List<(long Id, string Name, string? TargetVersion)> GroupTargets()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, agent_target_version FROM host_groups ORDER BY name";
        var list = new List<(long, string, string?)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return list;
    }

    public bool SetGroupTargetVersion(long groupId, string? version)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE host_groups SET agent_target_version = $v WHERE id = $id";
        cmd.Parameters.AddWithValue("$v", (object?)version ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", groupId);
        return cmd.ExecuteNonQuery() > 0;
    }

    // Целевая версия хоста — по его группе; без группы — не обновляется.
    public string? TargetVersionForHost(string host)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT g.agent_target_version FROM hosts h JOIN host_groups g ON g.id = h.group_id WHERE h.name = $n";
        cmd.Parameters.AddWithValue("$n", host);
        return cmd.ExecuteScalar() as string;
    }

    // Из heartbeat: на какой платформе агент и чем кончилось последнее обновление.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Выбор между двумя строковыми константами, значения — через Parameters.")]
    public void SetHostUpdateInfo(string host, string? platform, string? status)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = status is null
            ? "UPDATE hosts SET agent_platform = $p WHERE name = $n"
            : "UPDATE hosts SET agent_platform = $p, update_status = $s, update_status_at = $at WHERE name = $n";
        cmd.Parameters.AddWithValue("$p", (object?)platform ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", host);
        if (status is not null)
        {
            cmd.Parameters.AddWithValue("$s", status);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
        }
        cmd.ExecuteNonQuery();
    }

    public List<(string Name, string? Platform, string? Version, string? Status, DateTime? StatusAt)> HostUpdateStates()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name, agent_platform, agent_version, update_status, update_status_at FROM hosts ORDER BY name";
        var list = new List<(string, string?, string?, string?, DateTime?)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return list;
    }
}
