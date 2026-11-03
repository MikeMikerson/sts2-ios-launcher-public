namespace STS2Mobile.Platform;

/// <summary>
/// Checks every request against one network policy before transport, refuses a Host header that names another host, and refuses
/// redirects instead of following them. <see cref="GameOnlyHandler"/> and <see cref="RelayOnlyHandler"/> differ only in their policy.
/// Every refusal is an <see cref="HttpRequestException"/>.
/// </summary>
public abstract class PolicyHandler : DelegatingHandler
{
    private readonly string _name;

    /// <param name="name">Who refused, at the start of each refusal's message: "Game" or "Relay".</param>
    private protected PolicyHandler(string name, HttpMessageHandler transport) : base(transport) => _name = name;

    /// <summary>The transport each policy handler is given outside tests: it follows no redirect and uses no proxy and no cookies.</summary>
    internal static SocketsHttpHandler CreateTransport() => new() { AllowAutoRedirect = false, UseProxy = false, UseCookies = false };

    /// <summary>Throws <see cref="HttpRequestException"/> when the policy refuses the address.</summary>
    private protected abstract void RequireAllowed(Uri? uri);

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Validate(request);
        return RejectRedirect(base.Send(request, cancellationToken));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Validate(request);
        return RejectRedirect(await base.SendAsync(request, cancellationToken).ConfigureAwait(false));
    }

    private void Validate(HttpRequestMessage request)
    {
        RequireAllowed(request.RequestUri);
        // An allowed address must not carry another virtual host.
        if (HostNames.OverridesHost(request)) throw new HttpRequestException(_name + " Host header override rejected.");
    }

    private HttpResponseMessage RejectRedirect(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 300 and <= 399)
        {
            response.Dispose();
            throw new HttpRequestException(_name + " redirect rejected.");
        }
        return response;
    }
}
