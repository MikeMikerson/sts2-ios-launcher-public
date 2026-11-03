using System.Net;
using System.Text.RegularExpressions;

namespace STS2Mobile.Steam.Network;

/// <summary>
/// Allows Steam's own hosts over HTTPS or WSS on port 443 and nothing else, apart from
/// <see cref="IsCloudStorage"/>.
/// </summary>
public static class SteamNetworkPolicy
{
    private static readonly string[] Roots =
    [
        "steampowered.com",
        "steamcommunity.com",
        "steamcontent.com",
        "steamstatic.com",
        // Valve's connection-manager hosts. Steam's own directory (api.steampowered.com) returns
        // only names under this domain, so no sign-in, ownership check or cloud call works without it.
        "steamserver.net"
    ];

    // Steam Cloud keeps file contents with three storage providers and gives the signed-in client a
    // signed address for each file, choosing the provider per file. Examples:
    // steamcloud-us-west3.s3.dualstack.us-west-2.amazonaws.com, steamcloud-tokyo / -us-west1 / -hk
    // .storage.googleapis.com, steamcloudquincy / steamcloudlrstyo.blob.core.windows.net.
    // This exception permits moving the signed-in player's own cloud saves only. It is not part of
    // IsAllowed: only two clients accept it (see CloudStorageAccess). The one made by
    // SteamHttpClient.CreateForCloudDownloads may GET from these hosts; the one made by
    // CreateForCloudUploads may only PUT or POST, and is used only after the player confirms a Send.
    // No other client reaches these hosts. Each pattern names Steam Cloud's own buckets in the
    // provider's virtual-host form; path-style addresses, which could name any bucket, are refused.
    private static readonly Regex[] CloudStorageHosts =
    [
        // Amazon S3
        new(@"^steamcloud-[a-z0-9]+(?:-[a-z0-9]+)*\.s3\.dualstack\.[a-z]{2}-[a-z]+-[0-9]\.amazonaws\.com\z", RegexOptions.CultureInvariant),
        // Google Cloud Storage
        new(@"^steamcloud-[a-z0-9]+(?:-[a-z0-9]+)*\.storage\.googleapis\.com\z", RegexOptions.CultureInvariant),
        // Microsoft Azure Blob Storage (account names are at most 24 lowercase letters and digits)
        new(@"^steamcloud[a-z0-9]{1,14}\.blob\.core\.windows\.net\z", RegexOptions.CultureInvariant)
    ];

    public static bool IsAllowedHost(string? host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253 ||
            host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) ||
            IPAddress.TryParse(host, out _))
            return false;

        string normalized = host.ToLowerInvariant();
        if (normalized.Split('.').Any(label => label.Length is 0 or > 63 ||
            label.StartsWith('-') || label.EndsWith('-')))
            return false;

        return Roots.Any(root => normalized == root ||
            normalized.EndsWith("." + root, StringComparison.Ordinal));
    }

    public static bool IsAllowed(Uri? uri)
    {
        return uri is { IsAbsoluteUri: true } &&
            (uri.Scheme == "https" || uri.Scheme == "wss") &&
            uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo) &&
            string.IsNullOrEmpty(uri.Fragment) && IsAllowedHost(uri.IdnHost);
    }

    /// <summary>A Steam Cloud storage address, over HTTPS on port 443. See <see cref="CloudStorageHosts"/> and <see cref="CloudStorageAccess"/>.</summary>
    public static bool IsCloudStorage(Uri? uri)
    {
        return uri is { IsAbsoluteUri: true } && uri.Scheme == "https" && uri.Port == 443 &&
            string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
            CloudStorageHosts.Any(pattern => pattern.IsMatch(uri.IdnHost));
    }

    /// <summary>
    /// Names the destination a refusal concerns, for diagnostics; null when the destination is
    /// allowed. Scheme, host and port only: the path, query and headers of a Steam transfer URL
    /// carry signed tokens and must never reach a log. This reports a decision and changes none.
    /// </summary>
    public static string? DescribeRefusal(Uri? uri)
    {
        if (IsAllowed(uri)) return null;
        return uri is { IsAbsoluteUri: true } ? $"scheme={uri.Scheme} host={uri.IdnHost} port={uri.Port}" : "not an absolute URL";
    }

    public static void RequireAllowed(Uri? uri)
    {
        if (!IsAllowed(uri))
            throw new NetworkPolicyException(NetworkPolicyFailure.InvalidDestination);
    }
}
