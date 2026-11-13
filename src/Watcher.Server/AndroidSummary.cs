using System.Text.Json;

namespace Watcher.Server;

// Этап 4 Android (docs/android-plan.md): сводка устройства за период для веб-UI —
// какие приложения были на экране и сколько, установки/удаления, трафик по
// приложениям, последнее состояние устройства и перечень приложений. Считается
// по сырым событиям Этапа 3 (Db.AndroidEvents) на лету: событий за сутки у
// телефона — тысячи, не миллионы, отдельная агрегатная таблица не нужна.
// Чистая логика без БД — покрыта unit-тестами (AndroidSummaryTests).

public sealed record AndroidAppUsage(string Package, long ForegroundMs, int Launches, DateTime FirstAt, DateTime LastAt, bool OnScreen);
public sealed record AndroidAppChange(DateTime Ts, string Type, string Package, string? Installer);
public sealed record AndroidAppTraffic(string Package, long RxBytes, long TxBytes, long WifiBytes, long MobileBytes);
public sealed record AndroidInstalledApp(string Package, string? VersionName, string? Installer, string? Label);
public sealed record AndroidInventorySummary(DateTime Ts, int Total, int System, bool Partial, List<AndroidInstalledApp> UserApps);
// Этап 6: строка сетевого журнала (Kind = "dns" | "connect"; Target — домен или ip:port).
public sealed record AndroidNetworkRow(string Kind, string Target, string? Package, long Count, DateTime FirstAt, DateTime LastAt);
public sealed record AndroidSecurityRow(DateTime Ts, int Tag, string Name, string? Data);

public sealed record AndroidSummaryResult(
    DateTime From, DateTime To, int Events, bool Truncated,
    List<AndroidAppUsage> Apps, List<AndroidAppChange> Changes, List<AndroidAppTraffic> Traffic,
    DateTime? DeviceStateAt, JsonElement? DeviceState, AndroidInventorySummary? Inventory,
    List<AndroidNetworkRow> Network, bool NetworkTruncated,
    List<AndroidSecurityRow> Security, bool SecurityTruncated);

public static class AndroidSummary
{
    // Предел событий одного запроса сводки: сутки активного телефона — порядка
    // тысяч событий; больше — обрезаем и честно говорим об этом (Truncated).
    public const int MaxEvents = 100_000;
    // Самый длинный период сводки (веб-UI показывает сутки; неделя — с запасом).
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(7);
    // Этап 6: сколько агрегированных строк сетевого журнала и событий безопасности
    // отдаём в сводке (день телефона укладывается; сверх — обрезаем с флагом).
    public const int MaxNetworkRows = 2000;
    public const int MaxSecurityRows = 1000;

    // events — события периода [from, to) по возрастанию ts (Db.ListAndroidEventsRange).
    // Время на экране: от app_foreground до app_background того же пакета, до
    // выхода на экран другого приложения или до выключения экрана (device_state
    // screenOn=false) — что раньше. Незакрытый интервал в конце — до min(to, now),
    // такое приложение помечено OnScreen (если период ещё идёт).
    // Повторный foreground того же пакета без background между ними (смена
    // activity внутри приложения) — продолжение того же интервала, не запуск.
    public static AndroidSummaryResult Build(
        DateTime from, DateTime to, DateTime now, IReadOnlyList<AndroidEvent> events, bool truncated,
        AndroidEvent? lastDeviceState, IReadOnlyList<AndroidEvent> inventoryParts)
    {
        var usage = new Dictionary<string, (long Ms, int Launches, DateTime First, DateTime Last)>(StringComparer.Ordinal);
        var traffic = new Dictionary<string, (long Rx, long Tx, long Wifi, long Mobile)>(StringComparer.Ordinal);
        var changes = new List<AndroidAppChange>();
        // Этап 6: сетевой журнал (ключ Kind|Target|Package) и журнал безопасности.
        var net = new Dictionary<(string Kind, string Target, string? Pkg), (long Count, DateTime First, DateTime Last)>();
        var security = new List<AndroidSecurityRow>();
        string? openPkg = null;
        DateTime openAt = default;

        void Close(DateTime at)
        {
            if (openPkg is null) return;
            var u = usage[openPkg];
            if (at > openAt) u.Ms += (long)(at - openAt).TotalMilliseconds;
            usage[openPkg] = u with { Last = at > u.Last ? at : u.Last };
            openPkg = null;
        }

        foreach (var e in events)
        {
            switch (e.Type)
            {
                case "app_foreground" when e.Package is { } p:
                    if (openPkg == p) break;
                    Close(e.Ts);
                    var cur = usage.TryGetValue(p, out var x) ? x : (0, 0, e.Ts, e.Ts);
                    usage[p] = cur with { Launches = cur.Launches + 1, Last = e.Ts };
                    openPkg = p;
                    openAt = e.Ts;
                    break;
                case "app_background" when e.Package is { } p:
                    if (openPkg == p) Close(e.Ts);
                    break;
                case "device_state":
                    if (Bool(e.Detail, "screenOn") == false) Close(e.Ts);
                    break;
                case "app_installed" or "app_removed" when e.Package is { } p:
                    changes.Add(new AndroidAppChange(e.Ts, e.Type, p, e.Type == "app_installed" ? Str(e.Detail, "installer") : null));
                    break;
                case "app_traffic" when e.Package is { } p:
                    long rx = Long(e.Detail, "rxBytes"), tx = Long(e.Detail, "txBytes");
                    string? netType = Str(e.Detail, "networkType");
                    var t = traffic.TryGetValue(p, out var y) ? y : (0, 0, 0, 0);
                    traffic[p] = (t.Rx + rx, t.Tx + tx,
                        t.Wifi + (netType == "wifi" ? rx + tx : 0), t.Mobile + (netType == "mobile" ? rx + tx : 0));
                    break;
                case "net_dns" when Str(e.Detail, "domain") is { } domain:
                    AddNet(net, "dns", domain, Str(e.Detail, "package"), Count(e.Detail), e.Ts);
                    break;
                case "net_connect" when Str(e.Detail, "ip") is { } ip:
                    AddNet(net, "connect", ip + ":" + (IntOf(e.Detail, "port") ?? 0), Str(e.Detail, "package"), Count(e.Detail), e.Ts);
                    break;
                case "security":
                    if (security.Count < MaxSecurityRows)
                        security.Add(new AndroidSecurityRow(e.Ts, IntOf(e.Detail, "tag") ?? 0,
                            Str(e.Detail, "name") ?? "tag_" + (IntOf(e.Detail, "tag") ?? 0), Str(e.Detail, "data")));
                    break;
            }
        }
        string? onScreen = openPkg;
        DateTime end = to < now ? to : now;
        Close(end > openAt ? end : openAt);

        var apps = usage
            .Select(kv => new AndroidAppUsage(kv.Key, kv.Value.Ms, kv.Value.Launches, kv.Value.First, kv.Value.Last,
                kv.Key == onScreen && to > now))
            .OrderByDescending(a => a.ForegroundMs).ThenBy(a => a.Package, StringComparer.Ordinal).ToList();
        var trafficList = traffic
            .Select(kv => new AndroidAppTraffic(kv.Key, kv.Value.Rx, kv.Value.Tx, kv.Value.Wifi, kv.Value.Mobile))
            .OrderByDescending(a => a.RxBytes + a.TxBytes).ThenBy(a => a.Package, StringComparer.Ordinal).ToList();
        changes.Reverse();   // новые сверху
        var networkRows = net
            .Select(kv => new AndroidNetworkRow(kv.Key.Kind, kv.Key.Target, kv.Key.Pkg, kv.Value.Count, kv.Value.First, kv.Value.Last))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Target, StringComparer.Ordinal).ToList();
        bool networkTruncated = networkRows.Count > MaxNetworkRows;
        if (networkTruncated) networkRows = networkRows.Take(MaxNetworkRows).ToList();
        security.Reverse();   // новые сверху (события шли по возрастанию ts)
        bool securityTruncated = security.Count >= MaxSecurityRows;

        JsonElement? state = null;
        if (lastDeviceState?.Detail is { } sd)
        {
            try { using var doc = JsonDocument.Parse(sd); state = doc.RootElement.Clone(); }
            catch (JsonException) { /* detail проверен на приёме; битый — просто без состояния */ }
        }
        return new AndroidSummaryResult(from, to, events.Count, truncated, apps, changes, trafficList,
            lastDeviceState?.Ts, state, Inventory(inventoryParts),
            networkRows, networkTruncated, security, securityTruncated);
    }

    // Перечень приложений — последний полный снимок app_inventory (части одного
    // snapshotId). Не все части дошли (часть ещё в очереди агента) — Partial.
    // Системные приложения только считаем: в списке — установленные пользователем
    // или администратором, их и проверяют на источник установки.
    private static AndroidInventorySummary? Inventory(IReadOnlyList<AndroidEvent> parts)
    {
        if (parts.Count == 0) return null;
        var seen = new HashSet<int>();
        int expected = 0, total = 0, system = 0;
        var userApps = new List<AndroidInstalledApp>();
        foreach (var part in parts)
        {
            if (part.Detail is null) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(part.Detail); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (Int(root, "part") is not { } n || !seen.Add(n)) continue;
                expected = Math.Max(expected, Int(root, "parts") ?? 0);
                if (!root.TryGetProperty("packages", out var pkgs) || pkgs.ValueKind != JsonValueKind.Array) continue;
                foreach (var a in pkgs.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object || Str(a, "package") is not { } name) continue;
                    total++;
                    if (a.TryGetProperty("system", out var s) && s.ValueKind == JsonValueKind.True) system++;
                    else userApps.Add(new AndroidInstalledApp(name, Str(a, "versionName"), Str(a, "installer"), Str(a, "label")));
                }
            }
        }
        userApps.Sort((a, b) => string.CompareOrdinal(a.Package, b.Package));
        return new AndroidInventorySummary(parts.Max(p => p.Ts), total, system, seen.Count < expected, userApps);
    }

    private static void AddNet(
        Dictionary<(string, string, string?), (long Count, DateTime First, DateTime Last)> net,
        string kind, string target, string? pkg, long count, DateTime ts)
    {
        var key = (kind, target, pkg);
        if (net.TryGetValue(key, out var v))
            net[key] = (v.Count + count, ts < v.First ? ts : v.First, ts > v.Last ? ts : v.Last);
        else
            net[key] = (count, ts, ts);
    }

    // count в detail журнала (>=1): битое/отсутствует — считаем 1 (одно событие).
    private static long Count(string? detail) => Math.Max(1, Long(detail, "count"));

    private static int? IntOf(string? detail, string name) =>
        Root(detail) is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;

    private static JsonElement? Root(string? detail)
    {
        if (detail is null) return null;
        try { using var doc = JsonDocument.Parse(detail); return doc.RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    private static bool? Bool(string? detail, string name) =>
        Root(detail) is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    private static string? Str(string? detail, string name) =>
        Root(detail) is { ValueKind: JsonValueKind.Object } r ? Str(r, name) : null;

    private static long Long(string? detail, string name) =>
        Root(detail) is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) && n > 0 ? n : 0;

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;
}
