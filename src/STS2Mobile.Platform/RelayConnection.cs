using System.Net.WebSockets;
using System.Threading.Channels;

namespace STS2Mobile.Platform;

/// <summary>
/// The one relay the app may reach, for QuickMatch only (NETWORK.md): exactly the host named in QuickMatch's release
/// (<see cref="RelaySettings"/>), WebSocket over TLS on port 443, the relay's room paths only. Kept apart from the Steam and
/// game policies, which both refuse it. The host is passed in, not written down here.
/// </summary>
public sealed class RelayNetworkPolicy
{
    private const string RoomPath = "/v1/relay/";
    private static readonly string[] Regions = ["auto", "apac", "na", "eu"];

    public RelayNetworkPolicy(string host)
    {
        if (!RelaySettings.TryParseHost("https://" + host, out string? parsed, out string? why) || parsed != host)
            throw new ArgumentException("Relay host " + (why ?? "must be lower case"), nameof(host));
        Host = host;
    }

    public string Host { get; }

    public static bool IsAllowedRegion(string? region) => region != null && Regions.Contains(region, StringComparer.Ordinal);

    public Uri RoomAddress(string region) => new($"wss://{Host}{RoomPath}{region}");

    /// <summary>The upgrade request's address; ClientWebSocket sends it as wss or https depending on the runtime.</summary>
    public bool IsAllowed(Uri? uri) =>
        uri is { IsAbsoluteUri: true } && (uri.Scheme == "wss" || uri.Scheme == "https") && uri.Port == 443 &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.IdnHost.Equals(Host, StringComparison.Ordinal) &&
        uri.AbsolutePath.StartsWith(RoomPath, StringComparison.Ordinal) && IsAllowedRegion(uri.AbsolutePath[RoomPath.Length..]);

    public void RequireAllowed(Uri? uri)
    {
        if (!IsAllowed(uri)) throw new HttpRequestException("Relay destination rejected by network policy.");
    }
}

/// <summary>Checks every request before transport, like <see cref="GameOnlyHandler"/>, against one <see cref="RelayNetworkPolicy"/>.</summary>
public sealed class RelayOnlyHandler : PolicyHandler
{
    private readonly RelayNetworkPolicy _policy;

    public RelayOnlyHandler(RelayNetworkPolicy policy) : this(policy, CreateTransport()) { }

    internal RelayOnlyHandler(RelayNetworkPolicy policy, HttpMessageHandler transport) : base("Relay", transport) => _policy = policy;

    private protected override void RequireAllowed(Uri? uri) => _policy.RequireAllowed(uri);
}

/// <summary>
/// One WebSocket to the relay, opened through <see cref="RelayOnlyHandler"/> so the upgrade request is checked like any other
/// request. Callbacks run on worker threads, in order, and stop once the owner disposes the connection or <c>closed</c> was
/// called. Messages queued before disposal still leave for up to <see cref="CloseTimeoutMs"/>, then the socket closes.
/// </summary>
public sealed class RelayConnection : IDisposable
{
    public const int ConnectTimeoutMs = 10000;
    public const int CloseTimeoutMs = 2000;
    // The relay's data frame is 9 bytes of header and up to 64 KB; its other frames are small.
    private const int MaxIncomingBytes = 9 + 64 * 1024 + 1024;

    private readonly Action _opened;
    private readonly Action<byte[]> _received;
    private readonly Action<string> _closed;
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private int _ended;
    private string? _dropReason;

    private RelayConnection(Action opened, Action<byte[]> received, Action<string> closed)
    {
        _opened = opened;
        _received = received;
        _closed = closed;
    }

    public static RelayConnection Open(RelayNetworkPolicy policy, string region, Action opened, Action<byte[]> received, Action<string> closed) =>
        Open(policy, region, opened, received, closed, PolicyHandler.CreateTransport());

    internal static RelayConnection Open(RelayNetworkPolicy policy, string region, Action opened, Action<byte[]> received, Action<string> closed, HttpMessageHandler transport)
    {
        var connection = new RelayConnection(opened, received, closed);
        if (!RelayNetworkPolicy.IsAllowedRegion(region))
        {
            transport.Dispose();
            _ = Task.Run(() => connection.End("relay region rejected by network policy"));
        }
        else
        {
            _ = Task.Run(() => connection.RunAsync(policy.RoomAddress(region), new HttpMessageInvoker(new RelayOnlyHandler(policy, transport))));
        }
        return connection;
    }

    public void Send(byte[] message)
    {
        if (Volatile.Read(ref _ended) == 0) _outbox.Writer.TryWrite(message);
    }

    /// <summary>Ends the connection at once as if the relay had gone away: <c>closed</c> is called with <paramref name="reason"/>. For tests.</summary>
    public void Drop(string reason)
    {
        if (Volatile.Read(ref _ended) != 0 || Interlocked.CompareExchange(ref _dropReason, reason, null) != null) return;
        _outbox.Writer.TryComplete();
        _stop.Cancel();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
        {
            _outbox.Writer.TryComplete();
            _stop.CancelAfter(CloseTimeoutMs);
        }
    }

    private async Task RunAsync(Uri address, HttpMessageInvoker invoker)
    {
        string reason;
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                connect.CancelAfter(ConnectTimeoutMs);
                try
                {
                    await _socket.ConnectAsync(address, invoker, connect.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                {
                    throw new TimeoutException($"no connection after {ConnectTimeoutMs} ms");
                }
            }
            if (Volatile.Read(ref _ended) == 0) _opened();
            Task sending = SendLoopAsync();
            reason = await ReceiveLoopAsync().ConfigureAwait(false);
            _outbox.Writer.TryComplete();
            _stop.Cancel();
            await sending.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Messages only: a policy refusal names no path, and the relay's address carries no secret.
            reason = e switch
            {
                OperationCanceledException => "connection ended",
                WebSocketException { InnerException: HttpRequestException inner } => inner.Message,
                _ => e.Message,
            };
        }
        finally
        {
            _stop.Cancel();
            _socket.Dispose();
            invoker.Dispose();
        }
        End(Volatile.Read(ref _dropReason) ?? reason);
    }

    private async Task<string> ReceiveLoopAsync()
    {
        var buffer = new byte[MaxIncomingBytes];
        while (true)
        {
            int filled = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (filled == buffer.Length) return "message from the relay too large";
                result = await _socket.ReceiveAsync(buffer.AsMemory(filled), _stop.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return $"relay closed the connection ({(int?)_socket.CloseStatus} {_socket.CloseStatusDescription})";
                filled += result.Count;
            }
            while (!result.EndOfMessage);
            if (result.MessageType == WebSocketMessageType.Binary && Volatile.Read(ref _ended) == 0)
                _received(buffer.AsSpan(0, filled).ToArray());
        }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (byte[] message in _outbox.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                await _socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, _stop.Token).ConfigureAwait(false);
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", _stop.Token).ConfigureAwait(false);
        }
        catch (Exception) when (_stop.IsCancellationRequested || _socket.State != WebSocketState.Open)
        {
            // Ending anyway; the receive loop reports why.
        }
        finally
        {
            _stop.Cancel();
        }
    }

    private void End(string reason)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0) _closed(reason);
    }
}
