using SteamKit2;

namespace STS2Mobile.Steam.Connection;

/// <summary>Steam answered a Cloud request with a result other than OK. Retry and skip decisions use <see cref="Result"/>, not the message.</summary>
public sealed class CloudRequestException : InvalidOperationException
{
    public string Method { get; }
    public EResult Result { get; }

    public CloudRequestException(string method, EResult result) : base($"Cloud.{method} failed: {result}")
    {
        Method = method;
        Result = result;
    }
}
