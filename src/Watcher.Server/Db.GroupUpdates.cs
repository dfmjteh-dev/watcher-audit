using System.Text.Json;

namespace Watcher.Server;

public sealed record ActiveGroupDispatch(string Host, string Version, string Id, DateTime IssuedAt);

public sealed partial class Db
{
    // Последняя групповая заявка хоста отклонена Guard: нет базы восстановления.
    public bool GroupUpdateNeedsBase(long hostId)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.Parameters.AddWithValue("$h", hostId);
        cmd.CommandText = @"SELECT status, json_extract(progress_json,'$.reason') FROM agent_maintenance
WHERE host_id=$h AND action='update' AND code LIKE 'group-update:%' ORDER BY issued_at DESC, rowid DESC LIMIT 1";
        using var r = cmd.ExecuteReader();
        return r.Read() && r.GetString(0) == "failed" && !r.IsDBNull(1) && r.GetString(1) == "base-unavailable";
    }

    // seedFor — цель группы, ради которой выдаётся заявка на текущую версию (база Guard).
    public bool TryIssueGroupUpdate(AgentIdentity identity, SignedMaintenanceGrant signed, string sourcePlatform, DateTime now, string? seedFor = null)
    {
        var grant = signed.Grant;
        if (grant.Action != "update" || !MaintenanceSigning.Shape(grant) || grant.Kind != identity.Kind) return false;
        string? expectedPlatform = sourcePlatform switch { "windows-msi" => "windows-msi", "linux-deb" or "linux-rpm" => sourcePlatform, _ => null };
        if (expectedPlatform is null || grant.Platform != expectedPlatform) return false;
        using var c = Open();
        // Один писатель на хост, включая его video/terminal учётки и параллельные heartbeat.
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$cid", identity.CredentialId); cmd.Parameters.AddWithValue("$h", identity.HostId);
        cmd.Parameters.AddWithValue("$name", identity.HostName); cmd.Parameters.AddWithValue("$kind", identity.Kind);
        cmd.Parameters.AddWithValue("$version", grant.Version); cmd.Parameters.AddWithValue("$key", signed.KeyId);
        cmd.Parameters.AddWithValue("$now", now.ToString("o")); cmd.Parameters.AddWithValue("$recent", now.AddHours(-1).ToString("o"));
        cmd.CommandText = @"SELECT a.secret_hash FROM agent_credentials a
JOIN hosts h ON h.id=a.host_id JOIN host_groups g ON g.id=h.group_id
JOIN agent_diagnostic_state d ON d.credential_id=a.id
WHERE a.id=$cid AND a.host_id=$h AND h.name=$name AND a.kind=$kind AND a.revoked_at IS NULL
AND d.maintenance_key_id=$key AND g.agent_target_version=$targetVersion";
        cmd.Parameters.AddWithValue("$targetVersion", seedFor ?? grant.Version);
        if (cmd.ExecuteScalar() is not string secretHash || MaintenanceSigning.BindingFromHash(secretHash) != grant.Binding) return false;
        cmd.Parameters.AddWithValue("$pid", grant.PackageId); cmd.Parameters.AddWithValue("$platform", grant.Platform);
        cmd.Parameters.AddWithValue("$sha", grant.Sha256); cmd.Parameters.AddWithValue("$sig", grant.PackageSignature);
        cmd.Parameters.AddWithValue("$size", grant.PackageSize);
        cmd.CommandText = @"SELECT EXISTS(SELECT 1 FROM agent_packages WHERE id=$pid AND version=$version
AND platform=$platform AND sha256=$sha AND signature=$sig AND size=$size)";
        if (Convert.ToInt64(cmd.ExecuteScalar()) == 0) return false;
        // Завершённый stop остаётся временной паузой до срока разрешения.
        // Завершённый uninstall запрещает автоматическую переустановку до явного resume/repair.
        // Заявка на обновление с итогом от агента не держит хост до срока разрешения (после
        // заявки на базу сразу идёт обновление до цели); повторы той же версии и так
        // не чаще раза в час — условие ниже. Stop/resume и прочие держат, как раньше.
        cmd.CommandText = @"SELECT EXISTS(SELECT 1 FROM agent_maintenance WHERE host_id=$h
AND expires_at>$now AND NOT (action='update' AND status IN ('completed','failed')))
OR (COALESCE((SELECT action FROM agent_maintenance WHERE host_id=$h AND status='completed'
AND action IN ('uninstall','resume','repair') ORDER BY COALESCE(updated_at,issued_at) DESC,issued_at DESC,rowid DESC LIMIT 1),'')='uninstall'
-- 0.16: агента поставили заново (новая учётка после удаления) — обычный хост, а не «удалён».
AND NOT EXISTS(SELECT 1 FROM agent_credentials a WHERE a.host_id=$h AND a.revoked_at IS NULL AND a.created_at > (SELECT MAX(COALESCE(u.updated_at,u.issued_at))
FROM agent_maintenance u WHERE u.host_id=$h AND u.action='uninstall' AND u.status='completed')))
OR EXISTS(SELECT 1 FROM agent_maintenance WHERE host_id=$h AND action='update' AND issued_at>$recent
AND code LIKE 'group-update:%' AND code NOT IN ('group-update:target-changed','group-update:pin-changed','group-update:target-met','group-update:base-seeding')
AND json_extract(grant_json,'$.grant.version')=$version)";
        if (Convert.ToInt64(cmd.ExecuteScalar()) != 0) return false;
        cmd.Parameters.AddWithValue("$id", grant.Id); cmd.Parameters.AddWithValue("$expires", grant.ExpiresAt.ToString("o"));
        cmd.Parameters.AddWithValue("$grant", JsonSerializer.Serialize(signed, DiagnosticJson));
        cmd.CommandText = @"INSERT INTO agent_maintenance(id,credential_id,host_id,action,status,code,issued_at,expires_at,grant_json)
VALUES($id,$cid,$h,'update','queued',$queuedCode,$now,$expires,$grant)";
        cmd.Parameters.AddWithValue("$queuedCode", seedFor is null ? "group-update:queued" : "group-update:base-seed");
        cmd.ExecuteNonQuery();
        if (seedFor is not null)
        {
            // Отказ «нет базы» не держит повтор обновления до цели час, как обычная неудача.
            cmd.CommandText = @"UPDATE agent_maintenance SET code='group-update:base-seeding' WHERE host_id=$h AND action='update'
AND status='failed' AND json_extract(grant_json,'$.grant.version')=$targetVersion AND json_extract(progress_json,'$.reason')='base-unavailable'";
            cmd.ExecuteNonQuery();
        }
        cmd.CommandText = "UPDATE hosts SET update_status=$status,update_status_at=$now WHERE id=$h";
        cmd.Parameters.AddWithValue("$status", seedFor is null ? "queued " + grant.Version : $"queued {seedFor}: recovery base {grant.Version} first"); cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO audit_log(ts,username,action,target,details) VALUES($now,'system','agent.group-update-issued',$target,$details)";
        cmd.Parameters.AddWithValue("$target", "host:" + identity.HostId);
        cmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
        {
            id = grant.Id,
            credentialId = identity.CredentialId,
            version = grant.Version,
            packageId = grant.PackageId,
            sourcePlatform,
            packagePlatform = grant.Platform
        }));
        cmd.ExecuteNonQuery(); tx.Commit(); return true;
    }

    public List<ActiveGroupDispatch> ActiveGroupUpdates(DateTime now)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT h.name,h.agent_version,m.id,m.issued_at,json_extract(m.grant_json,'$.grant.version')
FROM agent_maintenance m JOIN hosts h ON h.id=m.host_id WHERE m.action='update' AND m.code LIKE 'group-update:%'
AND m.status IN ('queued','accepted','completed') AND m.expires_at>$now ORDER BY m.issued_at";
        cmd.Parameters.AddWithValue("$now", now.ToString("o"));
        using var reader = cmd.ExecuteReader(); var result = new List<ActiveGroupDispatch>();
        while (reader.Read())
        {
            string version = reader.GetString(4);
            if (AppVersion.Core(reader.IsDBNull(1) ? "" : reader.GetString(1)) == version) continue;
            result.Add(new(reader.GetString(0), version, reader.GetString(2), ParseUtc(reader.GetString(3))));
        }
        return result;
    }

    public bool IsGroupUpdate(long credentialId, string id)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_maintenance WHERE id=$id AND credential_id=$cid AND action='update' AND code LIKE 'group-update:%')";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$cid", credentialId);
        return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
    }
}
