using System.Text.Json;
using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

// 0.13: журнал потерь, отметки процессов записи, хэш конфига в кэше.
public sealed class AgentStatusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"watcher-astatus-{Guid.NewGuid():N}");

    public AgentStatusTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Loss_journal_persists_until_acknowledged()
    {
        string path = Path.Combine(_dir, "agent", "losses.jsonl");
        var j = LossJournal.At(path);
        j.Add(Path.Combine(_dir, "PC_ivan_20261013_113107.mp4"), 100, LossJournal.ReasonQuota, DateTime.UtcNow);
        j.Add("PowerShell_transcript.PC.ab+cd.20261013130321.txt", 5, LossJournal.ReasonTranscript, DateTime.UtcNow);
        j.Add("PC_20261013_120000.rec.mp4", 7, LossJournal.ReasonBroken, DateTime.UtcNow);

        Assert.Equal(3, File.ReadAllLines(path).Length);
        var pending = j.Pending(2);
        Assert.Equal(2, pending.Count);
        Assert.Equal("PC_ivan_20261013_113107.mp4", pending[0].File);   // только имя, без пути станции
        Assert.Equal(new DateTime(2026, 10, 13, 11, 31, 7, DateTimeKind.Local).ToUniversalTime(), pending[0].RecordedAt);
        Assert.Equal(new DateTime(2026, 10, 13, 13, 3, 21, DateTimeKind.Local).ToUniversalTime(), pending[1].RecordedAt);

        j.Acknowledge(2);
        var rest = File.ReadAllLines(path);
        Assert.Single(rest);
        Assert.Contains("rec.mp4", rest[0]);
    }

    [Fact]
    public void Loss_journal_keeps_last_entries_when_server_never_acknowledges()
    {
        var j = LossJournal.At(Path.Combine(_dir, "cap", "losses.jsonl"));
        for (int i = 0; i < LossJournal.MaxEntries + 5; i++) j.Add($"f{i}.mp4", 1, LossJournal.ReasonAge, DateTime.UtcNow);
        var all = j.Pending(int.MaxValue);
        Assert.Equal(LossJournal.MaxEntries, all.Count);
        Assert.Equal("f5.mp4", all[0].File);
    }

    [Fact]
    public void Capture_status_fresh_reported_stale_skipped_old_deleted()
    {
        void Write(string name, string json, TimeSpan age)
        {
            string p = Path.Combine(_dir, CaptureStatus.Prefix + name + CaptureStatus.Suffix);
            File.WriteAllText(p, json);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow - age);
        }
        Write("ivan", """{"user":"ivan","configHash":"abc123","recording":true,"at":"2026-10-14T10:00:00Z"}""", TimeSpan.FromSeconds(20));
        Write("petr", """{"user":"petr","configHash":"abc123","recording":false}""", TimeSpan.FromMinutes(20));
        Write("old", """{"user":"old"}""", TimeSpan.FromDays(2));
        Write("bad", "not json", TimeSpan.Zero);

        var list = CaptureStatus.Collect(_dir, DateTime.UtcNow);

        var only = Assert.Single(list);
        Assert.Equal("ivan", only.User);
        Assert.Equal("abc123", only.ConfigHash);
        Assert.True(File.Exists(Path.Combine(_dir, CaptureStatus.Prefix + "petr" + CaptureStatus.Suffix)));
        Assert.False(File.Exists(Path.Combine(_dir, CaptureStatus.Prefix + "old" + CaptureStatus.Suffix)));
    }

    [Fact]
    public void Config_hash_travels_inside_cached_config()
    {
        using var doc = JsonDocument.Parse("""{"outputFps":3,"excludeTitles":["Почта"]}""");
        string cached = Uploader.WithConfigHash(doc.RootElement, "0123456789ab");
        string path = Path.Combine(_dir, "capture-config.json");
        File.WriteAllText(path, cached);

        Assert.Equal("0123456789ab", Uploader.CachedConfigHash(path));
        using var back = JsonDocument.Parse(cached);
        Assert.Equal(3, back.RootElement.GetProperty("outputFps").GetInt32());
        Assert.Equal("Почта", back.RootElement.GetProperty("excludeTitles")[0].GetString());
        Assert.Equal(doc.RootElement.GetRawText(), Uploader.WithConfigHash(doc.RootElement, null));
        Assert.Null(Uploader.CachedConfigHash(Path.Combine(_dir, "missing.json")));
    }

    [Fact]
    public void Deletions_report_losses_with_file_and_size()
    {
        var lost = new List<(string File, long Size)>();
        string old = Path.Combine(_dir, "PC_20260901_100000.mp4");
        File.WriteAllBytes(old, new byte[42]);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-40));
        QueueAge.Apply(_dir, 30, DateTime.UtcNow, _ => { }, (f, s) => lost.Add((f, s)));
        Assert.Equal([("PC_20260901_100000.mp4", 42L)], lost);
    }
    [Fact]
    public void Detailed_loss_preserves_legacy_reason_and_is_idempotent()
    {
        string dir = Path.Combine(Path.GetTempPath(), "loss-detail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var journal = LossJournal.At(Path.Combine(dir, "loss.jsonl"));
            Assert.True(journal.TryAdd("a.txt", 10, LossJournal.ReasonTranscript, DateTime.UtcNow, "event-missing"));
            Assert.True(journal.TryAdd("a.txt", 10, LossJournal.ReasonTranscript, DateTime.UtcNow, "event-missing"));
            var loss = Assert.Single(journal.Pending(100));
            Assert.Equal("transcript-no-4688", loss.Reason);
            Assert.Equal("event-missing", loss.Detail);
            string blocker = Path.Combine(dir, "blocked");
            File.WriteAllText(blocker, "x");
            Assert.False(LossJournal.At(Path.Combine(blocker, "loss.jsonl")).TryAdd("b.txt", 10, LossJournal.ReasonTranscript, DateTime.UtcNow, "event-missing"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

}
