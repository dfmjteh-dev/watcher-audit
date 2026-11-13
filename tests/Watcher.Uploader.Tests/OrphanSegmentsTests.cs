using System.Text.Json;
using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

public sealed class OrphanSegmentsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"watcher-orphans-{Guid.NewGuid():N}");
    private readonly List<string> _log = new();
    private static readonly TimeSpan MinAge = TimeSpan.FromMinutes(30);

    public OrphanSegmentsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Make(string name, DateTime lastWriteUtc, bool withMeta = false, int size = 100)
    {
        string mp4 = Path.Combine(_dir, name);
        File.WriteAllBytes(mp4, new byte[size]);
        File.SetLastWriteTimeUtc(mp4, lastWriteUtc);
        if (withMeta) File.WriteAllText(mp4 + ".json", "{}");
        return mp4;
    }

    private int Run(Func<string, bool?> probe) =>
        OrphanSegments.Apply(_dir, MinAge, "PC1", "1.2.3",
            (_, _) => throw new InvalidOperationException("remux must not run"), probe, _log.Add);

    private int RunRec(Func<string, string, bool?> remux) =>
        OrphanSegments.Apply(_dir, MinAge, "PC1", "1.2.3", remux,
            _ => throw new InvalidOperationException("probe must not run"), _log.Add);

    // Имитация ffmpeg: «перепаковка» — копия содержимого.
    private static bool? CopyRemux(string rec, string mp4)
    {
        File.Copy(rec, mp4, overwrite: true);
        return true;
    }

    [Fact]
    public void Playable_orphan_gets_recovered_meta()
    {
        string mp4 = Make("PC1_alice_20261001_131927.mp4", DateTime.UtcNow.AddHours(-1));

        int dropped = Run(_ => true);

        Assert.Equal(0, dropped);
        Assert.True(File.Exists(mp4));
        using var meta = JsonDocument.Parse(File.ReadAllText(mp4 + ".json"));
        var root = meta.RootElement;
        Assert.Equal("recovered", root.GetProperty("startReason").GetString());
        Assert.Equal("PC1", root.GetProperty("host").GetString());
        Assert.Equal("alice", root.GetProperty("sessionUser").GetString());
        Assert.Equal("PC1_alice_20261001_131927.mp4", root.GetProperty("file").GetString());
        Assert.Equal(new DateTime(2026, 10, 1, 13, 19, 27), DateTime.Parse(root.GetProperty("startedAt").GetString()!));
    }

    [Fact]
    public void Broken_orphan_is_deleted_and_counted()
    {
        string mp4 = Make("PC1_20261001_131927.mp4", DateTime.UtcNow.AddHours(-1));

        int dropped = Run(_ => false);

        Assert.Equal(1, dropped);
        Assert.False(File.Exists(mp4));
        Assert.False(File.Exists(mp4 + ".json"));
        Assert.Contains(_log, l => l.StartsWith("orphan segment dropped: PC1_20261001_131927.mp4 (100 B", StringComparison.Ordinal));
        Assert.DoesNotContain(_log, l => l.Contains("failed", StringComparison.Ordinal));
    }

    // Нулевой файл — Capture убит до первого кадра: удалить, но это не потеря.
    [Fact]
    public void Empty_orphan_is_deleted_without_probe_and_not_counted()
    {
        string mp4 = Make("PC1_20261001_131927.mp4", DateTime.UtcNow.AddHours(-1), size: 0);

        int dropped = Run(_ => throw new InvalidOperationException("probe must not run"));

        Assert.Equal(0, dropped);
        Assert.False(File.Exists(mp4));
        Assert.Contains(_log, l => l.StartsWith("empty segment discarded: PC1_20261001_131927.mp4", StringComparison.Ordinal));
    }

    // Прод 2026-10-13 (VM-AVZ-IT009): .rec.mp4 в 28 Б — только ftyp, кадров нет.
    // Раньше — «orphan segment dropped» и красный счётчик потерь на ровном месте.
    [Fact]
    public void Header_only_rec_is_discarded_without_remux_and_not_counted()
    {
        string rec = Path.Combine(_dir, "PC1_alice_20261013_113107.rec.mp4");
        File.WriteAllBytes(rec, Mp4ProbeTests.Ftyp());
        File.SetLastWriteTimeUtc(rec, DateTime.UtcNow.AddHours(-1));

        int dropped = RunRec((_, _) => throw new InvalidOperationException("remux must not run"));

        Assert.Equal(0, dropped);
        Assert.False(File.Exists(rec));
        Assert.Contains(_log, l => l.StartsWith("empty segment discarded: PC1_alice_20261013_113107.rec.mp4 (28 B", StringComparison.Ordinal));
    }

    [Fact]
    public void Probe_unavailable_leaves_file_alone()
    {
        string mp4 = Make("PC1_20261001_131927.mp4", DateTime.UtcNow.AddHours(-1));

        int dropped = Run(_ => null);

        Assert.Equal(0, dropped);
        Assert.True(File.Exists(mp4));
        Assert.False(File.Exists(mp4 + ".json"));
    }

    [Fact]
    public void Recent_file_and_files_with_meta_are_not_touched()
    {
        string recent = Make("PC1_20261001_131927.mp4", DateTime.UtcNow.AddMinutes(-1));
        string paired = Make("PC1_20261008_120000.mp4", DateTime.UtcNow.AddHours(-2), withMeta: true);
        int probes = 0;

        int dropped = Run(_ => { probes++; return false; });

        Assert.Equal(0, dropped);
        Assert.Equal(0, probes);
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(paired));
    }

    [Fact]
    public void Meta_without_user_and_after_collision_rename()
    {
        string mp4 = Make("PC1_20261001_131927_c0a1b2c.mp4", DateTime.UtcNow.AddHours(-1));

        Run(_ => true);

        using var meta = JsonDocument.Parse(File.ReadAllText(mp4 + ".json"));
        Assert.Equal("", meta.RootElement.GetProperty("sessionUser").GetString());
        Assert.Equal(new DateTime(2026, 10, 1, 13, 19, 27), DateTime.Parse(meta.RootElement.GetProperty("startedAt").GetString()!));
    }

    [Fact]
    public void Unparseable_name_falls_back_to_file_time()
    {
        string mp4 = Make("weird.mp4", DateTime.UtcNow.AddHours(-1));

        Run(_ => true);

        using var meta = JsonDocument.Parse(File.ReadAllText(mp4 + ".json"));
        Assert.Equal(0, meta.RootElement.GetProperty("durationSec").GetDouble());
    }

    [Fact]
    public void Abandoned_rec_is_remuxed_and_gets_recovered_meta()
    {
        string rec = Make("PC1_alice_20261001_131927.rec.mp4", DateTime.UtcNow.AddHours(-1));
        string mp4 = Path.Combine(_dir, "PC1_alice_20261001_131927.mp4");

        int dropped = RunRec(CopyRemux);

        Assert.Equal(0, dropped);
        Assert.False(File.Exists(rec));
        Assert.True(File.Exists(mp4));
        using var meta = JsonDocument.Parse(File.ReadAllText(mp4 + ".json"));
        Assert.Equal("recovered", meta.RootElement.GetProperty("startReason").GetString());
        Assert.Equal("alice", meta.RootElement.GetProperty("sessionUser").GetString());
        Assert.Equal("PC1_alice_20261001_131927.mp4", meta.RootElement.GetProperty("file").GetString());
    }

    [Fact]
    public void Unrecoverable_rec_is_deleted_with_partial_output_and_counted()
    {
        string rec = Make("PC1_20261001_131927.rec.mp4", DateTime.UtcNow.AddHours(-1));
        string mp4 = Path.Combine(_dir, "PC1_20261001_131927.mp4");

        int dropped = RunRec((_, outPath) => { File.WriteAllText(outPath, "partial"); return false; });

        Assert.Equal(1, dropped);
        Assert.False(File.Exists(rec));
        Assert.False(File.Exists(mp4));
        Assert.False(File.Exists(mp4 + ".json"));
    }

    [Fact]
    public void Rec_with_partial_remux_output_from_killed_close_is_remuxed_again()
    {
        string rec = Make("PC1_20261001_131927.rec.mp4", DateTime.UtcNow.AddHours(-1));
        File.WriteAllText(rec, "fragmented");
        File.SetLastWriteTimeUtc(rec, DateTime.UtcNow.AddHours(-1));
        string mp4 = Make("PC1_20261001_131927.mp4", DateTime.UtcNow.AddHours(-1));

        RunRec(CopyRemux);

        Assert.False(File.Exists(rec));
        Assert.Equal("fragmented", File.ReadAllText(mp4));
        Assert.True(File.Exists(mp4 + ".json"));
    }

    [Fact]
    public void Rec_left_after_finished_close_is_just_deleted()
    {
        string rec = Make("PC1_20261001_131927.rec.mp4", DateTime.UtcNow.AddHours(-1));
        string mp4 = Make("PC1_20261001_131927.mp4", DateTime.UtcNow.AddHours(-1), withMeta: true);

        int dropped = RunRec((_, _) => throw new InvalidOperationException("remux must not run"));

        Assert.Equal(0, dropped);
        Assert.False(File.Exists(rec));
        Assert.True(File.Exists(mp4));
    }

    [Fact]
    public void Recent_rec_is_not_touched_and_its_output_is_not_probed()
    {
        string rec = Make("PC1_20261001_131927.rec.mp4", DateTime.UtcNow.AddMinutes(-1));
        // Выход перепаковки, которую Close() как раз делает, — без .json.
        string mp4 = Make("PC1_20261001_131927.mp4", DateTime.UtcNow.AddHours(-1));

        int dropped = OrphanSegments.Apply(_dir, MinAge, "PC1", "1.2.3",
            (_, _) => throw new InvalidOperationException("remux must not run"),
            _ => throw new InvalidOperationException("probe must not run"), _log.Add);

        Assert.Equal(0, dropped);
        Assert.True(File.Exists(rec));
        Assert.True(File.Exists(mp4));
    }

    [Fact]
    public void Remux_unavailable_leaves_rec_alone()
    {
        string rec = Make("PC1_20261001_131927.rec.mp4", DateTime.UtcNow.AddHours(-1));

        int dropped = RunRec((_, _) => null);

        Assert.Equal(0, dropped);
        Assert.True(File.Exists(rec));
    }
}
