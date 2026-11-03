using System.Net;
using STS2Mobile.Steam.Network;
using STS2Mobile.Tests;

namespace STS2Mobile.Steam.Tests;

// Only the cloud upload client writes to Steam Cloud storage, only with PUT or POST, it can do nothing else, and every other
// protection of the Steam client still applies to it.
public sealed class CloudUploadPolicyTests : IDisposable
{
    private const string CloudFile = "https://steamcloud-us-west3.s3.dualstack.us-west-2.amazonaws.com/2868840/abc?X-Amz-Signature=secret";
    private readonly RecordingTransport _transport = new();
    private readonly HttpClient _client;

    public CloudUploadPolicyTests() => _client = new HttpClient(new SteamOnlyHandler(_transport, CloudStorageAccess.Upload));

    public void Dispose() => _client.Dispose();

    public static TheoryData<string> StorageHosts =>
    [
        CloudFile,
        "https://steamcloud-tokyo.storage.googleapis.com/2868840/a?sig=secret",
        "https://steamcloudquincy.blob.core.windows.net/a?sig=secret",
    ];

    [Theory]
    [MemberData(nameof(StorageHosts))]
    public async Task PutReachesCloudStorage(string address)
    {
        using var response = await _client.PutAsync(address, new ByteArrayContent([1, 2, 3]));
        Assert.Equal(HttpMethod.Put, Assert.Single(_transport.Requests).Method);
    }

    [Fact]
    public async Task PostReachesCloudStorage()
    {
        using var response = await _client.PostAsync(CloudFile, new ByteArrayContent([1]));
        Assert.Equal(1, _transport.Calls);
    }

    [Fact]
    public async Task PutStillReachesSteam()
    {
        using var response = await _client.PutAsync("https://api.steampowered.com/upload", new ByteArrayContent([1]));
        Assert.Equal(1, _transport.Calls);
    }

    [Theory]
    [InlineData("GET", CloudFile)]
    [InlineData("HEAD", CloudFile)]
    [InlineData("DELETE", CloudFile)]
    [InlineData("PATCH", CloudFile)]
    [InlineData("GET", "https://api.steampowered.com/")]
    [InlineData("DELETE", "https://api.steampowered.com/")]
    public async Task CanOnlyWrite(string method, string address)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), address);
        var error = await Assert.ThrowsAsync<NetworkPolicyException>(() => _client.SendAsync(request));
        Assert.Equal(NetworkPolicyFailure.Method, error.Failure);
        Assert.Equal(0, _transport.Calls);
    }

    [Theory]
    [InlineData("https://attacker.s3.dualstack.us-west-2.amazonaws.com/a")]
    [InlineData("https://storage.googleapis.com/steamcloud-tokyo/a")]
    [InlineData("http://steamcloud-us-west3.s3.dualstack.us-west-2.amazonaws.com/a")]
    [InlineData("https://steamcloud-us-west3.s3.dualstack.us-west-2.amazonaws.com:8443/a")]
    [InlineData("https://attacker.test/a")]
    public async Task RefusesOtherHosts(string address)
    {
        await Assert.ThrowsAsync<NetworkPolicyException>(() => _client.PutAsync(address, new ByteArrayContent([1])));
        Assert.Equal(0, _transport.Calls);
    }

    [Fact]
    public async Task HostHeaderOverrideIsRefused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, CloudFile) { Content = new ByteArrayContent([1]) };
        request.Headers.Host = "attacker.test";
        await Assert.ThrowsAsync<NetworkPolicyException>(() => _client.SendAsync(request));
        Assert.Equal(0, _transport.Calls);
    }

    [Fact]
    public async Task RedirectIsNeverFollowed()
    {
        _transport.Status = HttpStatusCode.TemporaryRedirect;
        await Assert.ThrowsAsync<NetworkPolicyException>(() => _client.PutAsync(CloudFile, new ByteArrayContent([1])));
        Assert.Equal(1, _transport.Calls);
    }

    [Theory]
    [InlineData(CloudStorageAccess.None)]
    [InlineData(CloudStorageAccess.Download)]
    public async Task NoOtherClientCanWriteToCloudStorage(CloudStorageAccess access)
    {
        var transport = new RecordingTransport();
        using var client = new HttpClient(new SteamOnlyHandler(transport, access));
        await Assert.ThrowsAsync<NetworkPolicyException>(() => client.PutAsync(CloudFile, new ByteArrayContent([1])));
        await Assert.ThrowsAsync<NetworkPolicyException>(() => client.PostAsync(CloudFile, new ByteArrayContent([1])));
        Assert.Equal(0, transport.Calls);
    }
}
