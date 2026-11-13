using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// Задача G1 (roadmap-next.md): учётки агентов вместо одного ApiToken на парк.
// enroll_tokens — многоразовые токены регистрации (срок, лимит, группа по
// умолчанию), их вводят в инсталлятор. agent_credentials — собственный секрет
// каждого установленного агента, привязан к hosts.id: host в upload/heartbeat
// берётся из учётки, поле в meta только сверяется. Секреты и токены хранятся
// только хэшем (SHA-256 — значения случайные, 256 бит, медленный хэш паролей
// не нужен, а поиск по хэшу на каждом heartbeat 300 агентов должен быть дешёвым).
public sealed record EnrollToken(
    long Id, string Name, long? GroupId, string? GroupName, DateTime CreatedAt, string? CreatedBy,
    DateTime ExpiresAt, int? MaxUses, int Uses, DateTime? RevokedAt);

public sealed record AgentCredential(
    long Id, long HostId, string HostName, string Kind, DateTime CreatedAt, long? EnrollTokenId,
    string? EnrollTokenName, DateTime? LastUsedAt, string? LastIp, DateTime? RevokedAt, bool DuplicateKind);

// Результат проверки секрета агента: имя хоста из учётки.
public sealed record AgentIdentity(long CredentialId, long HostId, string HostName, string Kind);

public enum EnrollOutcome { Ok, InvalidToken }

public sealed partial class Db
{
    private static void InitAgents(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS enroll_tokens (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL,
  token_hash TEXT NOT NULL UNIQUE,
  group_id INTEGER,
  created_at TEXT NOT NULL,
  created_by TEXT,
  expires_at TEXT NOT NULL,
  max_uses INTEGER,
  uses INTEGER NOT NULL DEFAULT 0,
  revoked_at TEXT
);

CREATE TABLE IF NOT EXISTS agent_credentials (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  host_id INTEGER NOT NULL,
  kind TEXT NOT NULL,
  secret_hash TEXT NOT NULL UNIQUE,
  enroll_token_id INTEGER,
  created_at TEXT NOT NULL,
  last_used_at TEXT,
  last_ip TEXT,
  revoked_at TEXT,
  duplicate_kind INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_agent_credentials_host ON agent_credentials(host_id);
";
        cmd.ExecuteNonQuery();
        // 1 — последний heartbeat хоста пришёл без учётки (анонимный режим
        // разработки), 0 — по учётке агента, NULL — heartbeat не было.
        EnsureColumn(c, "hosts", "last_auth_legacy", "INTEGER");
    }

    public long InsertEnrollToken(string name, string tokenHash, long? groupId, DateTime expiresAt, int? maxUses, string? createdBy)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO enroll_tokens (name, token_hash, group_id, created_at, created_by, expires_at, max_uses)
VALUES ($n, $h, $g, $now, $by, $exp, $max);
SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$h", tokenHash);
        cmd.Parameters.AddWithValue("$g", (object?)groupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$by", (object?)createdBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exp", expiresAt.ToUniversalTime().ToString("o"));
        cmd.Parameters.AddWithValue("$max", (object?)maxUses ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public List<EnrollToken> ListEnrollTokens()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT t.id, t.name, t.group_id, g.name, t.created_at, t.created_by, t.expires_at, t.max_uses, t.uses, t.revoked_at
FROM enroll_tokens t LEFT JOIN host_groups g ON g.id = t.group_id
ORDER BY t.id DESC";
        var list = new List<EnrollToken>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new EnrollToken(
                r.GetInt64(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3),
                ParseUtc(r.GetString(4)), r.IsDBNull(5) ? null : r.GetString(5),
                ParseUtc(r.GetString(6)), r.IsDBNull(7) ? null : r.GetInt32(7), r.GetInt32(8),
                r.IsDBNull(9) ? null : ParseUtc(r.GetString(9))));
        }
        return list;
    }

    public EnrollToken? EnrollTokenById(long id) => ListEnrollTokens().FirstOrDefault(t => t.Id == id);

    // Токен комплекта агентов (kit.json хранит только его SHA-256).
    public EnrollToken? EnrollTokenByHash(string tokenHash)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM enroll_tokens WHERE token_hash = $h";
        cmd.Parameters.AddWithValue("$h", tokenHash);
        return cmd.ExecuteScalar() is long id ? EnrollTokenById(id) : null;
    }

    public bool RevokeEnrollToken(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE enroll_tokens SET revoked_at = $now WHERE id = $id AND revoked_at IS NULL";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    // Регистрация агента одной транзакцией: проверка и расход токена, строка
    // hosts (новый хост сразу попадает в группу токена), учётка. Отметка
    // duplicate_kind — у хоста уже есть действующая учётка того же вида
    // (переустановка или подделка имени): учётка работает, но видна в UI.
    public (EnrollOutcome Outcome, AgentCredential? Credential, string? TokenName) Enroll(
        string tokenHash, string hostName, string kind, string secretHash, string? ip, DateTime nowUtc)
    {
        using var c = Open();
        using var tx = c.BeginTransaction(System.Data.IsolationLevel.Serializable);

        long tokenId; long? tokenGroup; string tokenName;
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = @"
SELECT id, group_id, name FROM enroll_tokens
WHERE token_hash = $h AND revoked_at IS NULL AND expires_at > $now
  AND (max_uses IS NULL OR uses < max_uses)";
            q.Parameters.AddWithValue("$h", tokenHash);
            q.Parameters.AddWithValue("$now", nowUtc.ToString("o"));
            using var r = q.ExecuteReader();
            if (!r.Read()) return (EnrollOutcome.InvalidToken, null, null);
            tokenId = r.GetInt64(0);
            tokenGroup = r.IsDBNull(1) ? null : r.GetInt64(1);
            tokenName = r.GetString(2);
        }

        using (var u = c.CreateCommand())
        {
            u.Transaction = tx;
            u.CommandText = "UPDATE enroll_tokens SET uses = uses + 1 WHERE id = $id";
            u.Parameters.AddWithValue("$id", tokenId);
            u.ExecuteNonQuery();
        }

        long hostId;
        using (var h = c.CreateCommand())
        {
            h.Transaction = tx;
            h.CommandText = @"
INSERT INTO hosts (name, group_id) VALUES ($n, $g) ON CONFLICT(name) DO NOTHING;
UPDATE hosts SET group_id = $g WHERE name = $n AND group_id IS NULL AND $g IS NOT NULL;
SELECT id FROM hosts WHERE name = $n;";
            h.Parameters.AddWithValue("$n", hostName);
            h.Parameters.AddWithValue("$g", (object?)tokenGroup ?? DBNull.Value);
            hostId = Convert.ToInt64(h.ExecuteScalar());
        }

        bool duplicate;
        using (var d = c.CreateCommand())
        {
            d.Transaction = tx;
            d.CommandText = "SELECT COUNT(*) FROM agent_credentials WHERE host_id = $hid AND kind = $k AND revoked_at IS NULL";
            d.Parameters.AddWithValue("$hid", hostId);
            d.Parameters.AddWithValue("$k", kind);
            duplicate = Convert.ToInt32(d.ExecuteScalar()) > 0;
        }

        long credId;
        using (var ins = c.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = @"
INSERT INTO agent_credentials (host_id, kind, secret_hash, enroll_token_id, created_at, last_ip, duplicate_kind)
VALUES ($hid, $k, $s, $tid, $now, $ip, $dup);
SELECT last_insert_rowid();";
            ins.Parameters.AddWithValue("$hid", hostId);
            ins.Parameters.AddWithValue("$k", kind);
            ins.Parameters.AddWithValue("$s", secretHash);
            ins.Parameters.AddWithValue("$tid", tokenId);
            ins.Parameters.AddWithValue("$now", nowUtc.ToString("o"));
            ins.Parameters.AddWithValue("$ip", (object?)ip ?? DBNull.Value);
            ins.Parameters.AddWithValue("$dup", duplicate ? 1 : 0);
            credId = Convert.ToInt64(ins.ExecuteScalar());
        }
        tx.Commit();

        var cred = new AgentCredential(credId, hostId, hostName, kind, nowUtc, tokenId, tokenName, null, ip, null, duplicate);
        return (EnrollOutcome.Ok, cred, tokenName);
    }

    // Каждый запрос агента: поиск по уникальному индексу secret_hash. last_used_at
    // пишется не чаще раза в минуту — upload'ы идут пачками, heartbeat и так раз в минуту.
    public AgentIdentity? AuthenticateAgent(string secretHash, string? ip)
    {
        using var c = Open();
        AgentIdentity? id = null;
        DateTime? lastUsed = null;
        using (var q = c.CreateCommand())
        {
            q.CommandText = @"
SELECT a.id, a.host_id, h.name, a.kind, a.last_used_at
FROM agent_credentials a JOIN hosts h ON h.id = a.host_id
WHERE a.secret_hash = $s AND a.revoked_at IS NULL";
            q.Parameters.AddWithValue("$s", secretHash);
            using var r = q.ExecuteReader();
            if (!r.Read()) return null;
            id = new AgentIdentity(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3));
            lastUsed = r.IsDBNull(4) ? null : ParseUtc(r.GetString(4));
        }
        if (lastUsed is null || DateTime.UtcNow - lastUsed.Value > TimeSpan.FromMinutes(1))
        {
            using var u = c.CreateCommand();
            u.CommandText = "UPDATE agent_credentials SET last_used_at = $now, last_ip = $ip WHERE id = $id";
            u.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            u.Parameters.AddWithValue("$ip", (object?)ip ?? DBNull.Value);
            u.Parameters.AddWithValue("$id", id.CredentialId);
            u.ExecuteNonQuery();
        }
        return id;
    }

    public List<AgentCredential> ListAgentCredentials(long? hostId = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        string where = hostId is null ? "" : "WHERE a.host_id = $hid";
        cmd.CommandText = $@"
SELECT a.id, a.host_id, h.name, a.kind, a.created_at, a.enroll_token_id, t.name,
       a.last_used_at, a.last_ip, a.revoked_at, a.duplicate_kind
FROM agent_credentials a
JOIN hosts h ON h.id = a.host_id
LEFT JOIN enroll_tokens t ON t.id = a.enroll_token_id
{where}
ORDER BY a.revoked_at IS NOT NULL, h.name, a.id DESC";
        if (hostId is not null) cmd.Parameters.AddWithValue("$hid", hostId.Value);
        var list = new List<AgentCredential>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AgentCredential(
                r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), ParseUtc(r.GetString(4)),
                r.IsDBNull(5) ? null : r.GetInt64(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : ParseUtc(r.GetString(7)), r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : ParseUtc(r.GetString(9)), r.GetInt64(10) != 0));
        }
        return list;
    }

    public AgentCredential? AgentCredentialById(long id) => ListAgentCredentials().FirstOrDefault(a => a.Id == id);

    public bool RevokeAgentCredential(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE agent_credentials SET revoked_at = $now WHERE id = $id AND revoked_at IS NULL";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    // Снять отметку «повтор» — админ проверил, что это переустановка.
    public bool AcknowledgeAgentCredential(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE agent_credentials SET duplicate_kind = 0 WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void SetHostLegacyAuth(string hostName, bool legacy)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE hosts SET last_auth_legacy = $l WHERE name = $n";
        cmd.Parameters.AddWithValue("$l", legacy ? 1 : 0);
        cmd.Parameters.AddWithValue("$n", hostName);
        cmd.ExecuteNonQuery();
    }

    internal static DateTime ParseUtc(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
