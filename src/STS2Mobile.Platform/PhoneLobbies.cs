namespace STS2Mobile.Platform;

/// <summary>How entering a lobby went, as Steam reports it.</summary>
public enum LobbyEntry
{
    Ok,
    DoesNotExist,
    NotAllowed,
    Full,
    Error,
    Banned,
    Limited,
    Blocked,
    RateLimited,
}

public enum LobbyChange
{
    /// <summary>A lobby chat message; <see cref="LobbyUpdate.Data"/> holds its bytes.</summary>
    Chat,
    MemberJoined,
    MemberLeft,
}

/// <summary>Something that happened in a lobby we are in.</summary>
public readonly record struct LobbyUpdate(LobbyChange Change, ulong Lobby, ulong Member, byte[]? Data = null);

/// <summary>One search result: the lobby, its member count and its data values.</summary>
public readonly record struct FoundLobby(ulong Id, int Members, IReadOnlyDictionary<string, string> Data);

/// <summary>
/// Steam lobbies and lobby chat on the signed-in connection, without SteamKit2's types, so the
/// interpreted host can reach them. The launcher binds them in <see cref="RuntimeServices.BindLobbies"/> before the game starts.
/// Lobby types use Steam's ELobbyType numbers (0 private, 1 friends only, 2 public, 3 invisible).
/// </summary>
public interface IPhoneLobbies
{
    ulong LocalId { get; }

    /// <summary>Chat and membership changes in lobbies we are in. Raised on Steam's threads.</summary>
    event Action<LobbyUpdate>? Changed;

    Task<ulong?> CreateAsync(int type, int maxMembers, CancellationToken ct);

    Task<LobbyEntry> JoinAsync(ulong lobby, CancellationToken ct);

    void Leave(ulong lobby);

    /// <summary>Lobbies worldwide matching every value in <paramref name="equal"/> with enough free slots; null on a Steam error.</summary>
    Task<IReadOnlyList<FoundLobby>?> SearchAsync(IReadOnlyDictionary<string, string> equal, int minFreeSlots, int maxResults, CancellationToken ct);

    void SetData(ulong lobby, string key, string value);

    void DeleteData(ulong lobby, string key);

    string GetData(ulong lobby, string key);

    bool SetType(ulong lobby, int type);

    void SetJoinable(ulong lobby, bool joinable);

    ulong Owner(ulong lobby);

    IReadOnlyList<ulong> Members(ulong lobby);

    bool SendChat(ulong lobby, byte[] data, int length, ulong target);

    /// <summary>The Steam name of a member of a lobby we are in, or null.</summary>
    string? MemberName(ulong member);
}
