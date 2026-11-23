using System.Text;

namespace STS2Mobile.Platform;

internal interface IRelayPermissions
{
    string? Read(string id);
    void Write(string id, string decision);
}

internal sealed class RelayPermissions : IRelayPermissions
{
    internal static readonly RelayPermissions Device = new();
    private readonly Lazy<AppleKeychain> _store = new(() => new AppleKeychain(AppleKeychain.ApplicationIdentifier() + ".mod-relays"));

    public string? Read(string id) => _store.Value.Read(id) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    public void Write(string id, string decision) => _store.Value.Write(id, Encoding.UTF8.GetBytes(decision));
}
