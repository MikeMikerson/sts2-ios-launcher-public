using SteamKit2;
using SteamKit2.Internal;

namespace STS2Mobile.Steam.Lobby;

/// <summary>
/// Lobby chat, which SteamKit2's matchmaking handler does not cover. It travels on the signed-in connection like every other Steam
/// message. Its two contracts are in the app's wire model (tools/SteamAotPatcher).
/// </summary>
internal sealed class LobbyChatHandler : ClientMsgHandler
{
    private readonly uint _appId;

    public LobbyChatHandler(uint appId) => _appId = appId;

    /// <summary>Lobby, sender, bytes. Raised on SteamKit2's receiving thread.</summary>
    public event Action<ulong, ulong, byte[]>? Received;

    public void Send(ulong lobby, ulong target, byte[] data) => Client.Send(Build(_appId, lobby, target, data));

    /// <summary>Target 0 sends to every member of the lobby.</summary>
    internal static ClientMsgProtobuf<CMsgClientMMSSendLobbyChatMsg> Build(uint appId, ulong lobby, ulong target, byte[] data)
    {
        var message = new ClientMsgProtobuf<CMsgClientMMSSendLobbyChatMsg>(EMsg.ClientMMSSendLobbyChatMsg);
        message.ProtoHeader.routing_appid = appId;
        message.Body.app_id = appId;
        message.Body.steam_id_lobby = lobby;
        message.Body.steam_id_target = target;
        message.Body.lobby_message = data;
        return message;
    }

    public override void HandleMsg(IPacketMsg packetMsg)
    {
        if (packetMsg.MsgType != EMsg.ClientMMSLobbyChatMsg) return;
        var body = new ClientMsgProtobuf<CMsgClientMMSLobbyChatMsg>(packetMsg).Body;
        if (body.app_id == _appId && body.lobby_message != null) Received?.Invoke(body.steam_id_lobby, body.steam_id_sender, body.lobby_message);
    }
}
