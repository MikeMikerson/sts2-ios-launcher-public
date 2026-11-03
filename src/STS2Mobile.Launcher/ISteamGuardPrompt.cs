namespace STS2Mobile.Launcher;

/// <summary>What the launcher must show while Steam Guard completes a password sign-in.</summary>
public interface ISteamGuardPrompt
{
    /// <summary>Steam is waiting for the player to approve this sign-in in the Steam mobile app.</summary>
    void AwaitingMobileApproval();

    /// <summary>
    /// Steam needs a Steam Guard code. <paramref name="sentTo"/> is the masked email address for an
    /// emailed code, or null for a code from the Steam mobile app.
    /// </summary>
    Task<string> RequestCodeAsync(string? sentTo, bool previousCodeWasIncorrect);
}
