using SteamKit2;

namespace STS2Mobile.Steam.Depot;

/// <summary>What Steam says one branch of the game holds for one depot.</summary>
/// <param name="Branch">Steam's branch name.</param>
/// <param name="BuildId">Steam's build of the whole app. It moves when any depot changes.</param>
/// <param name="ManifestId">The depot's files on this branch, which is what decides whether they changed.</param>
public sealed record BranchBuild(string Branch, uint BuildId, ulong ManifestId)
{
    /// <summary>The branch in the app's product information, or null if it is not there or needs a password.</summary>
    public static BranchBuild? Read(KeyValue depots, uint depotId, string branch)
    {
        var details = depots["branches"][branch];
        if (details["pwdrequired"].AsBoolean()) return null;
        var entry = depots[depotId.ToString()]["manifests"][branch];
        // Steam's older form has the manifest as the branch's value, not under "gid".
        string? manifest = entry["gid"].Value ?? entry.Value;
        if (!ulong.TryParse(manifest, out ulong manifestId) || manifestId == 0) return null;
        return new BranchBuild(branch, details["buildid"].AsUnsignedInteger(), manifestId);
    }
}

/// <summary>Reads the game's branches from Steam. It downloads nothing and changes nothing.</summary>
public static class SteamBuilds
{
    public static Task<Dictionary<string, BranchBuild?>> ReadAsync(Connection.SteamConnection connection, uint appId, uint depotId, IEnumerable<string> branches, CancellationToken cancellationToken) =>
        // The connection's first sign-in blocks its caller.
        Task.Run(async () =>
        {
            var apps = connection.Apps;
            var tokens = await apps.PICSGetAccessTokens(appId, null).ToTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            tokens.AppTokens.TryGetValue(appId, out ulong token);
            var info = await apps.PICSGetProductInfo(new SteamApps.PICSRequest(appId, token), null).ToTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            var app = info.Results?.SelectMany(result => result.Apps.Values).FirstOrDefault(found => found.ID == appId)
                ?? throw new InvalidDataException("Steam returned no information about the game.");
            return branches.ToDictionary(branch => branch, branch => BranchBuild.Read(app.KeyValues["depots"], depotId, branch), StringComparer.Ordinal);
        }, cancellationToken);
}
