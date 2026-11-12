namespace STS2Mobile.Platform;

/// <summary>
/// What the game's patched platform calls reach on the phone (src/STS2Mobile.GamePatcher). The launcher
/// binds the signed-in account here before the game starts.
/// </summary>
public static class RuntimeServices
{
    private const string UnknownPlayerName = "[unknown]";
    private static Func<Task<bool>>? _verifyOwnership;
    private static ulong _steamId;
    private static string? _displayName;
    private static IPhoneLobbies? _lobbies;

    public static void BindOwnershipVerifier(Func<Task<bool>> verifyOwnership)
    {
        ArgumentNullException.ThrowIfNull(verifyOwnership);
        if (Interlocked.CompareExchange(ref _verifyOwnership, verifyOwnership, null) != null)
            throw new InvalidOperationException("An ownership verifier is already bound.");
    }

    /// <summary>
    /// The game's own start check (NGame.InitializePlatform). The launcher answers it from the stored sign-in, which has passed Steam's
    /// ownership check, so it needs no connection and the game starts offline too.
    /// </summary>
    public static async Task<bool> RequireOwnershipAsync()
    {
        var verifier = _verifyOwnership ?? throw new InvalidOperationException("Steam sign-in and ownership verification are required.");
        if (!await verifier().ConfigureAwait(false))
            throw new UnauthorizedAccessException("This Steam account does not own Slay the Spire 2.");
        return true;
    }

    public static void BindAccount(ulong steamId, string displayName)
    {
        if (steamId == 0 || string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Authenticated account required.");
        if (_steamId != 0) throw new InvalidOperationException("An account is already bound.");
        _steamId = steamId;
        _displayName = displayName;
    }

    /// <summary>Steam lobbies on the signed-in account, for a built-in multiplayer mod. Bound with the account.</summary>
    public static void BindLobbies(IPhoneLobbies lobbies)
    {
        ArgumentNullException.ThrowIfNull(lobbies);
        if (Interlocked.CompareExchange(ref _lobbies, lobbies, null) != null)
            throw new InvalidOperationException("Lobbies are already bound.");
    }

    /// <summary>The bound lobbies, or null before the launcher binds them.</summary>
    public static IPhoneLobbies? Lobbies => Volatile.Read(ref _lobbies);

    /// <summary>The game did not start, so the next attempt can bind again, perhaps with another account.</summary>
    public static void Unbind()
    {
        Volatile.Write(ref _verifyOwnership, null);
        Volatile.Write(ref _lobbies, null);
        _steamId = 0;
        _displayName = null;
    }

    public static ulong GetLocalPlayerId() => _steamId != 0 ? _steamId : throw new InvalidOperationException("Steam account not bound.");

    /// <summary>
    /// The account's own name for the local player, a lobby member's Steam name for another player in a lobby we are in, and
    /// Steam's placeholder for anyone else.
    /// </summary>
    public static string GetPlayerName(ulong playerId) =>
        playerId == GetLocalPlayerId() ? _displayName! : Lobbies?.MemberName(playerId) ?? UnknownPlayerName;

    /// <summary>
    /// The game waits on this before reading saves. Saves move to and from Steam Cloud only when the
    /// player asks in the launcher, never from the game, so there is nothing left to do.
    /// </summary>
    public static Task SynchronizeCloudAsync() => Task.CompletedTask;

    /// <summary>
    /// This port sends no logs, screenshots, run data or settings to anyone. The game's feedback form
    /// (which uploads zipped logs and a screenshot) reports a failed send instead of transmitting.
    /// </summary>
    public static Task<bool> RejectFeedbackUploadAsync() => Task.FromResult(false);

    /// <summary>External links stay disabled until an in-app browser with the same network policy exists.</summary>
    public static void RejectExternalLink(string url) => throw new NotSupportedException("External links require a guarded browser.");
}
