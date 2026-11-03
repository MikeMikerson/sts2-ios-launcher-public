using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using STS2Mobile.Launcher;

namespace STS2Mobile.FullGame;

/// <summary>
/// The version screen: a ledger of the game on this iPhone against what Steam serves now, then Update, Switch and Delete. What it
/// says and offers is <see cref="VersionScreen"/>'s; this only draws it.
/// </summary>
public sealed partial class LauncherView
{
    private VBoxContainer _versionFacts = null!;
    private Button _versionUpdate = null!;
    private Button _switchToStable = null!;
    private Button _switchToBeta = null!;
    private Button _askSteamAgain = null!;

    /// <summary>The player pressed a button of the version screen other than Delete, which is <see cref="DeleteRequested"/>.</summary>
    public event Action<VersionAction>? VersionActionRequested;

    /// <summary>Called once by the constructor: the ledger goes under the status line, the buttons above Delete. Returns what screens switch.</summary>
    private Control[] CreateVersionControls(VBoxContainer page, VBoxContainer actions)
    {
        _versionFacts = new VBoxContainer();
        _versionFacts.AddThemeConstantOverride("separation", 0);
        page.AddChild(_versionFacts);
        page.MoveChild(_versionFacts, _status.GetIndex() + 1);
        _versionUpdate = VersionButton(VersionScreen.UpdateLabel, VersionAction.Update);
        _switchToStable = VersionButton(VersionScreen.SwitchToStableLabel, VersionAction.SwitchToStable);
        _switchToBeta = VersionButton(VersionScreen.SwitchToBetaLabel, VersionAction.SwitchToBeta);
        _askSteamAgain = VersionButton(VersionScreen.CheckAgainLabel, VersionAction.CheckAgain);
        foreach (Button button in new[] { _versionUpdate, _switchToStable, _switchToBeta, _askSteamAgain })
        {
            button.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            actions.AddChild(button);
            actions.MoveChild(button, _deleteGame.GetIndex());
        }
        return [_versionFacts, _versionUpdate, _switchToStable, _switchToBeta, _askSteamAgain];
    }

    private Button VersionButton(string label, VersionAction action) =>
        LauncherTheme.MakeButton(label, "Button", () => VersionActionRequested?.Invoke(action));

    /// <summary>The version screen for <paramref name="screen"/>, with Back to the home screen.</summary>
    public void ShowVersion(VersionScreen screen)
    {
        FillVersionFacts(screen.Facts);
        var visible = new List<Control> { _versionFacts };
        foreach (var choice in screen.Buttons)
        {
            Button button = choice.Action switch
            {
                VersionAction.Update => _versionUpdate,
                VersionAction.SwitchToStable => _switchToStable,
                VersionAction.SwitchToBeta => _switchToBeta,
                VersionAction.CheckAgain => _askSteamAgain,
                _ => _deleteGame,
            };
            button.Text = choice.Label;
            button.ThemeTypeVariation = choice.Danger ? LauncherTheme.DangerButton : choice.Primary ? LauncherTheme.PrimaryButton : "Button";
            visible.Add(button);
        }
        visible.Add(_back);
        // For a run with nobody there: the ledger, which the screen line does not carry.
        GD.Print("Version screen: ", string.Join("; ", screen.Facts.Select(fact => fact.Label + " = " + fact.Value)), screen.UpdateAvailable ? "; update available" : "");
        Show(screen.Heading, screen.Status, form: false, visible.ToArray());
    }

    /// <summary>Each fact as a label over its value in figures, ruled off from the next like a ledger.</summary>
    private void FillVersionFacts(IReadOnlyList<VersionFact> facts)
    {
        foreach (Node child in _versionFacts.GetChildren())
        {
            _versionFacts.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var fact in facts)
        {
            var row = new PanelContainer();
            row.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = Colors.Transparent, BorderColor = LauncherTheme.Line, BorderWidthBottom = 2, ContentMarginTop = 18, ContentMarginBottom = 18,
            });
            var lines = new VBoxContainer();
            lines.AddThemeConstantOverride("separation", 6);
            lines.AddChild(LauncherTheme.Text(fact.Label, LauncherTheme.Body, LauncherTheme.SmallSize, LauncherTheme.Mist));
            var value = LauncherTheme.Text(fact.Value, LauncherTheme.Figures, LauncherTheme.SmallSize, LauncherTheme.Bone);
            value.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            lines.AddChild(value);
            row.AddChild(lines);
            _versionFacts.AddChild(row);
        }
    }
}
