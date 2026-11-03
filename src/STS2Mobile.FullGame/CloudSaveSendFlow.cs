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
/// Sends the saves on this iPhone to the account's Steam Cloud when the player asks on the cloud saves screen. It compares
/// both sides, shows what would change and asks; only a yes on that screen sends anything. Before writing, it keeps Steam Cloud's copies of
/// every file the Send replaces or removes in a backup on this iPhone. It never removes run history. Every end is a notice.
/// </summary>
/// <remarks>
/// Switches for a run with nobody there: <c>STS2_AUTO_TAP</c> presses the buttons by their labels, and
/// <c>STS2_CLOUD_SEND_DRY_RUN=1</c> does everything but the upload calls (the comparison, the question, the cloud backup and reading the
/// files) and logs what it would send. A real Send whose confirmation an automatic tap would press also needs <c>STS2_CLOUD_SEND_REAL=1</c>,
/// so a test run cannot write to an account by accident.
/// </remarks>
public sealed class CloudSaveSendFlow
{
    public const string ConfirmLabel = "Send my saves to Steam Cloud";
    public const string DeclineLabel = "Keep Steam Cloud as it is";
    public const string DryRunLine = "Test mode: Send shows and logs what it would send, and sends nothing.";
    private const string Sent = "Saves sent";
    private const string NotSent = "Saves not sent";
    private const string Unchanged = "Nothing changed: Steam Cloud is as it was.";
    private readonly LauncherView _view;
    private readonly SteamLoginSession _login;
    private TaskCompletionSource<bool>? _choice;

    public CloudSaveSendFlow(LauncherView view, SteamLoginSession login)
    {
        _view = view;
        _login = login;
        _view.ChoiceMade += answer => _choice?.TrySetResult(answer);
    }

    /// <summary><c>STS2_CLOUD_SEND_DRY_RUN=1</c>: everything but the upload calls.</summary>
    public static bool DryRun => OS.GetEnvironment("STS2_CLOUD_SEND_DRY_RUN") == "1";

    /// <summary>Returns how it ended: a notice, or a sentence when the player kept Steam Cloud as it was.</summary>
    public async Task<(Notice? Result, string? Status)> SendAsync(CancellationToken attempt)
    {
        bool dryRun = DryRun;
        string account = _login.SteamId.ToString();
        var imports = new SaveImportStore(ProjectSettings.GlobalizePath("user://"));
        _view.ShowProgress("Comparing the saves on this iPhone with Steam Cloud...", -1, canGoBack: true);
        IReadOnlyList<SaveFileState> local;
        try { local = imports.ListSaves(account); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            GD.PushError("The saves on this iPhone could not be read: " + error.Message);
            return (Notice.Failure(NotSent, $"The saves on this iPhone could not be read ({error.GetType().Name}). Nothing was sent."), null);
        }
        if (local.Count == 0) return (Notice.Failure(NotSent, "This iPhone has no saves for this account yet, so there is nothing to send. Steam Cloud is as it was."), null);

        using var cloud = new CloudClient(_login.Connection, GodotSteamLogger.Instance);
        var downloader = new CloudSaveDownloader(cloud);
        List<CloudFileInfo> listed;
        try { listed = (await downloader.ListAsync(attempt)).Where(file => SaveImportStore.IsImportable(file.Name)).ToList(); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            GD.PushError("Steam Cloud saves could not be listed: " + error.GetType().Name + ": " + error.Message);
            return (Notice.Failure(NotSent, $"Steam Cloud's saves could not be listed ({error.GetType().Name}). Check the connection and try again. Nothing was sent."), null);
        }
        var comparison = CloudSaveComparison.Compare(local, listed.Select(ToState));
        if (comparison.NothingToSend)
        {
            GD.Print("Cloud send: nothing to send.");
            return (NothingToSendNotice(comparison), null);
        }

        if (!await AskAsync(comparison, Question(comparison, dryRun), attempt))
        {
            GD.Print("Cloud send declined; Steam Cloud is unchanged.");
            return (null, Unchanged);
        }

        // Steam Cloud's copies of what this replaces or removes, kept before anything is written.
        var replaced = new HashSet<string>(comparison.ToSend.Concat(comparison.ToRemove), StringComparer.Ordinal);
        var backedUp = listed.Where(file => replaced.Contains(file.Name)).ToList();
        if (backedUp.Count > 0)
        {
            _view.ShowWorking($"Keeping Steam Cloud's copies of the {backedUp.Count} files this changes: 0 of {backedUp.Count}.");
            var progress = new Progress<int>(done => _view.ShowWorking($"Keeping Steam Cloud's copies of the {backedUp.Count} files this changes: {done} of {backedUp.Count}."));
            try
            {
                var copies = await downloader.DownloadAsync(backedUp, progress, attempt);
                string backup = imports.BackUpCloud(account, copies.Select(file => new StagedSave(file.Name, file.Content, file.Sha1Hex, file.Timestamp.ToUnixTimeSeconds())).ToList(), DateTime.UtcNow);
                GD.Print("Cloud send: Steam Cloud's copies of ", copies.Count, " files kept in ", Path.GetFileName(backup));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                GD.PushError("Steam Cloud's copies could not be kept before a Send: " + error.GetType().Name + ": " + error.Message);
                return (Notice.Failure(NotSent, $"Steam Cloud's copies of the files this changes could not be kept on this iPhone first ({error.GetType().Name}), so nothing was sent. Check the connection and space, and try again."), null);
            }
        }

        // The files as compared: one that changed since, for example by the game, stops the Send before anything is written.
        var states = local.ToDictionary(file => file.Name, StringComparer.Ordinal);
        var uploads = new List<CloudUpload>();
        try
        {
            foreach (string name in comparison.ToSend)
            {
                byte[] content = imports.ReadSave(account, name);
                if (!Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(content)).Equals(states[name].Sha1Hex, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A save changed after it was compared: " + name);
                uploads.Add(new CloudUpload(name, content, states[name].Time));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            GD.PushError("Cloud send stopped before writing: " + error.Message);
            return (Notice.Failure(NotSent, "The saves on this iPhone changed or could not be read while sending, so nothing was sent. Try again."), null);
        }

        if (dryRun)
        {
            foreach (var file in uploads) GD.Print($"Cloud send (dry run): would write {file.Name} ({file.Content.Length} bytes, changed {file.Timestamp:yyyy-MM-dd HH:mm:ss}Z)");
            foreach (string name in comparison.ToRemove) GD.Print($"Cloud send (dry run): would remove {name}");
            return (Notice.Success("Dry run: nothing sent",
                $"A Send would write {uploads.Count} {Files(uploads.Count)} to Steam Cloud and remove {comparison.ToRemove.Count}. Nothing was sent."
                + (backedUp.Count > 0 ? $" Steam Cloud's copies of {backedUp.Count} {Files(backedUp.Count)} are kept in a backup on this iPhone." : "")), null);
        }
        if (ConfirmedByAutoTap && OS.GetEnvironment("STS2_CLOUD_SEND_REAL") != "1")
        {
            GD.PushError("Cloud send refused: the confirmation is pressed by STS2_AUTO_TAP and STS2_CLOUD_SEND_REAL is not set.");
            return (Notice.Failure(NotSent, "A test run pressed the confirmation, and a real Send needs a person or STS2_CLOUD_SEND_REAL=1. Nothing was sent."), null);
        }

        int total = uploads.Count + comparison.ToRemove.Count;
        _view.ShowWorking($"Sending your saves to Steam Cloud: 0 of {total}.");
        var sending = new Progress<int>(done => _view.ShowWorking($"Sending your saves to Steam Cloud: {done} of {total}."));
        try
        {
            var result = await new CloudSaveUploader(cloud).SendAsync(uploads, comparison.ToRemove, sending, attempt);
            GD.Print($"Cloud send finished: {result.Written} written, {result.AlreadyThere} already there, {result.Removed} removed.");
            return (SentNotice(result, backedUp.Count), null);
        }
        catch (CloudSendException error)
        {
            Exception cause = error.InnerException ?? error;
            GD.PushError("Cloud send stopped: " + cause.GetType().Name + ": " + cause.Message);
            return (StoppedNotice(error.Done, uploads.Count, comparison.ToRemove.Count, cause, backedUp.Count), null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            GD.PushError("Cloud send failed before writing: " + error.GetType().Name + ": " + error.Message);
            return (Notice.Failure(NotSent, $"Steam did not start the Send ({error.GetType().Name}). Check the connection and try again. Nothing was sent."), null);
        }
    }

    /// <summary>What the player is asked before a Send, under the table of both sides.</summary>
    public static string Question(CloudSaveComparison comparison, bool dryRun)
    {
        var lines = new List<string>();
        string write = $"This writes {comparison.ToSend.Count} {Files(comparison.ToSend.Count)} from this iPhone to Steam Cloud";
        if (comparison.ToRemove.Count > 0)
            write += $", and removes {comparison.ToRemove.Count} that this iPhone no longer has ({Names(comparison.ToRemove)})";
        lines.Add(write + ".");
        if (comparison.NewerInCloud > 0)
            lines.Add($"Steam Cloud has a newer copy of {comparison.NewerInCloud} of them, for example from playing on a computer. Sending replaces it.");
        lines.Add("Steam Cloud's copies of what this changes are kept on this iPhone first. Run history is never removed.");
        if (dryRun) lines.Add(DryRunLine);
        return string.Join("\n", lines);
    }

    public static Notice NothingToSendNotice(CloudSaveComparison comparison) => Notice.Success("Nothing to send",
        "Steam Cloud already has every save on this iPhone, so nothing was sent."
        + (comparison.KeptInCloud > 0 ? $" It also has {comparison.KeptInCloud} {Files(comparison.KeptInCloud)} this iPhone does not, which stay there." : ""));

    public static Notice SentNotice(CloudSendResult result, int backedUp) => Notice.Success(Sent,
        $"Steam Cloud has the saves on this iPhone: {result.Written} {Files(result.Written)} written"
        + (result.AlreadyThere > 0 ? $", {result.AlreadyThere} already there" : "")
        + (result.Removed > 0 ? $", {result.Removed} removed" : "") + "."
        + (backedUp > 0 ? " Its copies from before are kept in a backup on this iPhone." : ""));

    public static Notice StoppedNotice(CloudSendResult done, int uploads, int removals, Exception cause, int backedUp)
    {
        int written = done.Written + done.AlreadyThere;
        string how = written == 0 && done.Removed == 0
            ? $"The Send stopped before any file got through ({cause.GetType().Name})."
            : $"The Send stopped ({cause.GetType().Name}) after {written} of {uploads} {Files(uploads)} were written"
              + (removals > 0 ? $" and {done.Removed} of {removals} removed" : "") + ". Steam Cloud has those.";
        return Notice.Failure(NotSent, how + " Check the connection and send again to finish."
            + (backedUp > 0 ? " Steam Cloud's copies from before are kept in a backup on this iPhone." : ""));
    }

    private static SaveFileState ToState(CloudFileInfo file) => new(file.Name, file.Sha1Hex, file.Size, file.Timestamp);

    private static string Files(int count) => count == 1 ? "file" : "files";

    /// <summary>Up to three names, so the player sees what goes; more are counted.</summary>
    private static string Names(IReadOnlyList<string> names) =>
        string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" and {names.Count - 3} more" : "");

    /// <summary>A run with nobody there would press the confirmation itself.</summary>
    private static bool ConfirmedByAutoTap =>
        (OS.GetEnvironment("STS2_AUTO_TAP") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).Contains(ConfirmLabel);

    private async Task<bool> AskAsync(CloudSaveComparison comparison, string question, CancellationToken attempt)
    {
        _choice = new TaskCompletionSource<bool>();
        _view.ShowCloudChoice(comparison, question, ConfirmLabel, DeclineLabel);
        try { return await _choice.Task.WaitAsync(attempt); }
        finally { _choice = null; }
    }
}
