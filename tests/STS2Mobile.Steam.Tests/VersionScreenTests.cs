using STS2Mobile.GamePatcher;
using STS2Mobile.Launcher;
using STS2Mobile.Steam.Depot;

namespace STS2Mobile.Steam.Tests;

/// <summary>The version screen: the game here against Steam now, Update available, Update, Switch and Delete.</summary>
public sealed class VersionScreenTests : IDisposable
{
    private static readonly ulong TestedManifest = GameProfile.Current.Steam!.Manifest;
    private static readonly BranchBuild BetaNow = new(GameInstaller.BetaBranch, 26625880, TestedManifest);
    private static readonly BranchBuild StableNow = new(GameInstaller.NormalBranch, 23811903, 8653035385353091849);
    /// <summary>A Stable that has reached the tested build, as the beta will.</summary>
    private static readonly BranchBuild StableTested = new(GameInstaller.NormalBranch, 26625880, TestedManifest);
    private static readonly InstalledBuild Beta = new(GameInstaller.BetaBranch, TestedManifest, 26625880, "0.113.0", Tested: true);
    private readonly string _root = Directory.CreateTempSubdirectory("version-screen-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static VersionScreen Screen(InstalledBuild? installed, BranchBuild? stable = null, BranchBuild? beta = null) =>
        VersionScreen.For(new VersionFacts(installed, new BuildsOnSteam(stable ?? StableNow, beta ?? BetaNow)));

    private static string[] Labels(VersionScreen screen) => screen.Buttons.Select(button => button.Label).ToArray();

    [Fact]
    public void AnUntestedStableIsNotOffered()
    {
        var screen = Screen(Beta);
        Assert.Equal([VersionScreen.DeleteLabel], Labels(screen));
        Assert.Equal(new VersionFact("Steam's Stable now", "build 23811903, untested, not offered yet"), screen.Facts.Single(fact => fact.Label == "Steam's Stable now"));
        Assert.Equal([VersionScreen.SwitchToBetaLabel, VersionScreen.DeleteLabel], Labels(Screen(new InstalledBuild("", 0, 0, "0.99.0", Tested: false))));
    }

    [Fact]
    public void UpToDateOnBetaOffersStableAndDelete()
    {
        var screen = Screen(Beta, stable: StableTested);
        Assert.False(screen.UpdateAvailable);
        Assert.Equal("Game version", screen.Heading);
        Assert.StartsWith("Up to date with Steam's Beta.", screen.Status);
        Assert.Equal([VersionScreen.SwitchToStableLabel, VersionScreen.DeleteLabel], Labels(screen));
        Assert.True(screen.Buttons[0].Primary);
        Assert.True(screen.Buttons[1].Danger);
        Assert.Equal(new VersionFact("On this iPhone", "v0.113.0, Beta, tested"), screen.Facts[0]);
        Assert.Equal(new VersionFact("Steam's Stable now", "build 26625880, v0.113.0, tested, the one on this iPhone"), screen.Facts.Single(fact => fact.Label == "Steam's Stable now"));
        Assert.Equal(new VersionFact("Steam's Beta now", "build 26625880, v0.113.0, tested, the one on this iPhone"), screen.Facts.Single(fact => fact.Label == "Steam's Beta now"));
    }

    [Fact]
    public void AnotherManifestOnTheInstalledBranchIsAnUpdate()
    {
        var screen = Screen(Beta, StableTested, new BranchBuild(GameInstaller.BetaBranch, 25000000, 1234));
        Assert.True(screen.UpdateAvailable);
        Assert.StartsWith("Update available", screen.Status);
        Assert.Equal([VersionScreen.UpdateLabel, VersionScreen.SwitchToStableLabel, VersionScreen.DeleteLabel], Labels(screen));
        Assert.True(screen.Buttons[0].Primary);
        Assert.False(screen.Buttons[1].Primary);
        Assert.Equal(GameInstaller.BetaBranch, VersionScreen.BranchOf(VersionAction.Update, Beta));
    }

    [Fact]
    public void ANewBuildNumberWithTheSameManifestIsNoUpdate() =>
        Assert.False(Screen(Beta, beta: new BranchBuild(GameInstaller.BetaBranch, 25000000, TestedManifest)).UpdateAvailable);

    [Fact]
    public void OnStableTheSwitchIsToBeta()
    {
        var stable = new InstalledBuild(GameInstaller.NormalBranch, StableNow.ManifestId, StableNow.BuildId, "0.103.2", Tested: false);
        var screen = Screen(stable);
        Assert.Equal([VersionScreen.SwitchToBetaLabel, VersionScreen.DeleteLabel], Labels(screen));
        Assert.Equal(GameInstaller.BetaBranch, VersionScreen.BranchOf(VersionAction.SwitchToBeta, stable));
        Assert.Contains("every change the app makes was applied to it", screen.Status);
    }

    [Fact]
    public void AnUntestedBuildSaysWhatItPlaysWithout()
    {
        var stable = new InstalledBuild(GameInstaller.NormalBranch, StableNow.ManifestId, StableNow.BuildId, "0.103.2", Tested: false,
            [new PatchOutcome("No mouse cursor", false, false, "gone"), new PatchOutcome("The game fills the screen", false, false, "gone"), new PatchOutcome("No LAN play", true, true)]);
        var screen = Screen(stable);
        Assert.Contains("It plays without: No mouse cursor; The game fills the screen.", screen.Status);
        Assert.Equal("v0.103.2, Stable, untested", screen.Facts[0].Value);
    }

    [Fact]
    public void AGameOfUnknownOriginCanBeReplacedByEither()
    {
        var unknown = new InstalledBuild("", 0, 0, "0.99.0", Tested: false);
        var screen = Screen(unknown, StableTested);
        Assert.False(screen.UpdateAvailable);
        Assert.StartsWith("Choose a version", screen.Status);
        Assert.Equal([VersionScreen.SwitchToStableLabel, VersionScreen.SwitchToBetaLabel, VersionScreen.DeleteLabel], Labels(screen));
        Assert.Single(screen.Buttons, button => button.Primary);
    }

    [Fact]
    public void ABranchSteamDoesNotOfferHasNoSwitch()
    {
        var screen = VersionScreen.For(new VersionFacts(Beta, new BuildsOnSteam(null, BetaNow)));
        Assert.Equal([VersionScreen.DeleteLabel], Labels(screen));
        Assert.Contains(screen.Facts, fact => fact.Value == "not offered to this account");
    }

    [Fact]
    public void WhenSteamCannotBeAskedTheGameIsShownWithAWayToAskAgain()
    {
        var screen = VersionScreen.For(new VersionFacts(Beta, null, "TimeoutException"));
        Assert.False(screen.UpdateAvailable);
        Assert.Contains("could not be asked", screen.Status);
        Assert.Contains("TimeoutException", screen.Status);
        Assert.Equal([VersionScreen.CheckAgainLabel, VersionScreen.DeleteLabel], Labels(screen));
        Assert.DoesNotContain(screen.Facts, fact => fact.Label.StartsWith("Steam's"));
    }

    [Fact]
    public void WhatSteamServedIsKeptForTheHomeScreen()
    {
        Assert.Null(SteamBuildsSeen.Read(_root));
        SteamBuildsSeen.Write(_root, new BuildsOnSteam(StableNow, null));
        var seen = SteamBuildsSeen.Read(_root)!;
        Assert.Equal(StableNow, seen.Normal);
        Assert.Null(seen.Beta);
        var onStable = new InstalledBuild(GameInstaller.NormalBranch, 1, 1, "0.1", Tested: false);
        Assert.True(VersionScreen.IsUpdateAvailable(onStable, seen));
        Assert.False(VersionScreen.IsUpdateAvailable(Beta, seen));
        File.WriteAllText(Path.Combine(_root, SteamBuildsSeen.FileName), "{ broken");
        Assert.Null(SteamBuildsSeen.Read(_root));
    }

    [Fact]
    public void ASwitchCopiesTheSavesAsideAndLeavesThemInPlace()
    {
        Assert.Null(SaveBackup.Copy(_root, GameInstaller.NormalBranch, DateTime.UtcNow));
        string account = Path.Combine(_root, "steam", "76561190000000000");
        Directory.CreateDirectory(Path.Combine(account, "modded"));
        File.WriteAllText(Path.Combine(account, "profile1.save"), "run");
        File.WriteAllText(Path.Combine(account, "modded", "profile1.save"), "modded run");

        string backup = SaveBackup.Copy(_root, GameInstaller.NormalBranch, new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc))!;

        Assert.Equal(Path.Combine(_root, "save-backups", "20261102T100000Z-before-stable"), backup);
        Assert.Equal("run", File.ReadAllText(Path.Combine(backup, "steam", "76561190000000000", "profile1.save")));
        Assert.Equal("modded run", File.ReadAllText(Path.Combine(backup, "steam", "76561190000000000", "modded", "profile1.save")));
        Assert.Equal("run", File.ReadAllText(Path.Combine(account, "profile1.save")));
        Assert.Throws<IOException>(() => SaveBackup.Copy(_root, GameInstaller.NormalBranch, new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc)));
    }
}
