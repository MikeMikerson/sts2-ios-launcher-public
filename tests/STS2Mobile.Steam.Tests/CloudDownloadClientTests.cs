using System.Net;
using STS2Mobile.Steam.Network;
using STS2Mobile.Tests;

namespace STS2Mobile.Steam.Tests;

// Only the cloud save download client reaches Steam Cloud storage, only with GET, and every other
// protection of the Steam client still applies to it.
public sealed class CloudDownloadClientTests : IDisposable
{
    private const string CloudFile = "https://steamcloud-us-west3.s3.dualstack.us-west-2.amazonaws.com/2868840/abc?X-Amz-Signature=secret";
    private readonly RecordingTransport _transport = new();
    private readonly HttpClient _client;

    public CloudDownloadClientTests() =>
        _client = new HttpClient(new SteamOnlyHandler(_transport, CloudStorageAccess.Download));

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task OrdinarySteamClientRefusesCloudStorage()
    {
        var transport = new RecordingTransport();
        using var client = new HttpClient(new SteamOnlyHandler(transport));
        await Assert.ThrowsAsync<NetworkPolicyException>(() => client.GetAsync(CloudFile));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task ReachesCloudStorage()
    {
        using var response = await _client.GetAsync(CloudFile);
        Assert.Equal(1, _transport.Calls);
    }

    [Fact]
    public async Task StillReachesSteam()
    {
        using var response = await _client.GetAsync("https://api.steampowered.com/");
        Assert.Equal(1, _transport.Calls);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task CannotWriteToCloudStorage(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), CloudFile);
        await Assert.ThrowsAsync<NetworkPolicyException>(() => _client.SendAsync(request));
        Assert.Equal(0, _transport.Calls);
    }

    [Fact]
    public async Task RefusesOtherStorageHosts()
    {
        await Assert.ThrowsAsync<NetworkPolicyException>(() => _client.GetAsync("https://attacker.s3.dualstack.us-west-2.amazonaws.com/a"));
        Assert.Equal(0, _transport.Calls);
    }

    [Fact]
    public async Task HostHeaderOverrideIsRefused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CloudFile);
        request.Headers.Host = "attacker.test";
        await Assert.ThrowsAsync<NetworkPolicyException>(() => _client.SendAsync(request));
        Assert.Equal(0, _transport.Calls);
    }

    [Fact]
    public async Task RedirectIsNeverFollowed()
    {
        _transport.Status = HttpStatusCode.TemporaryRedirect;
        await Assert.ThrowsAsync<NetworkPolicyException>(() => _client.GetAsync(CloudFile));
        Assert.Equal(1, _transport.Calls);
    }
}
