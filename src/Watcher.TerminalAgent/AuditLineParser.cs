using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Watcher.TerminalAgent;

public enum AuditLineType { Tty, UserStart, UserEnd, UserLogin, Syscall, Execve, Cwd, Other }

// Tty — "major:minor" терминала, с которого прочитан ввод (только у TTY);
// LoginTty — то же для терминала входа из USER_LOGIN ("terminal=/dev/pts/0" →
// "136:0"). Задача 17 (roadmap-fixes.md): по ним SessionCollector отделяет ввод
// человека от его копии во вложенном pty (script, tmux).
public sealed record AuditLine(AuditLineType Type, int Ses, double TimestampEpoch, string? Acct, string? DataHex, string? Addr, string? Tty = null, string? LoginTty = null)
{
    // 0.12.2, команды без терминала: номер события (msg=audit(ts:SERIAL)) —
    // SYSCALL, EXECVE и CWD одного запуска связаны только им.
    public long Serial { get; init; }
    // SYSCALL: терминал процесса ("(none)" — без терминала); USER_START: exe
    // процесса, открывшего сессию (/usr/sbin/sshd, /usr/sbin/cron).
    public string? Terminal { get; init; }
    public string? Exe { get; init; }
    // SYSCALL: uid процесса и auid — кто вошёл в систему (0.12.2).
    public long? Uid { get; init; }
    public long? Auid { get; init; }
    // EXECVE: argv; CWD: каталог.
    public IReadOnlyList<string>? Argv { get; init; }
    public string? Cwd { get; init; }
}

// Разбор строк /var/log/audit/audit.log (формат auditd, см. roadmap.md,
// Фаза 8). Debian-сборка auditd пишет "ENRICHED" лог — человеко-читаемые
// UID="..."/AUID="..." дописываются В КОНЕЦ той же строки БЕЗ пробела перед
// ними (воспроизведено живьём на watcher-test-1: "...data=6563686Fh...0D
// UID=\"watcher-term-test\"" — без пробела между hex-данными и "UID").
// Поэтому data=([0-9A-Fa-f]+) работает только благодаря тому, что 'U' не
// входит в hex-алфавит — это НЕ случайность, на неё и рассчитываем, но
// именно поэтому regex ограничен классом символов, а не "до конца строки".
public static class AuditLineParser
{
    private static readonly Regex TypeRegex = new(@"type=(\S+)", RegexOptions.Compiled);
    private static readonly Regex TimestampRegex = new(@"audit\(([\d.]+):\d+\)", RegexOptions.Compiled);
    private static readonly Regex SesRegex = new(@"\bses=(\d+)\b", RegexOptions.Compiled);
    private static readonly Regex AcctRegex = new(@"acct=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex DataRegex = new(@"data=([0-9A-Fa-f]+)", RegexOptions.Compiled);
    private static readonly Regex AddrRegex = new(@"\baddr=(\S+)", RegexOptions.Compiled);
    private static readonly Regex MajorMinorRegex = new(@"\bmajor=(\d+) minor=(\d+)\b", RegexOptions.Compiled);
    private static readonly Regex TerminalRegex = new(@"\bterminal=([^\s']+)", RegexOptions.Compiled);
    private static readonly Regex SerialRegex = new(@"audit\([\d.]+:(\d+)\)", RegexOptions.Compiled);
    private static readonly Regex KeyRegex = new(@"\bkey=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex SyscallTtyRegex = new(@"\btty=(\S+)", RegexOptions.Compiled);
    private static readonly Regex ExeRegex = new(@"\bexe=(""[^""]*""|[0-9A-Fa-f]+)", RegexOptions.Compiled);
    private static readonly Regex ArgRegex = new(@"\ba(\d+)(?:\[(\d+)\])?=(""[^""]*""|[0-9A-Fa-f]+)", RegexOptions.Compiled);
    private static readonly Regex CwdRegex = new(@"\bcwd=(""[^""]*""|[0-9A-Fa-f]+)", RegexOptions.Compiled);

    // Ключ правила auditd, которое ставит install-terminal-agent.sh.
    public const string ExecKey = "watcher-exec";
    private static readonly Regex UidRegex = new(@"\buid=(\d+)", RegexOptions.Compiled);
    private static readonly Regex AuidRegex = new(@"\bauid=(\d+)", RegexOptions.Compiled);

    private static long? Number(string? s) => long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out long v) ? v : null;

    public static AuditLine? TryParse(string line)
    {
        var typeMatch = TypeRegex.Match(line);
        if (!typeMatch.Success) return null;

        AuditLineType type = typeMatch.Groups[1].Value switch
        {
            "TTY" => AuditLineType.Tty,
            "USER_START" => AuditLineType.UserStart,
            "USER_END" => AuditLineType.UserEnd,
            "USER_LOGIN" => AuditLineType.UserLogin,
            "SYSCALL" => AuditLineType.Syscall,
            "EXECVE" => AuditLineType.Execve,
            "CWD" => AuditLineType.Cwd,
            _ => AuditLineType.Other,
        };
        if (type == AuditLineType.Other) return null;

        // EXECVE и CWD без ses= — привязываются к SYSCALL того же события по номеру.
        long serial = 0;
        var serialMatch = SerialRegex.Match(line);
        if (serialMatch.Success) long.TryParse(serialMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out serial);
        if (type is AuditLineType.Execve or AuditLineType.Cwd)
        {
            double ets = 0;
            var etsMatch = TimestampRegex.Match(line);
            if (etsMatch.Success) ets = double.Parse(etsMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            return type == AuditLineType.Execve
                ? new AuditLine(type, 0, ets, null, null, null) { Serial = serial, Argv = ParseArgv(line) }
                : new AuditLine(type, 0, ets, null, null, null) { Serial = serial, Cwd = Value(Extract(CwdRegex, line)) };
        }
        // SYSCALL — только запуски по нашему правилу, остальной аудит хоста не наш.
        if (type == AuditLineType.Syscall && Extract(KeyRegex, line) != ExecKey) return null;

        var sesMatch = SesRegex.Match(line);
        if (!sesMatch.Success) return null; // без ses= сгруппировать с остальными событиями сессии нельзя
        // ses=4294967295 (unset, (u32)-1) — у неудачных входов (USER_LOGIN
        // res=failed): сессии нет, группировать нечего. В int не влезает —
        // int.Parse бросал бы OverflowException на каждой ошибке пароля.
        if (!int.TryParse(sesMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int ses)) return null;

        double ts = 0;
        var tsMatch = TimestampRegex.Match(line);
        if (tsMatch.Success) ts = double.Parse(tsMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        string? acct = Extract(AcctRegex, line);
        string? dataHex = Extract(DataRegex, line);
        string? addr = Extract(AddrRegex, line);

        string? tty = null;
        string? loginTty = null;
        if (type == AuditLineType.Tty)
        {
            var mm = MajorMinorRegex.Match(line);
            if (mm.Success) tty = $"{mm.Groups[1].Value}:{mm.Groups[2].Value}";
        }
        else if (type == AuditLineType.UserLogin)
        {
            loginTty = TerminalToTty(Extract(TerminalRegex, line));
        }

        return new AuditLine(type, ses, ts, acct, dataHex, addr, tty, loginTty)
        {
            Serial = serial,
            Terminal = type == AuditLineType.Syscall ? Extract(SyscallTtyRegex, line) : null,
            Exe = type is AuditLineType.UserStart or AuditLineType.UserEnd or AuditLineType.Syscall ? Value(Extract(ExeRegex, line)) : null,
            Uid = type == AuditLineType.Syscall ? Number(Extract(UidRegex, line)) : null,
            Auid = type == AuditLineType.Syscall ? Number(Extract(AuidRegex, line)) : null,
        };
    }

    // a0="ls" — как есть; a1=2F746D70 — hex (аргумент с пробелом, кавычкой,
    // не-ASCII); длинный аргумент частями: a2_len=N a2[0]=… a2[1]=….
    private static List<string> ParseArgv(string line)
    {
        var parts = new SortedDictionary<int, SortedDictionary<int, string>>();
        foreach (Match m in ArgRegex.Matches(line))
        {
            int idx = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int part = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            if (!parts.TryGetValue(idx, out var p)) parts[idx] = p = new SortedDictionary<int, string>();
            p[part] = Value(m.Groups[3].Value) ?? "";
        }
        return parts.Values.Select(p => string.Concat(p.Values)).ToList();
    }

    // "текст" → текст; иначе hex → текст.
    private static string? Value(string? raw)
    {
        if (raw is null) return null;
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"') return raw[1..^1];
        try { return DecodeHex(raw); }
        catch (FormatException) { return raw; }
        catch (ArgumentOutOfRangeException) { return raw; }
    }

    // terminal= из USER_LOGIN → "major:minor", как в TTY-записях. Нумерация
    // устройств Linux (Documentation/admin-guide/devices.txt): /dev/pts/N —
    // Unix98 PTY slave, major 136..143, N = (major-136)*256 + minor; /dev/ttyN —
    // виртуальная консоль, major 4, minor N; /dev/ttySN — последовательный
    // порт, major 4, minor 64+N. Остальное (terminal=ssh, "?", cron) — null:
    // терминал входа неизвестен.
    public static string? TerminalToTty(string? terminal)
    {
        if (string.IsNullOrEmpty(terminal)) return null;
        string t = terminal.StartsWith("/dev/", StringComparison.Ordinal) ? terminal[5..] : terminal;
        if (t.StartsWith("pts/", StringComparison.Ordinal) && int.TryParse(t[4..], NumberStyles.None, CultureInfo.InvariantCulture, out int pts))
            return $"{136 + pts / 256}:{pts % 256}";
        if (t.StartsWith("ttyS", StringComparison.Ordinal) && int.TryParse(t[4..], NumberStyles.None, CultureInfo.InvariantCulture, out int serial))
            return $"4:{64 + serial}";
        if (t.StartsWith("tty", StringComparison.Ordinal) && int.TryParse(t[3..], NumberStyles.None, CultureInfo.InvariantCulture, out int vt))
            return $"4:{vt}";
        return null;
    }

    private static string? Extract(Regex re, string line)
    {
        var m = re.Match(line);
        return m.Success ? m.Groups[1].Value : null;
    }

    // Lossy по дизайну (см. AuditLineParser.cs вверху файла и roadmap.md) —
    // нажатия функциональных/спецклавиш (стрелки, Ctrl+код) дают байты,
    // которые не всегда валидный UTF-8; для текстового аудита ввода это
    // приемлемо, не претендует на побайтовую точность терминального
    // протокола.
    public static string DecodeHex(string hex)
    {
        int len = hex.Length / 2;
        var bytes = new byte[len];
        for (int i = 0; i < len; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return Encoding.UTF8.GetString(bytes);
    }
}
