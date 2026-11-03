using System.Net;
using STS2Mobile.Tests;

namespace STS2Mobile.Platform.Tests;

/// <summary>QuickMatch's relay: exactly the one host from its settings file, its room paths, wss on 443 (NETWORK.md).</summary>
public sealed class RelayNetworkTests
{
    // A placeholder: the real host is never written into a tracked file.
    private const string Host = "relay.example.test";
    private readonly RelayNetworkPolicy _policy = new(Host);

    [Theory]
    [InlineData("wss://relay.example.test/v1/relay/auto")]
    [InlineData("wss://relay.example.test/v1/relay/apac")]
    [InlineData("wss://relay.example.test:443/v1/relay/na")]
    [InlineData("https://relay.example.test/v1/relay/eu")]
    [InlineData("wss://RELAY.example.test/v1/relay/apac")]
    public void AllowsTheRelayRooms(string address) => Assert.True(_policy.IsAllowed(new Uri(address)));

    [Theory]
    [InlineData("ws://relay.example.test/v1/relay/apac")]
    [InlineData("http://relay.example.test/v1/relay/apac")]
    [InlineData("wss://relay.example.test:8443/v1/relay/apac")]
    [InlineData("wss://relay.example.test./v1/relay/apac")]
    [InlineData("wss://relay.example.test.evil.test/v1/relay/apac")]
    [InlineData("wss://evilrelay.example.test/v1/relay/apac")]
    [InlineData("wss://x.relay.example.test/v1/relay/apac")]
    [InlineData("wss://example.test/v1/relay/apac")]
    [InlineData("wss://name@relay.example.test/v1/relay/apac")]
    [InlineData("wss://relay.example.test/v1/relay/apac?x=1")]
    [InlineData("wss://relay.example.test/v1/relay/apac#f")]
    [InlineData("wss://relay.example.test/v1/relay/mars")]
    [InlineData("wss://relay.example.test/v1/relay/apac/")]
    [InlineData("wss://relay.example.test/v1/relay/")]
    [InlineData("wss://relay.example.test/probe")]
    [InlineData("wss://relay.example.test/")]
    [InlineData("wss://relay.example.test/v1/relay/%2e%2e/probe")]
    [InlineData("wss://relay.example.test/v1/relay/ap%2Fac")]
    [InlineData("wss://104.16.0.1/v1/relay/apac")]
    [InlineData("wss://[::1]/v1/relay/apac")]
    public void RefusesEverythingElse(string address) => Assert.False(_policy.IsAllowed(new Uri(address)));

    [Fact]
    public void RefusesBadRegionsAndNoAddress()
    {
        Assert.False(_policy.IsAllowed(null));
        Assert.False(RelayNetworkPolicy.IsAllowedRegion("../probe"));
        Assert.False(RelayNetworkPolicy.IsAllowedRegion(null));
    }

    [Fact]
    public void TheOtherPoliciesRefuseTheRelay() => Assert.False(GameNetworkPolicy.IsAllowed(new Uri($"https://{Host}/v1/relay/apac")));

    [Theory]
    [InlineData("https://relay.example.test/v1")]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("Relay.Example.Test")]
    public void APolicyNeedsAPlainLowerCaseHost(string host) => Assert.Throws<ArgumentException>(() => new RelayNetworkPolicy(host));

    [Fact]
    public async Task TheHandlerChecksBeforeTransport()
    {
        var transport = new RecordingTransport();
        using var client = new HttpClient(new RelayOnlyHandler(_policy, transport));
        using (await client.GetAsync($"https://{Host}/v1/relay/apac")) { }
        Assert.Equal(1, transport.Calls);
        foreach (string address in new[] { $"https://{Host}/probe", "https://time.megacrit.com/", "https://api.steampowered.com/" })
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(address));
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/v1/relay/apac"))
        {
            request.Headers.Host = "evil.test";
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.SendAsync(request));
        }
        Assert.Equal(1, transport.Calls);
        transport.Status = HttpStatusCode.Redirect;
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync($"https://{Host}/v1/relay/apac"));
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task TheWebSocketUpgradeGoesThroughThePolicy()
    {
        // The transport answers 200 instead of 101, so the connection ends in closed without opening.
        var transport = new RecordingTransport();
        var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool opened = false;
        using (RelayConnection.Open(_policy, "apac", () => opened = true, _ => { }, reason => closed.TrySetResult(reason), transport))
            Assert.Same(closed.Task, await Task.WhenAny(closed.Task, Task.Delay(10000)));
        Assert.False(opened);
        Uri upgrade = Assert.Single(transport.Requests).Uri;
        Assert.Equal((Host, "/v1/relay/apac", 443), (upgrade.IdnHost, upgrade.AbsolutePath, upgrade.Port));
    }

    [Fact]
    public async Task ARefusedRegionNeverReachesTransport()
    {
        var transport = new RecordingTransport();
        var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (RelayConnection.Open(_policy, "../probe", () => { }, _ => { }, reason => closed.TrySetResult(reason), transport))
            Assert.Same(closed.Task, await Task.WhenAny(closed.Task, Task.Delay(10000)));
        Assert.Equal(0, transport.Calls);
        Assert.Contains("rejected", await closed.Task);
    }

    [Fact]
    public async Task ADroppedConnectionReportsItsReasonOnce()
    {
        var transport = new HangingTransport();
        var reasons = new List<string>();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection = RelayConnection.Open(_policy, "auto", () => { }, _ => { }, reason => { lock (reasons) reasons.Add(reason); closed.TrySetResult(); }, transport);
        await transport.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        connection.Drop("test drop");
        connection.Drop("again");
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        Assert.Equal(["test drop"], reasons);
    }

    /// <summary>Never answers, like a relay that has gone quiet, until the request is cancelled.</summary>
    private sealed class HangingTransport : HttpMessageHandler
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}

public sealed class RelaySettingsTests
{
    [Theory]
    [InlineData("relay = https://relay.example.test", "relay.example.test")]
    [InlineData("# comment\n\nrelay = wss://Relay.Example.test:443/\nenabled = yes\n", "relay.example.test")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("enabled = true", null)]
    [InlineData("relay = https://relay.example.test\nenabled = false", null)]
    public void ReadsTheHost(string? text, string? host) => Assert.Equal(host, RelaySettings.ParseHost(text, _ => { }));

    [Theory]
    [InlineData("relay = http://relay.example.test", "https:// or wss://")]
    [InlineData("relay = https://relay.example.test:8443", "port 443")]
    [InlineData("relay = https://relay.example.test/v1", "host name only")]
    [InlineData("relay = https://name@relay.example.test", "user name")]
    [InlineData("relay = https://127.0.0.1", "not an IP address")]
    [InlineData("relay = https://localhost", "not an IP address")]
    [InlineData("relay = https://relay.example.test\nrelay = https://relay.example.test", "set twice")]
    [InlineData("relay = https://relay.example.test\nenabled = maybe", "true or false")]
    [InlineData("relay = https://relay.example.test\nport = 1", "unknown setting")]
    [InlineData("relay https://relay.example.test", "key = value")]
    public void AnyProblemMeansNoRelay(string text, string problem)
    {
        var problems = new List<string>();
        Assert.Null(RelaySettings.ParseHost(text, problems.Add));
        Assert.Contains(problems, line => line.Contains(problem));
    }
}
