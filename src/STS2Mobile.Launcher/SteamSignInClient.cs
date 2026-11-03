using SteamKit2;
using STS2Mobile.Steam.Connection;

namespace STS2Mobile.Launcher;

/// <summary>
/// A short-lived, not yet authenticated Steam connection used only while the player signs in.
/// It pumps callbacks on its own task and can reconnect, because iOS may suspend the launcher and
/// drop the socket while the player approves the sign-in in the Steam app.
/// </summary>
internal sealed class SteamSignInClient : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CallbackWait = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PumpStopTimeout = TimeSpan.FromSeconds(2);
    private readonly SteamClient _client = new(SteamClientConfiguration.Create());
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;
    private TaskCompletionSource _connected = NewSignal();

    public SteamSignInClient()
    {
        var callbacks = new CallbackManager(_client);
        callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => Volatile.Read(ref _connected).TrySetResult());
        _pump = Task.Run(() =>
        {
            while (!_stop.IsCancellationRequested) callbacks.RunWaitCallbacks(CallbackWait);
        });
    }

    public SteamClient Client => _client;

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client.IsConnected) return;
        TaskCompletionSource signal = NewSignal();
        Volatile.Write(ref _connected, signal);
        _client.Connect();
        await signal.Task.WaitAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        _stop.Cancel();
        _client.Disconnect();
        try { _pump.Wait(PumpStopTimeout); }
        // A pump that failed has nothing left to report once the sign-in client is gone.
        catch (AggregateException) { }
        _stop.Dispose();
    }
}
