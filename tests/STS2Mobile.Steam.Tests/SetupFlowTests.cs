using SteamKit2;
using SteamKit2.Authentication;
using STS2Mobile.GamePatcher;
using STS2Mobile.Launcher;
using STS2Mobile.Steam.Connection;
using STS2Mobile.Steam.Depot;

namespace STS2Mobile.Steam.Tests;

public sealed class SetupFlowTests
{
    private static readonly BranchBuild Normal = new(GameInstaller.NormalBranch, 23811903, 1);

    [Fact]
    public void StepsBeforeTheCurrentOneAreDoneAndTheRestWait()
    {
        var steps = SetupSteps.For(SetupStep.Download);

        Assert.Equal(["Sign in", "Ownership", "Download", "Prepare", "Effects", "Reopen", "Play"], steps.Select(step => step.Label));
        Assert.Equal([SetupStepState.Done, SetupStepState.Done, SetupStepState.Current, SetupStepState.Waiting, SetupStepState.Waiting, SetupStepState.Waiting, SetupStepState.Waiting],
            steps.Select(step => step.State));
    }

    [Theory]
    [InlineData(SetupStep.SignIn, 0)]
    [InlineData(SetupStep.Play, 6)]
    public void TheFirstAndLastStepsCanBeCurrent(SetupStep step, int index)
    {
        var steps = SetupSteps.For(step);

        Assert.Single(steps, view => view.State == SetupStepState.Current);
        Assert.Equal(SetupStepState.Current, steps[index].State);
    }

    private static readonly GameProfile Tested = new("0.111.0", new Dictionary<string, string>(), new Dictionary<string, string>());
    private static readonly GameVersionCheck BetaTested = new(GameVersionProblem.None, new BranchBuild(GameInstaller.BetaBranch, 24724944, 2), Tested, 4_950_000_000);
    private static readonly GameVersionCheck StableUntested = new(GameVersionProblem.None, Normal, null, 4_760_000_000);
    private static readonly InstalledBuild BetaInstalled = new(GameInstaller.BetaBranch, 2, 24724944, "0.111.0", Tested: true);

    [Fact]
    public void ConfirmingSaysWhatGoesAndWhatStays()
    {
        string replacing = GameInstallMessages.Confirm(BetaTested, 1_200_000_000, replacesGame: true);
        Assert.Contains("deletes the game", replacing);
        Assert.Contains("Beta version (v0.111.0, tested, 1.2 GB)", replacing);
        Assert.Contains("needs 5.0 GB free", replacing);
        Assert.Contains(GameInstallMessages.Kept, replacing);
        Assert.DoesNotContain("not been tested", replacing);

        Assert.DoesNotContain("deletes", GameInstallMessages.Confirm(BetaTested, 1, replacesGame: false));
    }

    [Fact]
    public void AnUntestedBuildIsSaidToBeOneByItsBuildNumber()
    {
        string question = GameInstallMessages.Confirm(StableUntested, 1_100_000_000, replacesGame: false);
        Assert.Contains("Stable version (build 23811903, untested, 1.1 GB)", question);
        Assert.Contains(GameInstallMessages.Untested, question);
    }

    [Fact]
    public void BetaToStableWarnsAboutSavesAndBacksThemUp()
    {
        string question = GameInstallMessages.Confirm(StableUntested, 1, replacesGame: true, BetaInstalled);
        Assert.Contains("(v0.111.0, Beta, tested)", question);
        Assert.Contains(GameInstallMessages.BetaToStable, question);
        Assert.Contains("copied to a backup", question);
        Assert.True(GameInstallMessages.IsSwitch(BetaInstalled, GameInstaller.NormalBranch));
    }

    [Fact]
    public void StableToBetaBacksUpWithoutTheWarningAndAnUpdateDoesNeither()
    {
        var stable = BetaInstalled with { Branch = GameInstaller.NormalBranch };
        string toBeta = GameInstallMessages.Confirm(BetaTested, 1, replacesGame: true, stable);
        Assert.DoesNotContain(GameInstallMessages.BetaToStable, toBeta);
        Assert.Contains("copied to a backup", toBeta);

        string update = GameInstallMessages.Confirm(BetaTested, 1, replacesGame: true, BetaInstalled);
        Assert.DoesNotContain("backup", update);
        Assert.False(GameInstallMessages.IsSwitch(BetaInstalled, GameInstaller.BetaBranch));
        // An older install of unknown origin is not a switch: nothing says which branch it came from.
        Assert.False(GameInstallMessages.IsSwitch(BetaInstalled with { Branch = "" }, GameInstaller.NormalBranch));
    }

    [Fact]
    public void WithOneVersionToOfferTheConfirmationSaysWhyTheOtherIsNotThere()
    {
        var other = new GameVersionCheck(GameVersionProblem.NotOnSteam, null, null);

        string question = GameInstallMessages.Confirm(BetaTested, 1, replacesGame: false, null, other, "Stable");

        Assert.Contains("Steam has no Stable version for this account", question);
    }

    [Fact]
    public void AnUntestedStableIsSaidNotToRunYetBesideTheBeta()
    {
        var other = new GameVersionCheck(GameVersionProblem.UntestedStable, Normal, null);

        string question = GameInstallMessages.Confirm(BetaTested, 1, replacesGame: false, null, other, "Stable");

        Assert.Contains("Steam's Stable version (build 23811903, untested) does not run with this app yet", question);
    }

    [Fact]
    public void NeitherVersionSaysWhyForEachAndThatNothingChanged()
    {
        string message = GameInstallMessages.NeitherUsable(new GameVersionCheck(GameVersionProblem.NotOnSteam, null, null),
            new GameVersionCheck(GameVersionProblem.NotOnSteam, null, null));

        Assert.Contains("no Stable version", message);
        Assert.Contains("no Beta version", message);
        Assert.Contains("Nothing was downloaded or deleted", message);
    }

    [Fact]
    public void TooLittleSpaceSaysHowMuchAndThatNothingChanged()
    {
        string message = GameInstallMessages.Problem(BetaTested with { Problem = GameVersionProblem.NotEnoughSpace, AvailableBytes = 1_000_000_000 }, "Beta");
        Assert.Contains("Beta version needs 5.0 GB free and this iPhone has 1.0 GB", message);
        Assert.Contains("nothing was downloaded or deleted", message);
    }

    [Fact]
    public void DeletingSaysWhatItFreesOnlyWhenThatIsKnown()
    {
        Assert.Contains("frees 2.0 GB", GameInstallMessages.ConfirmDelete(2_000_000_000));
        Assert.DoesNotContain("frees", GameInstallMessages.ConfirmDelete(null));
        Assert.Contains(GameInstallMessages.Kept, GameInstallMessages.ConfirmDelete(null));
    }

    [Fact]
    public void AStoppedDownloadSaysWhyAndThatItsWorkIsKept()
    {
        Assert.Contains("ran out of space", GameInstallMessages.Failure(new IOException("full", 28)));
        Assert.Contains("already fetched is kept", GameInstallMessages.Failure(new IOException("full", 28)));
        Assert.Contains("could not be reached", GameInstallMessages.Failure(new HttpRequestException("no route")));
        Assert.Contains("could not be reached", GameInstallMessages.Failure(new TaskCanceledException("timed out", new TimeoutException())));
        Assert.Contains("could not be reached", GameInstallMessages.Failure(new System.Net.Sockets.SocketException(61)));
        Assert.Contains("could not be reached", GameInstallMessages.Failure(new TimeoutException()));
        Assert.Contains("Something went wrong", GameInstallMessages.Failure(new InvalidOperationException("bug")));
        Assert.Contains("InvalidOperationException", GameInstallMessages.Failure(new InvalidOperationException("bug")));
        Assert.Contains("could not be reached", GameInstallMessages.Failure(new TaskCanceledException("timed out")));
        Assert.DoesNotContain("could not be reached", GameInstallMessages.Failure(new InvalidOperationException("bug")));
        Assert.Contains("not what this app expects", GameInstallMessages.Failure(new InvalidDataException("bad chunk")));
    }

    [Fact]
    public void AStoredSignInSteamRefusesAsksToSignInAgainButABusyServerDoesNot()
    {
        Assert.Contains("Sign in again", SignInFailure.Describe(new SteamLoginFailedException(EResult.AccessDenied)));
        Assert.DoesNotContain("Sign in again", SignInFailure.Describe(new SteamLoginFailedException(EResult.TryAnotherCM)));
        Assert.Contains("limiting", SignInFailure.Describe(new SteamLoginFailedException(EResult.RateLimitExceeded)));
    }

    [Fact]
    public void AnAccountWithoutTheGameIsToldToUseAnother() =>
        Assert.Contains("does not own", SignInFailure.Describe(new UnauthorizedAccessException("no")));

    [Fact]
    public void ANetworkFailureIsNotBlamedOnTheAccount() =>
        Assert.Contains("could not be reached", SignInFailure.Describe(new TimeoutException()));

    [Fact]
    public void DeletingLeavesNoGameNoDownloadAndEverythingElse()
    {
        string root = Directory.CreateTempSubdirectory("game-delete-").FullName;
        try
        {
            string game = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(game, "original"));
            File.WriteAllBytes(Path.Combine(game, "FullGame.pck"), new byte[300]);
            File.WriteAllText(Path.Combine(root, "save.json"), "kept");
            var profile = new GameProfile("1", new Dictionary<string, string>(), new Dictionary<string, string>(), "", [], new SteamGameBuild(7, 500, 1000, ""));
            var installer = new GameInstaller(game, [profile], () => 2000);
            installer.Begin(GameInstaller.BetaBranch, 500);
            File.WriteAllBytes(Path.Combine(game, "partial"), new byte[300]);
            Assert.NotNull(installer.Pending);
            Assert.Equal(300, installer.GameBytes);

            installer.Delete();

            Assert.False(Directory.Exists(game));
            Assert.Null(installer.Pending);
            Assert.False(File.Exists(game + GameInstaller.RecordSuffix));
            Assert.Equal(0, installer.GameBytes);
            Assert.True(File.Exists(Path.Combine(root, "save.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
