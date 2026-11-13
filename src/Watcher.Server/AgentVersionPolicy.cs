namespace Watcher.Server;

// Версии агента и сервера раздельные (Directory.Build.props: AgentVersion,
// ServerVersion) — «агент устарел» считается не от версии сервера, а от того,
// что сервер ему может дать: целевая версия группы (её агент получает в
// heartbeat, UpdateEndpoints.OfferFor), а без неё — последний загруженный пакет
// для платформы агента. Собирается на запрос: пара запросов к маленьким таблицам.
public sealed class AgentVersionPolicy
{
    // current — актуальна; pending — группе назначена версия новее, агент ещё не
    // обновился (это и есть «устарел»: на «Обзоре», в /metrics); available — группе
    // ничего не назначено, но загружен пакет новее (подсказка, не проблема);
    // ahead — агент новее назначенной группе; unknown — версия не известна.
    public sealed record State(string Status, string? Target, string? Latest);

    private readonly Dictionary<long, string> _groupTargets;
    private readonly Dictionary<string, string> _latestByPlatform;
    private readonly Dictionary<string, string?> _hostPlatform;
    private readonly string? _latestAny;

    public AgentVersionPolicy(IEnumerable<(long Id, string? TargetVersion)> groupTargets,
        IEnumerable<(string Platform, string Version)> packages, IEnumerable<(string Host, string? Platform)> hostPlatforms)
    {
        _groupTargets = groupTargets.Where(g => !string.IsNullOrEmpty(g.TargetVersion)).ToDictionary(g => g.Id, g => g.TargetVersion!);
        _latestByPlatform = packages.GroupBy(p => p.Platform).ToDictionary(g => g.Key, g => g.Select(p => p.Version).MaxBy(ParseOrZero)!);
        _latestAny = packages.Select(p => p.Version).MaxBy(ParseOrZero);
        _hostPlatform = hostPlatforms.GroupBy(h => h.Host).ToDictionary(g => g.Key, g => g.First().Platform);
    }

    public static AgentVersionPolicy Load(Db db) => new(
        db.GroupTargets().Select(g => (g.Id, g.TargetVersion)),
        db.ListAgentPackages().Select(p => (p.Platform, p.Version)),
        db.HostUpdateStates().Select(h => (h.Name, h.Platform)));

    // Последняя загруженная версия агентов (любая платформа) — для «Обновлений».
    public string? LatestAgentVersion => _latestAny;

    public State For(string host, long? groupId, string? agentVersion)
    {
        string? latest = _hostPlatform.GetValueOrDefault(host) is { } p && _latestByPlatform.TryGetValue(p, out var lp) ? lp : _latestAny;
        if (string.IsNullOrEmpty(agentVersion)) return new("unknown", null, latest);
        if (groupId is { } g && _groupTargets.TryGetValue(g, out var target))
        {
            if (AppVersion.IsAgentOutdated(agentVersion, target)) return new("pending", target, latest);
            if (AppVersion.IsAgentOutdated(target, agentVersion)) return new("ahead", target, latest);
            return new("current", target, latest);
        }
        return latest is not null && AppVersion.IsAgentOutdated(agentVersion, latest)
            ? new("available", null, latest)
            : new("current", null, latest);
    }

    public bool IsOutdated(string host, long? groupId, string? agentVersion) => For(host, groupId, agentVersion).Status == "pending";

    private static Version ParseOrZero(string v) => Version.TryParse(AppVersion.Core(v), out var x) ? x : new Version(0, 0);
}
