using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Watcher.Server.Tests;

// R14 (code-review-2026-10.md): эндпоинты разнесены по группам маршрутов, политика
// навешивается на группу. Эта таблица — контракт доступа целиком: новый эндпоинт,
// эндпоинт не в той группе или потерянная политика меняют её и роняют тест.
// «-» без политики — только аутентификация (AuthGate), агентский Bearer или
// свой токен (/metrics).
public class EndpointPolicyTests : IDisposable
{
    private static readonly string[] Expected =
    {
        "GET /api/transcode/issues auth=AdminOnly rate=-",
        "GET /api/transcode/quarantine/expired auth=AdminOnly rate=-",
        "POST /api/transcode/quarantine/{id:long}/cleanup auth=AdminOnly rate=-",
        "GET /api/inventory/{name}/losses/summary auth=- rate=-",
        "GET /api/host-names auth=- rate=-",
        "GET /api/settings/app-kinds/seen auth=AdminOnly rate=-",
        "POST /api/agent-diagnostics auth=- rate=-",
        "POST /api/agent-diagnostics/archive auth=- rate=-",
        "POST /api/hosts/{name}/diagnostics/archives auth=AdminOnly rate=-",
        "GET /api/hosts/{name}/diagnostics/archives auth=AdminOnly rate=-",
        "GET /api/hosts/{name}/diagnostics/archives/{requestId} auth=AdminOnly rate=-",
        "GET /api/hosts/{name}/diagnostics auth=- rate=-",
        "GET /api/hosts/{name}/diagnostics/state auth=- rate=-",
        "POST /api/hosts/{name}/diagnostics/request auth=AdminOnly rate=-",
        "GET /api/hosts/{name}/maintenance auth=AdminOnly rate=-",
        "POST /api/hosts/{name}/maintenance auth=AdminOnly rate=-",
        "DELETE /api/agents/credentials/{id:long} auth=AdminOnly rate=-",
        "DELETE /api/agent-packages/{id:long} auth=AdminOnly rate=-",
        "DELETE /api/fleet/{id:long} auth=AdminOnly rate=-",
        "DELETE /api/fleet/{id:long}/config auth=Management rate=-",
        "DELETE /api/groups/{id:long} auth=Management rate=-",
        "DELETE /api/groups/{id:long}/config auth=Management rate=-",
        "DELETE /api/server-update/uploads/{id} auth=AdminOnly rate=-",
        "DELETE /api/settings/ldap auth=AdminOnly rate=-",
        "GET /api/activity auth=Recordings rate=-",
        "GET /api/agent-kit auth=AdminOnly rate=-",
        "GET /api/agent-kit/android.zip auth=AdminOnly rate=-",
        "GET /api/agent-kit/files/{**path} auth=AdminOnly rate=-",
        "GET /api/agent-packages auth=AdminOnly rate=-",
        "GET /api/agent-packages/{id:long}/file auth=AdminOnly rate=-",
        "GET /api/agents/credentials auth=AdminOnly rate=-",
        "GET /api/agents/enroll-tokens auth=AdminOnly rate=-",
        "GET /api/audit auth=AdminOnly rate=-",
        "GET /api/export auth=Export rate=-",
        "GET /api/export/preview auth=Export rate=-",
        "GET /api/fleet auth=Management rate=-",
        "GET /api/fleet/{id:long}/android-apps auth=PhoneManagement rate=-",
        "GET /api/fleet/{id:long}/android-policy auth=PhoneManagement rate=-",
        "GET /api/fleet/{id:long}/config auth=Management rate=-",
        "GET /api/fleet/{id:long}/config-status auth=Management rate=-",
        "GET /api/fleet/{id:long}/known auth=Management rate=-",
        "GET /api/groups auth=- rate=-",
        "GET /api/groups/{id:long}/android-policy auth=PhoneManagement rate=-",
        "GET /api/groups/{id:long}/config auth=Management rate=-",
        "GET /api/groups/{id:long}/config-status auth=Management rate=-",
        "GET /api/groups/{id:long}/known auth=Management rate=-",
        "GET /api/inventory auth=- rate=-",
        "GET /api/inventory/{name} auth=- rate=-",
        "GET /api/inventory/{name}/android-summary auth=Recordings rate=-",
        "GET /api/inventory/{name}/events auth=Recordings rate=-",
        "GET /api/inventory/{name}/losses auth=- rate=-",
        "GET /api/inventory/{name}/people auth=- rate=-",
        "GET /api/reports/activity auth=Recordings rate=-",
        "GET /api/reports/source auth=Recordings rate=-",
        "GET /api/legal-holds auth=AdminOnly rate=-",
        "GET /api/me auth=- rate=-",
        "GET /api/overview auth=- rate=-",
        "GET /api/retention auth=AdminOnly rate=-",
        "GET /api/segments auth=Recordings rate=-",
        "GET /api/segments/{id:long}/activity auth=Recordings rate=-",
        "GET /api/segments/{id:long}/meta auth=Recordings rate=-",
        "GET /api/segments/{id:long}/video auth=Recordings rate=-",
        "GET /api/settings/app-kinds auth=AdminOnly rate=-",
        "GET /api/settings/ldap auth=AdminOnly rate=-",
        "GET /api/terminal-sessions auth=Recordings rate=-",
        "GET /api/terminal-sessions/{id:long} auth=Recordings rate=-",
        "GET /api/terminal-sessions/{id:long}/out auth=Recordings rate=-",
        "GET /api/terminal-sessions/{id:long}/timing auth=Recordings rate=-",
        "POST /api/terminal-sessions/{id:long}/service-rule auth=Recordings+Management rate=-",
        "GET /api/updates/{id:long} auth=- rate=-",
        "GET /api/users auth=AdminOnly rate=-",
        "GET /api/users/{id:long}/groups auth=AdminOnly rate=-",
        // H4: вне /api — AuthGate пропускает без cookie; /metrics проверяет свой токен.
        "GET /healthz auth=- rate=-",
        "GET /metrics auth=- rate=-",
        "PATCH /api/fleet/{id:long} auth=Management rate=-",
        "PATCH /api/users/{id:long} auth=AdminOnly rate=-",
        "POST /api/agent-packages auth=AdminOnly rate=-",
        "GET /api/server-update auth=AdminOnly rate=-",
        "GET /api/server-update/latest auth=AdminOnly rate=-",
        "POST /api/server-update/github auth=AdminOnly rate=-",
        "POST /api/server-update/upload auth=AdminOnly rate=-",
        "POST /api/server-update/uploads auth=AdminOnly rate=-",
        "POST /api/agents/credentials/{id:long}/acknowledge auth=AdminOnly rate=-",
        "POST /api/agents/credentials/{id:long}/revoke auth=AdminOnly rate=-",
        "POST /api/agents/enroll-tokens auth=AdminOnly rate=-",
        "POST /api/agents/enroll-tokens/{id:long}/revoke auth=AdminOnly rate=-",
        "POST /api/enroll auth=- rate=enroll",
        "POST /api/fleet/assign-group auth=Management rate=-",
        "POST /api/fleet/{id:long}/android/lock auth=PhoneManagement rate=-",
        "POST /api/fleet/{id:long}/android/clear-owner auth=Management rate=-",
        "POST /api/groups auth=Management rate=-",
        "POST /api/events auth=- rate=-",
        "POST /api/heartbeat auth=- rate=-",
        "POST /api/inventory/{name}/losses/acknowledge auth=Management rate=-",
        "POST /api/legal-holds auth=AdminOnly rate=-",
        "POST /api/legal-holds/{id:long}/release auth=AdminOnly rate=-",
        "POST /api/login auth=- rate=login",
        "GET /api/login auth=- rate=-",
        "POST /api/records/delete auth=AdminOnly rate=-",
        "POST /api/logout auth=- rate=-",
        "POST /api/me/password auth=- rate=-",
        "POST /api/upload auth=- rate=-",
        "POST /api/upload-terminal auth=- rate=-",
        "POST /api/users auth=AdminOnly rate=-",
        "POST /api/settings/ldap/test auth=AdminOnly rate=-",
        "POST /api/users/{id:long}/reset-password auth=AdminOnly rate=-",
        "PUT /api/fleet/{id:long}/android-policy auth=PhoneManagement rate=-",
        "PUT /api/fleet/{id:long}/config auth=Management rate=-",
        "PUT /api/groups/{id:long} auth=Management rate=-",
        "PUT /api/groups/{id:long}/agent-version auth=AdminOnly rate=-",
        "PUT /api/groups/{id:long}/android-policy auth=PhoneManagement rate=-",
        "PUT /api/groups/{id:long}/config auth=Management rate=-",
        "PUT /api/groups/{id:long}/retention auth=AdminOnly rate=-",
        "PUT /api/server-update/uploads/{id} auth=AdminOnly rate=-",
        "PUT /api/settings/app-kinds auth=AdminOnly rate=-",
        "PUT /api/settings/ldap auth=AdminOnly rate=-",
        "PUT /api/users/{id:long}/groups auth=AdminOnly rate=-",
    };

    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;

    public EndpointPolicyTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-endpoints-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AllowAnonymousAgents", "true");
        _factory = new WebApplicationFactory<Program>();
        _factory.CreateClient().Dispose();
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var key in new[] { "Server__DataDir", "Server__DbPath", "Server__AllowAnonymousAgents" })
            Environment.SetEnvironmentVariable(key, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    [Fact]
    public void Every_api_endpoint_has_the_expected_policy()
    {
        var actual = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is not null)
            .SelectMany(e =>
            {
                string route = e.RoutePattern.RawText!.TrimEnd('/');
                var policies = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy ?? "(default)").ToList();
                string auth = policies.Count == 0 ? "-" : string.Join("+", policies);
                string rate = e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "-";
                var methods = e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? new[] { "ANY" };
                return methods.Select(m => $"{m} {route} auth={auth} rate={rate}");
            })
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(actual.Except(Expected));
        Assert.Empty(Expected.Except(actual));
    }
}
