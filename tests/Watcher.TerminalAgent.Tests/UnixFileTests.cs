using Watcher.TerminalAgent;
using Xunit;

namespace Watcher.TerminalAgent.Tests;

// UnixFile (R5) — защита raw-output от подмены симлинком/чужим файлом через
// прямые вызовы libc. Заодно регрессия H11: [DefaultDllImportSearchPaths] на
// сборке агента не должен ломать загрузку libc (тесты вызывают настоящий P/Invoke).
public sealed class UnixFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"watcher-unixfile-{Guid.NewGuid():N}");

    public UnixFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static uint CurrentUid() => UnixFile.LookupUid(Environment.UserName)!.Value;

    [Fact]
    public void LookupUid_resolves_root_and_rejects_unknown()
    {
        Assert.Equal(0u, UnixFile.LookupUid("root"));
        Assert.Null(UnixFile.LookupUid("watcher-no-such-user-" + Guid.NewGuid().ToString("N")[..8]));
    }

    [Fact]
    public void OpenRegularNoFollow_opens_regular_file_and_reports_owner()
    {
        string file = Path.Combine(_dir, "a.json");
        File.WriteAllText(file, "{}");

        using var fs = UnixFile.OpenRegularNoFollow(file, out uint owner, out string? error);

        Assert.NotNull(fs);
        Assert.Null(error);
        Assert.Equal(CurrentUid(), owner);
        Assert.Equal("{}", new StreamReader(fs!).ReadToEnd());
    }

    [Fact]
    public void OpenRegularNoFollow_refuses_symlink_and_directory()
    {
        string target = Path.Combine(_dir, "target.json");
        File.WriteAllText(target, "{}");
        string link = Path.Combine(_dir, "link.json");
        File.CreateSymbolicLink(link, target);

        Assert.Null(UnixFile.OpenRegularNoFollow(link, out _, out string? linkError));
        Assert.NotNull(linkError);
        Assert.Null(UnixFile.OpenRegularNoFollow(_dir, out _, out string? dirError));
        Assert.NotNull(dirError);
    }

    [Fact]
    public void IsDirectoryOwnedBySelf_only_for_real_own_directory()
    {
        string link = Path.Combine(_dir, "dirlink");
        Directory.CreateSymbolicLink(link, _dir);
        string file = Path.Combine(_dir, "f");
        File.WriteAllText(file, "");

        Assert.True(UnixFile.IsDirectoryOwnedBySelf(_dir));
        Assert.False(UnixFile.IsDirectoryOwnedBySelf(link));
        Assert.False(UnixFile.IsDirectoryOwnedBySelf(file));
        Assert.False(UnixFile.IsDirectoryOwnedBySelf(Path.Combine(_dir, "missing")));
    }
}
