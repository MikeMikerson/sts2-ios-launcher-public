namespace STS2Mobile.Steam.Network;

public static class SteamHttpClient
{
    // HttpClient's own default.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(100);

    public static HttpClient Create(TimeSpan? timeout = null)
    {
        return new HttpClient(new SteamOnlyHandler())
        {
            Timeout = timeout ?? DefaultTimeout
        };
    }

    /// <summary>For downloading the player's cloud files only; see <see cref="SteamOnlyHandler.ForCloudDownloads"/>.</summary>
    public static HttpClient CreateForCloudDownloads(TimeSpan? timeout = null)
    {
        return new HttpClient(SteamOnlyHandler.ForCloudDownloads())
        {
            Timeout = timeout ?? DefaultTimeout
        };
    }

    /// <summary>For sending the player's cloud files only, after they confirm it; see <see cref="SteamOnlyHandler.ForCloudUploads"/>.</summary>
    public static HttpClient CreateForCloudUploads(TimeSpan? timeout = null)
    {
        return new HttpClient(SteamOnlyHandler.ForCloudUploads())
        {
            Timeout = timeout ?? DefaultTimeout
        };
    }
}
