using ProtoBuf;
using SteamKit2;
using STS2Mobile.Steam.Abstractions;
using STS2Mobile.Steam.Lobby;

namespace STS2Mobile.Steam.Connection;

/// <summary>
/// The signed-in Steam connection. It connects on first use, disconnects after a quiet period, and
/// reconnects with exponential backoff, so callers never manage the socket themselves.
/// </summary>
/// <remarks>
/// Connecting blocks the calling thread for up to <see cref="ConnectTimeout"/>; callers on the UI
/// thread go through <see cref="Task.Run(Func{Task})"/>.
/// </remarks>
public sealed class SteamConnection : ILobbyConnection, IDisposable
{
    private const string Tag = "Connection";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CallbackWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CallbackThreadJoin = TimeSpan.FromSeconds(2);
    private const int InitialBackoffMs = 2000;
    private const int MaxBackoffMs = 32000;

    private readonly string _accountName;
    private readonly string _refreshToken;
    private readonly ILogger _logger;
    private readonly SteamClient _client;
    private readonly CallbackManager _callbacks;
    private readonly SteamUser _user;
    private readonly SteamApps _apps;
    private readonly SteamContent _content;
    private readonly SteamMatchmaking _matchmaking;
    private readonly SteamUnifiedMessages _unifiedMessages;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _disposed = new();
    private readonly ManualResetEventSlim _signedIn = new(initialState: false);
    private Thread? _callbackThread;
    private volatile bool _callbacksRunning;
    private Timer? _idleTimer;
    private int _backoffMs;
    private int _idleHolds;
    private Exception? _signInError;
    private volatile string? _personaName;

    public SteamConnection(string accountName, string refreshToken, ILogger logger)
    {
        _accountName = accountName;
        _refreshToken = refreshToken;
        _logger = logger;
        _client = new SteamClient(SteamClientConfiguration.Create());
        _callbacks = new CallbackManager(_client);
        _user = _client.GetHandler<SteamUser>()!;
        _apps = _client.GetHandler<SteamApps>()!;
        _content = _client.GetHandler<SteamContent>()!;
        _matchmaking = _client.GetHandler<SteamMatchmaking>()!;
        _unifiedMessages = _client.GetHandler<SteamUnifiedMessages>()!;
        _unifiedMessages.CreateService<SteamKit2.Internal.Cloud>();
        _unifiedMessages.CreateService<SteamKit2.Internal.Authentication>();
        _callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => _user.LogOn(new SteamUser.LogOnDetails
        {
            Username = _accountName,
            AccessToken = _refreshToken,
            ShouldRememberPassword = true,
        }));
        _callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            if (State == ConnectionState.Connected)
            {
                _logger.Warn(Tag, "Dropped unexpectedly");
                EnterBackoff();
            }
        });
        _callbacks.Subscribe<SteamUser.LoggedOnCallback>(callback =>
        {
            if (callback.Result != EResult.OK) _signInError = new SteamLoginFailedException(callback.Result);
            _signedIn.Set();
        });
        // Steam sends the account's Steam name (the one other players see) just after sign-in. It is kept across reconnects.
        _callbacks.Subscribe<SteamUser.AccountInfoCallback>(callback =>
        {
            if (!string.IsNullOrWhiteSpace(callback.PersonaName)) _personaName = callback.PersonaName;
        });
    }

    /// <summary>The account's Steam name, once Steam has sent it after a sign-in; null before then.</summary>
    public string? PersonaName => _personaName;

    public ConnectionState State { get; private set; }

    public SteamClient Client => _client;

    public bool IsConnected => State == ConnectionState.Connected;

    public SteamApps Apps
    {
        get
        {
            EnsureConnected();
            return _apps;
        }
    }

    public SteamContent Content
    {
        get
        {
            EnsureConnected();
            return _content;
        }
    }

    /// <summary>Steam lobbies (src/STS2Mobile.Steam/Lobby). Connects first, like the other handlers.</summary>
    public SteamMatchmaking Matchmaking
    {
        get
        {
            EnsureConnected();
            return _matchmaking;
        }
    }

    /// <summary>Runs <paramref name="handler"/> on the connection's callback thread for each callback of that type.</summary>
    public IDisposable Subscribe<TCallback>(Action<TCallback> handler) where TCallback : CallbackMsg => _callbacks.Subscribe(handler);

    /// <summary>
    /// Keeps the connection open while something needs it between requests, such as a lobby we are in, which Steam leaves
    /// for us when the connection closes. Each call is paired with <see cref="ResumeIdleTimeout"/>.
    /// </summary>
    public void SuspendIdleTimeout()
    {
        lock (_stateLock)
        {
            _idleHolds++;
            StopIdleTimer();
        }
    }

    public void ResumeIdleTimeout()
    {
        lock (_stateLock)
        {
            if (_idleHolds > 0) _idleHolds--;
            if (_idleHolds == 0 && State == ConnectionState.Connected) ResetIdleTimer();
        }
    }

    /// <summary>Sends one request to Steam's Cloud service. Requests go out one at a time.</summary>
    public async Task<TResult> SendCloudAsync<TRequest, TResult>(string method, TRequest request)
        where TRequest : IExtensible, new()
        where TResult : IExtensible, new()
    {
        var (result, body) = await SendServiceAsync<TRequest, TResult>("Cloud", method, request).ConfigureAwait(false);
        if (result != EResult.OK) throw new CloudRequestException(method, result);
        return body;
    }

    /// <summary>
    /// Asks Steam to revoke the refresh token this connection signs in with, for good, so that nothing can sign in with it again. Connects
    /// first if needed. Throws when Steam cannot be reached or does not answer OK.
    /// </summary>
    public async Task RevokeRefreshTokenAsync()
    {
        var request = new SteamKit2.Internal.CAuthentication_Token_Revoke_Request
        {
            token = _refreshToken,
            revoke_action = SteamKit2.Internal.EAuthTokenRevokeAction.k_EAuthTokenRevokePermanent,
        };
        var (result, _) = await SendServiceAsync<SteamKit2.Internal.CAuthentication_Token_Revoke_Request, SteamKit2.Internal.CAuthentication_Token_Revoke_Response>(
            "Authentication", "RevokeToken", request).ConfigureAwait(false);
        if (result != EResult.OK) throw new InvalidOperationException("Authentication.RevokeToken failed: " + result);
        _logger.Info(Tag, "Refresh token revoked");
    }

    /// <summary>One request to a Steam service that this connection registered in its constructor, one at a time.</summary>
    private async Task<(EResult Result, TResult Body)> SendServiceAsync<TRequest, TResult>(string service, string method, TRequest request)
        where TRequest : IExtensible, new()
        where TResult : IExtensible, new()
    {
        EnsureConnected();
        await _sendLock.WaitAsync(_disposed.Token).ConfigureAwait(false);
        try
        {
            var response = await _unifiedMessages.SendMessage<TRequest, TResult>($"{service}.{method}#1", request).ToTask().ConfigureAwait(false);
            return (response.Result, response.Body);
        }
        finally
        {
            // Dispose may have disposed the semaphore while this request was in flight; there is
            // then no lock left to release.
            try { _sendLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Closes the socket without signing off, as a lost network would, for a test (<c>STS2_STEAM_DROP_AFTER</c>). The connection then
    /// waits out its backoff and reconnects on the next request.
    /// </summary>
    public void Drop(string reason)
    {
        if (State != ConnectionState.Connected) return;
        _logger.Warn(Tag, "Dropping the connection: " + reason);
        _client.Disconnect();
    }

    /// <summary>Waits for a request in flight, then disconnects.</summary>
    public void Flush()
    {
        lock (_stateLock)
        {
            if (State != ConnectionState.Connected) return;
            State = ConnectionState.Draining;
            StopIdleTimer();
        }
        _logger.Info(Tag, "Draining...");
        if (_sendLock.Wait(DrainTimeout)) _sendLock.Release();
        else _logger.Warn(Tag, "Drain timed out, forcing disconnect");
        Teardown();
        State = ConnectionState.Idle;
    }

    public void Dispose()
    {
        _disposed.Cancel();
        Flush();
        _idleTimer?.Dispose();
        _sendLock.Dispose();
        _signedIn.Dispose();
        _disposed.Dispose();
    }

    private void EnsureConnected()
    {
        lock (_stateLock)
        {
            if (State != ConnectionState.Connected) Connect();
            ResetIdleTimer();
        }
    }

    /// <summary>Connects and signs in. Called with <see cref="_stateLock"/> held.</summary>
    private void Connect()
    {
        if (State == ConnectionState.Backoff)
        {
            _logger.Info(Tag, $"Waiting {_backoffMs}ms backoff before reconnect...");
            // Let Flush and the idle timer run while this thread waits out the backoff.
            Monitor.Exit(_stateLock);
            try { Thread.Sleep(_backoffMs); }
            finally { Monitor.Enter(_stateLock); }
        }
        _signInError = null;
        _signedIn.Reset();
        State = ConnectionState.Connecting;
        try
        {
            StartCallbackThread();
            _client.Connect();
        }
        catch (Exception error)
        {
            _logger.Error(Tag, "Connect failed: " + error.Message);
            EnterBackoff();
            throw;
        }
        if (!_signedIn.Wait(ConnectTimeout))
        {
            _logger.Warn(Tag, "Connect timed out");
            Teardown();
            EnterBackoff();
            throw new TimeoutException("Steam connection timed out");
        }
        if (_signInError != null)
        {
            Teardown();
            EnterBackoff();
            throw _signInError;
        }
        _backoffMs = 0;
        State = ConnectionState.Connected;
        _logger.Info(Tag, "Connected to Steam");
    }

    private void StartCallbackThread()
    {
        if (_callbackThread is { IsAlive: true }) return;
        _callbacksRunning = true;
        _callbackThread = new Thread(() =>
        {
            while (_callbacksRunning) _callbacks.RunWaitCallbacks(CallbackWait);
        })
        {
            IsBackground = true,
            Name = "SteamConnectionCallbacks",
        };
        _callbackThread.Start();
    }

    private void Teardown()
    {
        _callbacksRunning = false;
        // Best effort: the socket may already be gone, and there is nothing to recover here.
        try { _user.LogOff(); }
        catch (Exception error) { _logger.Info(Tag, "Log off failed: " + error.Message); }
        try { _client.Disconnect(); }
        catch (Exception error) { _logger.Info(Tag, "Disconnect failed: " + error.Message); }
        _callbackThread?.Join(CallbackThreadJoin);
        _callbackThread = null;
    }

    private void EnterBackoff()
    {
        _backoffMs = _backoffMs == 0 ? InitialBackoffMs : Math.Min(_backoffMs * 2, MaxBackoffMs);
        State = ConnectionState.Backoff;
    }

    private void ResetIdleTimer()
    {
        StopIdleTimer();
        if (_idleHolds > 0) return;
        _idleTimer = new Timer(_ =>
        {
            if (State != ConnectionState.Connected) return;
            _logger.Info(Tag, "Idle timeout, disconnecting");
            Teardown();
            State = ConnectionState.Idle;
        }, null, IdleTimeout, Timeout.InfiniteTimeSpan);
    }

    private void StopIdleTimer()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
    }
}
