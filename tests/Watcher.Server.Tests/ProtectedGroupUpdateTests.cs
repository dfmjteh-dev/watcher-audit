using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Watcher;
using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

public sealed class ProtectedGroupUpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "watcher-group-" + Guid.NewGuid().ToString("N"));
    private readonly Db _db;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly long _group;
    private readonly MaintenanceSigner _signer;
    private DateTime _now = DateTime.UtcNow;
    private string Public => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
    public ProtectedGroupUpdateTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new Db(Path.Combine(_dir, "db"));
        _group = _db.CreateGroup("group"); _db.SetGroupTargetVersion(_group, "0.15.2");
        string keyPath = Path.Combine(_dir, "key.pem"); File.WriteAllText(keyPath, _key.ExportPkcs8PrivateKeyPem());
        _signer = new MaintenanceSigner(new ServerConfig { MaintenanceSigningKeyPath = keyPath });
    }
    public void Dispose() { _key.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }
    private AgentIdentity Agent(string name = "host", string kind = "video", bool protectedAgent = true, string? secret = null)
    {
        _db.EnsureHostExists(name); long host = _db.Inventory(onlyName: name).Single().Id!.Value;
        using var c = _db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE hosts SET group_id=$g WHERE id=$h";
        cmd.Parameters.AddWithValue("$g", _group); cmd.Parameters.AddWithValue("$h", host); cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$secret", AgentAuth.Hash(secret ?? Guid.NewGuid().ToString("N"))); cmd.Parameters.AddWithValue("$now", _now.ToString("o"));
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO agent_credentials(host_id,kind,secret_hash,created_at) VALUES($h,$kind,$secret,$now) RETURNING id";
        var identity = new AgentIdentity(Convert.ToInt64(cmd.ExecuteScalar()), host, name, kind);
        if (protectedAgent) _db.ReceiveDiagnostics(identity, new([], MaintenanceKeyId: MaintenanceSigning.KeyId(Public)), _now);
        return identity;
    }
    private AgentPackage Package(string platform = "windows-msi", string version = "0.15.2")
    {
        string sha = new string('a', 64);
        string signature = Convert.ToBase64String(_key.SignData(UpdateSigning.Message(platform, version, sha), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        var package = new AgentPackage(0, platform, version, "test-package", sha, 100, signature, "admin", _now);
        return package with { Id = _db.InsertAgentPackage(package) };
    }
    private SignedMaintenanceGrant Command(AgentIdentity a, string action = "stop") => MaintenanceSigning.Sign(
        new(Guid.NewGuid().ToString("N"), _db.CredentialBinding(a.CredentialId)!, a.Kind, action, _now, _now.AddMinutes(15)), _key);
    private object? Offer(AgentIdentity a, UpdateRollout rollout, string platform = "windows-msi", string version = "0.15.1", MaintenanceSigner? signer = null) =>
        ProtectedGroupUpdates.OfferFor(_db, a.HostName, platform, version, a, signer ?? _signer, rollout, _now);
    private void Complete(AgentIdentity a, SignedMaintenanceGrant command, string status = "completed") =>
        _db.AcceptMaintenanceResults(a, [new(command.Grant.Id, status, status == "failed" ? "operation-failed" : status)], _now);

    [Theory]
    [InlineData("windows-msi", "windows-msi")]
    [InlineData("linux-deb", "linux-deb")]
    [InlineData("linux-rpm", "linux-rpm")]
    public void Group_target_issues_pinned_credential_bound_native_command(string platform, string native)
    {
        var a = Agent(); var package = Package(native); var rollout = new UpdateRollout(2);
        Assert.Null(Offer(a, rollout, platform));
        var signed = _db.PendingMaintenance(a.CredentialId, _now)!;
        Assert.NotNull(signed);
        Assert.True(MaintenanceSigning.Verify(signed, Public, _db.CredentialBinding(a.CredentialId)!, a.Kind, _now));
        Assert.Equal(package.Id, signed.Grant.PackageId);
        Assert.Equal(package.Signature, signed.Grant.PackageSignature);
        Assert.Equal(native, signed.Grant.Platform);
        Assert.Equal(1, rollout.Active);
        Assert.Single(_db.QueryAudit(null, "agent.group-update-issued", null, null, 10, 0));
        Offer(a, rollout, platform);
        Assert.Single(_db.MaintenanceHistory(a.HostId));
        Assert.Equal(1, rollout.Active);
    }

    [Fact]
    public void Older_windows_group_target_is_blocked_for_a_guard_that_cannot_downgrade()
    {
        // Цель 0.15.2 (MSI 0.15.x запрещает понижение) при агенте 0.15.3: прежний Guard
        // не умеет удалить и поставить — заявка без толку откатилась бы.
        var agent = Agent(); Package(); var rollout = new UpdateRollout(1);
        Assert.Null(Offer(agent, rollout, version: "0.15.3+manual-recovery"));
        Assert.Empty(_db.MaintenanceHistory(agent.HostId));
        Assert.Equal(0, rollout.Active);
        Assert.Contains("needs agent 0.16 or newer", _db.HostUpdateStates().Single(h => h.Name == agent.HostName).Status);
    }

    [Fact]
    public void Older_windows_group_target_is_dispatched_to_a_guard_that_can_downgrade()
    {
        // 0.16: понижение целью группы — Guard 0.16+ ставит старый MSI сам.
        var agent = Agent(); var package = Package(); var rollout = new UpdateRollout(1);
        Assert.Null(Offer(agent, rollout, version: "0.16.1"));
        var signed = _db.PendingMaintenance(agent.CredentialId, _now)!;
        Assert.Equal("0.15.2", signed.Grant.Version);
        Assert.Equal(package.Id, signed.Grant.PackageId);
    }

    [Fact]
    public void Guard_without_recovery_base_gets_current_version_first_then_target()
    {
        // Защита включена обновлением с 0.15.x — у Guard нет базы, обновление он отклоняет.
        var a = Agent(); var current = Package(version: "0.16.0"); var target = Package(version: "0.16.1");
        _db.SetGroupTargetVersion(_group, "0.16.1");
        _db.UpsertHeartbeat(a.HostName, "10.0.0.1", "0.16.0");
        var rollout = new UpdateRollout(1);
        Offer(a, rollout, version: "0.16.0");
        var first = _db.PendingMaintenance(a.CredentialId, _now)!; Assert.Equal("0.16.1", first.Grant.Version);
        _db.AcceptUpdateProgress(a, [new(first.Grant.Id, 1, _now, "failed", "0.16.0", "0.16.1", Reason: "base-unavailable")], _now);
        Complete(a, first, "failed"); rollout.Release(a.HostName);

        // Сразу — заявка на текущую версию: Guard закрепит её базой без установки.
        _now = _now.AddMinutes(1);
        Offer(a, rollout, version: "0.16.0");
        var seed = _db.PendingMaintenance(a.CredentialId, _now)!;
        Assert.Equal("0.16.0", seed.Grant.Version); Assert.Equal(current.Id, seed.Grant.PackageId);
        Assert.Contains("recovery base 0.16.0 first", _db.HostUpdateStates().Single(h => h.Name == a.HostName).Status);
        Complete(a, seed); rollout.Release(a.HostName);

        // База есть — обновление до цели, без часовой паузы после отказа «нет базы».
        _now = _now.AddMinutes(1);
        Offer(a, rollout, version: "0.16.0");
        var update = _db.PendingMaintenance(a.CredentialId, _now)!;
        Assert.Equal("0.16.1", update.Grant.Version); Assert.Equal(target.Id, update.Grant.PackageId);
    }

    [Fact]
    public void Missing_current_version_package_is_explained()
    {
        var a = Agent(); Package(version: "0.16.1"); _db.SetGroupTargetVersion(_group, "0.16.1");
        var rollout = new UpdateRollout(1);
        Offer(a, rollout, version: "0.16.0");
        var first = _db.PendingMaintenance(a.CredentialId, _now)!;
        _db.AcceptUpdateProgress(a, [new(first.Grant.Id, 1, _now, "failed", "0.16.0", "0.16.1", Reason: "base-unavailable")], _now);
        Complete(a, first, "failed"); rollout.Release(a.HostName);
        _now = _now.AddMinutes(1);
        Offer(a, rollout, version: "0.16.0");
        Assert.Null(_db.PendingMaintenance(a.CredentialId, _now));
        Assert.Contains("no recovery base; upload signed windows-msi package 0.16.0", _db.HostUpdateStates().Single(h => h.Name == a.HostName).Status);
    }

    [Theory]
    [InlineData("windows-msi", "0.16.2", "0.16.1", true, null)]          // MSI 0.16+ понижаются сами
    [InlineData("windows-msi", "0.15.9", "0.15.2", true, "needs agent")] // старый Guard
    [InlineData("windows-msi", "0.16.0", "0.15.2", true, null)]          // Guard 0.16: удалить и поставить
    [InlineData("windows-msi", "0.16.0", "0.15.0", true, "before 0.15.1")]
    [InlineData("windows-msi", "0.16.1", "0.15.2", false, "only to 0.16")] // незащищённый: MSI 0.15.x запрещают
    [InlineData("windows-msi", "0.16.2", "0.16.1", false, null)]
    [InlineData("linux-deb", "0.15.9", "0.15.4", true, null)]            // dpkg понижает и старым Guard
    [InlineData("linux-rpm", "0.15.9", "0.15.4", true, "needs agent")]
    [InlineData("linux-rpm", "0.16.0", "0.15.4", true, null)]
    [InlineData("linux-deb", "0.15.9", "0.15.4", false, null)]
    [InlineData("windows-msi", "0.15.1", "0.15.2", true, null)]          // не понижение
    public void Downgrade_rules(string platform, string agent, string target, bool protectedAgent, string? reason)
    {
        string? actual = ProtectedGroupUpdates.DowngradeBlock(platform, agent, target, protectedAgent);
        if (reason is null) Assert.Null(actual); else Assert.Contains(reason, actual);
    }

    [Fact]
    public void Parallel_video_and_terminal_heartbeats_issue_one_host_command()
    {
        var video = Agent(); var terminal = Agent(kind: "terminal"); Package();
        var rollout = new UpdateRollout(2);
        Parallel.For(0, 12, i => Offer(i % 2 == 0 ? video : terminal, rollout));
        Assert.Single(_db.MaintenanceHistory(video.HostId));
        Assert.Equal(1, rollout.Active);
    }

    [Fact]
    public void Db_serializes_dispatch_even_between_separate_rollout_instances()
    {
        var a = Agent(); Package();
        Parallel.For(0, 8, _ => Offer(a, new UpdateRollout(2)));
        Assert.Single(_db.MaintenanceHistory(a.HostId));
    }

    [Fact]
    public void Rollout_limits_hosts_and_matched_version_releases_slot()
    {
        var a = Agent("a"); var b = Agent("b"); Package(); var rollout = new UpdateRollout(1);
        Offer(a, rollout); Offer(b, rollout);
        Assert.Empty(_db.MaintenanceHistory(b.HostId));
        Offer(a, rollout, version: "0.15.2+commit");
        Offer(b, rollout);
        Assert.Single(_db.MaintenanceHistory(b.HostId));
        Assert.Equal(1, rollout.Active);
    }

    [Theory]
    [InlineData("stop", false)]
    [InlineData("stop", true)]
    [InlineData("repair", false)]
    [InlineData("uninstall", true)]
    [InlineData("resume", true)]
    public void Manual_operations_including_offline_and_completed_stop_block_automatic_update(string action, bool offline)
    {
        var a = Agent(); Package(); var cmd = Command(a, action);
        _db.IssueMaintenance(_db.AgentCredentialById(a.CredentialId)!, cmd, "admin", offline);
        if (action == "stop") Complete(a, cmd);
        var rollout = new UpdateRollout(1); Offer(a, rollout);
        Assert.Single(_db.MaintenanceHistory(a.HostId));
        Assert.Equal(0, rollout.Active);
    }

    [Fact]
    public void Reinstalled_agent_with_new_credential_gets_group_updates_again()
    {
        // Разрешённое удаление, затем станцию поставили заново из комплекта: новая учётка.
        var old = Agent(); Package(); var removed = Command(old, "uninstall");
        _db.IssueMaintenance(_db.AgentCredentialById(old.CredentialId)!, removed, "admin"); Complete(old, removed);
        var rollout = new UpdateRollout(2); Offer(old, rollout);
        Assert.Single(_db.MaintenanceHistory(old.HostId));
        // Действующее разрешение удаления (15 мин) держит хост — переустановка позже.
        _now = _now.AddMinutes(20);
        var fresh = Agent(old.HostName);
        Offer(fresh, rollout);
        Assert.Equal("0.15.2", _db.PendingMaintenance(fresh.CredentialId, _now)!.Grant.Version);
    }

    [Fact]
    public void Uninstall_tombstone_survives_history_pruning_until_explicit_resume()
    {
        var a = Agent(); Package(); var removed = Command(a, "uninstall");
        _db.IssueMaintenance(_db.AgentCredentialById(a.CredentialId)!, removed, "admin"); Complete(a, removed);
        _now = _now.AddDays(31); _db.PruneDiagnostics(_now);
        var rollout = new UpdateRollout(2); Offer(a, rollout);
        Assert.Single(_db.MaintenanceHistory(a.HostId)); Assert.Equal(0, rollout.Active);
        var resume = Command(a, "resume"); _db.IssueMaintenance(_db.AgentCredentialById(a.CredentialId)!, resume, "admin");
        _now = _now.AddSeconds(1); Complete(a, resume);
        _now = _now.AddDays(31); _db.PruneDiagnostics(_now);
        Assert.Equal("resume", Assert.Single(_db.MaintenanceHistory(a.HostId)).Action);
        Offer(a, rollout);
        Assert.Equal(2, _db.MaintenanceHistory(a.HostId).Count);
    }

    [Fact]
    public void Failed_or_expired_dispatch_is_not_reissued_for_one_hour()
    {
        var a = Agent(); Package(); var rollout = new UpdateRollout(1); Offer(a, rollout);
        var first = _db.PendingMaintenance(a.CredentialId, _now)!; Complete(a, first, "failed"); rollout.Release(a.HostName);
        _now = _now.AddMinutes(20); _db.PruneDiagnostics(_now); Offer(a, rollout);
        Assert.Single(_db.MaintenanceHistory(a.HostId)); Assert.Equal(0, rollout.Active);
        _now = _now.AddMinutes(41); Offer(a, rollout);
        Assert.Equal(2, _db.MaintenanceHistory(a.HostId).Count);
    }

    [Fact]
    public void Pending_command_is_not_delivered_after_target_change_and_old_ttl_is_respected()
    {
        var a = Agent(); Package(); Package(version: "0.15.3"); var rollout = new UpdateRollout(1); Offer(a, rollout);
        _db.SetGroupTargetVersion(_group, "0.15.3");
        Assert.Null(_db.PendingMaintenance(a.CredentialId, _now));
        Offer(a, rollout); Assert.Single(_db.MaintenanceHistory(a.HostId));
        _now = _now.AddMinutes(16); Offer(a, rollout);
        Assert.Equal("0.15.3", _db.PendingMaintenance(a.CredentialId, _now)!.Grant.Version);
    }

    [Fact]
    public void Unknown_pin_missing_native_package_and_revoked_credential_never_issue_commands()
    {
        var a = Agent(); var rollout = new UpdateRollout(2);
        Package("linux-deb"); Offer(a, rollout, "windows-msi");
        Assert.Empty(_db.MaintenanceHistory(a.HostId));
        Package(); Offer(a, rollout, signer: new MaintenanceSigner(new ServerConfig()));
        Assert.Empty(_db.MaintenanceHistory(a.HostId));
        using var c = _db.Open(); using var sql = c.CreateCommand();
        sql.CommandText = "UPDATE agent_credentials SET revoked_at=$at WHERE id=$id";
        sql.Parameters.AddWithValue("$at", _now.ToString("o")); sql.Parameters.AddWithValue("$id", a.CredentialId); sql.ExecuteNonQuery();
        Offer(a, rollout); Assert.Empty(_db.MaintenanceHistory(a.HostId)); Assert.Equal(0, rollout.Active);
    }

    [Fact]
    public void Unprotected_agent_keeps_normal_offer_and_foreign_credential_cannot_acknowledge()
    {
        var a = Agent(protectedAgent: false); Package(); var rollout = new UpdateRollout(2);
        Assert.NotNull(Offer(a, rollout)); Assert.Empty(_db.MaintenanceHistory(a.HostId));
        _db.ReceiveDiagnostics(a, new([], MaintenanceKeyId: MaintenanceSigning.KeyId(Public)), _now);
        Offer(a, rollout); var pending = _db.PendingMaintenance(a.CredentialId, _now)!;
        var b = Agent("other"); Complete(b, pending);
        Assert.Equal("queued", Assert.Single(_db.MaintenanceHistory(a.HostId)).Status);
    }

    [Fact]
    public void Result_origin_and_retry_state_survive_expiry_and_reopening_database()
    {
        var a = Agent(); Package(); var rollout = new UpdateRollout(2); Offer(a, rollout);
        var cmd = _db.PendingMaintenance(a.CredentialId, _now)!;
        Complete(a, cmd, "accepted");
        Assert.True(_db.IsGroupUpdate(a.CredentialId, cmd.Grant.Id));
        Assert.StartsWith("installing", _db.HostUpdateStates().Single().Status);
        _now = _now.AddMinutes(20); _db.PruneDiagnostics(_now);
        Assert.Equal("group-update:expired", Assert.Single(_db.MaintenanceHistory(a.HostId)).Code);
        var reopened = new Db(Path.Combine(_dir, "db"));
        ProtectedGroupUpdates.OfferFor(reopened, a.HostName, "windows-msi", "0.15.1", a, _signer, new UpdateRollout(2), _now);
        Assert.Single(_db.MaintenanceHistory(a.HostId));
    }
    [Fact]
    public void Server_restart_restores_slots_before_other_hosts_can_dispatch()
    {
        var a = Agent("a"); var b = Agent("b"); Package();
        Offer(a, new UpdateRollout(1));
        var restarted = new UpdateRollout(1);
        ProtectedGroupUpdates.RestoreRollout(_db, restarted, _now);
        Assert.Equal(1, restarted.Active);
        Offer(b, restarted);
        Assert.Empty(_db.MaintenanceHistory(b.HostId));
    }

    [Fact]
    public void Changed_pin_cancels_undelivered_auto_command()
    {
        var a = Agent(); Package(); var rollout = new UpdateRollout(1); Offer(a, rollout);
        var first = _db.PendingMaintenance(a.CredentialId, _now)!;
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _now = _now.AddSeconds(10);
        _db.ReceiveDiagnostics(a, new([], MaintenanceKeyId: MaintenanceSigning.KeyId(Convert.ToBase64String(otherKey.ExportSubjectPublicKeyInfo()))), _now);
        Assert.Null(_db.PendingMaintenance(a.CredentialId, _now));
        Assert.Equal("group-update:pin-changed", Assert.Single(_db.MaintenanceHistory(a.HostId)).Code);
        Assert.True(_db.IsGroupUpdate(a.CredentialId, first.Grant.Id));
    }

    [Fact]
    public async Task Heartbeat_and_diagnostics_deliver_group_command_and_release_its_slot_on_completion()
    {
        _now = DateTime.UtcNow.AddSeconds(-10);
        string secret = AgentAuth.NewAgentSecret(); var a = Agent("http-agent", secret: secret); Package();
        var settings = new Dictionary<string, string>
        {
            ["Server__DataDir"] = _dir,
            ["Server__DbPath"] = Path.Combine(_dir, "db"),
            ["Server__AdminUser"] = "admin",
            ["Server__AdminPassword"] = "test-password-123",
            ["Server__CookieSecure"] = "false",
            ["Server__AllowAnonymousAgents"] = "true",
            ["Server__MaintenanceSigningKeyPath"] = Path.Combine(_dir, "key.pem")
        };
        var previous = settings.ToDictionary(p => p.Key, p => Environment.GetEnvironmentVariable(p.Key));
        try
        {
            foreach (var setting in settings) Environment.SetEnvironmentVariable(setting.Key, setting.Value);
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            using var heartbeat = await client.PostAsJsonAsync("/api/heartbeat", new { host = a.HostName, version = "0.15.1", platform = "windows-msi" });
            heartbeat.EnsureSuccessStatusCode();
            var body = await heartbeat.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(JsonValueKind.Null, body.GetProperty("update").ValueKind);
            Assert.Equal(1, factory.Services.GetRequiredService<UpdateRollout>().Active);
            using var response = await client.PostAsJsonAsync("/api/agent-diagnostics", new DiagnosticBatch([]));
            response.EnsureSuccessStatusCode();
            var receipt = (await response.Content.ReadFromJsonAsync<DiagnosticReceipt>())!;
            var grant = receipt.Maintenance!; Assert.NotNull(grant);
            Assert.True(MaintenanceSigning.Verify(grant, Public, _db.CredentialBinding(a.CredentialId)!, a.Kind, DateTime.UtcNow));
            using (var c = _db.Open())
            using (var sql = c.CreateCommand())
            {
                sql.CommandText = "UPDATE agent_diagnostic_state SET last_at=$at WHERE credential_id=$cid";
                sql.Parameters.AddWithValue("$at", DateTime.UtcNow.AddSeconds(-10).ToString("o"));
                sql.Parameters.AddWithValue("$cid", a.CredentialId); sql.ExecuteNonQuery();
            }
            using var completion = await client.PostAsJsonAsync("/api/agent-diagnostics", new DiagnosticBatch([], MaintenanceResults: [new(grant.Grant.Id, "completed", "completed")]));
            completion.EnsureSuccessStatusCode();
            Assert.Equal(0, factory.Services.GetRequiredService<UpdateRollout>().Active);
            Assert.Equal("ok 0.15.2", _db.HostUpdateStates().Single().Status);
            using var nextHeartbeat = await client.PostAsJsonAsync("/api/heartbeat", new { host = a.HostName, version = "0.15.2", platform = "windows-msi", updateStatus = "failed 0.14.0: obsolete normal updater result" });
            nextHeartbeat.EnsureSuccessStatusCode();
            Assert.Equal("ok 0.15.2", _db.HostUpdateStates().Single().Status);
        }
        finally { foreach (var setting in previous) Environment.SetEnvironmentVariable(setting.Key, setting.Value); }
    }

}
