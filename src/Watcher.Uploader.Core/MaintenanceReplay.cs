namespace Watcher.Uploader;

public sealed record ConsumedMaintenance(DateTime Watermark, Dictionary<string, DateTime> Used);
public sealed class MaintenanceReplay(string root)
{
    public bool Consume(SignedMaintenanceGrant signed, string publicKey, string binding, string kind, DateTime now)
    {
        if (!MaintenanceSigning.Verify(signed, publicKey, binding, kind, now)) return false;
        Directory.CreateDirectory(root);
        using var gate = new FileStream(Path.Combine(root, "maintenance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string path = Path.Combine(root, "consumed.json");
        var state = File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<ConsumedMaintenance>(File.ReadAllBytes(path), GuardPaths.Json) : null;
        state ??= new(now, []);
        if (now < state.Watermark.AddSeconds(-60) || state.Used.ContainsKey(signed.Grant.Id)) return false;
        var live = state.Used.Where(p => p.Value > now.AddDays(-1)).ToDictionary(p => p.Key, p => p.Value);
        if (live.Count >= 4096) return false; // Полный журнал запрещает новую операцию, не разрешает replay.
        live[signed.Grant.Id] = signed.Grant.ExpiresAt;
        GuardPaths.Write(path, new ConsumedMaintenance(now > state.Watermark ? now : state.Watermark, live));
        return true;
    }
}
