using STS2Mobile.Launcher;

namespace STS2Mobile.Steam.Tests;

public sealed class EntryScreenTests
{
    private static readonly LauncherFacts WithGame = new(SignedIn: true, AccountName: "player", NoGameReason: null, DownloadUnderWay: false, InstalledVersion: "0.111.0");

    [Fact]
    public void WithoutASignInOnlyTheWaysToSignInAreOffered()
    {
        var screen = EntryScreen.For(WithGame with { SignedIn = false });

        Assert.Equal(EntryKind.SignIn, screen.Kind);
        Assert.Equal(EntryScreen.Welcome, screen.Status);
        Assert.Empty(screen.Rows);
        Assert.False(screen.OfferGameVersion);
    }

    [Fact]
    public void WithoutASignInOrAGameTheWelcomeSaysTheGameIsDownloaded()
    {
        var screen = EntryScreen.For(WithGame with { SignedIn = false, NoGameReason = EntryScreen.NoGame });

        Assert.Equal(EntryScreen.WelcomeNoGame, screen.Status);
    }

    [Fact]
    public void SignedInWithAGameTheHomeScreenHasPlayAndARowPerArea()
    {
        var screen = EntryScreen.For(WithGame);

        Assert.Equal(EntryKind.Home, screen.Kind);
        Assert.Equal("Play", screen.PrimaryAction);
        Assert.Equal([EntryScreen.GameVersionRow, EntryScreen.CloudSavesRow, EntryScreen.AccountRow, EntryScreen.ModsRow], screen.Rows.Select(row => row.Label));
        Assert.Equal("v0.111.0", screen.Rows[0].Value);
        Assert.Equal("player", screen.Rows[2].Value);
        Assert.True(screen.OfferGameVersion);
        Assert.False(screen.OfferSignInAgain);
    }

    [Theory]
    [InlineData(false, "Off, tap to turn on")]
    [InlineData(true, "On, tap to turn off")]
    public void HomeShowsTheActiveModsChoice(bool enabled, string value)
    {
        var screen = EntryScreen.For(WithGame with { ModsEnabled = enabled });
        Assert.Equal(value, screen.Rows.Single(row => row.Label == EntryScreen.ModsRow).Value);
    }

    [Fact]
    public void AFailedAttemptOnHomeKeepsPlayAndOffersAnotherSignIn()
    {
        var screen = EntryScreen.For(WithGame, "Steam did not answer.", failed: true);

        Assert.Equal("Steam did not answer.", screen.Status);
        Assert.Equal("Play", screen.PrimaryAction);
        Assert.True(screen.OfferSignInAgain);
    }

    [Fact]
    public void WithNoGameTheScreenSaysWhyAndOffersTheDownload()
    {
        var screen = EntryScreen.For(WithGame with { NoGameReason = EntryScreen.NoGame });

        Assert.Equal(EntryKind.GetGame, screen.Kind);
        Assert.Equal(EntryScreen.NoGame, screen.Status);
        Assert.Equal("Download the game", screen.PrimaryAction);
        Assert.Empty(screen.Rows);
        Assert.False(screen.OfferGameVersion);
    }

    [Fact]
    public void AStoppedDownloadCarriesOnAndCanStillBeManaged()
    {
        var screen = EntryScreen.For(WithGame with { NoGameReason = EntryScreen.NoGame, DownloadUnderWay = true });

        Assert.Equal(EntryScreen.DownloadUnderWay, screen.Status);
        Assert.Equal("Continue the download", screen.PrimaryAction);
        Assert.True(screen.OfferGameVersion);
    }

    [Fact]
    public void AnUnknownAccountOrVersionStillFillsItsRow()
    {
        var screen = EntryScreen.For(WithGame with { AccountName = null, InstalledVersion = null });

        Assert.Equal("On this iPhone", screen.Rows[0].Value);
        Assert.Equal("Signed in", screen.Rows[2].Value);
    }

    [Fact]
    public void AFinishedDownloadSaysWhatCameAndWhatHappensNext()
    {
        var check = new GameVersionCheck(GameVersionProblem.None, new STS2Mobile.Steam.Depot.BranchBuild(GameInstaller.BetaBranch, 1, 2), null);
        var notice = GameInstallMessages.Downloaded(check, "0.112.0", 1_200_000_000);

        Assert.True(notice.Succeeded);
        Assert.Equal("Download complete", notice.Title);
        Assert.Contains("Beta version (v0.112.0, untested, 1.2 GB)", notice.Text);
        Assert.Contains("prepares it", notice.Text);
        Assert.DoesNotContain("backup", notice.Text);
        Assert.Contains("copied to a backup first", GameInstallMessages.Downloaded(check, "0.112.0", 1, backup: "/saves").Text);
    }

    [Fact]
    public void TheVersionRowSaysTheBranchAndAnUpdateSeenOnTheVersionScreen()
    {
        Assert.Equal("v0.111.0 Beta", EntryScreen.For(WithGame with { InstalledBranch = "Beta" }).Rows[0].Value);
        Assert.Equal("Update available", EntryScreen.For(WithGame with { InstalledBranch = "Beta", UpdateAvailable = true }).Rows[0].Value);
    }

    [Fact]
    public void AStoppedDownloadIsAFailureThatSaysWhy()
    {
        var notice = GameInstallMessages.NotDownloaded(GameInstallMessages.Failure(new IOException("full", 28)));

        Assert.False(notice.Succeeded);
        Assert.Equal("Not downloaded", notice.Title);
        Assert.Contains("ran out of space", notice.Text);
    }

    [Fact]
    public void AGameThatDidNotStartDoesNotBlameTheSignIn()
    {
        string text = SignInFailure.DescribeGameStart(new InvalidOperationException("test"));

        Assert.DoesNotContain("Sign-in", text);
        Assert.Contains("could not start", text);
        Assert.Contains("Play", text);
    }

    [Fact]
    public void DeletingTheGameIsASuccessThatKeepsTheSaves()
    {
        Assert.True(GameInstallMessages.DeletedNotice.Succeeded);
        Assert.Contains(GameInstallMessages.Kept, GameInstallMessages.DeletedNotice.Text);
    }
}
