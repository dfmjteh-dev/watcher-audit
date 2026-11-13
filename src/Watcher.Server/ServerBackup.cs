using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// H5 (docs/roadmap-next.md): резервная копия и восстановление.
//
// Копия — каталог watcher-backup-<время>/:
//   watcher.db     — консистентный снимок БД (VACUUM INTO), можно на работающем сервере;
//   blobs.jsonl    — по строке на файл записи (путь от DataDir, размер, sha256 из БД);
//   blobs/…        — сами файлы, только с --include-blobs;
//   manifest.json  — версия формата, сервера и схемы, sha256 снимка, итоги по блобам.
// Блобы по умолчанию не копируются: это неизменяемые файлы на терабайты, их
// удобнее копировать rsync/borg/restic по blobs.jsonl.
//
// БД открывается только сырым соединением, без класса Db: тот при открытии
// применяет миграции, и копия/восстановление более новым бинарником тихо
// мигрировали бы живую БД или снимок.
public static class ServerBackup
{
    public const int FormatVersion = 1;
    public const string ManifestFile = "manifest.json";
    public const string DbFile = "watcher.db";
    public const string BlobsListFile = "blobs.jsonl";
    public const string BlobsDir = "blobs";

    public sealed record Manifest(
        int Format, DateTime CreatedAt, string ServerVersion, int SchemaVersion,
        string DbSha256, long DbSize, int BlobCount, long BlobBytes, bool BlobsIncluded);

    public sealed record BlobEntry(string Path, long Size, string? Sha256);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions JsonLine = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Путь к созданному каталогу копии.
    public static string Backup(ServerConfig cfg, string outRoot, bool includeBlobs, Action<string> log)
    {
        if (!File.Exists(cfg.DbPath)) throw new FileNotFoundException($"БД не найдена: {cfg.DbPath}");
        string dir = Path.Combine(outRoot, $"watcher-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        if (Directory.Exists(dir)) throw new IOException($"Каталог уже существует: {dir}");
        Directory.CreateDirectory(dir);
        string snapshot = Path.Combine(dir, DbFile);

        using (var live = OpenReadOnly(cfg.DbPath))
        using (var cmd = live.CreateCommand())
        {
            cmd.CommandText = "VACUUM INTO $p";
            cmd.Parameters.AddWithValue("$p", snapshot);
            cmd.ExecuteNonQuery();
        }
        log($"БД: снимок {snapshot}");

        int schema;
        var blobs = new List<BlobEntry>();
        // Список блобов — из снимка, а не из живой БД: ровно те файлы, на которые
        // ссылается эта копия.
        using (var snap = OpenReadOnly(snapshot))
        {
            schema = SchemaVersion(snap);
            blobs.AddRange(ListBlobs(snap, cfg.DataDir));
        }

        using (var w = new StreamWriter(Path.Combine(dir, BlobsListFile)))
            foreach (var b in blobs) w.WriteLine(JsonSerializer.Serialize(b, JsonLine));

        if (includeBlobs)
        {
            int copied = 0, missing = 0;
            foreach (var b in blobs)
            {
                if (Inside(cfg.DataDir, b.Path) is not { } src || !File.Exists(src)) { missing++; continue; }
                string dst = Inside(Path.Combine(dir, BlobsDir), b.Path)!;
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst);
                copied++;
            }
            log($"Файлы записей: скопировано {copied}" + (missing > 0 ? $", не найдено на диске {missing}" : ""));
        }

        var manifest = new Manifest(FormatVersion, DateTime.UtcNow, AppVersion.Current, schema,
            Sha256(snapshot), new FileInfo(snapshot).Length, blobs.Count, blobs.Sum(b => b.Size), includeBlobs);
        File.WriteAllText(Path.Combine(dir, ManifestFile), JsonSerializer.Serialize(manifest, Json));
        log($"Готово: {dir} (схема v{schema}, записей с файлами {blobs.Count}, {blobs.Sum(b => b.Size) / 1024.0 / 1024:0.#} МБ" +
            (includeBlobs ? ", файлы включены)" : "; файлы — по blobs.jsonl отдельно)"));
        return dir;
    }

    // Восстановление на остановленном сервере. Текущая БД не удаляется, а
    // откладывается рядом (watcher.db.pre-restore-<время>). Отказ: копия
    // повреждена, снята более новым сервером (схема новее известной).
    public static void Restore(ServerConfig cfg, string backupDir, Action<string> log)
    {
        string manifestPath = Path.Combine(backupDir, ManifestFile);
        if (!File.Exists(manifestPath)) throw new InvalidOperationException($"Нет {ManifestFile} в {backupDir} — это не каталог копии Watcher.");
        var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), Json)
            ?? throw new InvalidOperationException($"Пустой {ManifestFile}.");
        if (m.Format != FormatVersion) throw new InvalidOperationException($"Формат копии {m.Format}, этот сервер знает {FormatVersion}.");

        string snapshot = Path.Combine(backupDir, DbFile);
        if (!File.Exists(snapshot) || !string.Equals(Sha256(snapshot), m.DbSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Снимок БД {snapshot} отсутствует или повреждён (sha256 не совпадает с manifest.json).");
        int known = Db.Migrations.Count;
        if (m.SchemaVersion > known)
            throw new InvalidOperationException($"Копия снята сервером {m.ServerVersion} со схемой v{m.SchemaVersion}, этот сервер знает только до v{known}. Восстанавливайте сервером не старее {m.ServerVersion}.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cfg.DbPath))!);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string cur = cfg.DbPath + suffix;
            if (File.Exists(cur))
            {
                string aside = $"{cfg.DbPath}.pre-restore-{stamp}{suffix}";
                File.Move(cur, aside);
                if (suffix == "") log($"Текущая БД отложена: {aside}");
            }
        }
        File.Copy(snapshot, cfg.DbPath);
        log($"БД восстановлена из копии {m.CreatedAt:yyyy-MM-dd HH:mm} UTC (сервер {m.ServerVersion}, схема v{m.SchemaVersion}).");

        string blobsDir = Path.Combine(backupDir, BlobsDir);
        if (m.BlobsIncluded && Directory.Exists(blobsDir))
        {
            int copied = 0, kept = 0;
            foreach (var b in ReadBlobList(backupDir))
            {
                // Путь — из файла копии: подделанная копия с «../» не должна писать
                // за пределы DataDir (восстановление идёт с правами сервиса).
                if (Inside(blobsDir, b.Path) is not { } src || Inside(cfg.DataDir, b.Path) is not { } dst)
                    throw new InvalidOperationException($"Путь файла вне каталога данных в {BlobsListFile}: {b.Path}");
                if (!File.Exists(src)) continue;
                if (File.Exists(dst) && new FileInfo(dst).Length == b.Size) { kept++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
                copied++;
            }
            log($"Файлы записей: восстановлено {copied}, уже были на месте {kept}.");
        }

        int missing = ReadBlobList(backupDir).Count(b => Inside(cfg.DataDir, b.Path) is not { } p || !File.Exists(p));
        log(missing == 0
            ? "Все файлы записей, на которые ссылается копия, на месте."
            : $"ВНИМАНИЕ: {missing} файлов записей из копии нет в {cfg.DataDir} — верните их (rsync/borg/restic по {BlobsListFile}), иначе эти записи будут без видео/вывода.");
        if (m.SchemaVersion < known)
            log($"При старте сервер применит миграции схемы v{m.SchemaVersion} → v{known}.");
    }

    public static IEnumerable<BlobEntry> ReadBlobList(string backupDir)
    {
        foreach (string line in File.ReadLines(Path.Combine(backupDir, BlobsListFile)))
            if (line.Length > 0)
                yield return JsonSerializer.Deserialize<BlobEntry>(line, JsonLine)!;
    }

    private static IEnumerable<BlobEntry> ListBlobs(SqliteConnection c, string dataDir)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT file_path, size_bytes, sha256 FROM segments
UNION ALL SELECT out_file_path, NULL, sha256 FROM terminal_sessions WHERE out_file_path IS NOT NULL
UNION ALL SELECT timing_file_path, NULL, NULL FROM terminal_sessions WHERE timing_file_path IS NOT NULL";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string path = r.GetString(0);
            // size_bytes терминальной записи — .out и .timing вместе, поэтому
            // размер файла — с диска (если файла нет — 0).
            long size = !r.IsDBNull(1) ? r.GetInt64(1) : FileSize(Path.Combine(dataDir, path));
            yield return new BlobEntry(path, size, r.IsDBNull(2) ? null : r.GetString(2));
        }
    }

    // root + относительный путь; null — если результат выходит за root.
    public static string? Inside(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        return full.StartsWith(fullRoot, StringComparison.Ordinal) ? full : null;
    }

    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static int SchemaVersion(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='schema_migrations'";
        if (cmd.ExecuteScalar() is null) return 0;
        return DbMigrations.CurrentVersion(c);
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using var p = c.CreateCommand();
        p.CommandText = "PRAGMA busy_timeout=5000;";
        p.ExecuteNonQuery();
        return c;
    }

    private static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }
}
