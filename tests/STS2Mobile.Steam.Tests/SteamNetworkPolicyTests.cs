using STS2Mobile.Steam.Network;

namespace STS2Mobile.Steam.Tests;

public class SteamNetworkPolicyTests
{
    [Theory]
    [InlineData("https://api.steampowered.com/")]
    [InlineData("https://store.steampowered.com/")]
    [InlineData("https://steamcommunity.com/")]
    [InlineData("https://cdn.steamstatic.com/")]
    [InlineData("https://cache1.steamcontent.com/depot/1")]
    [InlineData("wss://cm0.steampowered.com/cmsocket/")]
    [InlineData("https://API.STEAMPOWERED.COM:443/")]
    [InlineData("wss://cmp1-sea1.steamserver.net/cmsocket/")]
    public void AllowsSteamEndpoints(string address) => Assert.True(SteamNetworkPolicy.IsAllowed(new Uri(address)));

    [Theory]
    [InlineData("https://api.github.com/")]
    [InlineData("https://sentry.io/")]
    [InlineData("https://megacrit.com/")]
    [InlineData("wss://steamserver.net.evil.test/cmsocket/")]
    [InlineData("wss://evilsteamserver.net/cmsocket/")]
    [InlineData("ws://cmp1-sea1.steamserver.net/cmsocket/")]
    [InlineData("https://steamcontent.com.attacker.test/")]
    [InlineData("https://fakesteamcontent.com/")]
    [InlineData("https://steamcontent.com@attacker.test/")]
    [InlineData("https://attacker@steamcontent.com/")]
    [InlineData("https://steamcontent.com:8443/")]
    [InlineData("http://steamcontent.com/")]
    [InlineData("ws://cm0.steampowered.com/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://[::1]/")]
    [InlineData("https://localhost/")]
    [InlineData("https://steamcontent.com./")]
    [InlineData("https://xn--steam-c6a.com/")]
    [InlineData("https://steamcontent.com/#fragment")]
    [InlineData("https://steamcontent.com..attacker.test/")]
    [InlineData("file:///steamcontent.com")]
    [InlineData("ftp://steamcontent.com/")]
    public void RefusesOtherDestinations(string address) =>
        Assert.False(Uri.TryCreate(address, UriKind.Absolute, out var uri) && SteamNetworkPolicy.IsAllowed(uri));

    [Fact]
    public void RefusesMissingAndRelativeAddresses()
    {
        Assert.False(SteamNetworkPolicy.IsAllowed(null));
        Assert.False(SteamNetworkPolicy.IsAllowed(new Uri("relative/path", UriKind.Relative)));
    }

    [Fact]
    public void AllowedDestinationHasNoRefusal() =>
        Assert.Null(SteamNetworkPolicy.DescribeRefusal(new Uri("https://cdn.steamcontent.com/depot/1?token=secret")));

    // Refusals are logged, so they name the destination only: signed transfer paths and queries never appear.
    [Fact]
    public void PlainHttpRefusalNamesSchemeHostAndPortOnly() =>
        Assert.Equal("scheme=http host=cdn.steamcontent.com port=80",
            SteamNetworkPolicy.DescribeRefusal(new Uri("http://cdn.steamcontent.com/ugc/1?token=secret")));

    [Fact]
    public void ThirdPartyRefusalOmitsCredentialsPathQueryAndFragment() =>
        Assert.Equal("scheme=https host=storage.example.net port=8443",
            SteamNetworkPolicy.DescribeRefusal(new Uri("https://user:pw@storage.example.net:8443/a/b?sig=secret#f")));

    [Fact]
    public void UnusableDestinationIsDescribedWithoutItsText()
    {
        Assert.Equal("not an absolute URL", SteamNetworkPolicy.DescribeRefusal(null));
        Assert.Equal("not an absolute URL", SteamNetworkPolicy.DescribeRefusal(new Uri("a/b", UriKind.Relative)));
    }
}
