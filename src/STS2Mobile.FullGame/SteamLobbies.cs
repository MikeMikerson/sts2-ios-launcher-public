using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using STS2Mobile.Platform;
using STS2Mobile.Steam;
using STS2Mobile.Steam.Connection;
using STS2Mobile.Steam.Lobby;

namespace STS2Mobile.FullGame;

/// <summary>
/// The lobby service on the signed-in connection, as the host sees it (<see cref="IPhoneLobbies"/>): Steam's lobby types and enter
/// responses become plain numbers and <see cref="LobbyEntry"/>, so the interpreted host needs no SteamKit2 type.
/// </summary>
/// <remarks>
/// <c>STS2_STEAM_DROP_AFTER=&lt;seconds&gt;</c> closes the Steam connection that long after each lobby is entered or created, as a
/// lost network would, to test what a match does when Steam takes the phone out of its lobby.
/// </remarks>
internal sealed class SteamLobbies : IPhoneLobbies, IDisposable
{
    private readonly SteamLobbyService _lobbies;
    private readonly ILobbyConnection _connection;
    private readonly int _dropAfterSeconds =
        int.TryParse(System.Environment.GetEnvironmentVariable("STS2_STEAM_DROP_AFTER"), out int seconds) && seconds > 0 ? seconds : 0;

    public SteamLobbies(ILobbyConnection connection, ulong localId, Steam.Abstractions.ILogger logger)
    {
        _lobbies = new SteamLobbyService(connection, SteamApp.Id, logger);
        _connection = connection;
        LocalId = localId;
        // Steam has taken us out: QuickMatch hears it as our own departure, as it would from Steamworks.
        _lobbies.LobbiesLost += lost =>
        {
            foreach (ulong lobby in lost) Changed?.Invoke(new LobbyUpdate(LobbyChange.MemberLeft, lobby, LocalId));
        };
        _lobbies.ChatReceived += (lobby, sender, data) => Changed?.Invoke(new LobbyUpdate(LobbyChange.Chat, lobby, sender, data));
        _lobbies.MemberJoined += (lobby, member) => Changed?.Invoke(new LobbyUpdate(LobbyChange.MemberJoined, lobby, member));
        _lobbies.MemberLeft += (lobby, member) => Changed?.Invoke(new LobbyUpdate(LobbyChange.MemberLeft, lobby, member));
    }

    public ulong LocalId { get; }

    public event Action<LobbyUpdate>? Changed;

    public async Task<ulong?> CreateAsync(int type, int maxMembers, CancellationToken ct)
    {
        ulong? created = await _lobbies.CreateAsync((ELobbyType)type, maxMembers, ct).ConfigureAwait(false);
        if (created != null) DropLater();
        return created;
    }

    public async Task<LobbyEntry> JoinAsync(ulong lobby, CancellationToken ct)
    {
        var entry = Entry(await _lobbies.JoinAsync(lobby, ct).ConfigureAwait(false));
        if (entry == LobbyEntry.Ok) DropLater();
        return entry;
    }

    private void DropLater()
    {
        if (_dropAfterSeconds == 0 || _connection is not SteamConnection steam) return;
        _ = Task.Delay(TimeSpan.FromSeconds(_dropAfterSeconds)).ContinueWith(_ => steam.Drop($"STS2_STEAM_DROP_AFTER after {_dropAfterSeconds} s"), TaskScheduler.Default);
    }

    internal static LobbyEntry Entry(EChatRoomEnterResponse response) => response switch
    {
        EChatRoomEnterResponse.Success => LobbyEntry.Ok,
        EChatRoomEnterResponse.DoesntExist => LobbyEntry.DoesNotExist,
        EChatRoomEnterResponse.NotAllowed => LobbyEntry.NotAllowed,
        EChatRoomEnterResponse.Full => LobbyEntry.Full,
        EChatRoomEnterResponse.Banned => LobbyEntry.Banned,
        EChatRoomEnterResponse.Limited => LobbyEntry.Limited,
        EChatRoomEnterResponse.ClanDisabled or EChatRoomEnterResponse.CommunityBan
            or EChatRoomEnterResponse.MemberBlockedYou or EChatRoomEnterResponse.YouBlockedMember => LobbyEntry.Blocked,
        // Steamworks' k_EChatRoomEnterResponseRatelimitExceeded; SteamKit2's enum has no name for it.
        (EChatRoomEnterResponse)15 => LobbyEntry.RateLimited,
        _ => LobbyEntry.Error,
    };

    public void Leave(ulong lobby) => _lobbies.Leave(lobby);

    public async Task<IReadOnlyList<FoundLobby>?> SearchAsync(IReadOnlyDictionary<string, string> equal, int minFreeSlots, int maxResults, CancellationToken ct)
    {
        var found = await _lobbies.SearchAsync(equal, minFreeSlots, maxResults, ct).ConfigureAwait(false);
        return found?.Select(lobby => new FoundLobby(lobby.Id, lobby.Members, lobby.Data)).ToList();
    }

    public void SetData(ulong lobby, string key, string value) => _lobbies.SetData(lobby, key, value);

    public void DeleteData(ulong lobby, string key) => _lobbies.DeleteData(lobby, key);

    public string GetData(ulong lobby, string key) => _lobbies.GetData(lobby, key);

    public bool SetType(ulong lobby, int type) => _lobbies.SetType(lobby, (ELobbyType)type);

    public void SetJoinable(ulong lobby, bool joinable) => _lobbies.SetJoinable(lobby, joinable);

    public ulong Owner(ulong lobby) => _lobbies.Owner(lobby);

    public IReadOnlyList<ulong> Members(ulong lobby) => _lobbies.Members(lobby);

    public bool SendChat(ulong lobby, byte[] data, int length, ulong target) => _lobbies.SendChat(lobby, data, length, target);

    public string? MemberName(ulong member) => _lobbies.MemberName(member);

    public void Dispose() => _lobbies.Dispose();
}
