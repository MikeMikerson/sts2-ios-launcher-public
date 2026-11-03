using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.GamePatcher;
using STS2Mobile.Launcher;
using STS2Mobile.Platform;
using STS2Mobile.Steam;
using STS2Mobile.Steam.Depot;

namespace STS2Mobile.FullGame;

/// <summary>
/// The player's side of getting the game: choosing a version, being told why one cannot be used, confirming, and watching the download.
/// Nothing here starts on its own. Any build Steam serves on the chosen branch can be downloaded. The rules are in
/// <see cref="GameInstaller"/>, the words in <see cref="GameInstallMessages"/>.
/// </summary>
public sealed class GameInstallFlow
{
    private const long Megabyte = 1024 * 1024;
    private readonly LauncherView _view;
    private readonly SteamLoginSession _login;
    private readonly GameInstaller _installer;
    private readonly string _gameFolder;
    private TaskCompletionSource<bool>? _choice;

    public GameInstallFlow(LauncherView view, SteamLoginSession login, GameInstaller installer, string gameFolder)
    {
        _view = view;
        _login = login;
        _installer = installer;
        _gameFolder = gameFolder;
        _view.ChoiceMade += answer => _choice?.TrySetResult(answer);
    }

    /// <summary>Steam's current build of each branch. It downloads nothing and changes nothing.</summary>
    public async Task<BuildsOnSteam> ReadBuildsAsync(CancellationToken attempt)
    {
        var builds = await SteamBuilds.ReadAsync(_login.Connection, SteamApp.Id, GameLayout.Depot, [GameInstaller.NormalBranch, GameInstaller.BetaBranch], attempt);
        var now = new BuildsOnSteam(builds[GameInstaller.NormalBranch], builds[GameInstaller.BetaBranch]);
        GD.Print($"Steam's builds: Stable {Describe(now.Normal)}, Beta {Describe(now.Beta)}.");
        return now;
    }

    /// <summary>Asks which version to download and downloads it. Returns what went wrong to tell the player, or null when the game's files are all here.</summary>
    /// <exception cref="OperationCanceledException">The player went back, with nothing deleted unless the download had started.</exception>
    public async Task<string?> ChooseAndDownloadAsync(CancellationToken attempt)
    {
        try
        {
            _view.ShowProgress("Asking Steam which versions it has...", -1, canGoBack: true);
            var builds = await ReadBuildsAsync(attempt);
            var normal = _installer.Check(builds.Normal);
            var beta = _installer.Check(builds.Beta);
            bool normalUsable = normal.Problem == GameVersionProblem.None, betaUsable = beta.Problem == GameVersionProblem.None;
            if (!normalUsable && !betaUsable) return GameInstallMessages.NeitherUsable(normal, beta);
            // A game still here (one that does not run, say) is replaced: a switch backs the saves up first.
            var installed = _installer.Pending == null ? InstalledBuild.Read(_gameFolder) : null;
            // Only a real choice is asked. With one version to offer, the question is whether to download it.
            if (!normalUsable) return await ConfirmAndDownloadAsync(beta, installed, normal, "Stable", attempt);
            if (!betaUsable) return await ConfirmAndDownloadAsync(normal, installed, beta, "Beta", attempt);
            bool chooseNormal = await AskAsync(GameInstallMessages.ChooseVersion, "Normal version", "Beta version", attempt);
            return await ConfirmAndDownloadAsync(chooseNormal ? normal : beta, installed, null, null, attempt);
        }
        catch (Exception error) when (error is not OperationCanceledException || !attempt.IsCancellationRequested)
        {
            return Failure(error);
        }
    }

    /// <summary>
    /// Replaces the game on this iPhone with Steam's current build of <paramref name="branch"/>: an update when it is the installed branch, a
    /// switch otherwise, with the saves backed up first. Returns as <see cref="ChooseAndDownloadAsync"/> does.
    /// </summary>
    public async Task<string?> ReplaceAsync(string branch, InstalledBuild? installed, CancellationToken attempt)
    {
        try
        {
            _view.ShowProgress("Asking Steam for its current build...", -1, canGoBack: true);
            var builds = await ReadBuildsAsync(attempt);
            var check = _installer.Check(builds.For(branch));
            if (check.Problem != GameVersionProblem.None) return GameInstallMessages.Problem(check, GameInstallMessages.Name(branch));
            return await ConfirmAndDownloadAsync(check, installed, null, null, attempt);
        }
        catch (Exception error) when (error is not OperationCanceledException || !attempt.IsCancellationRequested)
        {
            return Failure(error);
        }
    }

    /// <summary>Confirms, then deletes the game and everything of a download under way. The player's saves and sign-in stay.</summary>
    public async Task DeleteAsync(CancellationToken attempt)
    {
        long? bytes = _installer.Pending != null ? null : await Task.Run(() => _installer.GameBytes, attempt);
        await AskAsync(GameInstallMessages.ConfirmDelete(bytes), "Delete the game", null, attempt, heading: "Delete the game");
        _view.ShowProgress("Deleting the game...", -1);
        await Task.Run(_installer.Delete, CancellationToken.None);
    }

    /// <summary>Carries on with the download the player started earlier. Returns as <see cref="ChooseAndDownloadAsync"/> does.</summary>
    public async Task<string?> ContinueAsync(PendingGameDownload pending, CancellationToken attempt)
    {
        try
        {
            var check = _installer.CheckPending(pending);
            using var depot = await OpenAsync(check, attempt);
            var plan = _installer.Plan(check.Profile, depot.Manifest);
            check = CheckSpace(check, plan, "chosen");
            if (check.Problem != GameVersionProblem.None) return GameInstallMessages.Problem(check, "chosen");
            return await DownloadAsync(check, depot, plan, null, attempt);
        }
        catch (Exception error) when (error is not OperationCanceledException || !attempt.IsCancellationRequested)
        {
            return Failure(error);
        }
    }

    /// <param name="installed">The game this replaces, if any: a switch from another branch backs the saves up first.</param>
    private async Task<string?> ConfirmAndDownloadAsync(GameVersionCheck check, InstalledBuild? installed, GameVersionCheck? other, string? otherName, CancellationToken attempt)
    {
        string branch = check.Build!.Branch, name = GameInstallMessages.Name(branch);
        using var depot = await OpenAsync(check, attempt);
        var plan = _installer.Plan(check.Profile, depot.Manifest);
        check = CheckSpace(check, plan, name);
        // Before anything is deleted.
        if (check.Problem != GameVersionProblem.None) return GameInstallMessages.Problem(check, name);
        long download = GameInstaller.DownloadBytes(plan);
        await AskAsync(GameInstallMessages.Confirm(check, download, _installer.HasGameFiles, installed, other, otherName), "Download", null, attempt);
        string? backup = null;
        if (GameInstallMessages.IsSwitch(installed, branch))
        {
            _view.ShowProgress("Copying your saves to a backup...", -1);
            try { backup = await Task.Run(() => SaveBackup.Copy(ProjectSettings.GlobalizePath("user://"), branch, DateTime.UtcNow), attempt); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                GD.PushError("The saves could not be backed up: " + error);
                return GameInstallMessages.BackupFailed(error);
            }
            GD.Print($"Switch from {installed!.Branch} to {branch}: saves backed up to {(backup == null ? "nothing (no saves)" : Path.GetFileName(backup))}.");
        }
        return await DownloadAsync(check, depot, plan, backup, attempt);
    }

    private GameVersionCheck CheckSpace(GameVersionCheck check, IReadOnlyList<DepotFile> plan, string name)
    {
        check = _installer.CheckSpace(check, plan);
        GD.Print($"Version {name}: {(check.Tested ? "tested" : "untested")}, {check.Problem}, needs {check.RequiredBytes / Megabyte} MB, {check.AvailableBytes / Megabyte} MB available counting the current game.");
        return check;
    }

    private async Task<SteamDepot> OpenAsync(GameVersionCheck check, CancellationToken attempt)
    {
        _view.ShowProgress("Asking Steam for the game's file list...", -1, canGoBack: true);
        return await SteamDepot.OpenAsync(_login.Connection, SteamApp.Id, GameLayout.Depot, check.Build!.ManifestId, check.Build.Branch, attempt);
    }

    private async Task<string?> DownloadAsync(GameVersionCheck check, SteamDepot depot, IReadOnlyList<DepotFile> plan, string? backup, CancellationToken attempt)
    {
        // The figure the player was asked about: what comes over the network, not what it unpacks to.
        long wire = GameInstaller.DownloadBytes(plan);
        _view.ShowProgress("Getting ready to download...", -1);
        await Task.Run(() => _installer.Begin(check.Build!.Branch, check.Build.ManifestId, check.Build.BuildId), attempt);
        var clock = Stopwatch.StartNew();
        var (before, _) = MemoryFootprint.Read();
        int reported = -1;
        var result = await _installer.DownloadAsync(plan, depot, (done, total) =>
        {
            int percent = (int)(done * 100 / Math.Max(total, 1));
            if (Interlocked.Exchange(ref reported, percent) == percent) return;
            Callable.From(() => { if (!attempt.IsCancellationRequested) _view.ShowProgress(GameInstallMessages.Progress(percent, wire * percent / 100, wire), percent / 100.0, canGoBack: true); }).CallDeferred();
            if (percent % 10 == 0)
                GD.Print($"Download: {percent}% after {clock.ElapsedMilliseconds} ms, at {DateTime.Now:HH:mm:ss}. Footprint {MemoryFootprint.Read().Current / Megabyte} MB, thermal state {DevicePower.ThermalState()}.");
        }, attempt);
        var (after, peak) = MemoryFootprint.Read();
        var installed = InstalledBuild.Read(_gameFolder);
        _view.SetNotice(GameInstallMessages.Downloaded(check, installed?.Version ?? check.Profile?.Version ?? "unknown", wire, backup));
        GD.Print($"Download: finished in {clock.ElapsedMilliseconds} ms. {result.Bytes / Megabyte} MB in {result.DownloadedChunks} chunks fetched, {result.ReusedChunks} chunks and {result.ReusedFiles} files from an earlier run. "
            + $"Footprint {before / Megabyte} MB before, {after / Megabyte} MB after, peak {peak / Megabyte} MB. Installed: {(installed == null ? "-" : GameInstallMessages.Describe(installed))}.");
        return null;
    }

    private async Task<bool> AskAsync(string question, string first, string? second, CancellationToken attempt, string? heading = null)
    {
        _choice = new TaskCompletionSource<bool>();
        _view.ShowChoice(question, first, second, canGoBack: second != null || first != "Back", heading);
        try { return await _choice.Task.WaitAsync(attempt); }
        finally { _choice = null; }
    }

    private static string Failure(Exception error)
    {
        GD.PushError("The game download stopped: " + error);
        return GameInstallMessages.Failure(error);
    }

    private static string Describe(BranchBuild? build) => build == null ? "none" : $"build {build.BuildId} manifest {build.ManifestId}";
}
