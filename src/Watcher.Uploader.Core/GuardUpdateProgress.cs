using System.Security.Cryptography;
using System.Text.Json;

namespace Watcher.Uploader;

public sealed record GuardProgressEnvelope(string Binding, string Kind, UpdateProgressReport Report, long AcknowledgedRevision = 0);
public sealed class GuardUpdateFailure(string reason, int? errorCode = null) : IOException("Guard update failed")
{
    public string Reason { get; } = UpdateProgressContract.Reasons.Contains(reason) ? reason : "operation-failed";
    public int? ErrorCode { get; } = errorCode is >= 0 and <= 65535 ? errorCode : null;
}
public static class GuardUpdateProgress
{
    public static (string Reason, int? Code) Failure(Exception error, string stage)
    {
        if (error is GuardUpdateFailure known) return (known.Reason, known.ErrorCode);
        if (error is HttpRequestException http) return (http.HttpRequestError == HttpRequestError.SecureConnectionError || http.InnerException is System.Security.Authentication.AuthenticationException ? "download-tls" : "download-http", http.StatusCode is { } status ? (int)status : null);
        if (error is OperationCanceledException) return ("download-timeout", null);
        if (error is TimeoutException) return ("installer-timeout", null);
        if (error is System.ComponentModel.Win32Exception native) return (stage is "installing" or "rolling-back" ? "install-failed" : "operation-failed", native.NativeErrorCode is >= 0 and <= 65535 ? native.NativeErrorCode : null);
        if (error is IOException && (error.HResult & 65535) is 28 or 112) return ("disk-space", error.HResult & 65535);
        string reason = error.Message switch
        {
            "Verified rollback base unavailable" => "base-unavailable",
            "Rollback requires a journal-aware package baseline" => "baseline-incompatible",
            "Protected package platform change unsupported" => "platform-mismatch",
            "Current files require repair before update" => "current-files-invalid",
            "Update changed an existing identity" or "Update identity changed before preparation" => "identity-changed",
            "Recovery publisher signature invalid" or "Offline package signature invalid" => "publisher-signature",
            "Recovery package integrity failure" => "package-integrity",
            "Offline package size mismatch" or "Package size mismatch" or "Package exceeds signed size" or "Recovery package exceeds declared size" => "package-size",
            "Invalid update journal" or "Update snapshot missing" or "Update snapshot integrity failure" or "Update configuration snapshot mismatch" => "snapshot-invalid",
            "Unsupported startup mode" or "Startup restoration failed" or "Startup restoration mismatch" or "Startup snapshot mismatch" => "startup-mode",
            "Service registration missing" or "Service registration mismatch" or "Service registration failed" or "Service path repair failed" or "Service account repair failed" => "service-registration",
            "Updated service did not reach expected state" or "Service did not remain in expected state" or "Service state timeout" or "Service start failed" or "Updated service did not start" => "service-start",
            "Installed agent version mismatch" => "version-mismatch",
            "Package reboot required" => "reboot-required",
            "Package installation failed" => "install-failed",
            "System tool timeout" => "installer-timeout",
            "Package rollback failed" or "Package rollback removal failed" or "MSI product remains during rollback" => "rollback-install-failed",
            _ => error is CryptographicException ? "package-integrity" : "operation-failed"
        };
        return (reason, null);
    }
    public static void Publish(string root, MaintenanceGrant grant, string stage, string? fromVersion, DateTime at,
        string? reason = null, string? rollback = null, string? rollbackReason = null, int? errorCode = null, int? rollbackErrorCode = null)
    {
        if (grant.Action != "update") return;
        if (!MaintenanceSigning.Shape(grant)) throw new IOException("Invalid progress grant");
        Directory.CreateDirectory(root);
        using var gate = Acquire(root);
        string directory = Path.Combine(root, "update-progress");
        string statePath = Path.Combine(directory, "state", grant.Id + ".json");
        var previous = Read(statePath);
        if (previous is not null && UpdateProgressContract.Terminal(previous.Report.Stage)) return;
        long revision = (previous?.Report.Revision ?? 0) + 1;
        var report = new UpdateProgressReport(grant.Id, revision, at, stage, fromVersion, grant.Version ?? "", reason, rollback, rollbackReason, errorCode, rollbackErrorCode);
        if (!UpdateProgressContract.Valid(report) || !Guid.TryParseExact(grant.Id, "N", out _)) throw new IOException("Invalid update progress");
        var envelope = new GuardProgressEnvelope(grant.Binding, grant.Kind, report, previous?.AcknowledgedRevision ?? 0);
        GuardPaths.Write(statePath, envelope);
        Prune(directory, at);
    }
    public static void TryPublish(string root, MaintenanceGrant grant, string stage, string? fromVersion, DateTime at,
        string? reason = null, string? rollback = null, string? rollbackReason = null, int? errorCode = null, int? rollbackErrorCode = null)
    {
        try { Publish(root, grant, stage, fromVersion, at, reason, rollback, rollbackReason, errorCode, rollbackErrorCode); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }
    private static FileStream Acquire(string root)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(Path.Combine(root, "progress.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 50) { Thread.Sleep(10); }
        }
    }
    private static GuardProgressEnvelope? Read(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 4096 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid progress file");
        return JsonSerializer.Deserialize<GuardProgressEnvelope>(File.ReadAllBytes(path), GuardPaths.Json);
    }
    public static List<UpdateProgressReport> Pending(string root, string? binding, string kind)
    {
        string directory = Path.Combine(root, "update-progress", "state"); var reports = new List<UpdateProgressReport>();
        if (binding is null || !Directory.Exists(directory)) return reports;
        foreach (string path in Directory.EnumerateFiles(directory, "*.json").Take(64))
        {
            try
            {
                var item = Read(path);
                if (item is not null && item.Binding == binding && item.Kind == kind && item.AcknowledgedRevision < item.Report.Revision && UpdateProgressContract.Valid(item.Report)) reports.Add(item.Report);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
            if (reports.Count == 16) break;
        }
        return reports;
    }
    public static void Acknowledge(string root, string? binding, string kind, IEnumerable<UpdateProgressAck> acknowledgements)
    {
        var pending = acknowledgements.Take(16).ToArray();
        if (binding is null || pending.Length == 0) return;
        using var gate = Acquire(root);
        foreach (var ack in pending)
        {
            if (!Guid.TryParseExact(ack.GrantId, "N", out _)) continue;
            string path = Path.Combine(root, "update-progress", "state", ack.GrantId + ".json");
            var item = Read(path);
            if (item is not null && item.Binding == binding && item.Kind == kind && item.Report.Revision == ack.Revision) GuardPaths.Write(path, item with { AcknowledgedRevision = ack.Revision });
        }
    }
    private static void Prune(string directory, DateTime now)
    {
        string state = Path.Combine(directory, "state");
        foreach (string path in Directory.EnumerateFiles(state, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Skip(64).Concat(Directory.EnumerateFiles(state, "*.json").Where(p => File.GetLastWriteTimeUtc(p) < now.AddDays(-30))).Distinct())
        {
            string name = Path.GetFileNameWithoutExtension(path); if (!Guid.TryParseExact(name, "N", out _)) continue;
            File.Delete(path);
        }
    }
}
