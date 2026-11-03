#if QUICKMATCH
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMatch.Lobby;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// QuickMatch's lobbies on the phone: the mod's lobby interface answered by the launcher's Steam lobbies (<see cref="IPhoneLobbies"/>),
/// because the phone has no Steamworks. They are the same lobbies, data and chat that desktop players reach through Steamworks, so
/// phone and desktop players match each other. The mod carries the game's messages over that chat itself.
/// </summary>
internal sealed class PhoneLobbyBackend : ILobbyBackend
{
    private readonly IPhoneLobbies _lobbies;

    public PhoneLobbyBackend(IPhoneLobbies lobbies)
    {
        _lobbies = lobbies;
        lobbies.Changed += update => Changed?.Invoke(new LobbyEvent(Kind(update.Change), update.Lobby, update.Member, update.Data));
    }

    /// <summary>The game only starts after Steam sign-in and the ownership check.</summary>
    public bool IsAvailable => true;

    public ulong LocalId => _lobbies.LocalId;

    /// <summary>The phone's clock; QuickMatch only uses it to tell older lobbies from newer ones.</summary>
    public uint ServerTime => (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public event Action<LobbyEvent>? Changed;

    public Task<ulong?> CreateAsync(LobbyKind kind, int maxMembers, CancellationToken ct) => _lobbies.CreateAsync((int)kind, maxMembers, ct);

    public async Task<LobbyJoinResult> JoinAsync(ulong lobby, CancellationToken ct) => await _lobbies.JoinAsync(lobby, ct) switch
    {
        LobbyEntry.Ok => LobbyJoinResult.Ok,
        LobbyEntry.DoesNotExist => LobbyJoinResult.DoesNotExist,
        LobbyEntry.NotAllowed => LobbyJoinResult.NotAllowed,
        LobbyEntry.Full => LobbyJoinResult.Full,
        LobbyEntry.Banned => LobbyJoinResult.Banned,
        LobbyEntry.Limited => LobbyJoinResult.Limited,
        LobbyEntry.Blocked => LobbyJoinResult.Blocked,
        LobbyEntry.RateLimited => LobbyJoinResult.RateLimited,
        _ => LobbyJoinResult.Error,
    };

    public void Leave(ulong lobby) => _lobbies.Leave(lobby);

    public async Task<IReadOnlyList<LobbyListing>?> SearchAsync(LobbyQuery query, CancellationToken ct)
    {
        var found = await _lobbies.SearchAsync(query.Equal, query.MinFreeSlots, query.MaxResults, ct);
        return found?.Select(lobby => new LobbyListing(lobby.Id, lobby.Members,
            query.ReadKeys.ToDictionary(key => key, key => lobby.Data.TryGetValue(key, out string? value) ? value : ""))).ToList();
    }

    public void SetData(ulong lobby, string key, string value) => _lobbies.SetData(lobby, key, value);

    public void DeleteData(ulong lobby, string key) => _lobbies.DeleteData(lobby, key);

    public string GetData(ulong lobby, string key) => _lobbies.GetData(lobby, key);

    public bool SetKind(ulong lobby, LobbyKind kind) => _lobbies.SetType(lobby, (int)kind);

    public void SetJoinable(ulong lobby, bool joinable) => _lobbies.SetJoinable(lobby, joinable);

    public ulong Owner(ulong lobby) => _lobbies.Owner(lobby);

    public IReadOnlyList<ulong> Members(ulong lobby) => _lobbies.Members(lobby);

    public bool SendChat(ulong lobby, byte[] data, int length, ulong target) => _lobbies.SendChat(lobby, data, length, target);

    public string? PlayerName(ulong id) => _lobbies.MemberName(id);

    private static LobbyEventKind Kind(LobbyChange change) => change switch
    {
        LobbyChange.Chat => LobbyEventKind.Chat,
        LobbyChange.MemberJoined => LobbyEventKind.MemberJoined,
        _ => LobbyEventKind.MemberLeft,
    };
}
#endif
