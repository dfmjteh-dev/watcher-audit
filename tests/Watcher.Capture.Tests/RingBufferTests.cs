using System.Drawing;
using Watcher.Capture;
using Xunit;

namespace Watcher.Capture.Tests;

public class RingBufferTests
{
    private static Bitmap TinyBitmap() => new(2, 2);

    [Fact]
    public void ZeroCapacity_disposes_pushed_frame_immediately()
    {
        using var ring = new RingBuffer(capacity: 0);
        var frame = TinyBitmap();

        ring.Push(frame);

        Assert.Throws<ArgumentException>(() => frame.Width);
        Assert.Empty(ring.DrainCopies());
    }

    [Fact]
    public void Push_beyond_capacity_evicts_oldest_and_disposes_it()
    {
        using var ring = new RingBuffer(capacity: 2);
        var frame1 = TinyBitmap();
        var frame2 = TinyBitmap();
        var frame3 = TinyBitmap();

        ring.Push(frame1);
        ring.Push(frame2);
        ring.Push(frame3); // should evict frame1

        Assert.Throws<ArgumentException>(() => frame1.Width);
        var copies = ring.DrainCopies();
        Assert.Equal(2, copies.Length);
        foreach (var c in copies) c.Dispose();
    }

    [Fact]
    public void DrainCopies_returns_independent_clones()
    {
        using var ring = new RingBuffer(capacity: 2);
        var frame = TinyBitmap();
        ring.Push(frame);

        var copies = ring.DrainCopies();
        copies[0].Dispose();

        // Disposing the copy must not affect the original still owned by the ring.
        Assert.Equal(2, frame.Width);
    }

    [Fact]
    public void Dispose_disposes_all_remaining_frames()
    {
        var ring = new RingBuffer(capacity: 3);
        var frame1 = TinyBitmap();
        var frame2 = TinyBitmap();
        ring.Push(frame1);
        ring.Push(frame2);

        ring.Dispose();

        Assert.Throws<ArgumentException>(() => frame1.Width);
        Assert.Throws<ArgumentException>(() => frame2.Width);
    }

    [Fact]
    public void Clear_disposes_frames_but_ring_stays_usable()
    {
        // Используется ConfigReload-путём при смене разрешения (Фаза 4) — старые кадры
        // не должны смешаться с новым разрешением в следующем pre-roll.
        using var ring = new RingBuffer(capacity: 2);
        var frame1 = TinyBitmap();
        ring.Push(frame1);

        ring.Clear();

        Assert.Throws<ArgumentException>(() => frame1.Width);
        Assert.Empty(ring.DrainCopies());

        var frame2 = TinyBitmap();
        ring.Push(frame2);
        var copies = ring.DrainCopies();
        Assert.Single(copies);
        copies[0].Dispose();
    }

    [Fact]
    public void Resize_shrinking_evicts_oldest_frames_immediately()
    {
        // CaptureIntervalMs меняется на лету (Фаза 4) — без пересчёта емкости
        // длительность pre-roll в секундах плывёт относительно PreRollSeconds.
        using var ring = new RingBuffer(capacity: 4);
        var frames = new[] { TinyBitmap(), TinyBitmap(), TinyBitmap(), TinyBitmap() };
        foreach (var f in frames) ring.Push(f);

        ring.Resize(2);

        Assert.Equal(2, ring.Capacity);
        Assert.Throws<ArgumentException>(() => frames[0].Width);
        Assert.Throws<ArgumentException>(() => frames[1].Width);
        var copies = ring.DrainCopies();
        Assert.Equal(2, copies.Length);
        foreach (var c in copies) c.Dispose();
    }

    [Fact]
    public void Resize_growing_keeps_existing_frames()
    {
        using var ring = new RingBuffer(capacity: 1);
        var frame = TinyBitmap();
        ring.Push(frame);

        ring.Resize(3);
        ring.Push(TinyBitmap());
        ring.Push(TinyBitmap());

        Assert.Equal(2, frame.Width); // not disposed by the resize itself
        var copies = ring.DrainCopies();
        Assert.Equal(3, copies.Length);
        foreach (var c in copies) c.Dispose();
    }

    [Fact]
    public void Resize_to_zero_disposes_everything_and_future_pushes_are_noop()
    {
        using var ring = new RingBuffer(capacity: 2);
        var frame = TinyBitmap();
        ring.Push(frame);

        ring.Resize(0);

        Assert.Throws<ArgumentException>(() => frame.Width);
        var next = TinyBitmap();
        ring.Push(next);
        Assert.Throws<ArgumentException>(() => next.Width);
        Assert.Empty(ring.DrainCopies());
    }
}
