using Godot;
using STS2Mobile.GamePatcher;
using STS2Mobile.Launcher;
using STS2Mobile.Platform;
using STS2Mobile.Steam.Depot;

namespace STS2Mobile.FullGame;

/// <summary>
/// <c>STS2_SCREEN=&lt;name&gt;</c> opens one launcher screen with made-up content and does nothing else, for a screenshot of it with nobody
/// there. Nothing goes to Steam and no file is touched. Names: start, home, failed, version, version-update, version-stable, version-offline,
/// switch, notprepared, cloud, account, account-signout, download, progress, prepare, choice, delete, credentials, guard, qr, reopen, downloaded,
/// notdownloaded, cloudgot, deleted, and for the cloud screen cloudget, cloudsend, cloudsenddry, cloudsent, cloudnotsent, cloudnothing.
/// </summary>
public static class LauncherPreview
{
    private static readonly LauncherFacts Facts = new(SignedIn: true, AccountName: "player_one", NoGameReason: null, DownloadUnderWay: false, InstalledVersion: "0.113.0",
        InstalledBranch: "Beta");
    private static readonly BranchBuild BetaNow = new(GameInstaller.BetaBranch, 26625880, GameProfile.Current.Steam!.Manifest);
    private static readonly BranchBuild StableNow = new(GameInstaller.NormalBranch, 23811903, 8653035385353091849);
    private static readonly InstalledBuild Beta = new(GameInstaller.BetaBranch, BetaNow.ManifestId, BetaNow.BuildId, "0.113.0", Tested: true);
    private static readonly InstalledBuild Stable = new(GameInstaller.NormalBranch, StableNow.ManifestId, StableNow.BuildId, "0.103.2", Tested: false,
        [new PatchOutcome("No mouse cursor", false, false, "MegaCrit.Sts2.Core.Nodes.CommonUi.NCursorManager.UpdateCursor is not in this game version."),
         new PatchOutcome("No LAN play", true, true)]);

    // A run finished on this iPhone after the last play on a computer: newer progress here, a newer setting there, and the computer's
    // unfinished run that the finished one replaces.
    private static readonly CloudSaveComparison PreviewComparison = CloudSaveComparison.Compare(
        [
            Save("profile.save", "a", 2026, 10, 30), Save("profile1/saves/progress.save", "phone", 2026, 11, 1), Save("profile1/saves/prefs.save", "phone", 2026, 10, 20),
            Save("profile1/saves/history/1793500000.run", "run", 2026, 11, 1), Save("profile1/saves/history/1793400000.run", "run2", 2026, 10, 31),
        ],
        [
            Save("profile.save", "a", 2026, 10, 30), Save("profile1/saves/progress.save", "cloud", 2026, 10, 30), Save("profile1/saves/prefs.save", "cloud", 2026, 10, 30),
            Save("profile1/saves/current_run.save", "run", 2026, 10, 30), Save("profile2/saves/progress.save", "p2", 2026, 9, 1),
        ]);

    private static SaveFileState Save(string name, string sha1, int year, int month, int day) =>
        new(name, sha1, 100, new System.DateTimeOffset(year, month, day, 21, 30, 0, System.TimeSpan.Zero));

    public static void Show(LauncherView view, string name)
    {
        GD.Print("Screen preview: ", name);
        switch (name)
        {
            case "start":
                view.Step = SetupStep.SignIn;
                view.ShowEntry(EntryScreen.For(Facts with { SignedIn = false, NoGameReason = EntryScreen.NoGame }));
                break;
            case "home":
                view.ShowEntry(EntryScreen.For(Facts));
                break;
            case "home-mods":
                view.ShowEntry(EntryScreen.For(Facts with { ModsEnabled = true }));
                break;
            case "mods-empty":
                view.ShowMods([], false);
                break;
            case "mods":
                view.ShowMods([new("QuickRestart", "Quick Restart", "Prepared for this launch"),
                    new("ExamplePack", "Example resource pack", "Prepared for this launch")], true);
                break;
            case "mods-refused":
                view.ShowMods([new("QuickRestart", "Quick Restart", "The manifest could not be read. Import its zip again.", true)], true,
                    "Quick Restart's manifest could not be read.", canGoBack: false);
                break;
            case "mods-code":
                view.ShowChoice(ModPackageImport.CodeWarning("QuickRestart"), "Add code mod", "Cancel", canGoBack: true, heading: "Mods");
                break;
            case "mods-delete":
                view.ShowChoice("Delete QuickRestart from this iPhone? Its imported files will be removed. Your game and saves are kept.",
                    "Delete mod", "Cancel", canGoBack: true, heading: "Mods");
                break;
            case "failed":
                view.ShowEntry(EntryScreen.For(Facts, "Steam did not answer in time. Check the connection and press Play again.", failed: true));
                break;
            case "version":
                view.ShowVersion(VersionScreen.For(new VersionFacts(Beta, new BuildsOnSteam(StableNow, BetaNow))));
                break;
            case "version-update":
                view.ShowVersion(VersionScreen.For(new VersionFacts(Beta, new BuildsOnSteam(StableNow, BetaNow with { BuildId = 25100000, ManifestId = 1 }))));
                break;
            case "version-stable":
                view.ShowVersion(VersionScreen.For(new VersionFacts(Stable, new BuildsOnSteam(StableNow, BetaNow))));
                break;
            case "version-offline":
                view.ShowVersion(VersionScreen.For(new VersionFacts(Beta, null, "TimeoutException")));
                break;
            case "switch":
                view.Step = SetupStep.Download;
                view.ShowChoice(GameInstallMessages.Confirm(new GameVersionCheck(GameVersionProblem.None, StableNow, null, 4_760_000_000), 1_150_000_000, replacesGame: true, Beta),
                    "Download", null, canGoBack: true);
                break;
            case "notprepared":
                view.Step = SetupStep.Download;
                view.SetNotice(Notice.Failure("Not prepared", "Game version 0.103.2 has not been tested with this app, and it cannot be prepared for this iPhone: the change "
                    + "\"No LAN play\" does not fit it (Godot.ENetConnection is not in this game version.). The game cannot run without that change, so nothing was changed.\n"
                    + "Choose the other version under Game version, or wait for an app update that supports this build."));
                view.ShowEntry(EntryScreen.For(Facts with { NoGameReason = EntryScreen.NoGame }));
                break;
            case "cloud":
                view.ShowCloudSaves();
                break;
            case "account":
                view.ShowAccount("player_one");
                break;
            case "account-signout":
                view.ShowChoice(SignOut.Question, SignOut.ConfirmLabel, SignOut.CancelLabel, canGoBack: true, heading: SignOut.Heading);
                break;
            case "download":
                view.Step = SetupStep.Download;
                view.ShowEntry(EntryScreen.For(Facts with { NoGameReason = EntryScreen.NoGame }));
                break;
            case "progress":
                view.Step = SetupStep.Download;
                view.ShowProgress(GameInstallMessages.Progress(42, 504_000_000, 1_200_000_000), 0.42, canGoBack: true);
                break;
            case "prepare":
                view.Step = SetupStep.Prepare;
                view.ShowProgress("Converting the game's textures for this iPhone: 512 of 1566.\nThis happens once. If you leave this app, it carries on from here when you come back.", 512 / 1566.0);
                break;
            case "choice":
                view.Step = SetupStep.Download;
                view.ShowChoice(GameInstallMessages.ChooseVersion, "Normal version", "Beta version", canGoBack: true);
                break;
            case "delete":
                view.ShowChoice(GameInstallMessages.ConfirmDelete(2_000_000_000), "Delete the game", null, canGoBack: true);
                break;
            case "credentials":
                view.Step = SetupStep.SignIn;
                view.ShowCredentials();
                break;
            case "guard":
                view.Step = SetupStep.SignIn;
                view.ShowGuardCode("Enter the Steam Guard code shown in the Steam app.");
                break;
            case "qr":
                view.Step = SetupStep.SignIn;
                using (Image code = LauncherView.RenderChallenge("https://s.team/q/1/0000000000000000000"))
                    view.ShowQr(code, "Scan this code with the Steam app on another device and approve the sign-in.");
                break;
            case "reopen":
                view.Step = SetupStep.Reopen;
                view.ShowReopen("The game is ready.\nClose the app and open it again from the Home Screen, and the game starts.");
                break;
            case "downloaded":
                view.Step = SetupStep.Prepare;
                view.SetNotice(GameInstallMessages.Downloaded(new GameVersionCheck(GameVersionProblem.None, StableNow, null), "0.103.2", 1_150_000_000, backup: "save-backups"));
                view.ShowProgress("Patching the game's code for this iPhone.", -1);
                break;
            case "notdownloaded":
                view.Step = SetupStep.Download;
                view.SetNotice(GameInstallMessages.NotDownloaded(GameInstallMessages.Failure(new System.Net.Http.HttpRequestException("preview"))));
                view.ShowEntry(EntryScreen.For(Facts with { NoGameReason = EntryScreen.NoGame, DownloadUnderWay = true }));
                break;
            case "cloudgot":
                view.SetNotice(Notice.Success("Saves downloaded", "Your Steam Cloud saves are on this iPhone: 37 files. The saves it had before are kept in a backup."));
                view.ShowEntry(EntryScreen.For(Facts));
                break;
            case "cloudget":
                view.ShowCloudChoice(PreviewComparison, "Your Steam Cloud has saves for this account: 3 profiles, 251 finished runs, last played 2026-10-30.\n"
                    + "Download them and use them on this iPhone? The saves on this iPhone now are kept in a backup, not deleted.",
                    "Download my Steam Cloud saves", "Keep the saves on this iPhone");
                break;
            case "cloudsend":
                view.ShowCloudChoice(PreviewComparison, CloudSaveSendFlow.Question(PreviewComparison, dryRun: false), CloudSaveSendFlow.ConfirmLabel, CloudSaveSendFlow.DeclineLabel);
                break;
            case "cloudsenddry":
                view.ShowCloudChoice(PreviewComparison, CloudSaveSendFlow.Question(PreviewComparison, dryRun: true), CloudSaveSendFlow.ConfirmLabel, CloudSaveSendFlow.DeclineLabel);
                break;
            case "cloudsent":
                view.SetNotice(CloudSaveSendFlow.SentNotice(new Steam.Cloud.CloudSendResult(4, 0, 1), backedUp: 3));
                view.ShowEntry(EntryScreen.For(Facts));
                break;
            case "cloudnotsent":
                view.SetNotice(CloudSaveSendFlow.StoppedNotice(new Steam.Cloud.CloudSendResult(2, 0, 0), 4, 1, new System.Net.Http.HttpRequestException("preview"), backedUp: 3));
                view.ShowEntry(EntryScreen.For(Facts));
                break;
            case "cloudnothing":
                view.SetNotice(CloudSaveSendFlow.NothingToSendNotice(PreviewComparison with { ToSend = [], ToRemove = [] }));
                view.ShowEntry(EntryScreen.For(Facts));
                break;
            case "deleted":
                view.Step = SetupStep.Download;
                view.SetNotice(GameInstallMessages.DeletedNotice);
                view.ShowEntry(EntryScreen.For(Facts with { NoGameReason = EntryScreen.NoGame }));
                break;
            default:
                view.ShowFatal("No preview called " + name + ".");
                break;
        }
    }
}
