#if QUICKMATCH
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using QuickMatch.Lobby;
using QuickMatch.Transport;
using QuickMatch.Voice;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// QuickMatch's three platform hooks. Each must be set before the mod initializes: an empty lobby
/// backend or voice device makes QuickMatch fall back to Steamworks, which this app patches out. Matches run over lobby chat, and
/// over the relay when this build carries QuickMatch's relay settings (<see cref="RelaySettings"/>), with voice chat
/// (<see cref="PhoneVoiceDevice"/>) when it does.
/// Without bound lobbies (a host started with no launcher) Steam lobbies are reported as unavailable, so QuickMatch shows its
/// buttons but cannot search. Only called when QuickMatch is among the woven mods, so nothing here loads its assembly otherwise.
/// </summary>
internal static class QuickMatchHooks
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Install()
    {
        var lobbies = RuntimeServices.Lobbies;
        LobbyBackend.Current = lobbies != null ? new PhoneLobbyBackend(lobbies) : new UnavailableLobbyBackend();
        string? relayHost = RelayHost(out string relayState);
        // No relay settings: lobby chat only, since QuickMatch opens no relay without a transport.
        RelayTransport.Current = relayHost != null ? new PhoneRelayConnector(new RelayNetworkPolicy(relayHost)) : null;
        // Voice travels only over the relay, so without one there is no device to offer. STS2_VOICE=off leaves
        // voice out of a match, to compare the game's sound with and without it.
        bool voiceOff = System.Environment.GetEnvironmentVariable("STS2_VOICE") == "off";
        PhoneVoiceDevice? voice = relayHost != null && !voiceOff ? PhoneVoiceDevice.TryCreate() : null;
        VoiceDevice.Current = voice != null ? voice : new NoVoiceDevice();
        voice?.NoteAudioState("at start");
        string voiceState = voice != null ? "voice on (microphone " + voice.CaptureState + ")" : relayHost == null ? "no voice (no relay)" :
            voiceOff ? "no voice (turned off by STS2_VOICE=off; audio " + VoiceAudio.AudioState() + ")" : "no voice (this app has no voice audio)";
        return (lobbies != null ? "Steam lobbies and lobby chat" : "no Steam lobbies bound") + "; " + relayState + "; " + voiceState;
    }

    /// <summary>The relay host from the settings this build carries, or null with why there is no relay. The host is not logged.</summary>
    private static string? RelayHost(out string state)
    {
        using Stream? stream = typeof(QuickMatchHooks).Assembly.GetManifestResourceStream(RelaySettings.FileName);
        if (stream == null)
        {
            state = "no relay (this build has no " + RelaySettings.FileName + ")";
            return null;
        }
        var problems = new List<string>();
        string? host = RelaySettings.ParseHost(new StreamReader(stream).ReadToEnd(), problems.Add);
        state = host != null ? "relay on" : problems.Count > 0 ? "no relay (" + string.Join("; ", problems) + ")" : "no relay (turned off in " + RelaySettings.FileName + ")";
        return host;
    }

    /// <summary>No Steam lobbies were bound.</summary>
    private sealed class UnavailableLobbyBackend : ILobbyBackend
    {
        public bool IsAvailable => false;
        public ulong LocalId => 0;
        public uint ServerTime => (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public event Action<LobbyEvent>? Changed { add { } remove { } }
        public Task<ulong?> CreateAsync(LobbyKind kind, int maxMembers, CancellationToken ct) => Task.FromResult<ulong?>(null);
        public Task<LobbyJoinResult> JoinAsync(ulong lobby, CancellationToken ct) => Task.FromResult(LobbyJoinResult.Error);
        public void Leave(ulong lobby) { }
        public Task<IReadOnlyList<LobbyListing>?> SearchAsync(LobbyQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<LobbyListing>?>(null);
        public void SetData(ulong lobby, string key, string value) { }
        public void DeleteData(ulong lobby, string key) { }
        public string GetData(ulong lobby, string key) => "";
        public bool SetKind(ulong lobby, LobbyKind kind) => false;
        public void SetJoinable(ulong lobby, bool joinable) { }
        public ulong Owner(ulong lobby) => 0;
        public IReadOnlyList<ulong> Members(ulong lobby) => [];
        public bool SendChat(ulong lobby, byte[] data, int length, ulong target) => false;
        public string? PlayerName(ulong id) => null;
    }

    /// <summary>No voice: no relay to carry it, or an app without the voice audio functions.</summary>
    private sealed class NoVoiceDevice : IVoiceDevice
    {
        public VoiceCaptureState CaptureState => VoiceCaptureState.Unsupported;
        public IVoiceOutput? Output => null;
        public void RequestPermission() { }
        public bool StartCapture() => false;
        public void StopCapture() { }
        public int ReadCapture(Span<short> destination) => 0;
    }
}
#endif
