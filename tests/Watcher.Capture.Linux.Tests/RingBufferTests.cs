using Watcher.Capture.Linux;
using Xunit;

namespace Watcher.Capture.Linux.Tests;

public class RingBufferTests
{
    private static Frame TinyFrame() => new(2, 2, new byte[2 * 2 * 3]);

    [Fact]
    public void ZeroCapacity_drops_pushed_frame()
    {
        var ring = new RingBuffer(capacity: 0);

        ring.Push(TinyFrame());

        Assert.Empty(ring.DrainCopies());
    }

    [Fact]
    public void Push_beyond_capacity_evicts_oldest()
    {
        var ring = new RingBuffer(capacity: 2);
        ring.Push(TinyFrame());
        ring.Push(TinyFrame());
        ring.Push(TinyFrame()); // should evict the first

        Assert.Equal(2, ring.DrainCopies().Length);
    }

    [Fact]
    public void DrainCopies_returns_independent_clones()
    {
        var ring = new RingBuffer(capacity: 2);
        var frame = TinyFrame();
        frame.Pixels[0] = 42;
        ring.Push(frame);

        var copies = ring.DrainCopies();
        copies[0].Pixels[0] = 99;

        // Мутация копии не должна задевать оригинал, который держит ring.
        Assert.Equal(42, frame.Pixels[0]);
    }

    [Fact]
    public void Clear_empties_ring_but_it_stays_usable()
    {
        var ring = new RingBuffer(capacity: 2);
        ring.Push(TinyFrame());

        ring.Clear();

        Assert.Empty(ring.DrainCopies());

        ring.Push(TinyFrame());
        Assert.Single(ring.DrainCopies());
    }

    [Fact]
    public void Resize_shrinking_evicts_oldest_frames_immediately()
    {
        var ring = new RingBuffer(capacity: 4);
        for (int i = 0; i < 4; i++) ring.Push(TinyFrame());

        ring.Resize(2);

        Assert.Equal(2, ring.Capacity);
        Assert.Equal(2, ring.DrainCopies().Length);
    }

    [Fact]
    public void Resize_growing_keeps_existing_frames()
    {
        var ring = new RingBuffer(capacity: 1);
        ring.Push(TinyFrame());

        ring.Resize(3);
        ring.Push(TinyFrame());
        ring.Push(TinyFrame());

        Assert.Equal(3, ring.DrainCopies().Length);
    }

    [Fact]
    public void Resize_to_zero_drops_everything_and_future_pushes_are_noop()
    {
        var ring = new RingBuffer(capacity: 2);
        ring.Push(TinyFrame());

        ring.Resize(0);
        ring.Push(TinyFrame());

        Assert.Empty(ring.DrainCopies());
    }
}
