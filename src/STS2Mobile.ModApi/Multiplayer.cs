namespace STS2Mobile.ModApi;

public static partial class Multiplayer
{
    private static ISteamLobbies? _lobbies;

    public static ISteamLobbies? Lobbies => Volatile.Read(ref _lobbies);

    public static IVoiceDevice? Voice { get; internal set; }

    internal static Func<ulong, bool>? ProfilePrompt { get; set; }

    /// <summary>On the game thread, ask the player whether to open a Steam profile. Returns true if the prompt opened.</summary>
    public static bool RequestSteamProfile(ulong steamId) =>
        (steamId >> 32) == 0x01100001 && (uint)steamId != 0 && ProfilePrompt?.Invoke(steamId) == true;

    internal static void BindLobbies(ISteamLobbies lobbies)
    {
        if (Interlocked.CompareExchange(ref _lobbies, lobbies, null) != null)
            throw new InvalidOperationException("Lobbies are already bound.");
    }

    internal static void UnbindLobbies() => Volatile.Write(ref _lobbies, null);
}
