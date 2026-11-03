using System.Net.Http;
using SteamKit2;
using SteamKit2.Authentication;
using STS2Mobile.Steam.Connection;

namespace STS2Mobile.Launcher;

/// <summary>Turns a sign-in failure into one sentence the player can act on. Details stay in the log.</summary>
public static class SignInFailure
{
    public static string Describe(Exception error) => error switch
    {
        AuthenticationException { Result: EResult.InvalidPassword } => "Steam did not accept that account name or password.",
        AuthenticationException { Result: EResult.RateLimitExceeded or EResult.AccountLoginDeniedThrottle } =>
            "Steam is limiting sign-in attempts for now. Wait a while before trying again.",
        AuthenticationException { Result: EResult.Expired or EResult.FileNotFound } => "That sign-in request expired. Start again.",
        AuthenticationException { Result: EResult.AccessDenied } => "The sign-in was declined in the Steam app.",
        AuthenticationException rejected => "Steam refused the sign-in (" + rejected.Result + ").",
        SteamLoginFailedException { Result: var result } when SteamLoginSession.IsServerBusy(result) =>
            "Steam is busy right now. Try again in a moment.",
        SteamLoginFailedException { Result: EResult.RateLimitExceeded or EResult.AccountLoginDeniedThrottle } =>
            "Steam is limiting sign-in attempts for now. Wait a while before trying again.",
        SteamLoginFailedException refused => "Steam no longer accepts the sign-in stored on this iPhone (" + refused.Result + "). Sign in again.",
        UnauthorizedAccessException => "This Steam account does not own Slay the Spire 2. Sign in with an account that does.",
        TimeoutException or HttpRequestException => "Steam could not be reached. Check the connection and try again.",
        _ => "Sign-in could not continue (" + error.GetType().Name + "). Try again.",
    };

    /// <summary>The game itself failed to start after a good sign-in: not the sign-in's fault, so it does not say so.</summary>
    public static string DescribeGameStart(Exception error) =>
        "The game could not start (" + error.GetType().Name + "). Your saves are unchanged. Press Play to try again.";
}
