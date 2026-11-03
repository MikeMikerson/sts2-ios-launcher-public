namespace STS2Mobile.Steam.Network;

/// <summary>What a <see cref="SteamOnlyHandler"/> may do with Steam Cloud's storage hosts (<see cref="SteamNetworkPolicy.IsCloudStorage"/>).</summary>
public enum CloudStorageAccess
{
    /// <summary>Nothing: Steam's own hosts only. Every client but the two below.</summary>
    None,
    /// <summary>GET from the storage hosts, for downloading the player's cloud saves. Steam's own hosts as usual.</summary>
    Download,
    /// <summary>
    /// PUT and POST only, to the storage hosts or Steam's own hosts, for sending the player's saves after they confirm it.
    /// Every other method is refused, so this client cannot read or delete anything.
    /// </summary>
    Upload
}
