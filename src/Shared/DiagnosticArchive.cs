namespace Watcher;

public sealed record DiagnosticArchiveRequest(string Id, DateTime ExpiresAt);
public sealed record DiagnosticLogSummary(string Source, int Lines, int Warnings, int Errors, bool Truncated, List<int> InstallerCodes);
public sealed record DiagnosticArchiveReport(string RequestId, DateTime CollectedAt, string Version, string Platform,
    List<AgentDiagnostic> Events, List<UpdateProgressReport> Updates, List<DiagnosticLogSummary> Logs);
public static class DiagnosticArchiveContract
{
    public const int MaxBytes = 131072;
    public static bool Valid(DiagnosticArchiveReport r) => Guid.TryParseExact(r.RequestId, "N", out _)
        && r.CollectedAt.Kind == DateTimeKind.Utc && UpdateSigning.IsValidVersion(r.Version) && r.Platform is "windows" or "linux"
        && r.Events is { Count: <= 128 } && r.Events.All(e => e is not null && DiagnosticContract.Valid(e))
        && r.Updates is { Count: <= 16 } && r.Updates.All(p => p is not null && UpdateProgressContract.Valid(p))
        && r.Logs is { Count: <= 8 } && r.Logs.All(l => l is not null && l.Source is "agent" or "service" or "terminal" or "msi"
            && l.Lines is >= 0 and <= 20000 && l.Warnings >= 0 && l.Errors >= 0 && l.Warnings <= l.Lines && l.Errors <= l.Lines
            && l.InstallerCodes is { Count: <= 32 } && l.InstallerCodes.All(c => c is >= 0 and <= 65535));
}
