using SteamKit2;
using SteamKit2.Internal;
using STS2Mobile.Steam.Abstractions;
using STS2Mobile.Steam.Lobby;

namespace STS2Mobile.Steam.Tests;

// Steam's answers are built as the bytes Steam sends and handed to SteamKit2's own handlers, as a connection would. Nothing is sent.
public sealed class SteamLobbyServiceTests : IDisposable
{
    private const uint AppId = SteamApp.Id;
    private const ulong Lobby = 109775241000000001;
    private const ulong Self = 76561198000000001;
    private const ulong Friend = 76561198000000002;
    private const ulong Third = 76561198000000003;
    private readonly FakeLobbyConnection _connection = new();
    private readonly SteamLobbyService _service;

    public SteamLobbyServiceTests() => _service = new SteamLobbyService(_connection, AppId, new QuietLogger());

    public void Dispose() => _service.Dispose();

    [Fact]
    public void ChatMessageCarriesAppLobbyTargetAndBytes()
    {
        var message = LobbyChatHandler.Build(AppId, Lobby, Friend, [1, 2, 3]);
        var sent = new ClientMsgProtobuf<CMsgClientMMSSendLobbyChatMsg>(Packet(message.MsgType, message.Serialize()));
        Assert.Equal(EMsg.ClientMMSSendLobbyChatMsg, sent.MsgType);
        Assert.Equal(AppId, sent.ProtoHeader.routing_appid);
        Assert.Equal(AppId, sent.Body.app_id);
        Assert.Equal(Lobby, sent.Body.steam_id_lobby);
        Assert.Equal(Friend, sent.Body.steam_id_target);
        Assert.Equal(new byte[] { 1, 2, 3 }, sent.Body.lobby_message);
    }

    [Fact]
    public void ChatForThisGameIsRaisedAndOtherGamesAreIgnored()
    {
        var received = new List<(ulong Lobby, ulong Sender, byte[] Data)>();
        _service.ChatReceived += (lobby, sender, data) => received.Add((lobby, sender, data));
        var handler = _connection.Client.GetHandler<LobbyChatHandler>()!;
        handler.HandleMsg(Chat(AppId, [9, 8]));
        handler.HandleMsg(Chat(AppId + 1, [7]));
        var only = Assert.Single(received);
        Assert.Equal((Lobby, Friend), (only.Lobby, only.Sender));
        Assert.Equal(new byte[] { 9, 8 }, only.Data);
    }

    [Fact]
    public void JoinAnswerGivesOwnerMembersAndTheirSteamNames()
    {
        Joined();
        Assert.Equal(Friend, _service.Owner(Lobby));
        Assert.Equal([Friend, Self], _service.Members(Lobby));
        Assert.Equal("Friend Name", _service.MemberName(Friend));
        Assert.Null(_service.MemberName(Third));
    }

    [Fact]
    public void MemberJoiningAndLeavingIsRaisedAndKeptInTheMemberList()
    {
        Joined();
        var joined = new List<ulong>();
        var left = new List<ulong>();
        _service.MemberJoined += (lobby, member) => joined.Add(member);
        _service.MemberLeft += (lobby, member) => left.Add(member);
        var matchmaking = _connection.Client.GetHandler<SteamMatchmaking>()!;

        var arrival = new ClientMsgProtobuf<CMsgClientMMSUserJoinedLobby>(EMsg.ClientMMSUserJoinedLobby);
        arrival.Body.app_id = AppId;
        arrival.Body.steam_id_lobby = Lobby;
        arrival.Body.steam_id_user = Third;
        arrival.Body.persona_name = "Third Name";
        matchmaking.HandleMsg(Packet(arrival.MsgType, arrival.Serialize()));
        _connection.Callbacks.RunCallbacks();
        Assert.Equal([Third], joined);
        Assert.Contains(Third, _service.Members(Lobby));
        Assert.Equal("Third Name", _service.MemberName(Third));

        var departure = new ClientMsgProtobuf<CMsgClientMMSUserLeftLobby>(EMsg.ClientMMSUserLeftLobby);
        departure.Body.app_id = AppId;
        departure.Body.steam_id_lobby = Lobby;
        departure.Body.steam_id_user = Third;
        matchmaking.HandleMsg(Packet(departure.MsgType, departure.Serialize()));
        _connection.Callbacks.RunCallbacks();
        Assert.Equal([Third], left);
        Assert.DoesNotContain(Third, _service.Members(Lobby));
    }

    [Fact]
    public void BeingInALobbyHoldsTheConnectionOpenUntilLeft()
    {
        _service.Hold(Lobby);
        _service.Hold(Lobby);
        Assert.Equal(1, _connection.Holds);
        _service.Leave(Lobby);
        Assert.Equal(0, _connection.Holds);
        _service.Leave(Lobby);
        Assert.Equal(0, _connection.Holds);
    }

    [Fact]
    public void ALostConnectionForgetsTheLobbiesReleasesTheConnectionAndSaysSoOnce()
    {
        Joined();
        var lost = new List<IReadOnlyList<ulong>>();
        _service.LobbiesLost += lobbies => lost.Add(lobbies);
        _service.ConnectionLost();
        Assert.Equal([Lobby], Assert.Single(lost));
        Assert.Empty(_service.Joined);
        Assert.Equal(0, _connection.Holds);
        Assert.False(_service.SendChat(Lobby, [1], 1, 0));
        _service.ConnectionLost();
        _service.Leave(Lobby);
        Assert.Single(lost);
        Assert.Equal(0, _connection.Holds);
    }

    [Fact]
    public void ChatIsRefusedOutsideALobbyAndWhileDisconnected()
    {
        Assert.False(_service.SendChat(Lobby, [1], 1, 0));
        _service.Hold(Lobby);
        _connection.IsConnected = false;
        Assert.False(_service.SendChat(Lobby, [1], 1, 0));
    }

    [Fact]
    public void DataOfALobbyNotOwnedComesFromSteamAndCannotBeChanged()
    {
        Joined();
        Assert.Equal("", _service.GetData(Lobby, "qm_version"));
        _service.SetData(Lobby, "qm_version", "1");
        Assert.False(_service.SetType(Lobby, ELobbyType.Public));
        Assert.Equal("", _service.GetData(Lobby, "qm_version"));
    }

    [Fact]
    public void ClosingAnOwnedLobbyMakesItPrivateAndOpeningRestoresTheLatestType()
    {
        var owned = new OwnedLobby(ELobbyType.Public, 4);
        owned.Set("qm_version", "1");
        owned.SetJoinable(false);
        Assert.Equal(ELobbyType.Private, owned.Snapshot().Type);
        owned.SetType(ELobbyType.FriendsOnly);
        Assert.Equal(ELobbyType.Private, owned.Snapshot().Type);
        owned.SetJoinable(true);
        var (type, data) = owned.Snapshot();
        Assert.Equal(ELobbyType.FriendsOnly, type);
        Assert.Equal("1", data["qm_version"]);
        Assert.True(owned.Delete("qm_version"));
        Assert.False(owned.Delete("qm_version"));
        Assert.Equal("", owned.Get("qm_version"));
    }

    [Fact]
    public void SearchAsksForEqualValuesFreeSlotsAndTheWholeWorld()
    {
        var filters = SteamLobbyService.Filters(new Dictionary<string, string> { ["qm_version"] = "1", ["qm_mode"] = "standard" }, 1);
        Assert.Equal(4, filters.Count);
        Assert.All(filters.OfType<SteamMatchmaking.Lobby.StringFilter>(), filter => Assert.Equal(ELobbyComparison.Equal, filter.Comparison));
        Assert.Equal(["qm_mode", "qm_version"], filters.OfType<SteamMatchmaking.Lobby.StringFilter>().Select(filter => filter.Key).Order());
        Assert.Equal(1, Assert.Single(filters.OfType<SteamMatchmaking.Lobby.SlotsAvailableFilter>()).SlotsAvailable);
        Assert.Equal(ELobbyDistanceFilter.Worldwide, Assert.Single(filters.OfType<SteamMatchmaking.Lobby.DistanceFilter>()).Value);
        Assert.Empty(SteamLobbyService.Filters(new Dictionary<string, string>(), 0).OfType<SteamMatchmaking.Lobby.SlotsAvailableFilter>());
    }

    [Fact]
    public async Task OfflineASearchHostOrJoinIsRefusedRatherThanThrown()
    {
        // The game may have started with no connection; connecting then fails as it does with no network.
        _connection.Unreachable = new TimeoutException("Steam connection timed out");
        Assert.Null(await _service.SearchAsync(new Dictionary<string, string>(), 1, 10, CancellationToken.None));
        Assert.Null(await _service.CreateAsync(ELobbyType.FriendsOnly, 4, CancellationToken.None));
        Assert.Equal(EChatRoomEnterResponse.Error, await _service.JoinAsync(Lobby, CancellationToken.None));
        Assert.Empty(_service.Joined);
        Assert.Equal(0, _connection.Holds);
    }

    [Fact]
    public async Task ACancelledRequestIsStillCancelledOffline()
    {
        _connection.Unreachable = new TimeoutException("Steam connection timed out");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.SearchAsync(new Dictionary<string, string>(), 1, 10, cancelled.Token));
    }

    /// <summary>Steam's answer to a join, owned by <see cref="Friend"/>, then the service told it is in.</summary>
    private void Joined()
    {
        var answer = new ClientMsgProtobuf<CMsgClientMMSJoinLobbyResponse>(EMsg.ClientMMSJoinLobbyResponse);
        answer.Body.app_id = AppId;
        answer.Body.steam_id_lobby = Lobby;
        answer.Body.chat_room_enter_response = (int)EChatRoomEnterResponse.Success;
        answer.Body.max_members = 4;
        answer.Body.lobby_type = (int)ELobbyType.Public;
        answer.Body.steam_id_owner = Friend;
        answer.Body.members.Add(new CMsgClientMMSJoinLobbyResponse.Member { steam_id = Friend, persona_name = "Friend Name" });
        answer.Body.members.Add(new CMsgClientMMSJoinLobbyResponse.Member { steam_id = Self, persona_name = "Self Name" });
        _connection.Client.GetHandler<SteamMatchmaking>()!.HandleMsg(Packet(answer.MsgType, answer.Serialize()));
        _connection.Callbacks.RunCallbacks();
        _service.Hold(Lobby);
    }

    private static IPacketMsg Chat(uint appId, byte[] data)
    {
        var message = new ClientMsgProtobuf<CMsgClientMMSLobbyChatMsg>(EMsg.ClientMMSLobbyChatMsg);
        message.Body.app_id = appId;
        message.Body.steam_id_lobby = Lobby;
        message.Body.steam_id_sender = Friend;
        message.Body.lobby_message = data;
        return Packet(message.MsgType, message.Serialize());
    }

    private static IPacketMsg Packet(EMsg type, byte[] bytes) => new PacketClientMsgProtobuf(type, bytes);

    private sealed class FakeLobbyConnection : ILobbyConnection
    {
        public FakeLobbyConnection()
        {
            Client = new SteamClient();
            Callbacks = new CallbackManager(Client);
        }

        public SteamClient Client { get; }
        public CallbackManager Callbacks { get; }
        public bool IsConnected { get; set; } = true;
        /// <summary>Set, connecting throws it, as a connection with no network does.</summary>
        public Exception? Unreachable { get; set; }
        public SteamMatchmaking Matchmaking => Unreachable is { } error ? throw error : Client.GetHandler<SteamMatchmaking>()!;
        public int Holds { get; private set; }
        public IDisposable Subscribe<TCallback>(Action<TCallback> handler) where TCallback : CallbackMsg => Callbacks.Subscribe(handler);
        public void SuspendIdleTimeout() => Holds++;
        public void ResumeIdleTimeout() => Holds--;
    }

    private sealed class QuietLogger : ILogger
    {
        public void Info(string tag, string message, Exception? exception = null) { }
        public void Warn(string tag, string message, Exception? exception = null) { }
        public void Error(string tag, string message, Exception? exception = null) { }
    }
}
