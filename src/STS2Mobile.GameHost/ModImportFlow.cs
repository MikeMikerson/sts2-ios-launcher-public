using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// "Add mod from Files" on the game's Mods screen: opens the Files picker, has
/// <see cref="ModImport"/> check the chosen .zip and install it into the app's mods folder, and tells
/// the player what happened in the game's own popups. The game's loader picks a new mod up at the next
/// launch, through its own consent and toggle flow. A failure here leaves the Mods screen as it was.
/// </summary>
public sealed partial class ModImportFlow : Node
{
    private const string ButtonLabel = "Add mod from Files";
    private const string Title = "Add mod";
    private bool _picking;
    private bool _installing;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        SetProcess(false);
        ModImportRequests.Bind(OnRequested);
        GetTree().NodeAdded += OnNodeAdded;
    }

    private void OnNodeAdded(Node node)
    {
        if (node is NModdingScreen screen)
            screen.Connect(Node.SignalName.Ready, Callable.From(() => Adapt(screen)), (uint)ConnectFlags.OneShot);
    }

    /// <summary>The Workshop button becomes the import button; the mod template link has no use on a phone.</summary>
    private static void Adapt(NModdingScreen screen)
    {
        try
        {
            screen.GetNode<NButton>("%GetModsButton").GetNode<MegaLabel>("Visuals/Label").SetTextAutoSize(ButtonLabel);
            screen.GetNode<CanvasItem>("%MakeModsButton").Visible = false;
        }
        catch (Exception error)
        {
            GD.PushError("Mods screen not adapted: " + error.GetType().Name + ": " + error.Message);
        }
    }

    private void OnRequested()
    {
        if (_picking || _installing) return;
        if (!FilesPicker.Open())
        {
            Report(TellAsync("The Files picker could not be opened."), "Mod import notice");
            return;
        }
        _picking = true;
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        var result = FilesPicker.Poll(out string? path);
        if (result == FilesPickerResult.Nothing) return;
        _picking = false;
        SetProcess(false);
        if (result == FilesPickerResult.Picked) Report(InstallAsync(path!), "Mod import");
        else if (result == FilesPickerResult.Failed) Report(TellAsync("The chosen file could not be read."), "Mod import notice");
    }

    private async Task InstallAsync(string archive)
    {
        _installing = true;
        try
        {
            string mods = ProjectSettings.GlobalizePath("user://" + ModPackageImport.FolderName);
            GD.Print("Mod import: checking ", Path.GetFileName(archive));
            if (await PickedModImport.InstallAsync(archive, mods, question => AskAsync(question.Message)) is not { } result) return;
            GD.Print("Mod import ", result.Status, ": ", result.Message);
            await TellAsync(result.Message);
        }
        catch (Exception error)
        {
            GD.PushError("Mod import failed: " + error.GetType().Name + ": " + error.Message);
            await TellAsync("The mod could not be added: " + error.Message);
        }
        finally
        {
            _installing = false;
            if (PickedModImport.Discard(archive) is { } why) GD.PushWarning("Picked mod copy not removed: " + why);
        }
    }

    private static void Report(Task task, string operation) =>
        task.ContinueWith(failed => GD.PushError(operation + " failed: " + failed.Exception?.GetBaseException()),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static Task TellAsync(string body) =>
        ShowAsync(body, null, new LocString("main_menu_ui", "GENERIC_POPUP.ok"));

    private static Task<bool> AskAsync(string body) =>
        ShowAsync(body, new LocString("main_menu_ui", "GENERIC_POPUP.cancel"), new LocString("main_menu_ui", "GENERIC_POPUP.confirm"));

    /// <summary>The game's generic popup with this host's own text; its buttons keep the game's labels.</summary>
    private static async Task<bool> ShowAsync(string body, LocString? no, LocString yes)
    {
        var popup = NGenericPopup.Create();
        if (popup == null || NModalContainer.Instance == null) return false;
        NModalContainer.Instance.Add(popup);
        var answer = popup.WaitForConfirmation(yes, yes, no, yes);
        popup.GetNode<NVerticalPopup>("VerticalPopup").SetText(Title, body);
        return await answer;
    }
}
