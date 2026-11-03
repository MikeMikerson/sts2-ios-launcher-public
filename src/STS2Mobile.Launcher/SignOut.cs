namespace STS2Mobile.Launcher;

/// <summary>How a sign-out ended.</summary>
/// <param name="Revoked">Steam confirmed that the sign-in's refresh token can never be used again.</param>
/// <param name="Forgotten">The sign-in is gone from this iPhone and does not come back at the next launch.</param>
public sealed record SignOutResult(bool Revoked, bool Forgotten);

/// <summary>
/// What the player reads about signing out on the Steam account screen. Signing out ends the sign-in on Steam's side too, and the game
/// and saves stay; the labels are also what <c>STS2_AUTO_TAP</c> presses.
/// </summary>
public static class SignOut
{
    public const string Heading = "Steam account";
    public const string ButtonLabel = "Sign out";
    public const string ConfirmLabel = "Sign out of Steam";
    public const string CancelLabel = "Cancel";
    public const string Working = "Signing out of Steam...";
    public const string Question = "Sign out of Steam on this iPhone?\nThe game and your saves stay on this iPhone. To play again, you sign in again.";
    private const string Kept = "The game and your saves are still on this iPhone.";

    public static Notice Result(SignOutResult result) => result switch
    {
        { Forgotten: false, Revoked: true } => Notice.Failure("Sign-in not deleted",
            "Steam has ended this iPhone's sign-in, so it no longer works, but it could not be deleted from this iPhone. Sign in again to play. " + Kept),
        { Forgotten: false } => Notice.Failure("Not signed out",
            "The sign-in could not be deleted from this iPhone, so it may come back when the app opens again. Try Sign out again. " + Kept),
        { Revoked: true } => Notice.Success("Signed out",
            "This iPhone is signed out of Steam, and Steam has ended its sign-in. Sign in again to play. " + Kept),
        _ => Notice.Failure("Signed out on this iPhone only",
            "This iPhone is signed out, but Steam could not be told to end the sign-in. It may still be listed under Authorized Devices in your Steam account's settings, where you can remove it. " + Kept),
    };
}
