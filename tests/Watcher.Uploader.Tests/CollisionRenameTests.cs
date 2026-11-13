using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

public sealed class CollisionRenameTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"watcher-rename-{Guid.NewGuid():N}");

    public CollisionRenameTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Renames_mp4_and_sidecar_together_and_keeps_content()
    {
        string mp4 = Path.Combine(_dir, "PC1_20261007_101500.mp4");
        File.WriteAllText(mp4, "video");
        File.WriteAllText(mp4 + ".json", "{}");

        string renamed = Uploader.RenameForCollision(mp4);

        Assert.False(File.Exists(mp4));
        Assert.False(File.Exists(mp4 + ".json"));
        Assert.True(File.Exists(renamed));
        Assert.True(File.Exists(renamed + ".json"));
        Assert.Equal("video", File.ReadAllText(renamed));
        Assert.StartsWith("PC1_20261007_101500_c", Path.GetFileName(renamed));
        Assert.EndsWith(".mp4", renamed);
    }
}
