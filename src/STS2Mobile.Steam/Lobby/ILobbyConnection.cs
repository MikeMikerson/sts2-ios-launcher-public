using SteamKit2;

namespace STS2Mobile.Steam.Lobby;

/// <summary>What the lobby service needs of the signed-in connection (<see cref="Connection.SteamConnection"/>), so tests can stand in for it.</summary>
public interface ILobbyConnection
{
    SteamClient Client { get; }

    /// <summary>True while signed in; nothing is sent otherwise.</summary>
    bool IsConnected { get; }

    /// <summary>Connects first if needed, which blocks.</summary>
    SteamMatchmaking Matchmaking { get; }

    IDisposable Subscribe<TCallback>(Action<TCallback> handler) where TCallback : CallbackMsg;

    void SuspendIdleTimeout();

    void ResumeIdleTimeout();
}
