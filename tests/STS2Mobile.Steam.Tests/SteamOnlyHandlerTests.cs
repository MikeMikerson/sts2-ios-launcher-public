using System.Net;
using System.Net.WebSockets;
using STS2Mobile.Steam.Network;
using STS2Mobile.Tests;

namespace STS2Mobile.Steam.Tests;

public sealed class SteamOnlyHandlerTests : IDisposable
{
    private readonly RecordingTransport _transport = new();
    private readonly HttpClient _client;

    public SteamOnlyHandlerTests() => _client = new HttpClient(new SteamOnlyHandler(_transport));

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task AllowedRequestReachesTransport()
    {
        using var response = await _client.GetAsync("https://api.steampowered.com/");
        Assert.Equal(1, _transport.Calls);
    }

    [Fact]
    public async Task RefusedRequestNeverReachesTransport()
    {
        await Assert.ThrowsAsync<NetworkPolicyException>(() => _client.GetAsync("https://api.github.com/"));
        Assert.Equal(0, _transport.Calls);
    }

    [Fact]
    public async Task HostHeaderOverrideIsRefused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.steampowered.com/");
        request.Headers.Host = "attacker.test";
        await Assert.ThrowsAsync<NetworkPolicyException>(() => _client.SendAsync(request));
        Assert.Equal(0, _transport.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task RedirectIsNeverFollowed(HttpStatusCode status)
    {
        _transport.Status = status;
        await Assert.ThrowsAsync<NetworkPolicyException>(() => _client.GetAsync("https://api.steampowered.com/"));
        Assert.Equal(1, _transport.Calls);
    }

    [Fact]
    public void SynchronousSendIsGuarded()
    {
        using (_client.Send(new HttpRequestMessage(HttpMethod.Get, "https://api.steampowered.com/"))) { }
        Assert.Throws<NetworkPolicyException>(() => _client.Send(new HttpRequestMessage(HttpMethod.Get, "https://sentry.io/")));
        Assert.Equal(1, _transport.Calls);
    }

    // ClientWebSocket sends its handshake through the invoker, so the guard has to run there too.
    [Fact]
    public async Task WebSocketHandshakeIsGuarded()
    {
        using var socket = new ClientWebSocket();
        using var invoker = new HttpMessageInvoker(new SteamOnlyHandler(_transport));
        var error = await Record.ExceptionAsync(() => socket.ConnectAsync(new Uri("wss://attacker.test/cmsocket/"), invoker, CancellationToken.None));
        Assert.True(error is HttpRequestException or WebSocketException, $"Unexpected result: {error}");
        Assert.Equal(0, _transport.Calls);
    }
}
