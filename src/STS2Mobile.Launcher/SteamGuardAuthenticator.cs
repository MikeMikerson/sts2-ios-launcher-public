using SteamKit2.Authentication;

namespace STS2Mobile.Launcher;

/// <summary>Adapts the launcher's prompt to SteamKit's authenticator contract.</summary>
internal sealed class SteamGuardAuthenticator : IAuthenticator
{
    private readonly ISteamGuardPrompt _prompt;
    public SteamGuardAuthenticator(ISteamGuardPrompt prompt) => _prompt = prompt;

    /// <summary>True once nothing more is needed from the player except, at most, an approval in the Steam app.</summary>
    public bool Submitted { get; private set; }

    public async Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
    {
        string code = await _prompt.RequestCodeAsync(null, previousCodeWasIncorrect).ConfigureAwait(false);
        Submitted = true;
        return code;
    }

    public async Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
    {
        string code = await _prompt.RequestCodeAsync(email, previousCodeWasIncorrect).ConfigureAwait(false);
        Submitted = true;
        return code;
    }

    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        Submitted = true;
        _prompt.AwaitingMobileApproval();
        return Task.FromResult(true);
    }
}
