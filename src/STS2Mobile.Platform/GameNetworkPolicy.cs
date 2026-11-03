namespace STS2Mobile.Platform;

public static class GameNetworkPolicy
{
    // Kept separate from the Steam-only service policy.
    private static readonly string[] Roots = ["megacrit.com", "steampowered.com", "steamcommunity.com", "steamcontent.com", "steamstatic.com"];

    public static bool IsAllowed(Uri? uri) =>
        uri is { IsAbsoluteUri: true } && uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        HostNames.IsPlainDnsName(uri.IdnHost) && HostNames.IsUnder(uri.IdnHost, Roots);

    public static void RequireAllowed(Uri? uri)
    {
        if (!IsAllowed(uri)) throw new HttpRequestException("Game destination rejected by network policy.");
    }
}
