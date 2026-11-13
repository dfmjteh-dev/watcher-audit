using System.IO.Compression;

namespace Watcher.Server;

// Страница «Установка агентов» (Настройки, только admin): комплект, который
// собрал install-server.sh, — скачать MSI/пакеты/сертификаты и готовые
// команды. В комплекте зашит токен регистрации — каждое скачивание в журнал
// доступа.
public static class AgentKitEndpoints
{
    public static void MapAgentKitEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/agent-kit").RequireAuthorization("AdminOnly");

        admin.MapGet("", (Db db, ServerConfig cfg) =>
        {
            if (string.IsNullOrWhiteSpace(cfg.AgentKitDir)) return Results.Ok(new { configured = false, available = false });
            var m = AgentKit.Load(cfg.AgentKitDir);
            if (m is null) return Results.Ok(new { configured = true, available = false });
            var t = string.IsNullOrEmpty(m.EnrollTokenSha256) ? null : db.EnrollTokenByHash(m.EnrollTokenSha256);
            return Results.Ok(new
            {
                configured = true,
                available = true,
                version = m.Version,
                // Версии агентов и сервера раздельные — комплект сверяется с
                // последним загруженным пакетом агентов, не с сервером.
                latestAgentVersion = AgentVersionPolicy.Load(db).LatestAgentVersion,
                serverUrl = m.ServerUrl,
                builtAt = m.BuiltAt.ToString("o"),
                updateKey = m.UpdateKey,
                files = m.Files.Select(f => new { f.Path, f.Size, f.Sha256 }),
                // 0.15.0: Android — инструменты (adb, install.cmd) из комплекта + APK из «Обновлений».
                android = new
                {
                    tools = AgentKit.Resolve(cfg.AgentKitDir, m, AndroidToolsPath) is not null,
                    apkVersion = LatestAndroidPackage(db)?.Version,
                },
                token = t is null ? null : new
                {
                    t.Name,
                    expiresAt = t.ExpiresAt.ToString("o"),
                    t.Uses,
                    t.MaxUses,
                    revoked = t.RevokedAt is not null,
                    groupName = t.GroupName,
                },
            });
        });

        // 0.15.0: один архив для подключения Android-устройства: adb, install.cmd с
        // адресом и токеном, инструкция (собраны в комплект) и APK — последний
        // android-пакет из «Обновлений», той же подписью, что агент потом обновляется.
        // Склеиваем при скачивании: APK загружают отдельно от сборки комплекта.
        admin.MapGet("/android.zip", (HttpContext ctx, Db db, ServerConfig cfg, AuditLog audit) =>
        {
            var m = AgentKit.Load(cfg.AgentKitDir);
            string? tools = m is null ? null : AgentKit.Resolve(cfg.AgentKitDir, m, AndroidToolsPath);
            var apk = LatestAndroidPackage(db);
            string? apkFull = apk is null ? null : Path.GetFullPath(Path.Combine(cfg.DataDir, apk.FilePath));
            if (tools is null || apkFull is null || !File.Exists(apkFull)) return Results.NotFound();
            audit.Write(ctx, "agent-kit.download", "agent-kit:android", new { version = apk!.Version });
            // Во временный файл: ZipArchive при закрытии пишет синхронно, а Kestrel
            // синхронную запись в ответ запрещает. Файл удаляется после отдачи.
            string tmp = Path.Combine(Path.GetTempPath(), $"watcher-android-{Guid.NewGuid():N}.zip");
            var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
            try
            {
                using (var outZip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true))
                {
                    using (var src = ZipFile.OpenRead(tools))
                        foreach (var e in src.Entries)
                        {
                            if (e.FullName.EndsWith('/')) continue;
                            var dst = outZip.CreateEntry(e.FullName, CompressionLevel.Fastest);
                            dst.LastWriteTime = e.LastWriteTime;
                            using var r = e.Open();
                            using var w = dst.Open();
                            r.CopyTo(w);
                        }
                    // APK уже сжат — без повторного сжатия.
                    var a = outZip.CreateEntry("watcher-android/watcher-agent.apk", CompressionLevel.NoCompression);
                    using (var r = File.OpenRead(apkFull))
                    using (var w = a.Open())
                        r.CopyTo(w);
                }
                fs.Position = 0;
            }
            catch
            {
                fs.Dispose();
                throw;
            }
            return Results.Stream(fs, "application/zip", $"watcher-android-{apk.Version}.zip");
        });

        admin.MapGet("/files/{**path}", (HttpContext ctx, string path, ServerConfig cfg, AuditLog audit) =>
        {
            var m = AgentKit.Load(cfg.AgentKitDir);
            string? full = m is null ? null : AgentKit.Resolve(cfg.AgentKitDir, m, path);
            if (full is null) return Results.NotFound();
            audit.Write(ctx, "agent-kit.download", $"agent-kit:{path}", new { version = m!.Version });
            return Results.File(full, "application/octet-stream", Path.GetFileName(full), enableRangeProcessing: true);
        });
    }

    public const string AndroidToolsPath = "android/watcher-android-tools.zip";

    private static AgentPackage? LatestAndroidPackage(Db db) =>
        db.ListAgentPackages().Where(p => p.Platform == AgentAuth.KindAndroid)
            .MaxBy(p => Version.TryParse(AppVersion.Core(p.Version), out var v) ? v : new Version(0, 0));
}
