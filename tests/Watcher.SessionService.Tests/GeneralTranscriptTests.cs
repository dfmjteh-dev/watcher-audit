using System.Text.Json;
using Watcher.SessionService;
using Watcher.Shared;
using Watcher.Uploader;
using Xunit;

namespace Watcher.SessionService.Tests;

public class GeneralTranscriptTests : IDisposable
{
    private static readonly DateTime T = new(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "watcher-general-" + Guid.NewGuid().ToString("N"));
    public GeneralTranscriptTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
    private static SshAncestry.Decision Decide(string application, params ProcessStart[] events) =>
        SshAncestry.ClassifyDetailed(100, T, SshAncestry.Index(events), application);

    [Theory]
    [InlineData(@"C:\Program Files\Custom Host\engine.exe -run")]
    [InlineData("\"C:\\Program Files\\Custom Host\\engine.exe\" -run")]
    [InlineData(@"c:\PROGRAM FILES\Custom Host\ENGINE.exe")]
    public void New_hosts_are_classified_without_name_allowlist(string application)
    {
        var root = new ProcessStart(T, 100, @"C:\Program Files\Custom Host\engine.exe", 10, "sshd-session.exe");
        Assert.Equal(SessionOrigin.Ssh, Decide(application, root).Origin);
        Assert.Equal(10, Decide(application, root).SshdPid);
        Assert.Equal(SessionOrigin.NotSsh, Decide(application, root with { ParentName = "SQLAGENT.EXE" }).Origin);
    }

    [Theory]
    [InlineData(@"C:\Tools\other.exe")]
    [InlineData(@"C:\Tools\engine.exe.bad")]
    [InlineData(@"engine.exe -run")]
    [InlineData("\"C:\\Tools\\engine.exe")]
    public void Pid_alone_and_partial_image_names_are_not_proof(string application)
    {
        var result = Decide(application, new ProcessStart(T, 100, @"C:\Tools\engine.exe", 10, "sshd.exe"));
        Assert.Equal(SessionOrigin.Unknown, result.Origin);
        Assert.Equal(TranscriptIssue.ProcessMismatch, result.Reason);
    }

    [Fact]
    public void Custom_host_through_multiple_wrappers_reaches_ssh()
    {
        var events = new[] {
            new ProcessStart(T, 100, @"C:\Tools\host.exe", 80, @"C:\Tools\bridge.exe"),
            new ProcessStart(T.AddSeconds(-1), 80, @"C:\Tools\bridge.exe", 60, @"C:\Windows\System32\cmd.exe"),
            new ProcessStart(T.AddSeconds(-2), 60, @"C:\Windows\System32\cmd.exe", 10, "sshd.exe"),
        };
        Assert.Equal(SessionOrigin.Ssh, Decide(@"C:\Tools\host.exe", events).Origin);
        Assert.Equal(SessionOrigin.Unknown, Decide(@"C:\Tools\host.exe", events[..1]).Origin);
        Assert.Equal(SessionOrigin.NotSsh, Decide(@"C:\Tools\host.exe", events.Select(e => e.NewPid == 60 ? e with { ParentName = "services.exe" } : e).ToArray()).Origin);
    }

    [Fact]
    public void Reused_root_pid_does_not_match_an_older_supported_process()
    {
        var events = new[] {
            new ProcessStart(T.AddMinutes(-1), 100, @"C:\Windows\powershell.exe", 10, "sshd.exe"),
            new ProcessStart(T, 100, @"C:\Tools\different.exe", 20, "services.exe"),
        };
        Assert.Equal(TranscriptIssue.ProcessMismatch, Decide(@"C:\Windows\powershell.exe", events).Reason);
    }

    [Fact]
    public void Same_image_with_pid_reused_after_transcript_is_ambiguous()
    {
        var events = new[] {
            new ProcessStart(T, 100, @"C:\Tools\host.exe", 10, "sshd.exe"),
            new ProcessStart(T.AddSeconds(10), 100, @"C:\Tools\host.exe", 20, "services.exe"),
        };
        Assert.Equal(TranscriptIssue.ProcessMismatch, Decide(@"C:\Tools\host.exe", events).Reason);
    }

    [Fact]
    public void Origin_cache_supports_collector_and_uploader_threads()
    {
        string path = Path.Combine(_dir, "origins.json");
        var cache = new TranscriptOrigins(path);
        Parallel.For(0, 1000, i =>
        {
            cache.Set(i + ".txt", SessionOrigin.Ssh, "10.0.0.1");
            Assert.NotNull(cache.Get(i + ".txt"));
            if (i % 100 == 0) cache.Save();
        });
        cache.Save();
        Assert.Equal(1000, new TranscriptOrigins(path).Count);
    }

    [Fact]
    public void Reused_parent_pid_and_fake_sshd_are_not_ssh()
    {
        var root = new ProcessStart(T, 100, @"C:\Tools\host.exe", 80, @"C:\Tools\bridge.exe");
        var events = new[] {
            root,
            new ProcessStart(T.AddSeconds(-2), 80, @"C:\Tools\bridge.exe", 10, "sshd.exe"),
            new ProcessStart(T.AddSeconds(-1), 80, @"C:\Tools\other.exe", 20, "services.exe"),
        };
        Assert.Equal(TranscriptIssue.AncestryIncomplete, Decide(@"C:\Tools\host.exe", events).Reason);
        Assert.Equal(SessionOrigin.Unknown, Decide(@"C:\Tools\host.exe", root with { ParentName = "sshd-fake.exe" }).Origin);
    }

    [Fact]
    public void Ancestry_cycles_stop_without_classifying_as_local_automation()
    {
        var events = new[] {
            new ProcessStart(T, 100, @"C:\Tools\host.exe", 80, @"C:\Tools\bridge.exe"),
            new ProcessStart(T, 80, @"C:\Tools\bridge.exe", 100, @"C:\Tools\host.exe"),
        };
        Assert.Equal(TranscriptIssue.AncestryCycle, Decide(@"C:\Tools\host.exe", events).Reason);
    }

    [Theory]
    [InlineData(-601)]
    [InlineData(31)]
    public void Full_path_does_not_bypass_time_window(int seconds) =>
        Assert.Equal(TranscriptIssue.EventMissing, Decide(@"C:\Tools\host.exe", new ProcessStart(T.AddSeconds(seconds), 100, @"C:\Tools\host.exe", 10, "sshd.exe")).Reason);

    [Fact]
    public void History_survives_restart_and_log_cursor_reset()
    {
        string path = Path.Combine(_dir, "history.json");
        var history = new ProcessStartHistory(path, T);
        history.Observe(50, T, new(T, 100, @"C:\Tools\host.exe", 10, "sshd.exe"));
        Assert.True(history.Save());
        var loaded = new ProcessStartHistory(path, T.AddMinutes(5));
        Assert.Equal(50, loaded.RecordId);
        Assert.Single(loaded.Events);
        loaded.ResetCursor();
        loaded.Observe(1, T.AddMinutes(5), new(T.AddMinutes(5), 200, "SQLPS.exe", 20, "SQLAGENT.EXE"));
        Assert.Equal(2, loaded.Events.Count);
        Assert.True(loaded.Save());
        Assert.Equal(1, new ProcessStartHistory(path, T.AddMinutes(6)).RecordId);
        Assert.DoesNotContain("CommandLine", File.ReadAllText(path));
    }

    [Fact]
    public void History_limits_age_count_and_rejects_invalid_metadata()
    {
        var h = new ProcessStartHistory(Path.Combine(_dir, "h.json"), T);
        h.Observe(1, T.AddDays(-3), new(T.AddDays(-3), 1, "old.exe", 10, "sshd.exe"));
        h.Observe(2, T, new(T, 0, "invalid.exe", 10, "sshd.exe"));
        for (int i = 3; i <= ProcessStartHistory.MaxEntries + 3; i++)
            h.Observe(i, T, new(T, i, "host.exe", 10, "sshd.exe"));
        h.Trim(T);
        Assert.Equal(ProcessStartHistory.MaxEntries, h.Events.Count);
        Assert.DoesNotContain(h.Events, e => e.NewPid == 1 || e.NewPid == 0);
    }

    [Fact]
    public void Corrupt_cache_and_unwritable_path_do_not_create_a_false_origin()
    {
        string path = Path.Combine(_dir, "h.json");
        File.WriteAllText(path, "{broken");
        Assert.Empty(new ProcessStartHistory(path, T).Events);
        string blocker = Path.Combine(_dir, "file");
        File.WriteAllText(blocker, "x");
        var h = new ProcessStartHistory(Path.Combine(blocker, "h.json"), T);
        h.Observe(1, T, new(T, 100, "powershell.exe", 10, "sshd.exe"));
        Assert.False(h.Save());
    }

    [Theory]
    [InlineData("0x64", 100)]
    [InlineData("100", 100)]
    [InlineData("-1", 0)]
    [InlineData("0xffffffff", 0)]
    public void Named_event_fields_support_schema_versions_and_validate_pids(string pid, int expected)
    {
        string xml = $"<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><TimeCreated SystemTime=\"2026-11-02T10:00:00Z\" /></System><EventData><Data Name=\"CommandLine\">secret must not be cached</Data><Data Name=\"NewProcessName\">C:\\SQL\\SQLPS.exe</Data><Data Name=\"NewProcessId\">{pid}</Data><Data Name=\"ProcessId\">0x20</Data></EventData></Event>";
        var e = ProcessStartEvent.Parse(xml);
        if (expected == 0) Assert.Null(e);
        else
        {
            Assert.Equal(expected, e!.NewPid);
            Assert.Equal(32, e.CreatorPid);
            Assert.Equal("", e.ParentName);
            Assert.DoesNotContain("secret", JsonSerializer.Serialize(e));
        }
    }

    [Fact]
    public void Missing_event_diagnostics_distinguish_observations_without_guessing_history()
    {
        var healthy = new ProcessHistoryStatus(false, false, false, true, T.AddHours(-2), null);
        Assert.Equal(TranscriptIssue.EventMissing, healthy.MissingReason(T.AddHours(-1), T));
        Assert.Equal(TranscriptIssue.EventPending, healthy.MissingReason(T.AddSeconds(-5), T));
        Assert.Equal(TranscriptIssue.HistoryUnavailable, healthy.MissingReason(T.AddHours(-3), T));
        Assert.Equal(TranscriptIssue.EventReadFailed, (healthy with { ReadFailed = true }).MissingReason(T.AddHours(-1), T));
        Assert.Equal(TranscriptIssue.AuditDisabled, (healthy with { AuditEnabled = false }).MissingReason(T.AddHours(-1), T));
        Assert.Equal(TranscriptIssue.EventPending, (healthy with { CatchingUp = true }).MissingReason(T.AddHours(-3), T));
        // Отсутствие доступа к политике не означает, что аудит выключен.
        Assert.Equal(TranscriptIssue.EventMissing, (healthy with { AuditEnabled = null }).MissingReason(T.AddHours(-1), T));
    }

    private TranscriptQuarantine Quarantine(long bytes = 8192) => new(Path.Combine(_dir, "q"), bytes, TimeSpan.FromDays(7));
    private string Source(string name = "transcript.txt")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "private transcript");
        return path;
    }

    [Fact]
    public void Quarantine_requires_durable_loss_record_before_removing_original()
    {
        string source = Source();
        var q = Quarantine();
        Assert.Throws<IOException>(() => q.Store(source, TranscriptIssue.EventMissing, T, _ => false));
        Assert.True(File.Exists(source));
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "q"), "*.txt"));
        var loss = LossJournal.At(Path.Combine(_dir, "losses.jsonl"));
        var entry = q.Store(source, TranscriptIssue.EventMissing, T.AddMinutes(1), e => loss.TryAdd(e.File, e.Size, LossJournal.ReasonTranscript, e.At, e.Reason));
        Assert.False(File.Exists(source));
        Assert.Equal(T, entry.At); // повтор не продлевает срок карантина
        Assert.Equal(TranscriptIssue.EventMissing, Assert.Single(loss.Pending(10)).Detail);
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "q"), "*.txt"));
    }

    [Fact]
    public void Quarantine_keeps_modified_source_and_does_not_accept_symlinks()
    {
        string source = Source();
        Assert.Throws<IOException>(() => Quarantine().Store(source, TranscriptIssue.EventMissing, T, _ => { File.WriteAllText(source, "changed"); return true; }));
        Assert.Equal("changed", File.ReadAllText(source));
        if (!OperatingSystem.IsWindows())
        {
            string linked = Path.Combine(_dir, "linked");
            Directory.CreateSymbolicLink(linked, Path.Combine(_dir, "q"));
            Assert.Throws<IOException>(() => new TranscriptQuarantine(linked, 8192, TimeSpan.FromDays(7)).Sweep(T));
        }
    }

    [Fact]
    public void Quarantine_directory_cannot_be_scanned_as_an_active_transcript_tree()
    {
        var q = Quarantine();
        q.ValidateSourceDirectory(Path.Combine(_dir, "transcripts"));
        Assert.Throws<ArgumentException>(() => q.ValidateSourceDirectory(_dir));
        Assert.Throws<ArgumentException>(() => q.ValidateSourceDirectory(Path.Combine(_dir, "q")));
        Assert.Throws<ArgumentException>(() => q.ValidateSourceDirectory(Path.Combine(_dir, "q", "nested")));
    }

    [Fact]
    public void Quarantine_limits_file_count_even_for_tiny_transcripts()
    {
        var q = new TranscriptQuarantine(Path.Combine(_dir, "q"), 8192, TimeSpan.FromDays(7), maxEntries: 2);
        for (int i = 0; i < 3; i++) q.Store(Source(i + ".txt"), TranscriptIssue.EventMissing, T.AddMinutes(i), _ => true);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_dir, "q"), "*.txt").Length);
    }

    [Fact]
    public void Quarantine_is_bounded_and_expired_copies_are_removed()
    {
        var q = Quarantine(1400);
        q.Store(Source("a.txt"), TranscriptIssue.EventMissing, T, _ => true);
        q.Store(Source("b.txt"), TranscriptIssue.EventMissing, T.AddMinutes(1), _ => true);
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "q"), "*.txt"));
        q.Sweep(T.AddDays(8));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "q"), "*.txt"));
        string source = Source();
        Assert.Throws<IOException>(() => Quarantine(4).Store(source, TranscriptIssue.EventMissing, T, _ => true));
        Assert.True(File.Exists(source));
    }
}
