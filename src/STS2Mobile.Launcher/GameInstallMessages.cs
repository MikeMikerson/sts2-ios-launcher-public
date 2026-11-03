using System.Globalization;
using STS2Mobile.GamePatcher;

namespace STS2Mobile.Launcher;

/// <summary>What the player reads about getting the game: plain sentences that say what happened to their files and what to do.</summary>
public static class GameInstallMessages
{
    public const string Kept = "Your saves and Steam sign-in stay on this iPhone.";
    public const string ChooseVersion = "Which version of the game do you want?\nSteam's normal version (Stable), or its beta. Saves do not always move between them.";
    public const string Untested = "This build has not been tested with this app. The app patches it on this iPhone after the download, and if a change the game needs cannot be made to this build, it tells you then.";
    public const string BetaToStable = "Saves made in the beta may not open in Stable.";
    private const double Gigabyte = 1_000_000_000;

    public static string Gb(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / Gigabyte:0.0} GB");

    /// <summary>The branch as the player knows it: Stable or Beta.</summary>
    public static string Name(string branch) => branch == GameInstaller.BetaBranch ? "Beta" : "Stable";

    /// <summary>Which build, and whether this app has tested it: "v0.111.0, tested" or "build 23811903, untested".</summary>
    public static string Describe(GameVersionCheck check) => check.Profile is { } profile
        ? $"v{profile.Version}, tested"
        : check.Build is { BuildId: > 0 } build ? $"build {build.BuildId}, untested" : "untested";

    /// <summary>Why this version cannot be used, with nothing changed.</summary>
    public static string Problem(GameVersionCheck check, string name) => check.Problem switch
    {
        GameVersionProblem.NotEnoughSpace => $"The {name} version needs {Gb(check.RequiredBytes)} free and this iPhone has {Gb(check.AvailableBytes)}, so nothing was downloaded or deleted.\nFree up some space and try again.",
        GameVersionProblem.UntestedStable => $"{NotOfferedYet(check)} Nothing was downloaded or deleted.",
        _ => $"Steam has no {name} version for this account, so nothing was downloaded or deleted.",
    };

    /// <summary>The same for neither version, which leaves the player nothing to choose.</summary>
    public static string NeitherUsable(GameVersionCheck normal, GameVersionCheck beta) =>
        Short(normal, "Stable") + "\n" + Short(beta, "Beta") + "\nNothing was downloaded or deleted.";

    /// <summary>Asked before anything is deleted or fetched.</summary>
    /// <param name="installed">The game on this iPhone, when this replaces it; null when there is none, or only part of a download.</param>
    /// <param name="other">The version that cannot be used, when that is why the player is not choosing. Null when they chose.</param>
    public static string Confirm(GameVersionCheck check, long downloadBytes, bool replacesGame, InstalledBuild? installed = null, GameVersionCheck? other = null, string? otherName = null)
    {
        string name = Name(check.Build!.Branch);
        string what = $"{Describe(check)}, {Gb(downloadBytes)}";
        string first = replacesGame
            ? $"This deletes the game on this iPhone{(installed == null ? "" : " (" + Describe(installed) + ")")}, then downloads Steam's {name} version ({what})."
            : $"Download Steam's {name} version ({what})?";
        var lines = new List<string> { first };
        if (other != null) lines.Add(Short(other, otherName!));
        if (IsSwitch(installed, check.Build.Branch))
            lines.Add((installed!.Branch == GameInstaller.BetaBranch && check.Build.Branch == GameInstaller.NormalBranch ? BetaToStable + " " : "")
                + "Your saves are copied to a backup before anything is deleted.");
        if (!check.Tested) lines.Add(Untested);
        lines.Add($"Setting it up needs {Gb(check.RequiredBytes)} free for a while. {Kept}");
        return string.Join("\n", lines);
    }

    /// <summary>The game on this iPhone came from the other branch, so replacing it is a switch, and the saves are backed up first.</summary>
    public static bool IsSwitch(InstalledBuild? installed, string branch) => installed is { Branch.Length: > 0 } && installed.Branch != branch;

    /// <summary>The game on this iPhone, as the version screen and the confirmations name it: "v0.111.0, Beta, tested".</summary>
    public static string Describe(InstalledBuild installed)
    {
        string branch = InstalledBuild.BranchName(installed.Branch);
        return $"v{installed.Version}{(branch.Length > 0 ? ", " + branch : "")}, {(installed.Tested ? "tested" : "untested")}";
    }

    /// <param name="gameBytes">What deleting frees, or null for a download under way, whose partial files have their final length but not that much on disk.</param>
    public static string ConfirmDelete(long? gameBytes) => gameBytes is { } bytes
        ? $"Delete the game from this iPhone? That frees {Gb(bytes)}.\n{Kept} Playing again means downloading it again."
        : $"Delete what was downloaded so far?\n{Kept} Playing means downloading the game from the start.";

    public static string Deleted => $"The game was deleted. {Kept}";

    /// <summary>The result the player sees once every file is in.</summary>
    /// <param name="version">The game's version, from the release information the download brought.</param>
    /// <param name="backup">Where the saves were copied before a switch, or null.</param>
    public static Notice Downloaded(GameVersionCheck check, string version, long downloadBytes, string? backup = null) => Notice.Success("Download complete",
        $"Steam's {Name(check.Build!.Branch)} version (v{version}, {(check.Tested ? "tested" : "untested")}, {Gb(downloadBytes)}) came from Steam and every file was checked. "
        + (backup == null ? "" : "Your saves were copied to a backup first. ")
        + "Next the app prepares it for this iPhone, once.");

    /// <summary>The result the player sees when a download stopped or never started; <paramref name="why"/> says which and what to do.</summary>
    public static Notice NotDownloaded(string why) => Notice.Failure("Not downloaded", why);

    public static Notice DeletedNotice => Notice.Success("Game deleted", Deleted);

    /// <summary>The saves could not be copied, so nothing was deleted and the switch did not start.</summary>
    public static string BackupFailed(Exception error) =>
        $"Your saves could not be copied to a backup ({error.GetType().Name}), so nothing was downloaded or deleted.\nFree up some space and try again.";

    public static string Progress(int percent, long doneBytes, long totalBytes) =>
        $"Downloading the game: {percent}%, {Gb(doneBytes)} of {Gb(totalBytes)}.\nIf you leave this app, it carries on from here when you come back.";

    /// <summary>Why a download stopped. <see cref="IOException"/> 28 is the system's "no space left on device".</summary>
    public static string Failure(Exception error)
    {
        const string Rest = "\nWhat it already fetched is kept. " + Kept;
        return error switch
        {
            IOException { HResult: 28 } => "This iPhone ran out of space, so the download stopped." + Rest,
            UnauthorizedAccessException => "Steam refused the download (" + error.Message + ").",
            InvalidDataException => "The downloaded game was not what this app expects (" + error.Message + ").",
            _ when IsNetwork(error) => "The download stopped because Steam could not be reached. Check the connection and try again." + Rest,
            _ => "Something went wrong and the download stopped (" + error.GetType().Name + "). Try again." + Rest,
        };
    }

    /// <summary>
    /// A failure of the connection itself: no route, a dropped socket or Steam connection, or a request that timed out. The player going back
    /// never reaches <see cref="Failure"/>, so a cancellation here is a timeout.
    /// </summary>
    private static bool IsNetwork(Exception error) =>
        error is HttpRequestException or TimeoutException or System.Net.Sockets.SocketException or OperationCanceledException or SteamKit2.AsyncJobFailedException;

    private static string NotOfferedYet(GameVersionCheck check) =>
        $"Steam's Stable version ({Describe(check)}) does not run with this app yet, so it is not offered until the app is tested with it.";

    private static string Short(GameVersionCheck check, string name) => check.Problem switch
    {
        GameVersionProblem.NotEnoughSpace => $"The {name} version needs {Gb(check.RequiredBytes)} free and this iPhone has {Gb(check.AvailableBytes)}.",
        GameVersionProblem.UntestedStable => NotOfferedYet(check),
        _ => $"Steam has no {name} version for this account.",
    };
}
