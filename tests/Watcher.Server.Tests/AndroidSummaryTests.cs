using Xunit;

namespace Watcher.Server.Tests;

public class AndroidSummaryTests
{
    private static readonly DateTime From = new(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddDays(1);
    private static readonly DateTime After = To.AddHours(1);
    private long _id;

    private AndroidEvent E(int minute, string type, string? pkg = null, string? detail = null) =>
        new(++_id, "PHONE", From.AddMinutes(minute), type, pkg, detail, From, Guid.NewGuid().ToString("D"));

    private static AndroidSummaryResult Build(IReadOnlyList<AndroidEvent> events, DateTime? now = null,
        AndroidEvent? state = null, IReadOnlyList<AndroidEvent>? inventory = null) =>
        AndroidSummary.Build(From, To, now ?? After, events, false, state, inventory ?? []);

    [Fact]
    public void ForegroundTime_IsPairedWithBackground()
    {
        var r = Build([E(10, "app_foreground", "a"), E(15, "app_background", "a"),
            E(20, "app_foreground", "a"), E(21, "app_background", "a")]);
        var a = Assert.Single(r.Apps);
        Assert.Equal(6 * 60_000, a.ForegroundMs);
        Assert.Equal(2, a.Launches);
        Assert.Equal(From.AddMinutes(10), a.FirstAt);
        Assert.Equal(From.AddMinutes(21), a.LastAt);
        Assert.False(a.OnScreen);
    }

    [Fact]
    public void NextForegroundClosesPrevious_RepeatedForegroundIsNotALaunch()
    {
        var r = Build([E(0, "app_foreground", "a"), E(1, "app_foreground", "a"),
            E(5, "app_foreground", "b"), E(7, "app_background", "b"), E(8, "app_background", "a")]);
        Assert.Equal(["a", "b"], r.Apps.Select(x => x.Package));
        Assert.Equal(5 * 60_000, r.Apps[0].ForegroundMs);
        Assert.Equal(1, r.Apps[0].Launches);
        Assert.Equal(2 * 60_000, r.Apps[1].ForegroundMs);
    }

    [Fact]
    public void ScreenOffClosesInterval()
    {
        var r = Build([E(0, "app_foreground", "a"), E(3, "device_state", null, "{\"screenOn\":false}")]);
        Assert.Equal(3 * 60_000, Assert.Single(r.Apps).ForegroundMs);
    }

    [Fact]
    public void OpenInterval_EndsAtNow_AndIsOnScreenWhileRangeIsCurrent()
    {
        var now = From.AddMinutes(30);
        var r = Build([E(10, "app_foreground", "a")], now);
        var a = Assert.Single(r.Apps);
        Assert.Equal(20 * 60_000, a.ForegroundMs);
        Assert.True(a.OnScreen);

        // Прошедший день: хвост — до конца периода, «на экране» не показываем.
        var past = Build([E(23 * 60, "app_foreground", "a")]);
        Assert.Equal(60 * 60_000, Assert.Single(past.Apps).ForegroundMs);
        Assert.False(past.Apps[0].OnScreen);
    }

    [Fact]
    public void ChangesAndTraffic_AreAggregated()
    {
        var r = Build([
            E(1, "app_installed", "x", "{\"installer\":\"com.android.vending\"}"),
            E(2, "app_removed", "y"),
            E(3, "app_traffic", "x", "{\"rxBytes\":100,\"txBytes\":10,\"intervalMs\":60000,\"networkType\":\"wifi\"}"),
            E(4, "app_traffic", "x", "{\"rxBytes\":5,\"txBytes\":5,\"intervalMs\":60000,\"networkType\":\"mobile\"}"),
            E(5, "app_traffic", "z", "{\"rxBytes\":1,\"txBytes\":-7,\"intervalMs\":60000}"),
        ]);
        Assert.Equal(["app_removed", "app_installed"], r.Changes.Select(c => c.Type));
        Assert.Equal("com.android.vending", r.Changes[1].Installer);
        Assert.Equal(new AndroidAppTraffic("x", 105, 15, 110, 10), r.Traffic[0]);
        // Отрицательные/битые счётчики — не вычитаем, считаем нулём.
        Assert.Equal(new AndroidAppTraffic("z", 1, 0, 0, 0), r.Traffic[1]);
    }

    [Fact]
    public void Inventory_CountsSystem_ListsUserApps_DetectsPartial()
    {
        string part(int n, int of, string pkgs) => $"{{\"snapshotId\":\"s\",\"part\":{n},\"parts\":{of},\"packages\":[{pkgs}]}}";
        var full = Build([], inventory: [
            E(0, "app_inventory", null, part(0, 2, "{\"package\":\"android\",\"versionCode\":1,\"system\":true}")),
            E(0, "app_inventory", null, part(1, 2, "{\"package\":\"com.b\",\"versionCode\":2,\"system\":false,\"versionName\":\"2.0\"},{\"package\":\"com.a\",\"versionCode\":1,\"system\":false,\"installer\":\"com.android.vending\"}")),
        ]);
        var inv = Assert.IsType<AndroidInventorySummary>(full.Inventory);
        Assert.Equal(3, inv.Total);
        Assert.Equal(1, inv.System);
        Assert.False(inv.Partial);
        Assert.Equal(["com.a", "com.b"], inv.UserApps.Select(a => a.Package));
        Assert.Equal("2.0", inv.UserApps[1].VersionName);

        var partial = Build([], inventory: [E(0, "app_inventory", null, part(0, 2, ""))]);
        Assert.True(partial.Inventory!.Partial);
        Assert.Null(Build([]).Inventory);
    }

    [Fact]
    public void NetworkLog_AggregatedByTargetAndApp_SecurityNewestFirst()
    {
        var r = Build([
            E(1, "net_dns", null, "{\"domain\":\"a.example\",\"package\":\"com.browser\",\"count\":2}"),
            E(2, "net_dns", null, "{\"domain\":\"a.example\",\"package\":\"com.browser\",\"count\":3}"),
            E(3, "net_dns", null, "{\"domain\":\"b.example\",\"count\":1}"),
            E(4, "net_connect", null, "{\"ip\":\"1.2.3.4\",\"port\":443,\"package\":\"com.browser\",\"count\":4}"),
            E(5, "security", null, "{\"tag\":210002,\"name\":\"adb_shell_command\",\"data\":\"ls\"}"),
            E(6, "security", null, "{\"tag\":210009,\"name\":\"os_startup\"}"),
        ]);
        // DNS a.example схлопнут в одну строку count=5; b.example (без пакета) отдельно; connect отдельно.
        Assert.Equal(3, r.Network.Count);
        var top = r.Network[0];
        Assert.Equal("dns", top.Kind);
        Assert.Equal("a.example", top.Target);
        Assert.Equal("com.browser", top.Package);
        Assert.Equal(5, top.Count);
        Assert.Contains(r.Network, n => n.Kind == "connect" && n.Target == "1.2.3.4:443" && n.Count == 4);
        Assert.Contains(r.Network, n => n.Target == "b.example" && n.Package == null);
        Assert.False(r.NetworkTruncated);
        // Журнал безопасности: новые сверху.
        Assert.Equal(["os_startup", "adb_shell_command"], r.Security.Select(x => x.Name));
        Assert.Equal("ls", r.Security[1].Data);
    }

    [Fact]
    public void DeviceState_IsPassedThrough()
    {
        var state = E(-60, "device_state", null, "{\"osVersion\":\"14\",\"usageAccess\":false}");
        var r = Build([], state: state);
        Assert.Equal(state.Ts, r.DeviceStateAt);
        Assert.Equal("14", r.DeviceState!.Value.GetProperty("osVersion").GetString());
    }
}
