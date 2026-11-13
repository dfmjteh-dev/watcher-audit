namespace Watcher.Server;

// Только общие счётчики процессов, доступные AdminOnly. Настройки и права не кэшируются.
public sealed class AppCatalogCache(Db db)
{
    public sealed record Item(string Process, long Count, bool HasSite);
    private sealed record Snapshot(string Id, DateTime At, IReadOnlyList<Item> Items);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Snapshot> _snapshots = [];
    private Snapshot? _current;
    public sealed record Page(IReadOnlyList<Item> Items, string? NextCursor, DateTime AsOf, int SeenDays);

    public async Task<Page?> GetAsync(string? q, int limit, string? cursor, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            string query = (q ?? "").Trim();
            Snapshot? snapshot;
            int offset = 0;
            if (cursor is not null)
            {
                var parts = cursor.Split(':');
                string fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(query.ToUpperInvariant())))[..16];
                if (parts.Length != 3 || parts[2] != fingerprint || !int.TryParse(parts[1], out offset) || offset < 0) return null;
                snapshot = _snapshots.FirstOrDefault(s => s.Id == parts[0] && DateTime.UtcNow - s.At < TimeSpan.FromMinutes(10));
                if (snapshot is null) return null;
            }
            else
            {
                if (_current is null || DateTime.UtcNow - _current.At > TimeSpan.FromMinutes(1))
                {
                    var values = await Task.Run(() => db.TopProcesses(AppKindsEndpoints.SeenDays, 10000)
                        .Select(p => new Item(p.Process, p.Count, p.HasSite)).ToList(), ct);
                    _current = new(Guid.NewGuid().ToString("N"), DateTime.UtcNow, values);
                    _snapshots.Add(_current);
                    _snapshots.RemoveAll(s => DateTime.UtcNow - s.At > TimeSpan.FromMinutes(10));
                    if (_snapshots.Count > 10) _snapshots.RemoveAt(0);
                }
                snapshot = _current;
            }
            // Снимок фиксирован: новые окна не сдвигают страницы текущего запроса.
            var matches = snapshot.Items.Where(i => query.Length == 0 || i.Process.Contains(query, StringComparison.OrdinalIgnoreCase));
            var items = matches.Skip(offset).Take(limit + 1).ToList();
            bool more = items.Count > limit;
            if (more) items.RemoveAt(items.Count - 1);
            return new(items, more ? snapshot.Id + ":" + (offset + limit) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(query.ToUpperInvariant())))[..16] : null, snapshot.At, AppKindsEndpoints.SeenDays);
        }
        finally { _gate.Release(); }
    }
}
