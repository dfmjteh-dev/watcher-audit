using System.Buffers.Binary;
using System.Text;

namespace Watcher.Uploader;

// Есть ли в mp4 хоть один кадр — по блокам верхнего уровня, без ffmpeg. Нужно,
// чтобы отличать потерю записи от пустого файла: Capture, убитый сразу после
// старта сегмента (выход из системы, смена пользователя), оставляет только
// заголовок (ftyp, иногда moov без сэмплов) — на проде 28 Б. Удалять такой файл
// надо, но считать потерей (droppedSegments, красный значок) — нет: терять нечего.
public static class Mp4Probe
{
    // true — есть данные кадров (moof фрагментированного mp4 или непустой mdat);
    // false — корректный заголовок mp4 без кадров; null — не похоже на mp4
    // (не разбирается) — решать не нам, пусть проверяет ffmpeg, как раньше.
    public static bool? HasMedia(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return HasMedia(fs);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static bool? HasMedia(Stream s)
    {
        long length = s.Length;
        Span<byte> head = stackalloc byte[16];
        long pos = 0;
        bool first = true;
        while (pos + 8 <= length)
        {
            s.Position = pos;
            int n = s.ReadAtLeast(head, 16, throwOnEndOfStream: false);
            if (n < 8) break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(head);
            string type = Encoding.ASCII.GetString(head.Slice(4, 4));
            int headerLen = 8;
            if (size == 1)
            {
                if (n < 16) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(head.Slice(8, 8));
                headerLen = 16;
            }
            else if (size == 0)
            {
                size = length - pos;   // блок до конца файла
            }
            if (first)
            {
                if (type != "ftyp") return null;
                first = false;
            }
            if (size < headerLen) return null;
            // Оборванный последний блок — считаем по тому, что успело лечь на диск.
            long payload = Math.Min(size, length - pos) - headerLen;
            if (type == "moof" && payload > 0) return true;
            if (type == "mdat" && payload > 0) return true;
            pos += size;
        }
        return first ? null : false;
    }
}
