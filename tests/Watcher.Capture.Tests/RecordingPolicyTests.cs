using Watcher.Capture;
using Xunit;

namespace Watcher.Capture.Tests;

// H8: расписание записи и исключённые окна. Копия Watcher.Capture.Linux.Tests/RecordingPolicyTests.cs.
public class RecordingPolicyTests
{
    // 2026-10-05 — понедельник.
    private static DateTime At(int day, int h, int m = 0) => new(2026, 10, 4 + day, h, m, 0);
    private static RecordingPolicy Schedule(params ScheduleWindow[] w) => new(w.ToList(), null, null);

    [Fact]
    public void Working_hours()
    {
        var p = Schedule(new ScheduleWindow([1, 2, 3, 4, 5], "09:00", "18:00"));
        Assert.True(p.InSchedule(At(1, 9)));
        Assert.True(p.InSchedule(At(5, 17, 59)));
        Assert.False(p.InSchedule(At(1, 8, 59)));
        Assert.False(p.InSchedule(At(1, 18)));
        Assert.False(p.InSchedule(At(6, 12)));
    }

    [Fact]
    public void Night_shift_crosses_midnight_and_whole_day()
    {
        var p = Schedule(new ScheduleWindow([5], "22:00", "06:00"), new ScheduleWindow([7], "00:00", "00:00"));
        Assert.True(p.InSchedule(At(5, 23)));          // пятница вечер
        Assert.True(p.InSchedule(At(6, 5, 59)));       // утро субботы — продолжение пятницы
        Assert.False(p.InSchedule(At(6, 6)));
        Assert.False(p.InSchedule(At(5, 3)));          // утро пятницы — четверг не в расписании
        Assert.True(p.InSchedule(At(7, 0)));
        Assert.True(p.InSchedule(At(7, 23, 59)));
    }

    [Fact]
    public void No_schedule_records_always_but_broken_schedule_records_never()
    {
        Assert.True(RecordingPolicy.RecordAlways.InSchedule(At(6, 3)));
        Assert.True(Schedule().InSchedule(At(6, 3)));
        var broken = Schedule(new ScheduleWindow([9], "09:00", "18:00"), new ScheduleWindow([1], "9", "18:00"));
        Assert.Equal(2, broken.InvalidScheduleEntries);
        Assert.False(broken.InSchedule(At(1, 12)));
    }

    [Fact]
    public void Excluded_windows_by_process_and_title()
    {
        var p = new RecordingPolicy(null, ["KeePass.exe", "telegram-desktop"], ["сбербанк онлайн"]);
        Assert.Equal(PauseReason.ExcludedProcess, p.Excluded(new("KeePass", "База.kdbx")));
        Assert.Equal(PauseReason.ExcludedProcess, p.Excluded(new("keepass.exe", null)));
        Assert.Equal(PauseReason.ExcludedProcess, p.Excluded(new("telegram-deskto", "Telegram")));   // comm обрезан до 15
        Assert.Equal(PauseReason.ExcludedTitle, p.Excluded(new("chrome", "Сбербанк Онлайн — Google Chrome")));
        Assert.Equal(PauseReason.None, p.Excluded(new("chrome", "Почта")));
        Assert.Equal(PauseReason.None, p.Excluded(null));
    }

    [Fact]
    public void Check_asks_window_only_when_needed()
    {
        int probes = 0;
        ActiveWindowInfo? Probe() { probes++; return new("KeePass", "x"); }
        Assert.Equal(PauseReason.None, RecordingPolicy.RecordAlways.Check(At(1, 12), Probe));
        Assert.Equal(0, probes);

        var p = new RecordingPolicy([new([1], "09:00", "18:00")], ["keepass"], null);
        Assert.Equal(PauseReason.Schedule, p.Check(At(1, 20), Probe));
        Assert.Equal(0, probes);
        Assert.Equal(PauseReason.ExcludedProcess, p.Check(At(1, 12), Probe));
        Assert.Equal(1, probes);
        Assert.Equal(PauseReason.None, p.Check(At(1, 12), () => throw new InvalidOperationException()));
    }

    [Fact]
    public void Built_from_config_payload()
    {
        var payload = ConfigReload.TryParse("""{"schedule":[{"days":[1],"from":"09:00","to":"10:00"}],"excludeTitles":["банк"]}""",
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var p = RecordingPolicy.From(payload);
        Assert.False(p.InSchedule(At(1, 11)));
        Assert.Equal(PauseReason.ExcludedTitle, p.Excluded(new("x", "Интернет-БАНК")));
        Assert.True(RecordingPolicy.From(null).InSchedule(At(1, 3)));
    }
}
