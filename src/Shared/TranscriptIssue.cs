namespace Watcher.Shared;

// Только коды, без команд, аргументов или содержимого транскриптов.
public static class TranscriptIssue
{
    public const string EventPending = "event-pending", EventMissing = "event-missing",
        HistoryUnavailable = "history-unavailable", EventReadFailed = "event-read-failed",
        AuditDisabled = "audit-disabled", HostUnconfirmed = "host-unconfirmed",
        ProcessMismatch = "process-mismatch", AncestryIncomplete = "ancestry-incomplete",
        AncestryCycle = "ancestry-cycle", InvalidTranscript = "invalid-transcript";

    public static bool IsKnown(string? code) => code is EventPending or EventMissing or HistoryUnavailable
        or EventReadFailed or AuditDisabled or HostUnconfirmed or ProcessMismatch or AncestryIncomplete
        or AncestryCycle or InvalidTranscript;
}
