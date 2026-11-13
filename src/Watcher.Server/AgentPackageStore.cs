using System.Security.Cryptography;

namespace Watcher.Server;

// H2: сохранение пакета обновления агента — общий код для загрузки из веб-UI
// (/api/agent-packages) и CLI (--import-agent-package, его зовёт
// install-server.sh после сборки комплекта агентов). Подпись проверяется
// открытым ключом сервера до записи в БД: не тот файл или не та версия видны
// сразу, а не на всём парке.
public static class AgentPackageStore
{
    public enum Outcome { Ok, NoPublicKey, BadPlatform, BadVersion, Empty, Exists, BadSignature }

    public sealed record Result(Outcome Outcome, long Id = 0, string? Sha256 = null, long Size = 0);

    public static async Task<Result> ImportAsync(Db db, ServerConfig cfg, string platform, string version,
        string signature, Stream content, string? uploadedBy, CancellationToken ct = default)
    {
        using var key = UpdateSigning.LoadPublicKey(cfg.UpdatePublicKey);
        if (key is null) return new(Outcome.NoPublicKey);
        if (!UpdateSigning.Platforms.Contains(platform)) return new(Outcome.BadPlatform);
        if (!UpdateSigning.IsValidVersion(version)) return new(Outcome.BadVersion);
        if (db.AgentPackageFor(platform, version) is not null) return new(Outcome.Exists);

        string dir = Path.Combine(cfg.DataDir, "agent-packages", platform, version);
        Directory.CreateDirectory(dir);
        string ext = UpdateSigning.PackageExtension(platform);
        string path = Path.Combine(dir, $"watcher-agent-{version}{ext}");
        string sha;
        long size;
        await using (var dst = File.Create(path))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[81920];
            int n;
            while ((n = await content.ReadAsync(buf, ct)) > 0)
            {
                hash.AppendData(buf, 0, n);
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
            }
            sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            size = dst.Length;
        }
        if (size == 0)
        {
            File.Delete(path);
            return new(Outcome.Empty);
        }
        if (!UpdateSigning.Verify(key, platform, version, sha, signature))
        {
            File.Delete(path);
            return new(Outcome.BadSignature, Sha256: sha);
        }
        long id = db.InsertAgentPackage(new AgentPackage(0, platform, version,
            Path.GetRelativePath(cfg.DataDir, path).Replace('\\', '/'), sha, size, signature, uploadedBy, DateTime.UtcNow));
        return new(Outcome.Ok, id, sha, size);
    }
}
