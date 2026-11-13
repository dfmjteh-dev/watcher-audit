using Watcher.Shared;
using System.Text.RegularExpressions;

namespace Watcher.SessionService;

// Задача F1: чей транскрипт — SSH-сессии или нет. Транскрипция включается на
// всю машину и пишет любую PowerShell (RDP, службы, задачи, автоматизацию).
// До 0.16 выгружался только SSH; с 0.16 — всё, с меткой происхождения
// (Decision.Label, TranscriptKind), а что хранить, решает сервер по настройке
// группы (у автоматизации в выводе бывают секреты). Признак — по событиям
// создания процессов (4688), проверено в спайке F0:
//   ssh host "команда" — powershell.exe, родитель sshd.exe;
//   интерактивная     — powershell.exe, родитель conhost.exe, у него — sshd.exe.
// sshd*.exe — и sshd.exe, и sshd-session.exe новых версий OpenSSH.
public sealed record ProcessStart(DateTime TimeUtc, int NewPid, string NewName, int CreatorPid, string ParentName);

public sealed record SshAccept(DateTime TimeUtc, int SshdPid, string User, string Address);

public enum SessionOrigin { Ssh, NotSsh, Unknown }

public static class SshAncestry
{
    // Окно поиска процесса относительно времени начала транскрипта: заголовок
    // пишется сразу после старта PowerShell, время — с точностью до секунды.
    public static readonly TimeSpan Before = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan After = TimeSpan.FromSeconds(30);

    // Label — происхождение для сервера (TranscriptKind): ssh, automation, local…
    public sealed record Decision(SessionOrigin Origin, int SshdPid, string? Reason, string Label = TranscriptKind.Unknown);

    public static IReadOnlyDictionary<int, IReadOnlyList<ProcessStart>> Index(IEnumerable<ProcessStart> events) =>
        events.GroupBy(e => e.NewPid).ToDictionary(g => g.Key, g => (IReadOnlyList<ProcessStart>)g.OrderByDescending(e => e.TimeUtc).ToList());

    public static (SessionOrigin Origin, int SshdPid) Classify(int pid, DateTime startUtc, IReadOnlyList<ProcessStart> events)
    {
        var result = ClassifyDetailed(pid, startUtc, Index(events), null);
        return (result.Origin, result.SshdPid);
    }

    // Новому хосту PowerShell не нужна правка списка имён. Для него требуется
    // полный путь из заголовка, совпавший с 4688 по PID, времени и образу.
    public static Decision ClassifyDetailed(int pid, DateTime startUtc,
        IReadOnlyDictionary<int, IReadOnlyList<ProcessStart>> events, string? hostApplication)
    {
        if (pid <= 0) return Unknown(TranscriptIssue.InvalidTranscript);
        var process = events.GetValueOrDefault(pid)?.FirstOrDefault(e => e.TimeUtc >= startUtc - Before && e.TimeUtc <= startUtc + After);
        if (process is null) return Unknown(TranscriptIssue.EventMissing);
        // После начала транскрипта PID уже мог перейти другому экземпляру
        // того же exe. При нескольких кандидатах такой случай неоднозначен.
        if (process.TimeUtc > startUtc.AddSeconds(1) && events[pid].Any(e => e != process
            && e.TimeUtc >= startUtc - Before && e.TimeUtc <= startUtc + After))
            return Unknown(TranscriptIssue.ProcessMismatch);
        // Берём самое новое событие PID, а не первое с подходящим именем:
        // иначе можно приписать транскрипт прежнему владельцу повторного PID.
        if (!string.IsNullOrWhiteSpace(hostApplication))
        {
            if (!MatchesHost(process.NewName, hostApplication)) return Unknown(TranscriptIssue.ProcessMismatch);
        }
        else if (!IsPowerShell(process.NewName)) return Unknown(TranscriptIssue.HostUnconfirmed);
        // PowerShell Remoting: хост сессии — сам wsmprovhost.exe (запускает его svchost WinRM).
        if (FileName(process.NewName).Equals("wsmprovhost.exe", StringComparison.OrdinalIgnoreCase))
            return new(SessionOrigin.NotSsh, 0, null, TranscriptKind.WinRm);
        var seen = new HashSet<int>();
        for (int depth = 0; depth < 16; depth++)
        {
            if (!seen.Add(process.NewPid)) return Unknown(TranscriptIssue.AncestryCycle);
            if (IsSshd(process.ParentName))
                return process.CreatorPid > 0 ? new(SessionOrigin.Ssh, process.CreatorPid, null, TranscriptKind.Ssh) : Unknown(TranscriptIssue.AncestryIncomplete);
            // Проверенные границы локальной автоматизации/интерактивного входа.
            // Неизвестный посредник не означает NotSsh: пробуем его родителей.
            if (BoundaryLabel(process.ParentName) is { } label) return new(SessionOrigin.NotSsh, 0, null, label);
            var parent = events.GetValueOrDefault(process.CreatorPid)?.FirstOrDefault(e => e.TimeUtc <= process.TimeUtc);
            if (parent is null || !SameImage(parent.NewName, process.ParentName)) return Unknown(TranscriptIssue.AncestryIncomplete);
            if (FileName(parent.NewName).Equals("conhost.exe", StringComparison.OrdinalIgnoreCase)
                && parent.TimeUtc < process.TimeUtc - TimeSpan.FromMinutes(1)) return Unknown(TranscriptIssue.AncestryIncomplete);
            process = parent;
        }
        return Unknown(TranscriptIssue.AncestryIncomplete);
    }

    private static Decision Unknown(string reason) => new(SessionOrigin.Unknown, 0, reason);

    private static bool HasDirectory(string path) => path.Contains('\\') || path.Contains('/');
    private static bool SameImage(string image, string parent) => !string.IsNullOrEmpty(parent)
        && (HasDirectory(parent) ? image.Equals(parent, StringComparison.OrdinalIgnoreCase)
            : FileName(image).Equals(parent, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesHost(string image, string application)
    {
        string command = application.TrimStart();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end <= 1) return false;
            string executable = command[1..end];
            return HasDirectory(executable) ? image.Equals(executable, StringComparison.OrdinalIgnoreCase)
                : IsPowerShell(image) && FileName(image).Equals(executable, StringComparison.OrdinalIgnoreCase);
        }
        // Не режем по пробелу: у незаключённого в кавычки пути тоже бывают пробелы.
        if (HasDirectory(image) && PrefixExecutable(command, image)) return true;
        return IsPowerShell(image) && PrefixExecutable(command, FileName(image));
    }

    private static bool PrefixExecutable(string command, string image) => command.StartsWith(image, StringComparison.OrdinalIgnoreCase)
        && (command.Length == image.Length || char.IsWhiteSpace(command[image.Length]));

    // svchost — и планировщик заданий (Schedule), и другие службы: различить их
    // по 4688 нельзя, поэтому одна метка «автоматизация».
    private static string? BoundaryLabel(string path) => FileName(path).ToLowerInvariant() switch
    {
        "sqlagent.exe" => TranscriptKind.SqlAgent,
        "explorer.exe" => TranscriptKind.Local,
        "services.exe" or "svchost.exe" or "taskeng.exe" or "taskhostw.exe" => TranscriptKind.Automation,
        _ => null,
    };

    // Адрес клиента — из OpenSSH/Operational («Accepted publickey for … from …
    // port …»). Точное совпадение — событие того же процесса sshd; иначе
    // последний вход этого пользователя перед началом сессии.
    public static string? RemoteAddress(string sessionUser, int sshdPid, DateTime startUtc, IReadOnlyList<SshAccept> accepts)
    {
        var window = accepts.Where(a => a.TimeUtc <= startUtc + After && a.TimeUtc >= startUtc - TimeSpan.FromMinutes(2)).ToList();
        var exact = window.FirstOrDefault(a => a.SshdPid == sshdPid && sshdPid != 0);
        if (exact is not null) return exact.Address;
        return window
            .Where(a => UserPart(a.User).Equals(sessionUser, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.TimeUtc)
            .FirstOrDefault()?.Address;
    }

    private static readonly Regex AcceptRx = new(@"Accepted \S+ for (\S+) from (\S+) port \d+", RegexOptions.CultureInvariant);

    public static SshAccept? ParseAccept(DateTime timeUtc, int pid, string message)
    {
        var m = AcceptRx.Match(message);
        return m.Success ? new SshAccept(timeUtc, pid, m.Groups[1].Value, m.Groups[2].Value) : null;
    }

    private static bool IsPowerShell(string path) =>
        FileName(path) is var n && (n.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) || n.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)
            || n.Equals("SQLPS.exe", StringComparison.OrdinalIgnoreCase));

    private static bool IsSshd(string path) =>
        FileName(path).ToLowerInvariant() is "sshd.exe" or "sshd-session.exe";

    private static string FileName(string path) =>
        path.Contains('\\') ? path[(path.LastIndexOf('\\') + 1)..] : path;

    private static string UserPart(string user) =>
        user.Contains('\\') ? user[(user.LastIndexOf('\\') + 1)..] : user;
}
