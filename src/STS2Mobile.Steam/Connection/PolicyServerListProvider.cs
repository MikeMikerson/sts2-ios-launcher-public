using SteamKit2;
using SteamKit2.Discovery;
using STS2Mobile.Steam.Network;

namespace STS2Mobile.Steam.Connection;

/// <summary>
/// Supplies SteamKit with connection servers that the network policy can actually reach.
/// Steam's directory also lists servers on other ports; offering those would only produce
/// connection attempts the policy rejects, so they are filtered out before SteamKit sees them.
/// </summary>
public sealed class PolicyServerListProvider : IServerListProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<ServerRecord> _servers = [];

    public DateTime LastServerListRefresh { get; private set; }

    public static bool IsReachable(ServerRecord server) =>
        server.ProtocolTypes.HasFlag(ProtocolTypes.WebSocket) &&
        SteamNetworkPolicy.IsAllowed(new UriBuilder("wss", server.GetHost(), server.GetPort()).Uri);

    public async Task<IEnumerable<ServerRecord>> FetchServerListAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_servers.Count == 0)
            {
                // The directory request itself goes through the guarded Steam HTTP client.
                var directory = SteamConfiguration.Create(builder => builder
                    .WithProtocolTypes(ProtocolTypes.WebSocket)
                    .WithHttpClientFactory(_ => SteamHttpClient.Create()));
                Store(await SteamDirectory.LoadAsync(directory).ConfigureAwait(false));
                if (_servers.Count == 0)
                    throw new NetworkPolicyException(NetworkPolicyFailure.InvalidDestination);
            }
            return _servers;
        }
        finally { _gate.Release(); }
    }

    public Task UpdateServerListAsync(IEnumerable<ServerRecord> endpoints)
    {
        Store(endpoints);
        return Task.CompletedTask;
    }

    private void Store(IEnumerable<ServerRecord> endpoints)
    {
        _servers = endpoints.Where(IsReachable).ToList();
        LastServerListRefresh = DateTime.UtcNow;
    }
}
