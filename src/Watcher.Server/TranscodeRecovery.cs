using System.Text.Json;

namespace Watcher.Server;

public sealed partial class TranscodeService
{
    private readonly SemaphoreSlim _quarantineSlot = new(1, 1);

    private string DataPath(string relative)
    {
        string root = Path.GetFullPath(_cfg.DataDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root, StringComparison.Ordinal)) throw new IOException("Recording path escapes DataDir");
        for (string? current = path; current is not null && current.Length >= root.Length; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Recording path contains a reparse point");
        return path;
    }

    private (Outcome, long) ScheduleFailure(TranscodeCandidate cand, TranscodeLease lease, string reason, string? error, DateTime now)
    {
        int[] delays = [5, 30, 120];
        int failures = lease.Failures + 1;
        if (failures <= delays.Length)
            _db.FinishTranscodeIssue(cand, lease, "retry", reason, error, now, nextAt: now.AddMinutes(delays[failures - 1]));
        else _db.FinishTranscodeIssue(cand, lease, "failed", reason, error, now);
        return (Outcome.Failed, 0);
    }

    private async Task<(Outcome, long)> HandleFailureAsync(TranscodeCandidate cand, TranscodeLease lease, string reason, string? error, DateTime now, CancellationToken ct)
    {
        string src = DataPath(cand.FilePath);
        if (!File.Exists(src)) return ScheduleFailure(cand, lease, "file-missing", error, now);
        var inspection = await _transcoder.InspectAsync(src, fullDecode: true, ct);
        ct.ThrowIfCancellationRequested();
        if (inspection.Valid == true)
        {
            if (lease.Failures < 3) return ScheduleFailure(cand, lease, reason, error, now);
            return _db.FinishTranscodeIssue(cand, lease, "kept", reason, error, now) ? (Outcome.Kept, 0) : (Outcome.Skipped, 0);
        }
        if (inspection.Valid is null) return ScheduleFailure(cand, lease, inspection.Reason, inspection.Error ?? error, now);
        string repairedRel = Path.ChangeExtension(cand.FilePath, "." + Guid.NewGuid().ToString("N") + ".repaired.mp4");
        string repaired = DataPath(repairedRel), part = BlobFiles.PartPath(repaired);
        try
        {
            if (await _transcoder.RemuxAsync(src, part, ct) && File.Exists(part))
            {
                var check = await _transcoder.InspectAsync(part, fullDecode: true, ct);
                if (check.Valid == true && check.Duration is { } duration && cand.DurationSec > 0
                    && Math.Abs(duration - cand.DurationSec) <= Math.Max(1, cand.DurationSec * 0.02))
                {
                    var replacement = new TranscodedFile(repairedRel, await Sha256Async(part, ct), new FileInfo(part).Length);
                    File.Move(part, repaired); // Уникальный путь, никогда не перезаписываем чужой blob.
                    if (_db.FinishTranscodeIssue(cand, lease, "repaired", inspection.Reason, inspection.Error, now, replacement: replacement))
                    { BlobFiles.TryDelete(src); return (Outcome.Kept, 0); }
                    BlobFiles.TryDelete(repaired);
                    return (Outcome.Skipped, 0);
                }
            }
        }
        finally { BlobFiles.TryDelete(part); }
        // Подтверждённо непригоден: копия и полный снимок метаданных до удаления из реестра.
        await _quarantineSlot.WaitAsync(ct);
        try
        {
            string folderRel = "transcode-quarantine/" + cand.Id;
            string directory = DataPath(folderRel);
            Directory.CreateDirectory(directory);
            string archive = Path.Combine(directory, "source.mp4");
            long used = new DirectoryInfo(DataPath("transcode-quarantine")).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            if (used + (File.Exists(archive) ? 0 : new FileInfo(src).Length) > Math.Max(1024 * 1024, T.QuarantineMaxBytes))
                return ScheduleFailure(cand, lease, "quarantine-full", "Original retained", now);
            string hash = await Sha256Async(src, ct);
            if (cand.Sha256.Length == 64 && !string.Equals(cand.Sha256, hash, StringComparison.OrdinalIgnoreCase))
                return ScheduleFailure(cand, lease, "source-changed", "Source hash differs from database; original retained", now);
            if (!File.Exists(archive)) File.Copy(src, archive);
            using (var durable = new FileStream(archive, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) durable.Flush(true);
            if (await Sha256Async(archive, ct) != hash) return ScheduleFailure(cand, lease, "quarantine-mismatch", "Original retained", now);
            var segment = _db.ById(cand.Id);
            await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new { segment, sha256 = hash, reason = inspection.Reason, at = now }), ct);
            using (var durable = new FileStream(Path.Combine(directory, "manifest.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) durable.Flush(true);
            // Ещё раз проверяем источник перед фиксацией: он мог измениться во время копирования.
            if (await Sha256Async(src, ct) != hash) return ScheduleFailure(cand, lease, "source-changed", "Original retained", now);
            if (!_db.FinishTranscodeIssue(cand, lease, "quarantined", inspection.Reason, inspection.Error, now,
                quarantine: folderRel, quarantineDays: T.QuarantineDays)) return (Outcome.Skipped, 0);
            BlobFiles.TryDelete(src); // после COMMIT: при сбое останется лишняя копия, а не потерянная запись
            return (Outcome.Kept, 0);
        }
        finally { _quarantineSlot.Release(); }
    }
}
