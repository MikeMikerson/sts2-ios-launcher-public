using System.Collections.Generic;
using System.Reflection;
using STS2Mobile.ModApi;

namespace STS2Mobile.Platform;

public sealed class PhoneRelayConnector : IRelayTransport
{
    private readonly RelayNetworkPolicy _policy;
    private PhoneRelayConnector(RelayNetworkPolicy policy) => _policy = policy;

    public static void Register(string inputsDirectory) =>
        Register(inputsDirectory, BuiltInMods.All.Select(mod => Assembly.Load(new AssemblyName(mod.Id))), RelayPermissions.Device);

    internal static void Register(string inputsDirectory, IEnumerable<Assembly> mods, IRelayPermissions permissions)
    {
        var approved = new Dictionary<Assembly, IRelayTransport>();
        foreach (var assembly in mods)
        {
            if (assembly.GetName().Name is { } id && RelaySettings.ApprovedHost(inputsDirectory, id, permissions) is { } host)
                approved.Add(assembly, new PhoneRelayConnector(new RelayNetworkPolicy(host)));
        }
        Multiplayer.ResolveRelay = assembly => approved.TryGetValue(assembly, out var relay) ? relay : null;
    }

    public IRelaySocket Open(string region, IRelayListener listener) =>
        new Socket(RelayConnection.Open(_policy, region, listener.Opened, listener.Received, listener.Closed));

    private sealed class Socket(RelayConnection connection) : IRelaySocket
    {
        public void Send(byte[] message) => connection.Send(message);

        public void Dispose() => connection.Dispose();
    }
}
