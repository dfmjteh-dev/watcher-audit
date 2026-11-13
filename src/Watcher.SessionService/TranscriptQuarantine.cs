using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Watcher.Shared;
using Watcher.Uploader;

namespace Watcher.SessionService;

// Карантин повреждённых транскриптов (до 0.16 — и закрытых Unknown, их копии
// теперь выгружаются, см. TerminalAudit.DrainQuarantineAsync): копия+хеш+
// метаданные+журнал до удаления исходника.
// Имена назначения — SHA-256, никакие пути из метаданных не используются.
public sealed class TranscriptQuarantine
{
    public const int MaxEntries = 2000;
    public sealed record Entry(string Key, string File, string Sha256, long Size, string Reason, DateTime At);
    private readonly string _dir;
    private readonly long _maxBytes;
    private readonly TimeSpan _retention;
    private readonly int _maxEntries;
    private Dictionary<string, Entry>? _entries;
    public TranscriptQuarantine(string dir, long maxBytes, TimeSpan retention, int maxEntries = MaxEntries)
    {
        _dir = Path.GetFullPath(dir);
        if (maxBytes <= 0 || retention <= TimeSpan.Zero || maxEntries <= 0 || maxEntries > MaxEntries) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxBytes = maxBytes;
        _retention = retention;
        _maxEntries = maxEntries;
    }

    public void ValidateSourceDirectory(string sourceDirectory)
    {
        string source = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string target = _dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (source.StartsWith(target, StringComparison.OrdinalIgnoreCase) || target.StartsWith(source, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Quarantine and transcript directories must not overlap", nameof(sourceDirectory));
    }

    private void Prepare()
    {
        // ACL — тот же защищённый механизм, что у учётных данных агента.
        for (string? parent = _dir; parent is not null; parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Quarantine path contains a reparse point");
        CredentialStore.CreateProtectedDirectory(_dir);
        if ((File.GetAttributes(_dir) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Quarantine directory is a reparse point");
    }

    public void Sweep(DateTime nowUtc)
    {
        Prepare();
        foreach (var e in Entries().Where(e => nowUtc - e.At > _retention)) Remove(e);
        foreach (var tmp in new DirectoryInfo(_dir).EnumerateFiles("*.tmp"))
            if (nowUtc - tmp.LastWriteTimeUtc > TimeSpan.FromDays(1)) tmp.Delete();
    }

    public Entry Store(string source, string reason, DateTime nowUtc, Func<Entry, bool> recordLoss)
    {
        if (!TranscriptIssue.IsKnown(reason)) throw new ArgumentException("Unknown transcript reason", nameof(reason));
        Prepare();
        var file = new FileInfo(source);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Transcript is a reparse point");
        if (file.Length > _maxBytes) throw new IOException("Transcript exceeds quarantine capacity; original kept");
        string hash;
        using (var input = File.OpenRead(source)) hash = Convert.ToHexString(SHA256.HashData(input));
        string name = file.Name;
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name + "|" + hash)));
        string destination = Path.Combine(_dir, key + ".txt");
        var entry = Entries().FirstOrDefault(e => e.Key == key) ?? new Entry(key, name, hash, file.Length, reason, nowUtc);
        if (!File.Exists(destination))
        {
            Sweep(nowUtc);
            long used = new DirectoryInfo(_dir).EnumerateFiles().Sum(f => f.Length);
            long required = file.Length + Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(entry)) + 1024;
            foreach (var old in Entries().OrderBy(e => e.At))
            {
                if (used + required <= _maxBytes && _entries!.Count < _maxEntries) break;
                Remove(old);
                used = new DirectoryInfo(_dir).EnumerateFiles().Sum(f => f.Length);
            }
            if (used + required > _maxBytes) throw new IOException("Quarantine is full; original kept");
            string tmp = destination + ".tmp";
            File.Copy(source, tmp, overwrite: true);
            using (var copy = File.OpenRead(tmp))
                if (Convert.ToHexString(SHA256.HashData(copy)) != hash) throw new IOException("Quarantine copy mismatch; original kept");
            using (var durable = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) durable.Flush(flushToDisk: true);
            File.Move(tmp, destination);
        }
        // Проверяем и уже существующую копию после прерванной операции.
        using (var copy = File.OpenRead(destination))
            if (Convert.ToHexString(SHA256.HashData(copy)) != hash) throw new IOException("Quarantine checksum mismatch; original kept");
        string meta = Path.Combine(_dir, key + ".json");
        using (var durable = new FileStream(meta + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            durable.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry)));
            durable.Flush(flushToDisk: true);
        }
        File.Move(meta + ".tmp", meta, overwrite: true);
        _entries![key] = entry;
        if (!recordLoss(entry)) throw new IOException("Loss journal could not be saved; original kept");
        using (var check = File.OpenRead(source))
            if (Convert.ToHexString(SHA256.HashData(check)) != hash) throw new IOException("Transcript changed during quarantine; original kept");
        File.Delete(source);
        return entry;
    }

    // 0.16: сохранённые копии, которые теперь выгружаются (origin=unknown), —
    // старейшие первыми. Путь — только из ключа (хеш), не из метаданных.
    public IReadOnlyList<(Entry Entry, string Path)> Pending(Func<Entry, bool> include, int max)
    {
        if (!Directory.Exists(_dir)) return [];
        Prepare();
        return Entries().Where(include).OrderBy(e => e.At).Take(max)
            .Select(e => (e, Path.Combine(_dir, e.Key + ".txt"))).Where(x => File.Exists(x.Item2)).ToList();
    }

    // Копия выгружена — удалить её и метаданные.
    public void Release(Entry e) => Remove(e);

    private List<Entry> Entries()
    {
        if (_entries is not null) return _entries.Values.ToList();
        var result = new List<Entry>();
        foreach (var file in new DirectoryInfo(_dir).EnumerateFiles("*.json"))
        {
            if (file.Length > 4096) continue;
            try
            {
                var e = JsonSerializer.Deserialize<Entry>(File.ReadAllText(file.FullName));
                if (e is not null && e.Key is { Length: 64 } && e.Key.All(char.IsAsciiHexDigit)
                    && file.Name == e.Key + ".json" && e.Sha256 is { Length: 64 } && e.Sha256.All(char.IsAsciiHexDigit)
                    && e.File is { Length: > 0 and <= 260 } && Path.GetFileName(e.File) == e.File && e.Size >= 0)
                    result.Add(e);
            }
            catch (JsonException) { /* чужие/битые метаданные не задают пути удаления */ }
        }
        _entries = result.ToDictionary(e => e.Key, StringComparer.Ordinal);
        return result;
    }

    private void Remove(Entry e)
    {
        File.Delete(Path.Combine(_dir, e.Key + ".txt"));
        File.Delete(Path.Combine(_dir, e.Key + ".json"));
        _entries?.Remove(e.Key);
    }
}
