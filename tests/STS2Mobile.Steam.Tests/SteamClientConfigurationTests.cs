using SteamKit2;
using STS2Mobile.Steam.Connection;
using STS2Mobile.Steam.Network;

namespace STS2Mobile.Steam.Tests;

public class SteamClientConfigurationTests
{
    [Fact]
    public void HasNoRawTcpOrUdpFallback() =>
        Assert.Equal(ProtocolTypes.WebSocket, SteamClientConfiguration.Create().ProtocolTypes);

    [Fact]
    public async Task EverySteamKitHttpClientIsGuarded()
    {
        var configuration = SteamClientConfiguration.Create();
        foreach (var purpose in Enum.GetValues<HttpClientPurpose>())
        {
            using var client = configuration.HttpClientFactory(purpose);
            var error = await Assert.ThrowsAsync<NetworkPolicyException>(() => client.GetAsync("https://attacker.test/"));
            Assert.Equal(NetworkPolicyFailure.InvalidDestination, error.Failure);
        }
    }
}
