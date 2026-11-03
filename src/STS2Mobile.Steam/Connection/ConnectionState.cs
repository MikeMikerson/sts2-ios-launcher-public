namespace STS2Mobile.Steam.Connection;

public enum ConnectionState
{
    Idle,
    Connecting,
    Connected,
    Draining,
    Backoff,
}
