using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Launcher;
using STS2Mobile.Platform;
using STS2Mobile.Steam.Cloud;

namespace STS2Mobile.FullGame;

/// <summary>
/// Offers the signed-in player's Steam Cloud saves on the launcher view before the game starts, when this iPhone has none of its own, and
/// gets them when the player asks on the cloud saves screen, so the game only ever reads one consistent set of saves. Sending is
/// <see cref="CloudSaveSendFlow"/>'s. Any failure leaves the saves on this iPhone as they were.
/// </summary>
public sealed class CloudSaveImportFlow
{
    private readonly LauncherView _view;
    private readonly SteamLoginSession _login;
    private TaskCompletionSource<bool>? _choice;

    public CloudSaveImportFlow(LauncherView view, SteamLoginSession login)
    {
        _view = view;
        _login = login;
        _view.ChoiceMade += answer => _choice?.TrySetResult(answer);
    }

    /// <summary>
    /// If verified saves were copied to this iPhone for this account, asks whether to use them;
    /// otherwise offers the account's Steam Cloud saves, only when this iPhone has no saves of the account's yet. With
    /// saves here, Get and Send are on the cloud saves screen, so Play never asks about them.
    /// </summary>
    public async Task OfferAsync(CancellationToken attempt)
    {
        var imports = new SaveImportStore(ProjectSettings.GlobalizePath("user://"));
        PendingSaveImport? pending;
        try { pending = imports.FindPending(_login.SteamId.ToString()); }
        catch (Exception error) when (error is InvalidDataException or IOException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            GD.PushError("Staged save import rejected: " + error.Message);
            await AskAsync("Saves were copied to this iPhone but could not be verified, so they will not be used.\nThe saves already on this iPhone are unchanged.",
                "Continue", null, attempt);
            return;
        }
        if (pending == null)
        {
            bool hasSaves;
            try { hasSaves = imports.HasSaves(_login.SteamId.ToString()); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                GD.PushError("Could not tell whether this iPhone has saves, so Steam Cloud's are not offered: " + error.Message);
                return;
            }
            if (hasSaves) GD.Print("Steam Cloud saves not offered: this iPhone has saves. Get and Send are on the cloud saves screen.");
            else await DownloadAsync(imports, onRequest: false, attempt);
            return;
        }
        string found = $"Steam Cloud saves are ready on this iPhone: {pending.Profiles} profiles, {pending.FinishedRuns} finished runs"
            + (pending.LastChangedUtc is { } changed ? $", last played {changed:yyyy-MM-dd}." : ".")
            + "\nUsing them replaces the saves on this iPhone. Those are kept in a backup, not deleted.";
        if (await AskAsync(found, "Use my Steam Cloud saves", "Keep the saves on this iPhone", attempt))
        {
            string backup = imports.Apply(pending, DateTime.UtcNow);
            GD.Print("Save import applied: ", pending.Files, " files; previous saves moved to ", Path.GetFileName(backup));
        }
        else
        {
            imports.Decline(pending);
            GD.Print("Save import declined; the saves on this iPhone are unchanged.");
        }
    }

    /// <summary>
    /// The player asked for the Steam Cloud's saves. Returns how it ended, as a notice when saves were or were not downloaded, otherwise
    /// as a sentence (nothing there, or the player kept the saves here).
    /// </summary>
    public async Task<(Notice? Result, string? Status)> GetAsync(CancellationToken attempt)
    {
        _view.ShowProgress("Asking Steam Cloud which saves it has...", -1, canGoBack: true);
        var imports = new SaveImportStore(ProjectSettings.GlobalizePath("user://"));
        return await DownloadAsync(imports, onRequest: true, attempt);
    }

    private const string Downloaded = "Saves downloaded";
    private const string NotDownloaded = "Saves not downloaded";

    private const string Unchanged = "Nothing changed: the saves on this iPhone are as they were.";

    /// <summary>
    /// Nothing to download, but with mods on the game reads its own copy of the saves (<c>modded/</c>), which it fills only on the first modded
    /// launch: a Get makes that copy the same as Steam Cloud's too.
    /// </summary>
    private static Notice InSyncResult(SaveImportStore imports, string account)
    {
        const string Same = "The saves on this iPhone are the same as Steam Cloud's, so nothing was downloaded.";
        try
        {
            if (imports.ModdedMatchesSynced(account)) return Notice.Success("Nothing to download", Same);
            string? backup = imports.CopySyncedToModded(account, DateTime.UtcNow);
            GD.Print("Steam Cloud saves already here; copied to the saves used with mods on", backup != null ? "; previous ones kept in " + Path.GetFileName(backup) : "", ".");
            return Notice.Success("Saves for mods updated", Same + " The saves the game uses with mods on are now the same too; the ones they replace are kept in a backup.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            GD.PushError("The saves could not be copied to the saves used with mods on: " + error.Message);
            return Notice.Failure("Saves for mods not updated", Same + " The saves the game uses with mods on could not be updated (" + error.GetType().Name + ").");
        }
    }

    /// <summary>
    /// Lists the cloud's saves, asks, then downloads and applies them. Offered before play, it asks once for each state of the cloud: after an
    /// answer it asks again only when the cloud has changed, for example after play on a computer. Asked for, it always asks. Returns what to
    /// tell the player when they asked, or null.
    /// </summary>
    private async Task<(Notice? Result, string? Status)> DownloadAsync(SaveImportStore imports, bool onRequest, CancellationToken attempt)
    {
        string account = _login.SteamId.ToString();
        using var cloud = new CloudClient(_login.Connection, GodotSteamLogger.Instance);
        var downloader = new CloudSaveDownloader(cloud);
        List<CloudFileInfo> saves;
        try { saves = (await downloader.ListAsync(attempt)).Where(file => SaveImportStore.IsImportable(file.Name)).ToList(); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            GD.PushError("Steam Cloud saves could not be listed: " + error.GetType().Name + ": " + error.Message);
            return onRequest ? (Notice.Failure(NotDownloaded, $"Steam Cloud's saves could not be listed ({error.GetType().Name}). Check the connection and try again. {Unchanged}"), null) : default;
        }
        if (saves.Count == 0) return onRequest ? (null, "Steam Cloud has no saves for this account yet. " + Unchanged) : default;
        string fingerprint = SaveImportStore.Fingerprint(saves.Select(file => (file.Name, file.Sha1Hex)));
        if (!onRequest && imports.WasOffered(account, fingerprint)) return default;
        var (profiles, finishedRuns) = SaveImportStore.Describe(saves.Select(file => file.Name));
        string question = $"Your Steam Cloud has saves for this account: {profiles} profiles, {finishedRuns} finished runs, "
            + $"last played {saves.Max(file => file.Timestamp).UtcDateTime:yyyy-MM-dd}.\n"
            + "Download them and use them on this iPhone? The saves on this iPhone now are kept in a backup, not deleted.";
        // Asked for, the question stands under the table of both sides, as Send's does.
        CloudSaveComparison? comparison = null;
        if (onRequest)
        {
            try { comparison = CloudSaveComparison.Compare(imports.ListSaves(account), saves.Select(file => new SaveFileState(file.Name, file.Sha1Hex, file.Size, file.Timestamp))); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                GD.PushError("The saves could not be compared, so the question is asked without the table: " + error.Message);
            }
            if (comparison is { InSync: true })
                return (InSyncResult(imports, account), null);
            if (comparison is { NewerHere: > 0 } or { OnlyHere: > 0 })
                question += $"\nThis iPhone has {comparison.NewerHere + comparison.OnlyHere} files that are newer or not on Steam Cloud. Send them first to keep them there.";
        }
        if (!await AskAsync(question, "Download my Steam Cloud saves", "Keep the saves on this iPhone", attempt, onRequest ? "Cloud saves" : null, comparison))
        {
            imports.RememberOffer(account, fingerprint);
            GD.Print("Steam Cloud saves declined; the saves on this iPhone are unchanged.");
            return (null, Unchanged);
        }
        _view.ShowWorking($"Downloading your Steam Cloud saves: 0 of {saves.Count} files.");
        var progress = new Progress<int>(done => _view.ShowWorking($"Downloading your Steam Cloud saves: {done} of {saves.Count} files."));
        string? staged = null;
        bool applied = false;
        try
        {
            var downloaded = await downloader.DownloadAsync(saves, progress, attempt);
            staged = imports.Stage(account, downloaded.Select(file => new StagedSave(file.Name, file.Content, file.Sha1Hex, file.Timestamp.ToUnixTimeSeconds())).ToList(),
                "Steam Cloud download on this iPhone", DateTime.UtcNow);
            PendingSaveImport pending = imports.FindPending(account) is { } found && found.Directory == staged
                ? found : throw new InvalidDataException("The downloaded saves were not found after staging.");
            string backup = imports.Apply(pending, DateTime.UtcNow);
            applied = true;
            GD.Print("Steam Cloud saves downloaded and applied: ", pending.Files, " files; previous saves moved to ", Path.GetFileName(backup));
            imports.RememberOffer(account, fingerprint);
            return (Notice.Success(Downloaded, $"Your Steam Cloud saves are on this iPhone: {pending.Files} files. The saves it had before are kept in a backup."), null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (applied)
            {
                // The saves are in place; only remembering the answer failed, so the offer comes back once.
                GD.PushError("Steam Cloud saves applied, but the answer could not be remembered: " + error.Message);
                return (Notice.Success(Downloaded, "Your Steam Cloud saves are on this iPhone. The saves it had before are kept in a backup."), null);
            }
            GD.PushError("Steam Cloud saves could not be downloaded: " + error.GetType().Name + ": " + error.Message);
            try { if (staged != null && Directory.Exists(staged)) Directory.Delete(staged, recursive: true); }
            catch (IOException cleanup) { GD.PushError("The unused download could not be removed: " + cleanup.Message); }
            if (onRequest) return (Notice.Failure(NotDownloaded, $"Your Steam Cloud saves could not be downloaded ({error.GetType().Name}). Check the connection and try again. {Unchanged}"), null);
            await AskAsync("Your Steam Cloud saves could not be downloaded, so the saves on this iPhone are unchanged.\nThe game starts with them; it offers the download again next time.",
                "Continue", null, attempt);
            return default;
        }
    }

    private async Task<bool> AskAsync(string question, string first, string? second, CancellationToken attempt, string? heading = null,
        CloudSaveComparison? comparison = null)
    {
        _choice = new TaskCompletionSource<bool>();
        if (comparison != null && second != null) _view.ShowCloudChoice(comparison, question, first, second);
        else _view.ShowChoice(question, first, second, heading: heading);
        try { return await _choice.Task.WaitAsync(attempt); }
        finally { _choice = null; }
    }
}
