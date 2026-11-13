using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

// 0.13: хост удалён в веб-UI — сервер отвечает 410 + X-Watcher-Uninstall.
public sealed class AgentRemovalTests
{
    private static HttpResponseMessage Resp(HttpStatusCode code, bool header)
    {
        var r = new HttpResponseMessage(code);
        if (header) r.Headers.Add(AgentRemoval.Header, "1");
        return r;
    }

    [Fact]
    public void Only_410_with_header_means_uninstall()
    {
        Assert.True(AgentRemoval.IsUninstallResponse(Resp(HttpStatusCode.Gone, true)));
        Assert.False(AgentRemoval.IsUninstallResponse(Resp(HttpStatusCode.Gone, false)));
        Assert.False(AgentRemoval.IsUninstallResponse(Resp(HttpStatusCode.Unauthorized, true)));
        Assert.False(AgentRemoval.IsUninstallResponse(Resp(HttpStatusCode.OK, true)));
    }

    [Fact]
    public void Remover_runs_once_per_process()
    {
        int calls = 0;
        AgentRemoval.Remover = _ => calls++;
        try
        {
            Assert.False(AgentRemoval.Check(Resp(HttpStatusCode.OK, false), NullLogger.Instance));
            Assert.True(AgentRemoval.Check(Resp(HttpStatusCode.Gone, true), NullLogger.Instance));
            Assert.True(AgentRemoval.Check(Resp(HttpStatusCode.Gone, true), NullLogger.Instance));
            Assert.Equal(1, calls);
        }
        finally { AgentRemoval.Remover = null; }
    }
}
