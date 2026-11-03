using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Launcher;
using STS2Mobile.Platform;

namespace STS2Mobile.FullGame;

public partial class Launcher
{
    private bool _modPicking;
    private bool _modInstalling;
    private bool _modsPrepared;
    private string? _modRefusal;
    private Action<bool>? _modAnswer;
    private TaskCompletionSource<bool>? _modQuestion;
    private static string ModInputs => ProjectSettings.GlobalizePath("user://" + ModPackageImport.FolderName);

    private void WireMods()
    {
        _view.ModsRequested += ShowMods;
        _view.ModImportRequested += PickMod;
        _view.ModDeleteRequested += id => DeleteModAsync(id).FireAndForget("Delete mod");
    }

    private void ShowMods()
    {
        _view.Step = null;
        try
        {
            ModPackageImport.Recover(ModInputs);
            _view.ShowMods(ModLibrary.Read(ModInputs, ModLaunchSettings.Enabled, _modsPrepared), ModLaunchSettings.Enabled,
                _modRefusal, canGoBack: _modRefusal == null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _view.SetNotice(Notice.Failure("Mods unavailable", "The imported files could not be read: " + error.Message));
            _view.ShowMods([], ModLaunchSettings.Enabled, _modRefusal, canGoBack: _modRefusal == null);
        }
    }

    private void PickMod()
    {
        if (_modPicking || _modInstalling) return;
        _view.SetNotice(null);
        if (!FilesPicker.Open())
        {
            _view.SetNotice(Notice.Failure("Import not started", "The Files picker could not be opened. Try again."));
            return;
        }
        _modPicking = true;
    }

    public override void _Process(double delta)
    {
        if (!_modPicking) return;
        var result = FilesPicker.Poll(out string? path);
        if (result == FilesPickerResult.Nothing) return;
        _modPicking = false;
        if (result == FilesPickerResult.Picked) ImportModAsync(path!).FireAndForget("Import mod");
        else
        {
            if (result == FilesPickerResult.Failed)
                _view.SetNotice(Notice.Failure("Mod not imported", "The chosen file could not be read. Try choosing it again."));
            ShowMods();
        }
    }

    private async Task ImportModAsync(string archive)
    {
        _modInstalling = true;
        try
        {
            string inputs = ModInputs;
            _view.ShowWorking("Checking the mod's zip...");
            if (await PickedModImport.InstallAsync(archive, inputs, ConfirmModAsync) is not { } result) { ShowMods(); return; }
            GD.Print($"Mod import {result.Status}: {result.Message}");
            if (result.Status == ModImportStatus.Installed)
                ShowReopen(result.Message + (ModLaunchSettings.Enabled ? "" : " Mods are off; turn them on from Mods after reopening."));
            else
            {
                _view.SetNotice(Notice.Failure("Mod not imported", result.Message));
                ShowMods();
            }
        }
        catch (Exception error)
        {
            GD.PushError("Mod import failed: " + error);
            _view.SetNotice(Notice.Failure("Mod not imported", "The zip could not be imported: " + error.Message));
            ShowMods();
        }
        finally
        {
            _modInstalling = false;
            if (PickedModImport.Discard(archive) is { } why) GD.PushWarning("Picked mod copy not removed: " + why);
        }
    }

    /// <summary>Asks before a mod with code is added or a mod is replaced, then says what happens next.</summary>
    private async Task<bool> ConfirmModAsync(ModImportResult question)
    {
        bool code = question.Status == ModImportStatus.NeedsCodeConfirmation;
        if (!await AskModAsync(code ? question.Message : question.Message + " Your saves are kept.", code ? "Add code mod" : "Replace mod")) return false;
        _view.ShowWorking(code ? "Adding the mod..." : "Replacing the mod...");
        return true;
    }

    private async Task DeleteModAsync(string id)
    {
        if (_modInstalling || _modPicking) return;
        _modInstalling = true;
        _view.SetNotice(null);
        try
        {
            if (!await AskModAsync($"Delete {id} from this iPhone? Its imported files will be removed. Your game and saves are kept.", "Delete mod"))
            { ShowMods(); return; }
            _view.ShowWorking("Deleting the mod...");
            string inputs = ModInputs;
            await Task.Run(() => ModPackageImport.Delete(inputs, id));
            GD.Print("Mod deleted: ", id);
            ShowReopen(id + " was deleted. Close the app and open it again to apply the change.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _view.SetNotice(Notice.Failure("Mod not deleted", error.Message));
            ShowMods();
        }
        finally { _modInstalling = false; }
    }

    private async Task<bool> AskModAsync(string question, string confirm)
    {
        _modQuestion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _modAnswer = answer => _modQuestion.TrySetResult(answer);
        _view.ChoiceMade += _modAnswer;
        try
        {
            _view.ShowChoice(question, confirm, "Cancel", canGoBack: true, heading: "Mods");
            return await _modQuestion.Task;
        }
        finally
        {
            _view.ChoiceMade -= _modAnswer;
            _modAnswer = null;
            _modQuestion = null;
        }
    }
}
