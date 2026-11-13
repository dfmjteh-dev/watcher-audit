using System.Buffers.Binary;
using System.Text;
using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

public sealed class Mp4ProbeTests
{
    internal static byte[] Box(string type, int payload) => Box(type, new byte[payload]);

    internal static byte[] Box(string type, byte[] payload)
    {
        var b = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        Encoding.ASCII.GetBytes(type, b.AsSpan(4, 4));
        payload.CopyTo(b, 8);
        return b;
    }

    // ftyp, который пишет ffmpeg для mp4: isom, minor 0x200, совместимость isom/iso2/mp41 — 28 Б.
    internal static byte[] Ftyp() => Box("ftyp", Encoding.ASCII.GetBytes("isom\0\0\u0002\0isomiso2mp41"));

    private static bool? Probe(params byte[][] parts) => Mp4Probe.HasMedia(new MemoryStream(parts.SelectMany(p => p).ToArray()));

    [Fact]
    public void Header_only_is_empty()
    {
        Assert.Equal(28, Ftyp().Length);
        Assert.False(Probe(Ftyp()));
        Assert.False(Probe(Ftyp(), Box("moov", 600)));
    }

    [Fact]
    public void Fragment_or_mdat_with_data_has_media()
    {
        Assert.True(Probe(Ftyp(), Box("moov", 600), Box("moof", 100), Box("mdat", 5000)));
        Assert.True(Probe(Ftyp(), Box("mdat", 5000)));
    }

    // Оборванный ffmpeg: mdat объявлен большим, на диске — только часть.
    [Fact]
    public void Truncated_mdat_with_some_data_has_media()
    {
        var mdat = Box("mdat", 5000);
        Assert.True(Probe(Ftyp(), mdat[..300]));
    }

    [Fact]
    public void Empty_mdat_is_empty()
    {
        Assert.False(Probe(Ftyp(), Box("free", 8), Box("mdat", 0)));
    }

    [Fact]
    public void Not_mp4_is_unknown()
    {
        Assert.Null(Probe(new byte[100]));
        Assert.Null(Probe(Array.Empty<byte>()));
        Assert.Null(Probe(Encoding.ASCII.GetBytes("not an mp4 file at all, just text")));
    }

    // 64-битный размер блока (largesize) — у длинных mdat.
    [Fact]
    public void Large_size_box_is_parsed()
    {
        var mdat = new byte[16 + 10];
        BinaryPrimitives.WriteUInt32BigEndian(mdat, 1);
        Encoding.ASCII.GetBytes("mdat", mdat.AsSpan(4, 4));
        BinaryPrimitives.WriteUInt64BigEndian(mdat.AsSpan(8), (ulong)mdat.Length);
        Assert.True(Probe(Ftyp(), mdat));
    }
}
