using System.Globalization;
using System.Text.RegularExpressions;

namespace Watcher.SessionService;

// Задача F1 (roadmap-next.md): разбор транскрипта PowerShell (политика
// Transcription). Подписи в заголовке локализованы («Время начала:» /
// «Start time:», «ИД процесса:» / «Process ID:») — разбираем по ПОЗИЦИИ строк,
// не по меткам (спайк F0); PID — по строке перед нелокализуемой «PSVersion:»
// (ведущее приложение бывает многострочным). Блоки разделены строками из звёздочек:
//   заголовок  — название, время начала, пользователь, «запуск от имени»,
//                конфигурация, компьютер, ведущее приложение, PID, PSVersion…
//   команда    — одна строка «<метка>: yyyyMMddHHmmss», дальше сама команда
//                («PS C:\…> cmd» в интерактиве, «PS>cmd» в командном режиме) и вывод
//   конец      — название + «<метка>: yyyyMMddHHmmss»
// После Stop-Transcript политика начинает запись заново в тот же файл — второй
// заголовок пропускаем (F0).
public sealed record TranscriptInfo(
    DateTime StartedLocal,
    DateTime? EndedLocal,
    string User,
    string HostApplication,
    int ProcessId,
    string? PsVersion,
    string? PsEdition,
    IReadOnlyList<string> Commands,
    string? RunAsUser = null)
{
    public bool Completed => EndedLocal is not null;

    // DOMAIN\user → user, как sessionUser у Linux-агента.
    public string SessionUser => User.Contains('\\') ? User[(User.LastIndexOf('\\') + 1)..] : User;

    public string Shell => PsEdition == "Core"
        ? $"PowerShell {PsVersion}".Trim()
        : $"Windows PowerShell {PsVersion}".Trim();
}

public static class TranscriptParser
{
    private static readonly Regex Separator = new(@"^\*{10,}$", RegexOptions.CultureInvariant);
    private static readonly Regex Stamp = new(@"^[^:]+:\s*(\d{14})$", RegexOptions.CultureInvariant);

    // Служебные строки транскрипта, не команды пользователя: «$global:?» —
    // хвост командного режима (ssh host "cmd"), остальное — диагностика ошибок.
    private static readonly string[] NotCommands = { "$global:?" };

    public static TranscriptInfo? Parse(string text)
    {
        var lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Split('\n');
        var blocks = new List<(int Start, int End)>(); // строки между разделителями
        int prev = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!Separator.IsMatch(lines[i].Trim())) continue;
            if (prev >= 0) blocks.Add((prev + 1, i));
            prev = i;
        }
        if (prev >= 0) blocks.Add((prev + 1, lines.Length));

        DateTime? started = null, ended = null;
        string? user = null, runAs = null, hostApp = null, psVersion = null, psEdition = null;
        int pid = 0;
        var commands = new List<string>();
        bool expectCommand = false;

        foreach (var (start, end) in blocks)
        {
            var b = lines[start..end].Select(l => l.TrimEnd()).ToList();
            var meaningful = b.Where(l => l.Length > 0).ToList();

            // Заголовок: ≥ 8 строк, вторая — время начала.
            if (meaningful.Count >= 8 && Stamp.Match(meaningful[1]) is { Success: true } hs)
            {
                expectCommand = false;
                if (started is not null) continue; // повторный заголовок после Stop-Transcript
                started = ParseStamp(hs.Groups[1].Value);
                user = Value(meaningful[2]);
                // «Запуск от имени» — учётка процесса (у задач и служб — SYSTEM и т.п.).
                runAs = Value(meaningful[3]);
                // «Ведущее приложение» — полная командная строка: powershell -Command
                // с многострочным скриптом (CompatTelRunner, SCCM, ssh host "…")
                // занимает несколько строк, и PID на позиции [7] не оказывался —
                // PID 0, транскрипт не классифицировался и через сутки удалялся
                // как потеря transcript-no-4688. Метка PSVersion: не локализуется —
                // PID берём из строки перед ней, команду — все строки до PID.
                int psv = meaningful.FindIndex(7, l => l.StartsWith("PSVersion:", StringComparison.Ordinal));
                int pidAt = psv > 7 ? psv - 1 : 7;
                hostApp = string.Join("\n", meaningful.Skip(7).Take(pidAt - 7).Prepend(Value(meaningful[6])));
                int.TryParse(Value(meaningful[pidAt]), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
                foreach (var l in meaningful)
                {
                    if (l.StartsWith("PSVersion:", StringComparison.Ordinal)) psVersion = Value(l);
                    else if (l.StartsWith("PSEdition:", StringComparison.Ordinal)) psEdition = Value(l);
                }
                continue;
            }
            // Конец записи: название + время окончания.
            if (meaningful.Count == 2 && Stamp.Match(meaningful[1]) is { Success: true } fs)
            {
                ended = ParseStamp(fs.Groups[1].Value);
                expectCommand = false;
                continue;
            }
            // Начало команды: одна строка с временем; сама команда — первая строка
            // следующего блока.
            if (meaningful.Count == 1 && Stamp.IsMatch(meaningful[0]))
            {
                expectCommand = true;
                continue;
            }
            if (expectCommand)
            {
                expectCommand = false;
                if (CommandFrom(b) is { } cmd) commands.Add(cmd);
            }
        }

        if (started is null || user is null) return null;
        return new TranscriptInfo(started.Value, ended, user, hostApp ?? "", pid, psVersion, psEdition, commands, runAs);
    }

    // «PS C:\Users\x> whoami» / «PS>whoami» + продолжения «>> …» многострочной команды.
    private static string? CommandFrom(List<string> block)
    {
        int i = block.FindIndex(l => l.Length > 0);
        if (i < 0) return null;
        string first = block[i];
        if (!first.StartsWith("PS", StringComparison.Ordinal)) return null;
        int gt = first.IndexOf('>');
        if (gt < 0) return null;
        var parts = new List<string> { first[(gt + 1)..].TrimStart() };
        for (int j = i + 1; j < block.Count && block[j].StartsWith(">> ", StringComparison.Ordinal); j++)
            parts.Add(block[j][3..]);
        string cmd = string.Join("\n", parts).Trim();
        if (cmd.Length == 0 || NotCommands.Contains(cmd)) return null;
        // Диагностика PowerShell, записанная вместо команды (TerminatingError(…)).
        if (Regex.IsMatch(cmd, @"^[A-Za-z]+\(", RegexOptions.CultureInvariant) && cmd.Contains("):", StringComparison.Ordinal)) return null;
        return cmd;
    }

    private static string Value(string line)
    {
        int at = line.IndexOf(": ", StringComparison.Ordinal);
        if (at >= 0) return line[(at + 2)..].Trim();
        return line.EndsWith(':') ? "" : line.Trim();
    }

    private static DateTime ParseStamp(string s) =>
        DateTime.ParseExact(s, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
}
