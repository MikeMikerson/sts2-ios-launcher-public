using ProtoBuf;

namespace STS2Mobile.SteamWire;

// These signatures match the SteamKit2 3.4.0 call sites that SteamAotPatcher redirects here. Unknown contracts fail closed.
public static class SteamWire
{
    public static void Serialize<T>(Stream destination, T value) => SteamWireModel.Instance.Serialize(destination, value);
    public static T Deserialize<T>(Stream source) => SteamWireModel.Instance.Deserialize<T>(source);
    public static T Deserialize<T>(Stream source, T value, object userState, long length)
    {
        using var state = ProtoReader.State.Create(source, SteamWireModel.Instance, userState, length);
        return state.DeserializeRoot<T>(value);
    }
    // iOS does not expose a process start time. SteamKit2 only uses it to stamp job identifiers,
    // so the moment this library was first used is an equivalent, stable value.
    private static readonly DateTime FirstUse = DateTime.Now;
    public static DateTime ProcessStartTime(System.Diagnostics.Process process) => FirstUse;
    public static IEnumerable<T> DeserializeItems<T>(Stream source, PrefixStyle style, int fieldNumber)
        => SteamWireModel.Instance.DeserializeItems<T>(source, style, fieldNumber);
}
