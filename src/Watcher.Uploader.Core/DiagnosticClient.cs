using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Watcher.Uploader;

public sealed class DiagnosticClient(HttpClient http, AgentIdentity identity, string serverUrl, DiagnosticJournal journal, string? archiveLogDirectory = null, string? archiveUpdateDirectory = null)
{
    private DateTime _next;
    private int _failures;
    private string? _request;
    private Task? _packageDownload;
    public Task FinishPendingAsync() => _packageDownload ?? Task.CompletedTask;
    public async Task SendAsync(string version, Func<AgentResourceReport?>? resourceProvider, string queueDir, CancellationToken ct, bool allowMaintenance = true, bool force = false)
    {
        if (!identity.HasCredential || !force && DateTime.UtcNow < _next) return;
        _next = DateTime.UtcNow.AddSeconds(30);
        try
        {
            AgentResourceReport? resources = null;
            if (_request is not null)
                try { resources = resourceProvider?.Invoke(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
            long? free = null;
            if (_request is not null)
                try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(queueDir))!).AvailableFreeSpace; } catch (IOException) { }
            var snapshot = _request is null ? null : new DiagnosticSnapshot(_request, DateTime.UtcNow, version,
                OperatingSystem.IsWindows() ? "windows" : "linux", resources?.QueueBytes ?? 0, resources?.QueueFiles ?? 0,
                resources?.MemBytes ?? Environment.WorkingSet, resources?.Processes ?? 1, free);
            string acknowledgementPath = journal.AcknowledgementPath;
            var captureEvents = DiagnosticSpool.Read(queueDir, acknowledgementPath);
            var recoveryJournal = DiagnosticJournal.At(Path.Combine(GuardPaths.Root, "recovery-diagnostics.json"));
            var recoveryEvents = GuardPaths.Enabled ? recoveryJournal.Pending().Take(8).ToList() : [];
            GuardPolicy? policy = null;
            try { policy = GuardPaths.Enabled ? GuardPaths.Load() : null; } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException) { }
            if (policy?.Credentials.ContainsKey("video") == true && identity.Kind != "video") recoveryEvents = [];
            var events = recoveryEvents.Concat(journal.Pending().Take(16)).Concat(captureEvents.Take(8)).ToList();
            var results = MaintenanceResults(identity.MaintenanceBinding, identity.Kind);
            var progress = GuardUpdateProgress.Pending(GuardPaths.Root, identity.MaintenanceBinding, identity.Kind);

            using var request = new HttpRequestMessage(HttpMethod.Post, serverUrl.TrimEnd('/') + "/api/agent-diagnostics")
            { Content = JsonContent.Create(new DiagnosticBatch(events, snapshot, policy is null ? null : MaintenanceSigning.KeyId(policy.MaintenancePublicKey), results.Select(r => new MaintenanceResult(r.GrantId, r.Status, r.Code)).ToList(), progress)) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Bearer);
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) identity.OnUnauthorized();
            if (!response.IsSuccessStatusCode) { Retry(); return; }
            var receipt = await response.Content.ReadFromJsonAsync<DiagnosticReceipt>(cancellationToken: ct);
            if (receipt?.Accepted is null || receipt.Accepted.Count > 32) { Retry(); return; }
            var accepted = receipt.Accepted.Intersect(events.Select(e => e.Id)).ToList();
            journal.Acknowledge(accepted);
            if (recoveryEvents.Count > 0) recoveryJournal.Acknowledge(accepted.Intersect(recoveryEvents.Select(e => e.Id)));
            DiagnosticSpool.Acknowledge(acknowledgementPath, accepted.Intersect(captureEvents.Select(e => e.Id)));
            if (snapshot is not null && receipt.SnapshotAccepted == snapshot.RequestId) _request = null;
            if (receipt.RequestId is { } id && Guid.TryParseExact(id, "N", out _)) { _request = id; _next = DateTime.UtcNow; }
            if (receipt.MaintenanceAccepted is { } confirmed)
                foreach (var result in results.Where(r => confirmed.Contains(r.GrantId)))
                {
                    string path = Path.Combine(GuardPaths.Root, "results", result.GrantId + ".json");
                    // Не удалять новую фазу операции, записанную Guard во время HTTP-запроса.
                    if (File.Exists(path) && System.Text.Json.JsonSerializer.Deserialize<GuardExecution>(File.ReadAllBytes(path), GuardPaths.Json) == result) File.Delete(path);
                }
            if (receipt.ProgressAccepted is { } progressAccepted) GuardUpdateProgress.Acknowledge(GuardPaths.Root, identity.MaintenanceBinding, identity.Kind, progressAccepted.Where(a => progress.Any(p => p.GrantId == a.GrantId && p.Revision == a.Revision)));
            if (allowMaintenance && receipt.Maintenance is { } grant && policy is not null && identity.MaintenanceBinding is { } binding
                && MaintenanceSigning.Verify(grant, policy.MaintenancePublicKey, binding, identity.Kind, DateTime.UtcNow))
            {
                if (grant.Grant.Action == "update")
                {
                    // Пока Guard не принял заявку, сервер присылает её в каждом ответе. Уже
                    // переданную (в очереди, с результатом или идёт обновление) не качаем
                    // заново: повторная загрузка перезаписывала пакет в кэше во время
                    // установки, падала и докладывала серверу «не выполнено» (0.16, стенд).
                    if ((_packageDownload is null || _packageDownload.IsCompleted) && !HandedToGuard(GuardPaths.Root, grant.Grant.Id))
                        _packageDownload = DownloadPackageAsync(grant, policy, ct);
                }
                else GuardPaths.Queue(grant);
            }
            if (receipt.ArchiveRequest is { } archive && Guid.TryParseExact(archive.Id, "N", out _)
                && archive.ExpiresAt > DateTime.UtcNow && archive.ExpiresAt <= DateTime.UtcNow.AddHours(2))
            {
                var report = DiagnosticArchiveCollector.Collect(archive, version, events, GuardPaths.Root, identity.MaintenanceBinding, identity.Kind, archiveLogDirectory, DateTime.UtcNow, archiveUpdateDirectory);
                using var upload = new HttpRequestMessage(HttpMethod.Post, serverUrl.TrimEnd('/') + "/api/agent-diagnostics/archive") { Content = JsonContent.Create(report) };
                upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Bearer);
                using var uploaded = await http.SendAsync(upload, ct);
                if (uploaded.StatusCode == System.Net.HttpStatusCode.Unauthorized) identity.OnUnauthorized();
            }
            _failures = 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or IOException or System.Text.Json.JsonException or UnauthorizedAccessException or OperationCanceledException) { Retry(); }
    }
    private static List<GuardExecution> MaintenanceResults(string? binding, string kind)
    {
        string directory = Path.Combine(GuardPaths.Root, "results"); var results = new List<GuardExecution>();
        if (binding is null || !Directory.Exists(directory)) return results;
        foreach (string path in Directory.EnumerateFiles(directory, "*.json").Take(64))
        {
            if (new FileInfo(path).Length > 4096) continue;
            try
            {
                var result = System.Text.Json.JsonSerializer.Deserialize<GuardExecution>(File.ReadAllBytes(path), GuardPaths.Json);
                if (result is not null && Guid.TryParseExact(result.GrantId, "N", out _) && result.Binding == binding && result.Kind == kind) results.Add(result);
            }
            catch (System.Text.Json.JsonException) { }
            if (results.Count == 16) break;
        }
        return results;
    }
    public static bool HandedToGuard(string root, string grantId) =>
        File.Exists(Path.Combine(root, "inbox", grantId + ".json"))
        || File.Exists(Path.Combine(root, "results", grantId + ".json"))
        || Directory.Exists(Path.Combine(root, "update-state"));

    private async Task DownloadPackageAsync(SignedMaintenanceGrant signed, GuardPolicy policy, CancellationToken ct)
    {
        var grant = signed.Grant; string? part = null; FileStream? partLease = null;
        try
        {
            GuardUpdateProgress.TryPublish(GuardPaths.Root, grant, "downloading", policy.LastVerifiedPackage?.Version ?? policy.InitialPackage?.Version, DateTime.UtcNow);
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server) || !(server.Scheme == "https" || (server.Scheme == "http" && server.IsLoopback))) throw new IOException("Maintenance requires HTTPS");
            string directory = Path.Combine(GuardPaths.Root, "cache"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, grant.Sha256!.ToLowerInvariant() + UpdateSigning.PackageExtension(grant.Platform!));
            part = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            partLease = new FileStream(part + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var handler = AgentTls.PackageHandler(); using var download = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
            using var request = new HttpRequestMessage(HttpMethod.Get, serverUrl.TrimEnd('/') + "/api/updates/" + grant.PackageId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Bearer);
            using var response = await download.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[65536]; long total = 0; int read; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
                while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0) { total += read; if (total > grant.PackageSize) throw new IOException("Package exceeds signed size"); await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token); }
                if (total != grant.PackageSize) throw new IOException("Package size mismatch"); output.Flush(true);
            }
            GuardUpdateProgress.TryPublish(GuardPaths.Root, grant, "verifying-package", policy.LastVerifiedPackage?.Version ?? policy.InitialPackage?.Version, DateTime.UtcNow);
            using (var file = File.OpenRead(part))
            {
                string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)).ToLowerInvariant();
                using var key = UpdateSigning.LoadPublicKey(policy.UpdatePublicKey);
                if (!string.Equals(hash, grant.Sha256, StringComparison.OrdinalIgnoreCase)) throw new GuardUpdateFailure("package-integrity");
                if (key is null || !UpdateSigning.Verify(key, grant.Platform!, grant.Version!, hash, grant.PackageSignature!)) throw new GuardUpdateFailure("publisher-signature");
            }
            using (var cacheGate = await AcquireCacheGateAsync(ct))
            { File.Move(part, path, true); GuardPaths.Queue(signed); }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException or System.Security.Cryptography.CryptographicException)
        {
            var failure = GuardUpdateProgress.Failure(e, "downloading");
            GuardUpdateProgress.TryPublish(GuardPaths.Root, grant, "failed", policy.LastVerifiedPackage?.Version ?? policy.InitialPackage?.Version, DateTime.UtcNow, failure.Reason, errorCode: failure.Code);
            GuardPaths.Write(Path.Combine(GuardPaths.Root, "results", grant.Id + ".json"), new GuardExecution(grant.Id, grant.Binding, grant.Kind, grant.Action, "failed", "package-download-failed", DateTime.UtcNow));
        }
        finally
        {
            partLease?.Dispose();
            if (part is not null) try { File.Delete(part); File.Delete(part + ".lock"); } catch (IOException) { }
        }
    }
    private static async Task<FileStream> AcquireCacheGateAsync(CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(Path.Combine(GuardPaths.Root, "cache.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 50) { await Task.Delay(100, ct); }
        }
    }
    private void Retry() { _failures = Math.Min(6, _failures + 1); _next = DateTime.UtcNow.AddSeconds(Math.Min(900, 15 * (1 << _failures))); }
}
