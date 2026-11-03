using STS2Mobile.GamePatcher;
using STS2Mobile.Steam.Depot;

namespace STS2Mobile.Launcher;

/// <summary>What a button on the version screen does.</summary>
public enum VersionAction
{
    /// <summary>Steam's current build of the installed branch replaces the game.</summary>
    Update,
    SwitchToStable,
    SwitchToBeta,
    /// <summary>Ask Steam again, after it could not be reached.</summary>
    CheckAgain,
    Delete,
}

/// <summary>One button: its label is also what <c>STS2_AUTO_TAP</c> presses.</summary>
public sealed record VersionButton(string Label, VersionAction Action, bool Primary = false, bool Danger = false);

/// <summary>One line of the screen's ledger: what it is about, and how that stands.</summary>
public sealed record VersionFact(string Label, string Value);

/// <summary>Steam's current builds of the two branches, as last asked; a branch it does not offer this account is null.</summary>
public sealed record BuildsOnSteam(BranchBuild? Normal, BranchBuild? Beta)
{
    public BranchBuild? For(string branch) => branch == GameInstaller.BetaBranch ? Beta : branch == GameInstaller.NormalBranch ? Normal : null;
}

/// <summary>Where things stand when the version screen is drawn.</summary>
/// <param name="Installed">The game on this iPhone, or null if nothing says which build it is.</param>
/// <param name="Steam">Steam's builds now, or null if Steam could not be asked.</param>
/// <param name="SteamFailure">Why Steam could not be asked, when <paramref name="Steam"/> is null.</param>
public sealed record VersionFacts(InstalledBuild? Installed, BuildsOnSteam? Steam, string? SteamFailure = null);

/// <summary>
/// What the version screen says and offers: the game on this iPhone against what Steam serves now, whether an update is available,
/// and Update, Switch and Delete. Decided here so the view only draws it. Nothing here asks Steam or changes a file.
/// </summary>
public sealed record VersionScreen(string Heading, string Status, IReadOnlyList<VersionFact> Facts, IReadOnlyList<VersionButton> Buttons, bool UpdateAvailable)
{
    public const string Title = "Game version";
    public const string UpdateLabel = "Update";
    public const string SwitchToStableLabel = "Switch to Stable";
    public const string SwitchToBetaLabel = "Switch to Beta";
    public const string CheckAgainLabel = "Ask Steam again";
    public const string DeleteLabel = "Delete the game";

    public static VersionScreen For(VersionFacts facts)
    {
        var installed = facts.Installed;
        var lines = new List<VersionFact> { new("On this iPhone", installed == null ? "A game of unknown version" : GameInstallMessages.Describe(installed)) };
        if (installed is { Build: > 0 }) lines.Add(new("Its Steam build", installed.Build.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var buttons = new List<VersionButton>();
        string status;
        bool update = false;
        if (facts.Steam is not { } steam)
        {
            status = $"Steam could not be asked which builds it has now{(facts.SteamFailure == null ? "" : " (" + facts.SteamFailure + ")")}. The game on this iPhone is unchanged.";
            buttons.Add(new(CheckAgainLabel, VersionAction.CheckAgain, Primary: true));
        }
        else
        {
            lines.Add(new("Steam's Stable now", Describe(steam.Normal, installed)));
            lines.Add(new("Steam's Beta now", Describe(steam.Beta, installed)));
            update = IsUpdateAvailable(installed, steam);
            string branch = installed is { Branch.Length: > 0 } ? GameInstallMessages.Name(installed.Branch) : "";
            status = update
                ? $"Update available: Steam's {branch} has a different build from the one on this iPhone. Updating deletes the game and downloads the new build. {GameInstallMessages.Kept}"
                : branch.Length > 0 && steam.For(installed!.Branch) != null
                    ? $"Up to date with Steam's {branch}. The app never updates the game on its own."
                    : "Choose a version from Steam to replace the game on this iPhone. The app never updates the game on its own.";
            if (update) buttons.Add(new(UpdateLabel, VersionAction.Update, Primary: true));
            // Stable only when this app has tested its build (GameVersionProblem.UntestedStable).
            if (steam.Normal != null && GameInstaller.IsOffered(steam.Normal, GameProfile.ForManifest(steam.Normal.ManifestId)) && installed?.Branch != GameInstaller.NormalBranch) buttons.Add(new(SwitchToStableLabel, VersionAction.SwitchToStable, Primary: !update && buttons.Count == 0));
            if (steam.Beta != null && installed?.Branch != GameInstaller.BetaBranch) buttons.Add(new(SwitchToBetaLabel, VersionAction.SwitchToBeta, Primary: !update && buttons.Count == 0));
        }
        if (installed is { Tested: false })
        {
            var skipped = installed.Skipped;
            status += skipped.Count == 0
                ? "\nThis build has not been tested with this app, but every change the app makes was applied to it."
                : $"\nThis build has not been tested with this app. It plays without: {string.Join("; ", skipped.Select(patch => patch.Name))}.";
        }
        buttons.Add(new(DeleteLabel, VersionAction.Delete, Danger: true));
        return new VersionScreen(Title, status, lines, buttons, update);
    }

    /// <summary>Steam's build of the installed branch is another manifest than the one on this iPhone. A new build number alone is not an update: it also moves when only another system's files changed.</summary>
    public static bool IsUpdateAvailable(InstalledBuild? installed, BuildsOnSteam? steam) =>
        installed is { Branch.Length: > 0, Manifest: > 0 } && steam?.For(installed.Branch) is { } now && now.ManifestId != installed.Manifest;

    /// <summary>The branch a button switches to.</summary>
    public static string BranchOf(VersionAction action, InstalledBuild? installed) => action switch
    {
        VersionAction.SwitchToStable => GameInstaller.NormalBranch,
        VersionAction.SwitchToBeta => GameInstaller.BetaBranch,
        _ => installed?.Branch ?? "",
    };

    private static string Describe(BranchBuild? build, InstalledBuild? installed)
    {
        if (build == null) return "not offered to this account";
        var profile = GameProfile.ForManifest(build.ManifestId);
        string tested = profile != null ? $"v{profile.Version}, tested" : GameInstaller.IsOffered(build, profile) ? "untested" : "untested, not offered yet";
        string same = installed != null && installed.Manifest == build.ManifestId ? ", the one on this iPhone" : "";
        return $"build {build.BuildId}, {tested}{same}";
    }
}
