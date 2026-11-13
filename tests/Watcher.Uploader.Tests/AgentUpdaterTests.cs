using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

// H2 (roadmap-next.md): обновление агента — решение браться ли за предложение,
// проверка SHA-256 и подписи до установки, итог для heartbeat.
public class AgentUpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"watcher-agentupd-{Guid.NewGuid():N}");
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private DateTime _now = new(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);

    private sealed class FakeInstaller : IUpdateInstaller
    {
        public string Platform { get; init; } = "windows-msi";
        public bool Busy;
        public readonly List<(string Package, string Version)> Started = new();
        public bool InProgress() => Busy;
        public void Start(string packagePath, string version, string workDir, string resultFile) =>
            Started.Add((packagePath, version));
    }

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private UploaderConfig Cfg(string? key = null) => new()
    {
        UpdatePublicKey = key ?? Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()),
        UpdateDir = _dir,
    };

    private AgentUpdater Updater(FakeInstaller? installer, string? key = null) =>
        new(Cfg(key), installer, NullLogger.Instance, () => _now);

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private UpdateOffer Offer(byte[] content, string version = "0.10.0", string platform = "windows-msi", ECDsa? signer = null, string? signedVersion = null)
    {
        string sig = Convert.ToBase64String((signer ?? _key).SignData(
            UpdateSigning.Message(platform, signedVersion ?? version, Sha(content)), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        return new UpdateOffer(1, version, platform, Sha(content), content.Length, sig, "/api/updates/1");
    }

    // «Скачивание» — пишет content в путь и возвращает его SHA-256, как Uploader.DownloadAsync.
    private static Func<string, string, CancellationToken, Task<string>> Serve(byte[] content) => async (_, path, ct) =>
    {
        await File.WriteAllBytesAsync(path, content, ct);
        return Sha(content);
    };

    [Fact]
    public void No_key_or_no_installer_means_no_updates()
    {
        Assert.Null(Updater(new FakeInstaller(), key: "").Platform);
        Assert.Null(Updater(new FakeInstaller(), key: "not-a-key").Platform);
        Assert.Null(Updater(null).Platform);
        Assert.Equal("linux-deb", Updater(new FakeInstaller { Platform = "linux-deb" }).Platform);
    }

    [Fact]
    public async Task Valid_package_goes_to_installer()
    {
        var installer = new FakeInstaller();
        var u = Updater(installer);
        byte[] pkg = Encoding.ASCII.GetBytes("zip-0.10.0");
        var offer = Offer(pkg);

        Assert.True(await u.ApplyAsync(offer, Serve(pkg), default));

        var (path, version) = Assert.Single(installer.Started);
        Assert.Equal("0.10.0", version);
        Assert.Equal(pkg, File.ReadAllBytes(path));
        // Подпись предложения рядом с пакетом — база восстановления Guard при установке.
        Assert.Equal(offer.Signature, File.ReadAllText(path + ".sig"));
        Assert.Null(u.LastResult());   // итог пишет установщик, не updater
    }

    [Fact]
    public async Task Tampered_or_foreign_package_is_refused_before_install()
    {
        var installer = new FakeInstaller();
        var u = Updater(installer);
        byte[] pkg = Encoding.ASCII.GetBytes("zip-0.10.0"), evil = Encoding.ASCII.GetBytes("evil");

        // Сервер прислал правильные хеш и подпись, а отдал другой файл.
        Assert.False(await u.ApplyAsync(Offer(pkg), Serve(evil), default));
        Assert.Contains("sha256", u.LastResult()!.Message);

        // Хеш сходится, но подпись чужим ключом / под другую версию / другую платформу.
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(await u.ApplyAsync(Offer(evil, signer: other), Serve(evil), default));
        Assert.False(await u.ApplyAsync(Offer(pkg, signedVersion: "0.9.0"), Serve(pkg), default));
        var wrongPlatform = Offer(pkg, platform: "linux-deb") with { Platform = "windows-msi" };
        Assert.False(await u.ApplyAsync(wrongPlatform, Serve(pkg), default));
        Assert.Contains("signature", u.LastResult()!.Message);

        // Ошибка скачивания — тоже итог, а не исключение в цикл выгрузки.
        Assert.False(await u.ApplyAsync(Offer(pkg), (_, _, _) => throw new HttpRequestException("503"), default));
        Assert.Equal("failed 0.10.0: 503", u.StatusForHeartbeat());

        Assert.Empty(installer.Started);
    }

    [Fact]
    public void Offers_are_filtered_before_download()
    {
        var installer = new FakeInstaller();
        var u = Updater(installer);
        byte[] pkg = Encoding.ASCII.GetBytes("x");

        Assert.Null(u.WhyNot(Offer(pkg)));
        Assert.NotNull(u.WhyNot(Offer(pkg, platform: "linux-deb")));
        Assert.NotNull(u.WhyNot(Offer(pkg, version: "0.10")));

        installer.Busy = true;
        Assert.Contains("in progress", u.WhyNot(Offer(pkg)));
        installer.Busy = false;

        // Неудачную версию — не чаще раза в час; другую версию — сразу.
        u.WriteResult(new UpdateResult("0.10.0", false, "service did not start", _now));
        _now = _now.AddMinutes(30);
        Assert.Contains("failed", u.WhyNot(Offer(pkg)));
        Assert.Null(u.WhyNot(Offer(pkg, version: "0.10.1")));
        _now = _now.AddMinutes(31);
        Assert.Null(u.WhyNot(Offer(pkg)));
    }

    [Fact]
    public void Results_written_by_installers_are_read()
    {
        var u = Updater(new FakeInstaller());
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, AgentUpdater.ResultFileName);

        // update.ps1: ConvertTo-Json | Set-Content -Encoding UTF8 — с BOM и переносами.
        File.WriteAllText(path, "{\r\n    \"ok\":  true,\r\n    \"message\":  \"installed\",\r\n    \"version\":  \"0.10.0\",\r\n    \"at\":  \"2026-10-12T10:00:00.0000000Z\"\r\n}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal("ok 0.10.0", u.StatusForHeartbeat());

        // apply-update.sh: printf одной строкой.
        File.WriteAllText(path, "{\"version\":\"0.10.1\",\"ok\":false,\"message\":\"deb install exit 100: E: lock\",\"at\":\"2026-10-12T10:05:00Z\"}\n");
        Assert.Equal("failed 0.10.1: deb install exit 100: E: lock", u.StatusForHeartbeat());

        File.WriteAllText(path, "not json");
        Assert.Null(u.StatusForHeartbeat());
    }

    [Fact]
    public async Task Interrupted_download_leaves_service_alone_and_retry_starts_from_clean_package()
    {
        var installer = new FakeInstaller();
        var u = Updater(installer);
        byte[] pkg = Encoding.ASCII.GetBytes("complete-package");
        Assert.False(await u.ApplyAsync(Offer(pkg), async (_, path, ct) =>
        {
            await File.WriteAllBytesAsync(path + ".part", pkg[..3], ct);
            throw new IOException("connection interrupted");
        }, default));
        Assert.Empty(installer.Started);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "0.10.0")));
        _now += AgentUpdater.RetryFailedAfter;
        Assert.Null(u.WhyNot(Offer(pkg)));
        Assert.True(await u.ApplyAsync(Offer(pkg), Serve(pkg), default));
        Assert.Single(installer.Started);
    }

    [Fact]
    public async Task Cancellation_releases_busy_state_and_does_not_launch_installer()
    {
        var installer = new FakeInstaller();
        var u = Updater(installer);
        byte[] pkg = Encoding.ASCII.GetBytes("package");
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => u.ApplyAsync(Offer(pkg), async (_, path, ct) =>
        {
            await File.WriteAllBytesAsync(path + ".part", pkg[..2], ct);
            cancel.Cancel();
            ct.ThrowIfCancellationRequested();
            return "";
        }, cancel.Token));
        Assert.Empty(installer.Started);
        Assert.Null(u.LastResult());
        Assert.True(await u.ApplyAsync(Offer(pkg), Serve(pkg), default));
        Assert.Single(installer.Started);
    }

    [Fact]
    public async Task Invalid_size_and_fake_downloader_hash_cannot_start_installer()
    {
        var installer = new FakeInstaller();
        var u = Updater(installer);
        byte[] pkg = Encoding.ASCII.GetBytes("package");
        Assert.False(await u.ApplyAsync(Offer(pkg) with { Size = pkg.Length + 1 }, Serve(pkg), default));
        Assert.Contains("size", u.LastResult()!.Message);
        Assert.False(await u.ApplyAsync(Offer(pkg), async (_, path, ct) =>
        {
            await File.WriteAllTextAsync(path, "bad", ct);
            return Sha(pkg);
        }, default));
        Assert.Contains("sha256", u.LastResult()!.Message);
        Assert.False(await u.ApplyAsync(Offer(pkg) with { Version = "../outside" }, Serve(pkg), default));
        Assert.False(await u.ApplyAsync(Offer(pkg) with { Size = long.MaxValue }, Serve(pkg), default));
        Assert.Empty(installer.Started);
    }

    [Fact]
    public async Task Insufficient_space_rejects_update_before_download_and_installer()
    {
        var installer = new FakeInstaller();
        var u = new AgentUpdater(Cfg(), installer, NullLogger.Instance, () => _now, _ => 0);
        byte[] pkg = Encoding.ASCII.GetBytes("package");
        bool downloaded = false;
        Assert.False(await u.ApplyAsync(Offer(pkg), (_, _, _) => { downloaded = true; return Task.FromResult(""); }, default));
        Assert.False(downloaded);
        Assert.Empty(installer.Started);
        Assert.Contains("disk space", u.LastResult()!.Message);
    }

    [Fact]
    public async Task Download_timeout_is_reported_without_unobserved_cancellation()
    {
        var installer = new FakeInstaller();
        var u = Updater(installer);
        byte[] pkg = Encoding.ASCII.GetBytes("package");
        Assert.False(await u.ApplyAsync(Offer(pkg), (_, _, _) => throw new TaskCanceledException("network timeout"), default));
        Assert.Contains("timed out", u.LastResult()!.Message);
        Assert.Empty(installer.Started);
    }

    [Fact]
    public async Task Concurrent_offer_and_running_installer_never_delete_active_package()
    {
        var installer = new FakeInstaller();
        var u = Updater(installer);
        byte[] pkg = Encoding.ASCII.GetBytes("package");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = u.ApplyAsync(Offer(pkg), async (url, path, ct) =>
        {
            entered.SetResult();
            await release.Task;
            return await Serve(pkg)(url, path, ct);
        }, default);
        await entered.Task;
        Assert.False(await u.ApplyAsync(Offer(pkg), Serve(pkg), default));
        release.SetResult();
        Assert.True(await first);
        installer.Busy = true;
        Assert.False(await u.ApplyAsync(Offer(pkg), Serve(pkg), default));
        Assert.Equal(pkg, File.ReadAllBytes(Assert.Single(installer.Started).Package));
    }
}
