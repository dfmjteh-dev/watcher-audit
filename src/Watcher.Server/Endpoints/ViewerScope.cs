using System.Security.Claims;

namespace Watcher.Server;

// Этап C1 (roadmap-next.md): ограничение viewer'а группами хостов. Политика роли
// (Recordings) его не видит — применяется внутри хендлеров.
public static class ViewerScope
{
    // null = не ограничен (admin, operator); у viewer'а — его группы, список
    // передаётся в Inventory/Query/QueryTerminal и проверяется вручную на by-id
    // эндпоинтах (video/meta/out/timing), где нет общего WHERE-фильтра по хосту.
    // 0.14.4: viewer без групп не видит ничего (раньше — всё: новый пользователь
    // из AD получал доступ ко всем записям, включая ввод в консолях).
    public static List<long>? AllowedGroupIds(HttpContext ctx, Db db)
    {
        if (!ctx.User.IsInRole("viewer")) return null;
        long userId = long.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return db.UserGroupIds(userId);
    }

    // По-id эндпоинты (segments/{id}/meta|video, terminal-sessions/{id}/out|timing)
    // не проходят через WHERE в Query/QueryTerminal — host ограниченного viewer'а
    // проверяется отдельно здесь. Хост без строки в hosts (ещё не было heartbeat)
    // трактуется как "не в допущенных группах", не "видно всем" — opt-in, не opt-out.
    public static bool CanSeeHost(HttpContext ctx, Db db, string hostName)
    {
        var allowed = AllowedGroupIds(ctx, db);
        if (allowed is null) return true;
        long? groupId = db.HostGroupId(hostName);
        return groupId is not null && allowed.Contains(groupId.Value);
    }
}
