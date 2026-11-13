namespace Watcher.Server;

public static class AgentAccess
{
    // Ответ агенту удалённого хоста: «удали себя» (см. Watcher.Uploader.Core/AgentRemoval).
    public const string UninstallHeader = "X-Watcher-Uninstall";

    // Задача G1: учётка агента (секрет из /api/enroll) → хост из учётки; общий
    // ApiToken (если задан и не выключен AllowLegacyApiToken) или анонимный dev-режим
    // → переходный путь. null — ответ 401 уже выставлен. requiredKind — учётка
    // другого вида (terminal шлёт видео и наоборот) получает 403.
    public static AgentCaller? Authenticate(HttpContext ctx, Db db, ServerConfig cfg, string? requiredKind = null)
    {
        string? presented = AgentAuth.BearerToken(ctx.Request.Headers.Authorization.ToString());
        if (presented is not null && presented.StartsWith(AgentAuth.SecretPrefix, StringComparison.Ordinal))
        {
            var identity = db.AuthenticateAgent(AgentAuth.Hash(presented), ctx.Connection.RemoteIpAddress?.ToString());
            if (identity is not null)
            {
                if (requiredKind is not null && identity.Kind != requiredKind)
                {
                    ctx.Response.StatusCode = 403;
                    return null;
                }
                return new AgentCaller(identity);
            }
            // 0.13: хост этой учётки удалён в веб-UI — агент должен удалить себя
            // (а не перерегистрироваться и вернуть хост). 410 + заголовок; в журнал
            // доступа — один раз на учётку.
            if (db.DeletedHostCredential(AgentAuth.Hash(presented)) is { } deleted)
            {
                if (deleted.First)
                    ctx.RequestServices.GetService<AuditLog>()?.Write(ctx, "agent.uninstall-sent", $"host:{deleted.Host}", null, username: "agent");
                ctx.Response.StatusCode = StatusCodes.Status410Gone;
                ctx.Response.Headers[UninstallHeader] = "1";
                return null;
            }
        }
        else if (presented is not null && cfg.AllowLegacyApiToken && AgentAuth.MatchesSharedToken(presented, cfg.ApiToken))
        {
            return new AgentCaller(null);
        }
        else if (presented is null && cfg.AllowAnonymousAgents && string.IsNullOrWhiteSpace(cfg.ApiToken))
        {
            return new AgentCaller(null);
        }
        ctx.Response.StatusCode = 401;
        return null;
    }
}
