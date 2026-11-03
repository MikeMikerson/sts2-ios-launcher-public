#if QUICKMATCH
using System;
using System.Threading;
using Timer = System.Threading.Timer;
using Godot;
using QuickMatch.Transport;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// QuickMatch's relay connections: the mod's relay interface answered by <see cref="RelayConnection"/>, which reaches only the one
/// host QuickMatch's release names (<see cref="RelayNetworkPolicy"/>, NETWORK.md). Nothing else in the app uses it, and it carries
/// only QuickMatch's encrypted game messages, never the Steam sign-in.
/// <c>STS2_RELAY_DROP_AFTER=&lt;seconds&gt;</c> drops each relay connection that long after it opens, as if the relay had gone
/// away, so a match's fallback to lobby chat can be tested with nobody there.
/// </summary>
internal sealed class PhoneRelayConnector(RelayNetworkPolicy policy) : IRelayTransport
{
    private readonly int _dropAfterSeconds =
        int.TryParse(System.Environment.GetEnvironmentVariable("STS2_RELAY_DROP_AFTER"), out int seconds) && seconds > 0 ? seconds : 0;

    public IRelaySocket Open(string region, IRelayListener listener)
    {
        GD.Print($"QuickMatch relay: connecting (region {region}).");
        Timer? drop = null;
        RelayConnection? connection = null;
        connection = RelayConnection.Open(policy, region,
            opened: () =>
            {
                GD.Print("QuickMatch relay: connected.");
                if (_dropAfterSeconds > 0)
                    drop = new Timer(_ => connection?.Drop($"dropped by STS2_RELAY_DROP_AFTER after {_dropAfterSeconds} s"), null, _dropAfterSeconds * 1000, Timeout.Infinite);
                listener.Opened();
            },
            received: listener.Received,
            closed: reason =>
            {
                drop?.Dispose();
                GD.Print($"QuickMatch relay: closed ({reason}).");
                listener.Closed(reason);
            });
        return new Socket(connection, () => drop?.Dispose());
    }

    private sealed class Socket(RelayConnection connection, Action disposed) : IRelaySocket
    {
        public void Send(byte[] message) => connection.Send(message);

        public void Dispose()
        {
            disposed();
            connection.Dispose();
        }
    }
}
#endif
