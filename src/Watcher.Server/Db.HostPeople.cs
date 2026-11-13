using System.Diagnostics.CodeAnalysis;

namespace Watcher.Server;

// 0.13 (п.4 обратной связи 2026-10-14): кто и что на хосте — для «Инфо» и для
// выбора в конфиге (кого писать, какие программы) вместо ручного ввода.
public sealed record HostUser(string User, int Segments, DateTime? LastVideo, int TerminalSessions, DateTime? LastConsole);

public sealed record HostApp(string Process, double Seconds, DateTime LastSeen);

public sealed partial class Db
{
    public List<HostUser> HostUsers(string host)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
WITH v AS (SELECT session_user u, COUNT(*) n, MAX(started_at) last FROM segments
           WHERE host = $h AND session_user IS NOT NULL AND session_user <> '' GROUP BY session_user),
     t AS (SELECT session_user u, COUNT(*) n, MAX(started_at) last FROM terminal_sessions
           WHERE host = $h AND session_user NOT IN ('', 'unknown') AND parent_id IS NULL GROUP BY session_user),
     us AS (SELECT u FROM v UNION SELECT u FROM t)
SELECT us.u, COALESCE(v.n, 0), v.last, COALESCE(t.n, 0), t.last
FROM us LEFT JOIN v ON v.u = us.u LEFT JOIN t ON t.u = us.u
ORDER BY MAX(COALESCE(v.last, ''), COALESCE(t.last, '')) DESC
LIMIT 500";
        cmd.Parameters.AddWithValue("$h", host);
        var list = new List<HostUser>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new HostUser(r.GetString(0), r.GetInt32(1), r.IsDBNull(2) ? null : ParseUtc(r.GetString(2)),
                r.GetInt32(3), r.IsDBNull(4) ? null : ParseUtc(r.GetString(4))));
        return list;
    }

    // Время на переднем плане по программам: у каждой смены окна — до следующей
    // смены в том же сегменте, у последней — до конца сегмента (журнал активных
    // окон H9; при activity=off процесса нет — такие записи не считаются).
    public List<HostApp> HostApps(string host, DateTime sinceUtc, int limit = 50)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
WITH a AS (
  SELECT a.process, a.ts,
         COALESCE(LEAD(a.offset_sec) OVER (PARTITION BY a.segment_id ORDER BY a.offset_sec), s.duration_sec) - a.offset_sec AS dur
  FROM segment_activity a JOIN segments s ON s.id = a.segment_id
  WHERE a.host = $h AND a.ts >= $since
)
SELECT process, SUM(MAX(dur, 0)), MAX(ts) FROM a
WHERE process IS NOT NULL AND process <> ''
GROUP BY process ORDER BY 2 DESC LIMIT $lim";
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$lim", limit);
        var list = new List<HostApp>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new HostApp(r.GetString(0), r.GetDouble(1), ParseUtc(r.GetString(2))));
        return list;
    }

    // Подсказки для конфига хоста или группы: пользователи и программы, которые
    // встречались на этих хостах (за 90 дней), по убыванию частоты.
    [SuppressMessage("Security", "CA2100", Justification = "IN-список — только имена параметров (AddInParams); значения — через Parameters.")]
    public (List<string> Users, List<string> Processes, List<string> Titles, List<string> Sites) KnownForHosts(IReadOnlyCollection<string> hosts)
    {
        if (hosts.Count == 0) return (new(), new(), new(), new());
        using var c = Open();
        using var cmd = c.CreateCommand();
        string inHosts = AddInParams(cmd, "kh", hosts);
        cmd.Parameters.AddWithValue("$since", DateTime.UtcNow.AddDays(-90).ToString("o"));
        cmd.CommandText = $@"
SELECT 'u', u FROM (
  SELECT session_user u, COUNT(*) n FROM segments WHERE host IN ({inHosts}) AND session_user IS NOT NULL AND session_user <> '' AND started_at >= $since GROUP BY u
  UNION ALL
  SELECT session_user u, COUNT(*) n FROM terminal_sessions WHERE host IN ({inHosts}) AND session_user NOT IN ('', 'unknown') AND started_at >= $since GROUP BY u
) GROUP BY u ORDER BY SUM(n) DESC LIMIT 300;";
        var users = new List<string>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) users.Add(r.GetString(1));
        cmd.CommandText = $@"
SELECT process FROM segment_activity WHERE host IN ({inHosts}) AND ts >= $since AND process IS NOT NULL AND process <> ''
GROUP BY process ORDER BY COUNT(*) DESC LIMIT 300";
        var processes = new List<string>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) processes.Add(r.GetString(0));
        // 0.13 (проверка RC): заголовки окон — подсказки для «пауза/запись по
        // заголовку», как программы. 0.14.3: только у программ, где заголовок что-то
        // значит (браузер — страница, документ — файл, RDP — сервер, AppKinds);
        // у 1С и кассы — нет. Варианты с разными цифрами — одна подсказка: кусок
        // без цифр, он есть в заголовке в любой день. С запасом — сливаются.
        cmd.CommandText = $@"
SELECT process, title, COUNT(*), MAX(site IS NOT NULL) FROM segment_activity WHERE host IN ({inHosts}) AND ts >= $since AND title IS NOT NULL AND title <> ''
GROUP BY process, title ORDER BY COUNT(*) DESC LIMIT 5000";
        var kinds = AppKinds.Load(this);
        var subjects = new List<(string, double)>();
        var targets = new List<(string, double)>();   // RDP: адреса не сливаем по цифрам
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                string? process = r.IsDBNull(0) ? null : r.GetString(0);
                var kind = kinds.KindOf(process, r.GetInt64(3) == 1);
                if (kind is AppKind.App or AppKind.Ignore) continue;
                if (AppKinds.Subject(kind, process, r.GetString(1), null, forTitleRule: true) is { } s)
                    (AppKinds.DigitsAreNoise(kind, null) ? subjects : targets).Add((s, r.GetInt64(2)));
            }
        var titles = WindowTitles.Group(subjects).Concat(WindowTitles.Group(targets, maskDigits: false))
            .OrderByDescending(g => g.Weight).Select(g => g.Display)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(300).ToList();
        // 0.14.3: домены вкладок — подсказки для правил по сайтам.
        cmd.CommandText = $@"
SELECT site FROM segment_activity WHERE host IN ({inHosts}) AND ts >= $since AND site IS NOT NULL AND site <> ''
GROUP BY site ORDER BY COUNT(*) DESC LIMIT 300";
        var sites = new List<string>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) sites.Add(r.GetString(0));
        return (users, processes, titles, sites);
    }
}
