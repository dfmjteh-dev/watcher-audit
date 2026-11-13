using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Watcher.Server;

// 0.13 (п.7): «служебные подключения» — машина ходит на машину по SSH по
// расписанию (LLDP-сборщик раз в минуту, бот конфигов раз в 5 минут), и
// «Консоль» тонет в сотнях одинаковых сессий в сутки. Правило в конфиге
// группы/хоста (serviceConnections) описывает такое подключение: кто (user),
// откуда (from — IP или подсеть) и что запускает (command — начало каждой
// команды). Что делать с совпавшей сессией — решает само правило:
//   hide — запись сохраняется с пометкой terminal_sessions.service = 1, в
//          списке её не видно, пока не включить «служебные» (по умолчанию);
//   drop — сервер не сохраняет запись вовсе (как excludeUsers).
//
// Сессия совпадает, только если КАЖДАЯ её команда начинается с command:
// чужая команда под той же учёткой и с того же адреса снимает пометку —
// правило не прячет то, что в нём не описано.
public sealed record ServiceRule(string? User, string? From, string? Command, bool Drop, string? Note);

public static partial class ServiceConnections
{
    public const string Key = "serviceConnections";
    public const int MaxRules = 100;
    public const int MaxFieldLength = 256;
    private static readonly string[] RuleKeys = ["user", "from", "command", "action", "note"];

    public static List<ServiceRule> Rules(string? effectiveConfigJson)
    {
        var list = new List<ServiceRule>();
        if (string.IsNullOrEmpty(effectiveConfigJson)) return list;
        try
        {
            using var doc = JsonDocument.Parse(effectiveConfigJson);
            if (!doc.RootElement.TryGetProperty(Key, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var r in arr.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) continue;
                string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } s ? s : null;
                list.Add(new ServiceRule(S("user"), S("from"), S("command"), S("action") == "drop", S("note")));
            }
        }
        catch (JsonException) { }
        return list;
    }

    // Первое совпавшее правило (порядок — как в конфиге) или null. Без ввода
    // (нет ни одной команды) правило с command не совпадает.
    public static ServiceRule? Match(IReadOnlyList<ServiceRule> rules, string sessionUser, string? remoteAddr, string? input)
    {
        if (rules.Count == 0) return null;
        List<string>? commands = null;
        foreach (var rule in rules)
        {
            if (rule.User is null && rule.From is null) continue;   // Validate не пускает, но конфиг мог прийти до неё
            if (rule.User is not null && !TerminalUserFilter.UserMatches(rule.User, sessionUser)) continue;
            if (rule.From is not null && !AddrMatches(rule.From, remoteAddr)) continue;
            if (rule.Command is not null)
            {
                commands ??= Commands(input);
                if (commands.Count == 0 || !commands.All(c => c.StartsWith(rule.Command, StringComparison.Ordinal))) continue;
            }
            return rule;
        }
        return null;
    }

    // Строка linux-exec: «HH:mm:ssZ <cwd>$ <argv>» (SessionCollector.CommitExec);
    // ввод с терминала (pam_tty_audit) и Windows — строка как есть.
    [GeneratedRegex(@"^\d\d:\d\d:\d\dZ .*?\$ (.*)$")]
    private static partial Regex ExecLine();

    public static List<string> Commands(string? input)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(input)) return list;
        foreach (var raw in input.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var m = ExecLine().Match(raw);
            string cmd = (m.Success ? m.Groups[1].Value : raw).Trim();
            if (m.Success) cmd = UnwrapShell(cmd);
            if (cmd.Length > 0) list.Add(cmd);
        }
        return list;
    }

    // Команда по ssh запускается через оболочку пользователя: в записи linux-exec
    // это две строки — «bash -c 'git-upload-pack x'» и «git-upload-pack x».
    // Обёртка разворачивается в саму команду, иначе у таких сессий не было бы
    // общего начала команд и правило не подобрать (проверка RC 0.13).
    [GeneratedRegex(@"^(?:\S*/)?(?:bash|sh|dash|zsh|ksh) -c (.+)$")]
    private static partial Regex ShellWrapper();

    public static string UnwrapShell(string cmd)
    {
        var m = ShellWrapper().Match(cmd);
        return m.Success ? UnquoteFirst(m.Groups[1].Value) : cmd;
    }

    // Первый аргумент в записи SessionCollector.Quote: как есть или '…' с '\'' внутри.
    private static string UnquoteFirst(string s)
    {
        if (!s.StartsWith('\'')) { int sp = s.IndexOf(' '); return sp < 0 ? s : s[..sp]; }
        var sb = new System.Text.StringBuilder();
        int i = 1;
        while (i < s.Length)
        {
            if (s[i] == '\'')
            {
                if (s.AsSpan(i).StartsWith("'\\''")) { sb.Append('\''); i += 4; continue; }
                break;
            }
            sb.Append(s[i++]);
        }
        return sb.ToString();
    }

    // Подпись для свёртки одинаковых сессий в списке: команды без времени.
    public static string Signature(string? input) => string.Join("\n", Commands(input));

    public static bool AddrMatches(string from, string? remoteAddr)
    {
        if (remoteAddr is null || !IPAddress.TryParse(remoteAddr.Trim(), out var addr)) return false;
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        if (from.Contains('/'))
            return IPNetwork.TryParse(from, out var net) && net.Contains(addr);
        return IPAddress.TryParse(from, out var one) && (one.IsIPv4MappedToIPv6 ? one.MapToIPv4() : one).Equals(addr);
    }

    // Строго: IPAddress.TryParse принимает и «10.0.0» (= 10.0.0.0) — опечатка
    // превратилась бы в правило на чужой адрес.
    private static bool IsAddrOrSubnet(string from)
    {
        string addr = from.Split('/')[0];
        if (!addr.Contains(':') && addr.Split('.').Length != 4) return false;
        return from.Contains('/') ? IPNetwork.TryParse(from, out _) : IPAddress.TryParse(from, out _);
    }

    // Для AgentConfigValidation: null — корректно.
    public static string? Validate(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return $"{Key}: must be an array of {{user, from, command, action, note}}";
        if (value.GetArrayLength() > MaxRules)
            return $"{Key}: at most {MaxRules} rules";
        foreach (var r in value.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object)
                return $"{Key}: must be an array of {{user, from, command, action, note}}";
            foreach (var p in r.EnumerateObject())
            {
                if (!RuleKeys.Contains(p.Name)) return $"{Key}: unknown key '{p.Name}'";
                if (p.Value.ValueKind == JsonValueKind.Null) continue;
                if (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString()!.Length > MaxFieldLength || p.Value.GetString()!.Any(char.IsControl))
                    return $"{Key}: {p.Name} must be a string up to {MaxFieldLength} characters without control characters";
            }
            var rule = Rules($"{{\"{Key}\":[{r.GetRawText()}]}}").Single();
            if (rule.User is null && rule.From is null)
                return $"{Key}: each rule needs user or from — otherwise it would hide everyone";
            if (rule.From is not null && !IsAddrOrSubnet(rule.From))
                return $"{Key}: from must be an IP address or a subnet (10.20.30.0/24)";
            if (r.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() is not ("hide" or "drop"))
                return $"{Key}: action must be hide or drop";
        }
        return null;
    }
}

public static partial class ServiceConnections
{
    // Пометка — всегда по текущим правилам: после правки конфига группы/хоста
    // или смены группы хоста пересчитывается вся консоль затронутых хостов
    // (правило добавили — прошлые сессии скрываются, убрали — снова видны).
    // drop задним числом не удаляет: уже сохранённое только скрывается.
    public static int Reclassify(Db db, IEnumerable<string> hosts)
    {
        int changed = 0;
        foreach (string host in hosts.Distinct(StringComparer.Ordinal))
        {
            var rules = Rules(db.GetEffectiveConfig(host)?.ConfigJson);
            if (rules.Count == 0) { changed += db.ClearTerminalService(host); continue; }
            var on = new List<long>();
            var off = new List<long>();
            foreach (var row in db.TerminalInputsOfHost(host))
            {
                bool match = Match(rules, row.User, row.Addr, row.Input) is not null;
                if (match != row.Service) (match ? on : off).Add(row.Id);
            }
            db.SetTerminalService(on, true);
            db.SetTerminalService(off, false);
            changed += on.Count + off.Count;
        }
        return changed;
    }

    public static IEnumerable<string> HostsOfGroup(Db db, long groupId) =>
        db.Inventory().Where(h => h.GroupId == groupId).Select(h => h.Name);
}

public static partial class ServiceConnections
{
    // Конфиг хоста со своим ключом перекрывает список группы целиком (MergeConfig).
    public static bool HasOwnRules(string? configJson)
    {
        if (string.IsNullOrEmpty(configJson)) return false;
        return System.Text.Json.Nodes.JsonNode.Parse(configJson) is System.Text.Json.Nodes.JsonObject o && o[Key] is not null;
    }

    // Конфиг с дописанным в конец правилом (остальные ключи — как были).
    public static string WithRule(string? configJson, System.Text.Json.Nodes.JsonObject rule)
    {
        var obj = (string.IsNullOrEmpty(configJson) ? null : System.Text.Json.Nodes.JsonNode.Parse(configJson) as System.Text.Json.Nodes.JsonObject)
                  ?? new System.Text.Json.Nodes.JsonObject { ["schemaVersion"] = 1 };
        if (obj[Key] is not System.Text.Json.Nodes.JsonArray arr)
            obj[Key] = arr = new System.Text.Json.Nodes.JsonArray();
        arr.Add(rule);
        return obj.ToJsonString();
    }
}
