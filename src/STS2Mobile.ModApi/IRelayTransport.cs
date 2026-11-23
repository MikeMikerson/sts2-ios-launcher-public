using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace STS2Mobile.ModApi;

public static partial class Multiplayer
{
    internal static Func<Assembly, IRelayTransport?>? ResolveRelay { get; set; }

    /// <summary>Gets the calling mod's approved relay, or null if absent, disabled or declined.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IRelayTransport? GetRelay() => ResolveRelay?.Invoke(Assembly.GetCallingAssembly());
}

public interface IRelayTransport
{
    /// <summary>Opens a binary WebSocket for auto, apac, na or eu. Callbacks run on a worker thread.</summary>
    IRelaySocket Open(string region, IRelayListener listener);
}

public interface IRelayListener
{
    void Opened();
    void Received(byte[] message);
    void Closed(string reason);
}

public interface IRelaySocket : IDisposable
{
    void Send(byte[] message);
}
