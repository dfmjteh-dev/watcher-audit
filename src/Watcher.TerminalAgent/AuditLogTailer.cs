namespace Watcher.TerminalAgent;

// tail -F-подобное чтение audit.log с персистентным byte-offset (bookmark) —
// переживает перезапуск агента, не перечитывает файл с начала (он общий для
// всего хоста, может быть большим). Если файл стал короче, чем bookmark —
// считаем, что его повернули (logrotate/auditd `-ic`) и читаем с начала.
public sealed class AuditLogTailer
{
    private readonly string _logPath;
    private readonly string _bookmarkPath;

    public AuditLogTailer(string logPath, string bookmarkPath)
    {
        _logPath = logPath;
        _bookmarkPath = bookmarkPath;
    }

    // 0.12.2: закладка — «смещение inode». Раньше только смещение, и ротация
    // распознавалась по «файл стал короче закладки»: если новый audit.log успевал
    // дорасти дальше старого смещения (правило watcher-exec заметно увеличило
    // объём журнала), агент продолжал читать его с середины и терял начало, а
    // хвост старого файла пропадал. Теперь inode сменился — дочитываем хвост
    // повёрнутого файла (audit.log.1, если это он), потом новый с начала.
    public IReadOnlyList<string> ReadNewLines()
    {
        if (!File.Exists(_logPath)) return Array.Empty<string>();
        var (offset, inode) = ReadBookmark();
        ulong? current = UnixFile.Inode(_logPath);
        var lines = new List<string>();

        if (inode is not null && current is not null && inode != current)
        {
            string rotated = _logPath + ".1";
            if (UnixFile.Inode(rotated) == inode)
                lines.AddRange(ReadFrom(rotated, offset, out _));
            offset = 0;
        }
        else if (new FileInfo(_logPath).Length < offset)
        {
            offset = 0; // обрезан на месте (или закладка старого формата и ротация)
        }

        lines.AddRange(ReadFrom(_logPath, offset, out long next));
        WriteBookmark(next, current);
        return lines;
    }

    // Строки от offset до последнего '\n' — auditd может писать ещё не
    // завершённую строку в момент нашего опроса (классическая гонка tail -f);
    // незавершённый хвост остаётся непрочитанным и придёт целиком позже.
    private static IReadOnlyList<string> ReadFrom(string path, long offset, out long next)
    {
        next = offset;
        long length = new FileInfo(path).Length;
        if (length <= offset) return Array.Empty<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[length - offset];
        int read = stream.Read(buf, 0, buf.Length);
        if (read == 0) return Array.Empty<string>();
        int lastNewline = Array.LastIndexOf(buf, (byte)'\n', read - 1, read);
        if (lastNewline < 0) return Array.Empty<string>();
        next = offset + lastNewline + 1;
        return System.Text.Encoding.UTF8.GetString(buf, 0, lastNewline + 1).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private (long Offset, ulong? Inode) ReadBookmark()
    {
        try
        {
            if (!File.Exists(_bookmarkPath)) return (0, null);
            var parts = File.ReadAllText(_bookmarkPath).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long offset = parts.Length > 0 && long.TryParse(parts[0], out long o) ? o : 0;
            ulong? inode = parts.Length > 1 && ulong.TryParse(parts[1], out ulong i) ? i : null;
            return (offset, inode);
        }
        catch (IOException)
        {
            return (0, null);
        }
    }

    private void WriteBookmark(long offset, ulong? inode)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_bookmarkPath)!);
        File.WriteAllText(_bookmarkPath, inode is null ? offset.ToString() : $"{offset} {inode}");
    }
}
