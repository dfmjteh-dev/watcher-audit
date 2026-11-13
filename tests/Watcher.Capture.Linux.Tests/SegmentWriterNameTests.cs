using Watcher.Capture.Linux;
using Xunit;

namespace Watcher.Capture.Linux.Tests;

public class SegmentWriterNameTests
{
    [Fact]
    public void Base_name_includes_session_user_so_parallel_sessions_differ()
    {
        Assert.Equal("srv1_alice", SegmentWriter.BuildBaseName("srv1", "alice"));
        Assert.NotEqual(SegmentWriter.BuildBaseName("srv1", "alice"), SegmentWriter.BuildBaseName("srv1", "bob"));
    }

    [Fact]
    public void Base_name_strips_unsafe_characters_and_falls_back_to_host()
    {
        Assert.Equal("srv1_domainuser", SegmentWriter.BuildBaseName("srv1", "domain\\user"));
        Assert.Equal("srv1", SegmentWriter.BuildBaseName("srv1", "../"));
    }
}
