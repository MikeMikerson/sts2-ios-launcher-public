using System.Net;
using STS2Mobile.Tests;

namespace STS2Mobile.Platform.Tests;

public sealed class GameNetworkTests : IDisposable
{
    private readonly RecordingTransport _transport = new();
    private readonly HttpClient _client;

    public GameNetworkTests() => _client = new HttpClient(new GameOnlyHandler(_transport));

    public void Dispose() => _client.Dispose();

    [Theory]
    [InlineData("https://time.megacrit.com")]
    [InlineData("https://feedback.sts2.megacrit.com")]
    [InlineData("https://api.steampowered.com")]
    [InlineData("https://megacrit.com:443")]
    public void AllowsGameEndpoints(string address) => Assert.True(GameNetworkPolicy.IsAllowed(new Uri(address)));

    [Theory]
    [InlineData("https://megacrit.com.evil.test")]
    [InlineData("https://evilmegacrit.com")]
    [InlineData("https://sentry.io")]
    [InlineData("https://megacrit.com.")]
    [InlineData("https://name@megacrit.com")]
    [InlineData("https://megacrit.com:8443")]
    [InlineData("http://megacrit.com")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://[::1]")]
    [InlineData("https://megacrit.com/#fragment")]
    [InlineData("wss://megacrit.com")]
    public void RefusesOtherDestinations(string address) => Assert.False(GameNetworkPolicy.IsAllowed(new Uri(address)));

    [Fact]
    public async Task AllowedRequestReachesTransport()
    {
        using var response = await _client.GetAsync("https://time.megacrit.com");
        Assert.Equal(1, _transport.Calls);
    }

    [Fact]
    public async Task RefusedRequestNeverReachesTransport()
    {
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _client.GetAsync("https://evil.test"));
        Assert.Equal(0, _transport.Calls);
    }

    [Fact]
    public async Task HostHeaderOverrideIsRefused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://time.megacrit.com");
        request.Headers.Host = "evil.test";
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _client.SendAsync(request));
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
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _client.GetAsync("https://time.megacrit.com"));
        Assert.Equal(1, _transport.Calls);
    }

    [Fact]
    public void SynchronousSendIsGuarded()
    {
        Assert.ThrowsAny<HttpRequestException>(() => _client.Send(new HttpRequestMessage(HttpMethod.Get, "https://sentry.io")));
        Assert.Equal(0, _transport.Calls);
    }
}
