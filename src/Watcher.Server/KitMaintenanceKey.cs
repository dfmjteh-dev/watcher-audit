namespace Watcher.Server;

// 0.16: открытый ключ обслуживания из комплекта агентов вместе с подписью ключом
// обновлений (build-kit.sh: certs/maintenance-key.pub.txt + .sig). Сервер передаёт
// его Linux-агентам в heartbeat; агент без защиты проверяет подпись своим ключом
// обновлений и включает защиту (как Windows-агент из MSI). Нет файлов — null.
public sealed record KitMaintenanceKey(string Key, string Signature)
{
    private static readonly object Gate = new();
    private static (string Dir, DateTime KeyAt, DateTime SigAt, KitMaintenanceKey? Value)? _cache;

    public static KitMaintenanceKey? Read(string? kitDir)
    {
        if (string.IsNullOrWhiteSpace(kitDir)) return null;
        string key = Path.Combine(kitDir, "certs", "maintenance-key.pub.txt"), sig = Path.Combine(kitDir, "certs", "maintenance-key.sig");
        try
        {
            if (!File.Exists(key) || !File.Exists(sig)) return null;
            DateTime keyAt = File.GetLastWriteTimeUtc(key), sigAt = File.GetLastWriteTimeUtc(sig);
            lock (Gate)
            {
                if (_cache is { } c && c.Dir == kitDir && c.KeyAt == keyAt && c.SigAt == sigAt) return c.Value;
                if (new FileInfo(key).Length > 1024 || new FileInfo(sig).Length > 1024) return null;
                var value = new KitMaintenanceKey(File.ReadAllText(key).Trim(), File.ReadAllText(sig).Trim());
                _cache = (kitDir, keyAt, sigAt, value);
                return value;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
