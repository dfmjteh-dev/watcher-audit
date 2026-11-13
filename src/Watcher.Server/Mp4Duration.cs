using System.Buffers.Binary;
using System.Text;

namespace Watcher.Server;

// Длительность mp4 из заголовка (moov/mvhd) — без запуска ffprobe. Пережатие
// (Transcode.cs) сверяет длительность до и после на каждом файле; два процесса
// ffprobe на файл — ~5% времени (замер на сегментах прода) и лишний бинарник
// в образе. null — не разобрали (фрагментированный mp4 без длительности в
// mvhd, битый файл) — запись уходит в failed (ffprobe в образе больше нет).
public static class Mp4Duration
{
    public static double? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
            return Read(fs);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static double? Read(Stream s)
    {
        // moov — на верхнем уровне, mvhd — первый блок внутри него (faststart:
        // moov в начале файла, без faststart — в конце; ищем по всему файлу).
        var moov = FindBox(s, 0, s.Length, "moov");
        if (moov is not { } m) return null;
        var mvhd = FindBox(s, m.Start, m.End, "mvhd");
        if (mvhd is not { } h) return null;
        Span<byte> b = stackalloc byte[32];
        s.Position = h.Start;
        int n = s.ReadAtLeast(b, 32, throwOnEndOfStream: false);
        if (n < 20) return null;
        byte version = b[0];
        uint timescale;
        ulong duration;
        if (version == 1)
        {
            if (n < 32) return null;
            timescale = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(20, 4));
            duration = BinaryPrimitives.ReadUInt64BigEndian(b.Slice(24, 8));
        }
        else
        {
            timescale = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(12, 4));
            duration = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(16, 4));
        }
        if (timescale == 0 || duration == 0 || duration == uint.MaxValue || duration == ulong.MaxValue) return null;
        return (double)duration / timescale;
    }

    // Первый блок type в [from, to): (начало содержимого, конец блока).
    private static (long Start, long End)? FindBox(Stream s, long from, long to, string type)
    {
        Span<byte> head = stackalloc byte[16];
        long pos = from;
        while (pos + 8 <= to)
        {
            s.Position = pos;
            int n = s.ReadAtLeast(head, 16, throwOnEndOfStream: false);
            if (n < 8) return null;
            long size = BinaryPrimitives.ReadUInt32BigEndian(head);
            int headerLen = 8;
            if (size == 1)
            {
                if (n < 16) return null;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(head.Slice(8, 8));
                headerLen = 16;
            }
            else if (size == 0)
            {
                size = to - pos;
            }
            if (size < headerLen || pos + size > to) return null;
            if (Encoding.ASCII.GetString(head.Slice(4, 4)) == type) return (pos + headerLen, pos + size);
            pos += size;
        }
        return null;
    }
}
