namespace Watcher.Server;

// Мост для уже защищённых агентов: версия группы автоматически попадает
// в существующий проверяемый канал Guard, без отдельного действия в UI хоста.
public static class ProtectedGroupUpdates
{
    public static bool UsesMaintenanceChannel(Db db, AgentIdentity? identity) =>
        identity is { Kind: "video" or "terminal" } && db.DiagnosticStates(identity.HostId)
            .Any(s => s.CredentialId == identity.CredentialId && s.MaintenanceKeyId is not null);

    public static object? OfferFor(Db db, string host, string? platform, string? version,
        AgentIdentity? identity, MaintenanceSigner signer, UpdateRollout rollout, DateTime? nowUtc = null)
    {
        if (identity is not null && !string.Equals(host, identity.HostName, StringComparison.OrdinalIgnoreCase)) return null;
        if (identity is not null)
        {
            var credential = db.AgentCredentialById(identity.CredentialId);
            if (credential is null || credential.RevokedAt is not null || credential.HostId != identity.HostId || credential.Kind != identity.Kind) return null;
        }
        var state = identity is null || identity.Kind is not ("video" or "terminal") ? null
            : db.DiagnosticStates(identity.HostId).FirstOrDefault(s => s.CredentialId == identity.CredentialId);
        if (state?.MaintenanceKeyId is not { } keyId)
            return UpdateEndpoints.OfferFor(db, host, platform, version, rollout);
        if (db.TargetVersionForHost(host) is not { } target || AppVersion.Core(version ?? "") == target)
        { rollout.Release(host); return null; }
        string? nativePlatform = platform switch
        {
            "windows-msi" => "windows-msi",
            "linux-deb" or "linux-rpm" => platform,
            _ => null
        };
        if (nativePlatform is null) return null;
        // 0.16: понижение версии целью группы. Guard 0.16+ ставит старую версию через
        // удаление и установку (MSI/rpm не ставятся поверх новой); прежний Guard так не
        // умеет — заявка без толку откатилась бы, поэтому для него понятная причина.
        if (DowngradeBlock(nativePlatform, version, target, protectedAgent: true) is { } reason)
        {
            db.SetHostUpdateInfo(host, platform, $"blocked {target}: {reason}");
            rollout.Release(host);
            return null;
        }
        // 0.16: Guard, защиту которого включило обновление с 0.15.x, не имеет базы
        // восстановления и отказывает в обновлении (base-unavailable). Тогда сначала —
        // заявка на ТЕКУЩУЮ версию хоста: Guard закрепит этот подписанный пакет базой,
        // ничего не переустанавливая, и следующий heartbeat выдаст обновление до цели.
        string current = AppVersion.Core(version ?? "");
        bool seed = db.GroupUpdateNeedsBase(identity!.HostId);
        var package = db.AgentPackageFor(nativePlatform, seed ? current : target);
        if (package is null)
        {
            db.SetHostUpdateInfo(host, platform, seed
                ? $"blocked {target}: GuardRepair has no recovery base; upload signed {nativePlatform} package {current} to Updates"
                : $"blocked {target}: signed {nativePlatform} package unavailable");
            return null;
        }
        using var key = signer.Key(keyId);
        if (key is null)
        {
            db.SetHostUpdateInfo(host, platform, $"blocked {target}: pinned maintenance signing key unavailable");
            return null;
        }
        string? binding = db.CredentialBinding(identity.CredentialId);
        if (binding is null) return null;
        DateTime now = nowUtc ?? DateTime.UtcNow;
        var grant = new MaintenanceGrant(Guid.NewGuid().ToString("N"), binding, identity.Kind, "update", now,
            now.AddMinutes(15), package.Version, package.Sha256, package.Platform, package.Signature,
            PackageId: package.Id, PackageSize: package.Size);
        if (!MaintenanceSigning.Shape(grant)) return null;
        var signed = MaintenanceSigning.Sign(grant, key);
        rollout.TryDispatch(host, target, () => db.TryIssueGroupUpdate(identity, signed, platform!, now, seed ? target : null), signed.Grant.Id);
        // Обычный updater старого агента отклонит protected update; пакет доставит Guard.
        return null;
    }
    // MSI с этой версии допускают понижение сами; Guard с этой версии умеет ставить
    // более старый пакет через удаление и установку (MSI 0.15.x, rpm).
    public static readonly Version DowngradeCapable = new(0, 16, 0);
    // Ниже — Guard без журнала обновления: вернуть станцию на неё он не может.
    public static readonly Version OldestRestorable = new(0, 15, 1);

    // Причина, по которой понижение версии не выполнить (null — можно или это не понижение).
    public static string? DowngradeBlock(string platform, string? agentVersion, string target, bool protectedAgent)
    {
        if (!Version.TryParse(AppVersion.Core(agentVersion ?? ""), out var current) || !Version.TryParse(target, out var wanted) || wanted >= current) return null;
        if (!protectedAgent)
            return platform == "windows-msi" && wanted < DowngradeCapable
                ? $"unprotected Windows agents can return only to {DowngradeCapable.ToString(2)} or newer (older MSI forbid downgrade)" : null;
        if (wanted < OldestRestorable) return $"protected agents cannot return to versions before {OldestRestorable.ToString(3)}";
        bool reinstall = platform == "linux-rpm" || platform == "windows-msi" && wanted < DowngradeCapable;
        return reinstall && current < DowngradeCapable
            ? $"returning to {target} needs agent {DowngradeCapable.ToString(2)} or newer on the host (installed {current.ToString(3)})" : null;
    }

    public static void RestoreRollout(Db db, UpdateRollout rollout, DateTime now)
    {
        if (rollout.MaxConcurrent <= 0) return;
        foreach (var dispatch in db.ActiveGroupUpdates(now))
            rollout.RestoreProtected(dispatch.Host, dispatch.Version, dispatch.Id, dispatch.IssuedAt);
    }

}
