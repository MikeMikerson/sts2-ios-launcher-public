using SteamKit2;

namespace STS2Mobile.Steam.Connection;

public sealed class SteamLoginFailedException(EResult result) : InvalidOperationException($"Login failed: {result}")
{
    public EResult Result { get; } = result;
}
