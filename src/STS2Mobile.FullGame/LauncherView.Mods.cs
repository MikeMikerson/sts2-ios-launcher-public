using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.FullGame;

public sealed partial class LauncherView
{
    private ScrollContainer _modScroll = null!;
    private VBoxContainer _modList = null!;
    private Button _modImport = null!;
    private Button _modToggle = null!;
    private readonly List<Button> _modDeleteButtons = [];
    public event Action? ModsRequested;
    public event Action? ModImportRequested;
    public event Action<string>? ModDeleteRequested;

    private Control[] CreateModControls(VBoxContainer page, VBoxContainer actions)
    {
        _modScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 180) };
        _modList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _modList.AddThemeConstantOverride("separation", 24);
        _modScroll.AddChild(_modList);
        page.AddChild(_modScroll);
        page.MoveChild(_modScroll, _status.GetIndex() + 1);
        _modImport = LauncherTheme.MakeButton("Import from Files", LauncherTheme.PrimaryButton, () => ModImportRequested?.Invoke());
        _modToggle = LauncherTheme.MakeButton("Turn mods on", "Button", () => ModsToggleRequested?.Invoke());
        actions.AddChild(_modImport);
        actions.AddChild(_modToggle);
        return [_modScroll, _modImport, _modToggle];
    }

    public void ShowMods(IReadOnlyList<ModLibraryItem> mods, bool enabled, string? refusal = null, bool canGoBack = true)
    {
        foreach (Node child in _modList.GetChildren()) { _modList.RemoveChild(child); child.QueueFree(); }
        _modDeleteButtons.Clear();
        if (refusal != null)
        {
            var reason = LauncherTheme.Text(refusal, LauncherTheme.Body, LauncherTheme.SmallSize, LauncherTheme.Bone);
            reason.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _modList.AddChild(reason);
        }
        foreach (var mod in mods)
        {
            var row = new VBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            var name = LauncherTheme.Text(mod.Name, LauncherTheme.BodyStrong, LauncherTheme.BodySize, LauncherTheme.Bone);
            name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            row.AddChild(name);
            var state = LauncherTheme.Text(mod.Status, LauncherTheme.Body, LauncherTheme.SmallSize, mod.Refused ? LauncherTheme.Blood : LauncherTheme.Mist);
            state.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            row.AddChild(state);
            string id = mod.Id;
            var delete = LauncherTheme.MakeButton("Delete " + id, LauncherTheme.QuietButton, () => ModDeleteRequested?.Invoke(id));
            delete.Alignment = HorizontalAlignment.Left;
            delete.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            row.AddChild(delete);
            _modDeleteButtons.Add(delete);
            row.AddChild(new HSeparator());
            _modList.AddChild(row);
        }
        _modToggle.Text = enabled ? "Turn mods off" : "Turn mods on";
        string status = mods.Count == 0 ? "No mods imported. Choose a mod's zip from Files to add it."
            : enabled ? "Mods are on. Changes take effect after reopening the app." : "Mods are off. Your imported mods stay here for later.";
        if (refusal != null) status = "Mods could not be prepared. Replace or delete the affected mod, or turn mods off to play.";
        var visible = new List<Control> { _modScroll, _modImport, _modToggle };
        if (canGoBack) visible.Add(_back);
        GD.Print("Mods list: ", string.Join("; ", mods.Select(mod => mod.Id + " = " + mod.Status)));
        Show("Mods", status, form: true, visible.ToArray());
    }
}
