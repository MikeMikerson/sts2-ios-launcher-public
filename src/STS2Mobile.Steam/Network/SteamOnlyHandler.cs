namespace STS2Mobile.Steam.Network;

/// <summary>
/// Checks every request against <see cref="SteamNetworkPolicy"/> before it is sent, and refuses
/// redirects instead of following them.
/// </summary>
public sealed class SteamOnlyHandler : DelegatingHandler
{
    private readonly CloudStorageAccess _cloudStorage;

    public SteamOnlyHandler() : this(CreateTransport()) { }

    // Internal injection allows tests to run without contacting any server.
    internal SteamOnlyHandler(HttpMessageHandler transport, CloudStorageAccess cloudStorage = CloudStorageAccess.None) : base(transport) =>
        _cloudStorage = cloudStorage;

    /// <summary>
    /// The one handler that may also GET from Steam Cloud's storage hosts
    /// (<see cref="SteamNetworkPolicy.IsCloudStorage"/>). Every other rule still applies.
    /// </summary>
    internal static SteamOnlyHandler ForCloudDownloads() => new(CreateTransport(), CloudStorageAccess.Download);

    /// <summary>
    /// The one handler that may PUT or POST to Steam Cloud's storage hosts, and that refuses every other method
    /// (<see cref="CloudStorageAccess.Upload"/>). Every other rule still applies.
    /// </summary>
    internal static SteamOnlyHandler ForCloudUploads() => new(CreateTransport(), CloudStorageAccess.Upload);

    private static SocketsHttpHandler CreateTransport() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false
    };

    protected override HttpResponseMessage Send(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        return RejectRedirect(base.Send(request, cancellationToken));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return RejectRedirect(response);
    }

    private void Validate(HttpRequestMessage request)
    {
        switch (_cloudStorage)
        {
            case CloudStorageAccess.Download:
                if (!(request.Method == HttpMethod.Get && SteamNetworkPolicy.IsCloudStorage(request.RequestUri)))
                    SteamNetworkPolicy.RequireAllowed(request.RequestUri);
                break;
            case CloudStorageAccess.Upload:
                if (request.Method != HttpMethod.Put && request.Method != HttpMethod.Post)
                    throw new NetworkPolicyException(NetworkPolicyFailure.Method);
                if (!SteamNetworkPolicy.IsCloudStorage(request.RequestUri))
                    SteamNetworkPolicy.RequireAllowed(request.RequestUri);
                break;
            default:
                SteamNetworkPolicy.RequireAllowed(request.RequestUri);
                break;
        }
        // Prevent an allowed destination from tunnelling a different virtual host.
        if (request.Headers.Host is { } host &&
            !string.Equals(host, request.RequestUri!.IdnHost, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(host, request.RequestUri.IdnHost + ":443", StringComparison.OrdinalIgnoreCase))
            throw new NetworkPolicyException(NetworkPolicyFailure.HostOverride);
    }

    private static HttpResponseMessage RejectRedirect(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 300 and <= 399)
        {
            response.Dispose();
            throw new NetworkPolicyException(NetworkPolicyFailure.Redirect);
        }
        return response;
    }
}
