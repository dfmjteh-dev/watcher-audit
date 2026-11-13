using System.Net;
using Watcher.Uploader;
using Xunit;

namespace Watcher.Uploader.Tests;

// R16: IP для heartbeat — по маршруту к серверу Watcher, 8.8.8.8 только запасной.
public sealed class LocalAddressTests
{
    // Адрес основного интерфейса этой машины (null — нет маршрута по умолчанию).
    private static IPAddress? DefaultRouteAddress() => LocalAddress.LocalFor(IPAddress.Parse("8.8.8.8"), 65530);

    [Fact]
    public void LocalFor_Loopback_IsLoopback()
    {
        Assert.Equal(IPAddress.Loopback, LocalAddress.LocalFor(IPAddress.Loopback, 5050));
    }

    [Fact]
    public async Task ServerOnOwnAddress_ReturnsThatAddress()
    {
        var own = DefaultRouteAddress();
        if (own is null) return; // нет сети — нечего проверять
        Assert.Equal(own.ToString(), await LocalAddress.TowardsServerAsync($"https://{own}:5443", default));
    }

    [Fact]
    public async Task ServerOnLoopback_FallsBackToDefaultRoute()
    {
        string expected = DefaultRouteAddress()?.ToString() ?? "127.0.0.1";
        Assert.Equal(expected, await LocalAddress.TowardsServerAsync("http://127.0.0.1:5050", default));
    }

    [Theory]
    [InlineData("http://watcher-r16-test.invalid:5050")] // не резолвится
    [InlineData("not a url")]
    [InlineData("")]
    public async Task BadServer_FallsBackWithoutThrowing(string serverUrl)
    {
        Assert.Equal(DefaultRouteAddress()?.ToString(), await LocalAddress.TowardsServerAsync(serverUrl, default));
    }
}
