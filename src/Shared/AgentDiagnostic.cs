namespace Watcher;

// Закрытый контракт: ни произвольных сообщений, ни исключений, ни данных сессий.
public sealed record AgentDiagnostic(string Id, DateTime At, string Level, string Component, string Code);
public sealed record DiagnosticSnapshot(string RequestId, DateTime At, string Version, string Platform,
    long QueueBytes, int QueueFiles, long MemoryBytes, int Processes, long? FreeBytes);
public sealed record DiagnosticBatch(List<AgentDiagnostic>? Events, DiagnosticSnapshot? Snapshot = null, string? MaintenanceKeyId = null, List<MaintenanceResult>? MaintenanceResults = null, List<UpdateProgressReport>? UpdateProgress = null);
public sealed record DiagnosticReceipt(List<string> Accepted, string? RequestId = null, string? SnapshotAccepted = null, SignedMaintenanceGrant? Maintenance = null, List<string>? MaintenanceAccepted = null, List<UpdateProgressAck>? ProgressAccepted = null, DiagnosticArchiveRequest? ArchiveRequest = null);

public static class DiagnosticContract
{
    public static readonly HashSet<string> Levels = ["warning", "error"];
    public static readonly HashSet<string> Components = ["service", "uploader", "capture", "terminal", "recovery"];
    public static readonly HashSet<string> Codes = ["agent-warning", "agent-error", "server-unreachable", "upload-failed", "queue-pressure",
        "capture-start-failed", "capture-stopped", "audit-read-failed", "transcript-unclassified", "update-failed", "config-invalid",
        "credential-rejected", "repair-failed", "maintenance-rejected"];
    public static bool Valid(AgentDiagnostic e) => Guid.TryParseExact(e.Id, "N", out _) && Levels.Contains(e.Level)
        && Components.Contains(e.Component) && Codes.Contains(e.Code) && e.At.Kind == DateTimeKind.Utc;
}
