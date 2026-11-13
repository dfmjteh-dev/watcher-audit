namespace Watcher.SessionService;

// 0.16: транскрипт PowerShell выгружается всегда, с метками для сервера.
// Происхождение — по цепочке родителей из 4688 (SshAncestry) или по учётке
// процесса; не определено — "unknown", запись всё равно выгружается (раньше
// такой транскрипт через сутки становился потерей transcript-no-4688).
// Режим — по командной строке хоста PowerShell: интерактивная консоль или
// одна команда/скрипт (ssh host "cmd", -EncodedCommand, -File, задача).
public static class TranscriptKind
{
    public const string Ssh = "ssh";
    public const string Automation = "automation";   // планировщик, службы
    public const string SqlAgent = "sql-agent";
    public const string Local = "local";             // вход на рабочий стол (explorer)
    public const string WinRm = "winrm";             // PowerShell Remoting
    public const string Unknown = "unknown";

    public const string Interactive = "interactive";
    public const string Exec = "exec";

    // Учётки служб и машин: по SSH под ними не входят, значит, это автоматизация.
    // В заголовке транскрипта SYSTEM выглядит как «WORKGROUP\СИСТЕМА» (домен
    // машины + локализованное имя, проверено на стенде), поэтому решает SID:
    // sidOf — перевод имени в SID средствами Windows (TerminalAudit), null — не
    // удалось. Без SID — только то, что видно из самой строки.
    public static bool IsServiceAccount(string? runAs, Func<string, string?>? sidOf = null)
    {
        if (string.IsNullOrWhiteSpace(runAs)) return false;
        string account = runAs.Trim();
        int slash = account.LastIndexOf('\\');
        string domain = slash > 0 ? account[..slash] : "";
        string name = slash >= 0 ? account[(slash + 1)..] : account;
        if (domain.Equals("NT AUTHORITY", StringComparison.OrdinalIgnoreCase) || name.EndsWith('$')) return true;
        if (sidOf is null) return false;
        string? sid = sidOf(account) ?? (name.Length > 0 ? sidOf(name) : null);
        return IsServiceSid(sid);
    }

    // SYSTEM, LOCAL SERVICE, NETWORK SERVICE, виртуальные учётки служб и пулов IIS.
    public static bool IsServiceSid(string? sid) => sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20"
        || sid?.StartsWith("S-1-5-80-", StringComparison.Ordinal) == true
        || sid?.StartsWith("S-1-5-82-", StringComparison.Ordinal) == true;

    // Параметры powershell.exe/pwsh.exe, после которых идёт значение (не команда).
    private static readonly string[] WithValue =
    [
        "executionpolicy", "windowstyle", "inputformat", "outputformat", "configurationname",
        "version", "psconsolefile", "settingsfile", "workingdirectory", "custompipename",
    ];

    // null — командной строки нет (старый заголовок), режим не утверждаем.
    public static string? Mode(string? hostApplication)
    {
        if (string.IsNullOrWhiteSpace(hostApplication)) return null;
        var tokens = Tokens(hostApplication);
        if (tokens.Count == 0) return null;
        bool exec = false, noExit = false;
        for (int i = 1; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (token.Length < 2 || token[0] is not ('-' or '/'))
            {
                exec = true;            // позиционный аргумент — команда (pwsh — файл скрипта)
                break;
            }
            string name = token.TrimStart('-', '/').ToLowerInvariant();
            if (name.Length == 0) continue;
            if (name is "c" or "e" or "ec" or "f" || Abbrev(name, "command") || Abbrev(name, "encodedcommand") || Abbrev(name, "file"))
            {
                exec = true;
                break;
            }
            if (name.Length >= 3 && "noexit".StartsWith(name, StringComparison.Ordinal)) { noExit = true; continue; }
            if (name is "ex" or "ep" or "w" or "wd" || WithValue.Any(v => Abbrev(name, v))) i++;
        }
        return exec && !noExit ? Exec : Interactive;
    }

    private static bool Abbrev(string name, string full) => name.Length >= 2 && full.StartsWith(name, StringComparison.Ordinal);

    // Разбор командной строки Windows: пробелы разделяют, кавычки группируют.
    private static List<string> Tokens(string commandLine)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (char ch in commandLine)
        {
            if (ch == '"') { quoted = !quoted; any = true; continue; }
            if (!quoted && char.IsWhiteSpace(ch))
            {
                if (any) { result.Add(current.ToString()); current.Clear(); any = false; }
                continue;
            }
            current.Append(ch);
            any = true;
        }
        if (any) result.Add(current.ToString());
        return result;
    }
}
