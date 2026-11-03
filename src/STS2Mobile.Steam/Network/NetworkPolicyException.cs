namespace STS2Mobile.Steam.Network;

/// <summary>UI, logging and retry decisions use <see cref="Failure"/>, which is stable; the message is not.</summary>
public sealed class NetworkPolicyException : HttpRequestException
{
    public NetworkPolicyFailure Failure { get; }

    public NetworkPolicyException(NetworkPolicyFailure failure) : base(failure switch
    {
        NetworkPolicyFailure.InvalidDestination => "Destination blocked by the Steam network policy.",
        NetworkPolicyFailure.HostOverride => "Host override blocked by the Steam network policy.",
        NetworkPolicyFailure.Redirect => "Redirect blocked by the Steam network policy.",
        NetworkPolicyFailure.Method => "Method blocked by the Steam network policy.",
        _ => "Request blocked by the Steam network policy."
    })
    {
        Failure = failure;
    }
}
