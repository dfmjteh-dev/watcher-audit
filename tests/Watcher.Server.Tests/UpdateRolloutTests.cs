using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

// 0.15.0: раскатка обновлений агентов не больше N хостов одновременно.
public class UpdateRolloutTests
{
    [Fact]
    public void Limits_concurrent_hosts_and_reoffers_holder()
    {
        var r = new UpdateRollout(2);
        Assert.True(r.TryAcquire("a", "0.14.5"));
        Assert.True(r.TryAcquire("b", "0.14.5"));
        Assert.False(r.TryAcquire("c", "0.14.5"));
        Assert.True(r.TryAcquire("A", "0.14.5"));   // держатель слота получает повторно
        r.Release("a");
        Assert.True(r.TryAcquire("c", "0.14.5"));
        Assert.Equal(2, r.Active);
    }

    [Fact]
    public void Slot_expires_when_agent_disappears()
    {
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var r = new UpdateRollout(1, TimeSpan.FromMinutes(30), () => now);
        Assert.True(r.TryAcquire("a", "1.0.0"));
        Assert.False(r.TryAcquire("b", "1.0.0"));
        now = now.AddMinutes(31);
        Assert.True(r.TryAcquire("b", "1.0.0"));
    }

    [Fact]
    public void Zero_means_unlimited()
    {
        var r = new UpdateRollout(0);
        for (int i = 0; i < 100; i++) Assert.True(r.TryAcquire("h" + i, "1.0.0"));
    }
    [Fact]
    public void Failed_dispatch_does_not_reserve_or_release_a_slot()
    {
        var r = new UpdateRollout(1);
        Assert.False(r.TryDispatch("a", "1.0.0", () => false));
        Assert.Equal(0, r.Active);
        Assert.True(r.TryDispatch("a", "1.0.0", () => true, "current"));
        Assert.False(r.TryDispatch("a", "1.0.0", () => false));
        Assert.Equal(1, r.Active);
        Assert.False(r.TryDispatch("b", "1.0.0", () => throw new Exception("must not run at capacity")));
        r.ReleaseProtected("a", "old-request");
        Assert.Equal(1, r.Active);
        r.ReleaseProtected("a", "current");
        Assert.Equal(0, r.Active);
    }

}
