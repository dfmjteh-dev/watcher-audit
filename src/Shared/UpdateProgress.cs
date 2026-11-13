namespace Watcher;

// Отдельно от результата команды: этап не подтверждает исполнение и не освобождает rollout.
public sealed record UpdateProgressReport(string GrantId, long Revision, DateTime At, string Stage, string? FromVersion,
    string ToVersion, string? Reason = null, string? Rollback = null, string? RollbackReason = null,
    int? ErrorCode = null, int? RollbackErrorCode = null);
public sealed record UpdateProgressAck(string GrantId, long Revision);
public static class UpdateProgressContract
{
    public static readonly HashSet<string> Stages = ["downloading", "verifying-package", "preparing", "installing", "checking", "completed", "rolling-back", "restoring", "rollback-pending", "rolled-back", "failed"];
    public static readonly HashSet<string> Reasons = ["process-interrupted", "download-http", "download-tls", "download-timeout", "package-size", "package-integrity", "publisher-signature", "base-unavailable", "baseline-incompatible", "platform-mismatch", "current-files-invalid", "identity-changed", "snapshot-invalid", "disk-space", "file-busy", "install-failed", "installer-busy", "installer-timeout", "reboot-required", "service-start", "service-registration", "startup-mode", "version-mismatch", "rollback-install-failed", "cleanup-pending", "operation-failed"];
    public static bool Terminal(string stage) => stage is "completed" or "rolled-back" or "failed";
    public static bool Valid(UpdateProgressReport p) => Guid.TryParseExact(p.GrantId, "N", out _) && p.Revision is > 0 and <= 1000000000
        && p.At.Kind == DateTimeKind.Utc && Stages.Contains(p.Stage) && UpdateSigning.IsValidVersion(p.ToVersion)
        && (p.FromVersion is null || UpdateSigning.IsValidVersion(p.FromVersion))
        && (p.Reason is null || Reasons.Contains(p.Reason)) && (p.RollbackReason is null || Reasons.Contains(p.RollbackReason))
        && p.Rollback is null or "pending" or "succeeded"
        && (p.ErrorCode is null or >= 0 and <= 65535) && (p.RollbackErrorCode is null or >= 0 and <= 65535)
        && (p.Stage != "rolled-back" || p.Rollback == "succeeded");
}
