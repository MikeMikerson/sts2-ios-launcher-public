using System.Collections.Concurrent;
using SteamKit2;
using STS2Mobile.Steam.Abstractions;

namespace STS2Mobile.Steam.Lobby;

/// <summary>One lobby found by a search: its ID, member count and data values.</summary>
public readonly record struct LobbySummary(ulong Id, int Members, IReadOnlyDictionary<string, string> Data);

/// <summary>
/// Steam lobbies and lobby chat for QuickMatch. Everything travels as ordinary Steam messages
/// on the signed-in connection, so nothing here opens a connection of its own and the network policy is unchanged. Losing that
/// connection leaves every lobby, so it is held open while we are in one (it otherwise closes after 30 idle seconds). SteamKit2
/// keeps each lobby's owner, members and their Steam names up to date.
/// </summary>
public sealed class SteamLobbyService : IDisposable
{
    private const string Tag = "Lobby";

    /// <summary>Several data changes in a row (QuickMatch publishes seven values and a type) go out as one request.</summary>
    private const int DataSendDelayMs = 50;

    private readonly ILobbyConnection _connection;
    private readonly uint _appId;
    private readonly ILogger _logger;
    private readonly LobbyChatHandler _chat;
    private readonly SteamMatchmaking _matchmaking;
    private readonly ConcurrentDictionary<ulong, OwnedLobby> _owned = new();
    private readonly ConcurrentDictionary<ulong, byte> _joined = new();
    private readonly List<IDisposable> _subscriptions = [];

    public SteamLobbyService(ILobbyConnection connection, uint appId, ILogger logger)
    {
        _connection = connection;
        _appId = appId;
        _logger = logger;
        _matchmaking = connection.Client.GetHandler<SteamMatchmaking>() ?? throw new InvalidOperationException("SteamKit2's matchmaking handler is missing.");
        _chat = new LobbyChatHandler(appId);
        _chat.Received += (lobby, sender, data) => ChatReceived?.Invoke(lobby, sender, data);
        connection.Client.AddHandler(_chat);
        _subscriptions.Add(connection.Subscribe<SteamMatchmaking.UserJoinedLobbyCallback>(callback =>
        {
            if (callback.AppID == _appId) MemberJoined?.Invoke(callback.LobbySteamID.ConvertToUInt64(), callback.User.SteamID.ConvertToUInt64());
        }));
        _subscriptions.Add(connection.Subscribe<SteamMatchmaking.UserLeftLobbyCallback>(callback =>
        {
            if (callback.AppID == _appId) MemberLeft?.Invoke(callback.LobbySteamID.ConvertToUInt64(), callback.User.SteamID.ConvertToUInt64());
        }));
        _subscriptions.Add(connection.Subscribe<SteamClient.DisconnectedCallback>(_ => ConnectionLost()));
    }

    /// <summary>Lobby, sender, bytes. Raised on SteamKit2's receiving thread.</summary>
    public event Action<ulong, ulong, byte[]>? ChatReceived;

    /// <summary>Lobby, member. Raised on the connection's callback thread.</summary>
    public event Action<ulong, ulong>? MemberJoined;

    /// <summary>Lobby, member (ourselves too, when Steam takes us out). Raised on the connection's callback thread.</summary>
    public event Action<ulong, ulong>? MemberLeft;

    /// <summary>
    /// Lobbies Steam took us out of because the connection closed. Raised on the connection's callback thread. Nothing rejoins them:
    /// the other players end the match within QuickMatch's grace period (1.5 s) once Steam tells them we left.
    /// </summary>
    public event Action<IReadOnlyList<ulong>>? LobbiesLost;

    /// <summary>The lobbies we are in.</summary>
    public IReadOnlyCollection<ulong> Joined => _joined.Keys.ToArray();

    /// <summary>Creates a lobby we own and are in; null when Steam refused or could not be reached.</summary>
    public async Task<ulong?> CreateAsync(ELobbyType type, int maxMembers, CancellationToken ct)
    {
        if (await ConnectedAsync("Lobby creation", ct).ConfigureAwait(false) is not { } matchmaking) return null;
        var job = matchmaking.CreateLobby(_appId, type, maxMembers);
        if (job == null)
        {
            _logger.Warn(Tag, "Lobby creation refused: not signed in");
            return null;
        }
        var created = await AnswerAsync(job.ToTask(), callback => callback.Result == EResult.OK ? callback.LobbySteamID.ConvertToUInt64() : 0, ct).ConfigureAwait(false);
        if (created.Result != EResult.OK)
        {
            _logger.Warn(Tag, "Steam refused to create a lobby: " + created.Result);
            return null;
        }
        ulong lobby = created.LobbySteamID.ConvertToUInt64();
        _owned[lobby] = new OwnedLobby(type, maxMembers);
        Hold(lobby);
        _logger.Info(Tag, $"Created lobby {lobby} ({type}, {maxMembers} members)");
        return lobby;
    }

    public async Task<EChatRoomEnterResponse> JoinAsync(ulong lobby, CancellationToken ct)
    {
        if (await ConnectedAsync("Lobby join", ct).ConfigureAwait(false) is not { } matchmaking) return EChatRoomEnterResponse.Error;
        var job = matchmaking.JoinLobby(_appId, lobby);
        if (job == null)
        {
            // SteamKit2 sends a join only once the account's Steam name has arrived.
            _logger.Warn(Tag, "Lobby join refused: no Steam name yet");
            return EChatRoomEnterResponse.Error;
        }
        var joined = await AnswerAsync(job.ToTask(), callback => callback.ChatRoomEnterResponse == EChatRoomEnterResponse.Success ? lobby : 0, ct).ConfigureAwait(false);
        if (joined.ChatRoomEnterResponse == EChatRoomEnterResponse.Success)
        {
            Hold(lobby);
            _logger.Info(Tag, $"Entered lobby {lobby}");
        }
        else _logger.Info(Tag, $"Lobby {lobby} not entered: {joined.ChatRoomEnterResponse}");
        return joined.ChatRoomEnterResponse;
    }

    public void Leave(ulong lobby)
    {
        _owned.TryRemove(lobby, out _);
        if (!_joined.TryRemove(lobby, out _)) return;
        if (_connection.IsConnected) _matchmaking.LeaveLobby(_appId, lobby);
        _connection.ResumeIdleTimeout();
        _logger.Info(Tag, $"Left lobby {lobby}");
    }

    /// <summary>
    /// The connection closed, and with it every lobby we were in, as Steam sees it. The lobbies are forgotten here, so nothing more
    /// is sent to them, the connection may close when idle again, and a later search or host connects afresh.
    /// </summary>
    public void ConnectionLost()
    {
        var lost = new List<ulong>();
        foreach (ulong lobby in _joined.Keys)
        {
            if (!_joined.TryRemove(lobby, out _)) continue;
            _owned.TryRemove(lobby, out _);
            _connection.ResumeIdleTimeout();
            lost.Add(lobby);
        }
        if (lost.Count == 0) return;
        _logger.Warn(Tag, $"Steam connection lost while in {lost.Count} lobby(s); Steam has taken us out of them");
        LobbiesLost?.Invoke(lost);
    }

    /// <summary>
    /// Lobbies of our app worldwide whose data holds every value in <paramref name="equal"/> and that have at least
    /// <paramref name="minFreeSlots"/> free slots. Null when Steam reported an error or could not be reached.
    /// </summary>
    public async Task<IReadOnlyList<LobbySummary>?> SearchAsync(IReadOnlyDictionary<string, string> equal, int minFreeSlots, int maxResults, CancellationToken ct)
    {
        if (await ConnectedAsync("Lobby search", ct).ConfigureAwait(false) is not { } matchmaking) return null;
        var job = matchmaking.GetLobbyList(_appId, Filters(equal, minFreeSlots), maxResults);
        if (job == null)
        {
            _logger.Warn(Tag, "Lobby search refused: not signed in");
            return null;
        }
        var list = await job.ToTask().WaitAsync(ct).ConfigureAwait(false);
        if (list.Result != EResult.OK)
        {
            _logger.Warn(Tag, "Lobby search failed: " + list.Result);
            return null;
        }
        _logger.Info(Tag, $"Lobby search found {list.Lobbies.Count}");
        return list.Lobbies.Select(found => new LobbySummary(found.SteamID.ConvertToUInt64(), found.NumMembers, found.Metadata)).ToList();
    }

    internal static List<SteamMatchmaking.Lobby.Filter> Filters(IReadOnlyDictionary<string, string> equal, int minFreeSlots)
    {
        var filters = new List<SteamMatchmaking.Lobby.Filter>();
        foreach (var pair in equal) filters.Add(new SteamMatchmaking.Lobby.StringFilter(pair.Key, ELobbyComparison.Equal, pair.Value));
        if (minFreeSlots > 0) filters.Add(new SteamMatchmaking.Lobby.SlotsAvailableFilter(minFreeSlots));
        filters.Add(new SteamMatchmaking.Lobby.DistanceFilter(ELobbyDistanceFilter.Worldwide));
        return filters;
    }

    public void SetData(ulong lobby, string key, string value)
    {
        if (!_owned.TryGetValue(lobby, out var owned)) return;
        owned.Set(key, value);
        ScheduleDataSend(lobby, owned);
    }

    public void DeleteData(ulong lobby, string key)
    {
        if (_owned.TryGetValue(lobby, out var owned) && owned.Delete(key)) ScheduleDataSend(lobby, owned);
    }

    /// <summary>A data value of a lobby we are in, or "" if unset.</summary>
    public string GetData(ulong lobby, string key)
    {
        if (_owned.TryGetValue(lobby, out var owned)) return owned.Get(key);
        return Cached(lobby)?.Metadata.TryGetValue(key, out string? value) == true ? value : "";
    }

    public bool SetType(ulong lobby, ELobbyType type)
    {
        if (!_owned.TryGetValue(lobby, out var owned)) return false;
        owned.SetType(type);
        ScheduleDataSend(lobby, owned);
        return true;
    }

    public void SetJoinable(ulong lobby, bool joinable)
    {
        if (!_owned.TryGetValue(lobby, out var owned)) return;
        owned.SetJoinable(joinable);
        ScheduleDataSend(lobby, owned);
    }

    public ulong Owner(ulong lobby) => Cached(lobby)?.OwnerSteamID?.ConvertToUInt64() ?? 0;

    /// <summary>Members of a lobby we are in, ourselves included.</summary>
    public IReadOnlyList<ulong> Members(ulong lobby) => Cached(lobby)?.Members.Select(member => member.SteamID.ConvertToUInt64()).ToList() ?? [];

    /// <summary>The Steam name of a member of a lobby we are in, or null.</summary>
    public string? MemberName(ulong member)
    {
        foreach (ulong lobby in _joined.Keys)
        {
            var found = Cached(lobby)?.Members.FirstOrDefault(entry => entry.SteamID.ConvertToUInt64() == member);
            if (!string.IsNullOrEmpty(found?.PersonaName)) return found.PersonaName;
        }
        return null;
    }

    /// <summary>Sends one lobby chat message; target 0 goes to every member. False when not connected or not in the lobby.</summary>
    public bool SendChat(ulong lobby, byte[] data, int length, ulong target)
    {
        if (!_connection.IsConnected || !_joined.ContainsKey(lobby)) return false;
        _chat.Send(lobby, target, data.AsSpan(0, length).ToArray());
        return true;
    }

    public void Dispose()
    {
        foreach (ulong lobby in _joined.Keys.ToList()) Leave(lobby);
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _connection.Client.RemoveHandler(_chat);
    }

    private SteamMatchmaking.Lobby? Cached(ulong lobby) => _matchmaking.GetLobby(_appId, lobby);

    /// <summary>
    /// Waits for Steam's answer. When the wait is cancelled the request still completes, and a lobby it put us in
    /// (<paramref name="entered"/> is not 0) is left again.
    /// </summary>
    private async Task<T> AnswerAsync<T>(Task<T> answer, Func<T, ulong> entered, CancellationToken ct)
    {
        try
        {
            return await answer.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _ = answer.ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully && entered(task.Result) is var lobby and not 0) _matchmaking.LeaveLobby(_appId, lobby);
            }, TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>
    /// Connecting blocks, so it happens on a worker thread rather than the game's. Null when Steam cannot be reached, for example
    /// offline, since the game may start without a connection: the caller answers as a refusal, which QuickMatch reports to the
    /// player, rather than throwing into the game.
    /// </summary>
    private async Task<SteamMatchmaking?> ConnectedAsync(string request, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => _connection.Matchmaking, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.Warn(Tag, $"{request} not sent: Steam could not be reached ({error.GetType().Name}: {error.Message})");
            return null;
        }
    }

    /// <summary>Called once Steam has put us in <paramref name="lobby"/>.</summary>
    internal void Hold(ulong lobby)
    {
        if (_joined.TryAdd(lobby, 0)) _connection.SuspendIdleTimeout();
    }

    private void ScheduleDataSend(ulong lobby, OwnedLobby owned)
    {
        if (Interlocked.Exchange(ref owned.SendScheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(DataSendDelayMs).ConfigureAwait(false);
            Interlocked.Exchange(ref owned.SendScheduled, 0);
            await SendDataAsync(lobby, owned).ConfigureAwait(false);
        });
    }

    private async Task SendDataAsync(ulong lobby, OwnedLobby owned)
    {
        if (!_owned.ContainsKey(lobby) || !_connection.IsConnected) return;
        var (type, data) = owned.Snapshot();
        try
        {
            var result = await _matchmaking.SetLobbyData(_appId, lobby, type, owned.MaxMembers, 0, data).ToTask().ConfigureAwait(false);
            if (result.Result != EResult.OK) _logger.Warn(Tag, $"Steam refused the data of lobby {lobby}: {result.Result}");
        }
        catch (Exception error)
        {
            _logger.Warn(Tag, $"Setting the data of lobby {lobby} failed: {error.Message}");
        }
    }
}
