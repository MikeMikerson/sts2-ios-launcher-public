namespace STS2Mobile.Launcher;

/// <summary>Which first screen the launcher shows: nothing goes to Steam until the player chooses on it.</summary>
public enum EntryKind
{
    /// <summary>No stored sign-in: the ways to sign in.</summary>
    SignIn,
    /// <summary>Signed in, with no game to play: the way to get one.</summary>
    GetGame,
    /// <summary>Signed in, with a game: Play, and a row for each thing the player can look after.</summary>
    Home,
}

/// <summary>Where things stand when the launcher shows its first screen.</summary>
/// <param name="SignedIn">A sign-in is stored on this iPhone.</param>
/// <param name="AccountName">The stored sign-in's account name, if known.</param>
/// <param name="NoGameReason">Why there is no game to play, or null when there is one.</param>
/// <param name="DownloadUnderWay">A download was started and has not finished, so it can carry on.</param>
/// <param name="InstalledVersion">The game version on this iPhone, as the player knows it, or null.</param>
/// <param name="InstalledBranch">Stable or Beta, if known.</param>
/// <param name="UpdateAvailable">Steam's build of that branch, as the version screen last saw it, is another one.</param>
/// <param name="OwnershipConfirmed">The stored sign-in has passed Steam's ownership check, so Play needs no connection.</param>
public sealed record LauncherFacts(bool SignedIn, string? AccountName, string? NoGameReason, bool DownloadUnderWay, string? InstalledVersion,
    string? InstalledBranch = null, bool UpdateAvailable = false, bool ModsEnabled = false, bool OwnershipConfirmed = false);

/// <summary>One row of the home screen: what it is about, and how that stands now. The label is also the button the row is.</summary>
public sealed record HomeRow(string Label, string Value);

/// <summary>What the first screen says and offers, decided here so the view only draws it.</summary>
/// <param name="Status">The sentence under the heading.</param>
/// <param name="PrimaryAction">The label of the button at the bottom.</param>
/// <param name="Rows">The home screen's rows, top to bottom; empty on the other screens.</param>
/// <param name="OfferSignInAgain">The last attempt failed and the stored sign-in may be why, so another sign-in is offered.</param>
/// <param name="OfferGameVersion">The game version screen is reachable from here.</param>
public sealed record EntryScreen(EntryKind Kind, string Heading, string Status, string PrimaryAction, IReadOnlyList<HomeRow> Rows, bool OfferSignInAgain, bool OfferGameVersion)
{
    public const string Welcome = "Sign in to Steam to verify that you own the game.\nThis app keeps its saves in its own iPhone sandbox.";
    public const string WelcomeNoGame = "Sign in to Steam to download the game you own.\nThis app holds none of it, and keeps its saves in its own iPhone sandbox.";
    public const string NoGame = "The game is not on this iPhone yet.";
    public const string DownloadUnderWay = "The download stopped before it finished.\nIt carries on from where it was.";
    public const string Ready = "The game is on this iPhone. Nothing is sent to Steam until you press Play.";
    public const string ReadyOffline = "The game is on this iPhone. Play needs no connection to Steam, so it works offline too.";

    public const string GameVersionRow = "Game version";
    public const string CloudSavesRow = "Cloud saves";
    public const string AccountRow = "Steam account";
    public const string ModsRow = "Mods";

    /// <summary>The first screen for <paramref name="facts"/>.</summary>
    /// <param name="message">Said instead of the usual sentence, for example why the last attempt stopped.</param>
    /// <param name="failed">The message is why the last attempt failed.</param>
    public static EntryScreen For(LauncherFacts facts, string? message = null, bool failed = false)
    {
        if (!facts.SignedIn)
            return new(EntryKind.SignIn, "Sign in to Steam", message ?? (facts.NoGameReason == null ? Welcome : WelcomeNoGame), "", [], false, false);
        if (facts.NoGameReason != null)
        {
            string status = message ?? (facts.DownloadUnderWay ? DownloadUnderWay : facts.NoGameReason);
            string action = facts.DownloadUnderWay ? "Continue the download" : "Download the game";
            return new(EntryKind.GetGame, "Get the game", status, action, [], failed, facts.DownloadUnderWay);
        }
        HomeRow[] rows =
        [
            new(GameVersionRow, facts.UpdateAvailable ? "Update available"
                : facts.InstalledVersion is { } version ? "v" + version + (string.IsNullOrEmpty(facts.InstalledBranch) ? "" : " " + facts.InstalledBranch) : "On this iPhone"),
            new(CloudSavesRow, "Steam Cloud"),
            new(AccountRow, facts.AccountName ?? "Signed in"),
            new(ModsRow, facts.ModsEnabled ? "On, tap to turn off" : "Off, tap to turn on"),
        ];
        return new(EntryKind.Home, "Slay the Spire 2", message ?? (facts.OwnershipConfirmed ? ReadyOffline : Ready), "Play", rows, failed, true);
    }
}
