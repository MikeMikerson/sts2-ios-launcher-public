using SteamKit2;
using SteamKit2.CDN;
using STS2Mobile.Steam.Connection;
using STS2Mobile.Steam.Network;

namespace STS2Mobile.Steam.Depot;

/// <summary>
/// One depot manifest and its chunks from Steam's content servers, for the signed-in owner. Every request goes through the
/// connection's <see cref="SteamOnlyHandler"/>, so <c>NETWORK.md</c> holds: a server the policy would refuse is never tried.
/// </summary>
public sealed class SteamDepot : IDepotChunkSource, IDisposable
{
    private const int MaxServers = 8;
    private readonly Client _cdn;
    private readonly uint _depotId;
    private readonly byte[] _key;
    private readonly IReadOnlyList<Server> _servers;
    private int _next;

    private SteamDepot(Client cdn, uint depotId, byte[] key, IReadOnlyList<Server> servers, DepotManifest manifest)
    {
        _cdn = cdn;
        _depotId = depotId;
        _key = key;
        _servers = servers;
        Manifest = manifest;
    }

    public DepotManifest Manifest { get; }

    public static Task<SteamDepot> OpenAsync(SteamConnection connection, uint appId, uint depotId, ulong manifestId, string branch, CancellationToken cancellationToken) =>
        // The connection's first sign-in blocks its caller.
        Task.Run(async () =>
        {
            var keyResult = await connection.Apps.GetDepotDecryptionKey(depotId, appId).ToTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (keyResult.Result != EResult.OK) throw new UnauthorizedAccessException("Steam refused the game's depot key: " + keyResult.Result);
            var content = connection.Content;
            var servers = (await content.GetServersForSteamPipe().WaitAsync(cancellationToken).ConfigureAwait(false))
                .Where(server => IsUsable(server, appId)).OrderBy(server => server.WeightedLoad).Take(MaxServers).ToList();
            if (servers.Count == 0) throw new InvalidOperationException("Steam offered no content server this app may use.");
            ulong code = await connection.Content.GetManifestRequestCode(depotId, appId, manifestId, branch).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (code == 0) throw new UnauthorizedAccessException("Steam refused the game's file list.");
            var cdn = new Client(connection.Client);
            try
            {
                var manifest = await DownloadManifestAsync(cdn, depotId, manifestId, code, servers, keyResult.DepotKey, cancellationToken).ConfigureAwait(false);
                return new SteamDepot(cdn, depotId, keyResult.DepotKey, servers, manifest);
            }
            catch
            {
                cdn.Dispose();
                throw;
            }
        }, cancellationToken);

    private static async Task<DepotManifest> DownloadManifestAsync(Client cdn, uint depotId, ulong manifestId, ulong code, List<Server> servers, byte[] key, CancellationToken cancellationToken)
    {
        for (int index = 0; ; index++)
        {
            try
            {
                var manifest = await cdn.DownloadManifestAsync(depotId, manifestId, code, servers[index], key).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (manifest.FilenamesEncrypted && !manifest.DecryptFilenames(key)) throw new InvalidDataException("The game's file list could not be read.");
                return manifest;
            }
            catch (Exception) when (index + 1 < servers.Count && !cancellationToken.IsCancellationRequested) { }
        }
    }

    /// <summary>Round the servers, so a retried chunk goes to another one and the load spreads while they all work.</summary>
    public Task<int> ReadAsync(DepotManifest.ChunkData chunk, byte[] destination, CancellationToken cancellationToken)
    {
        var server = _servers[(int)((uint)Interlocked.Increment(ref _next) % _servers.Count)];
        return _cdn.DownloadDepotChunkAsync(_depotId, chunk, server, destination, _key).WaitAsync(cancellationToken);
    }

    /// <summary>A server Steam offers for this app that the network policy allows, reached directly.</summary>
    public static bool IsUsable(Server server, uint appId) =>
        IsUsable(server.Protocol == Server.ConnectionProtocol.HTTPS, server.Host, server.Port, server.UseAsProxy, server.SteamChinaOnly, server.AllowedAppIds, appId);

    public static bool IsUsable(bool https, string? host, int port, bool proxy, bool chinaOnly, uint[]? allowedApps, uint appId) =>
        https && port == 443 && !proxy && !chinaOnly && SteamNetworkPolicy.IsAllowedHost(host)
        && (allowedApps is not { Length: > 0 } || allowedApps.Contains(appId));

    public void Dispose() => _cdn.Dispose();
}
