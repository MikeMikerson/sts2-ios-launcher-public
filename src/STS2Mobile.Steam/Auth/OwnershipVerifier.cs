using SteamKit2;
using STS2Mobile.Steam.Connection;

namespace STS2Mobile.Steam.Auth;

public static class OwnershipVerifier
{
    /// <summary>
    /// True when Steam issues an ownership ticket for the game to the signed-in account. Product
    /// information access tokens are not used: they are no proof of a license.
    /// </summary>
    public static async Task<bool> VerifyAsync(SteamConnection connection)
    {
        SteamApps.AppOwnershipTicketCallback ownership = await connection.Apps.GetAppOwnershipTicket(SteamApp.Id);
        return ownership.Result == EResult.OK && ownership.AppID == SteamApp.Id && ownership.Ticket is { Length: > 0 };
    }
}
