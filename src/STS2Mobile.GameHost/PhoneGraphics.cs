using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Saves;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// The phone's battery and heat choices: keeps them (<see cref="PhoneGraphicsSettings"/>), applies the
/// frame cap they ask for, and shows them in the game's own Graphics settings tab in place of the
/// desktop-only window rows. The render resolution is applied by native code at launch
/// (<c>native/power_state.mm</c>). A failure here leaves the game as it was.
/// </summary>
public sealed partial class PhoneGraphics : Node
{
    private const string SettingsPath = "user://" + PhoneGraphicsSettings.FileName;
    private static readonly string[] DesktopRows =
        ["Fullscreen", "FullscreenDivider", "DisplaySelection", "Line11", "WindowedResolution", "Line12",
         "AspectRatio", "Line10", "WindowResizing", "Line14", "VSync", "Line13"];
    // "Custom" is only listed while it is in force, so paging never lands on a choice that changes nothing.
    private static readonly string[] PresetChoices = ["Off", "Balanced", "Maximum"];
    private static readonly string[] PresetChoicesWithCustom = [.. PresetChoices, "Custom"];
    private static readonly string[] ResolutionChoices = ["100%", "83%", "67%"];
    private static readonly string[] OffOn = ["Off", "On"];
    private readonly HashSet<int> _touches = new();
    private readonly List<PhoneOptionPaginator> _rows = new();
    private readonly long _origin = Stopwatch.GetTimestamp();
    private PhoneGraphicsSettings _settings = PhoneGraphicsSettings.Default;
    private string _path = "";
    private double _appliedFactor = 1;
    private double _lastTouch;
    private bool _gameRunning;
    private bool _focused = true;
    private MegaRichTextLabel? _resolutionLabel;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        try
        {
            _path = ProjectSettings.GlobalizePath(SettingsPath);
            _settings = PhoneGraphicsSettings.Load(_path);
            _appliedFactor = DevicePower.RenderScale();
            GD.Print("Phone graphics: ", _settings.Describe(_appliedFactor));
            GetTree().Root.WindowInput += OnWindowInput;
            GetTree().NodeAdded += OnNodeAdded;
        }
        catch (Exception error)
        {
            Disable(error);
        }
    }

    /// <summary>Called once the real game has started and its settings save is loaded.</summary>
    public void GameStarted()
    {
        try
        {
            // The imported desktop settings say 2x MSAA, but the game only applies MSAA when the row is
            // changed, never at launch, so the phone has always drawn without it. Make the row say so
            // once; a later choice by the player is kept.
            if (!_settings.MsaaCleared)
            {
                SaveManager.Instance.SettingsSave.Msaa = 0;
                SaveManager.Instance.SaveSettings();
                Store(_settings with { MsaaCleared = true });
            }
            _gameRunning = true;
        }
        catch (Exception error)
        {
            Disable(error);
        }
    }

    public override void _Process(double delta)
    {
        if (!_gameRunning || !_focused) return;
        try
        {
            double now = Now();
            if (_touches.Count > 0) _lastTouch = now;
            int target = FrameRateGovernor.Target(SaveManager.Instance.SettingsSave.FpsLimit, _settings, _touches.Count > 0,
                now - _lastTouch, _settings.CoolDownWhenHot ? DevicePower.ThermalState() : DevicePower.UnknownThermalState);
            if (Engine.MaxFps != target) Engine.MaxFps = target;
        }
        catch (Exception error)
        {
            Disable(error);
        }
    }

    public override void _Notification(int what)
    {
        // While the app is in the background the game's own handler owns the frame cap.
        if (what == NotificationApplicationFocusOut) _focused = false;
        else if (what == NotificationApplicationFocusIn)
        {
            _focused = true;
            _lastTouch = Now();
        }
    }

    private void OnWindowInput(InputEvent input)
    {
        if (input is InputEventScreenTouch touch)
        {
            if (touch.Pressed) _touches.Add(touch.Index);
            else _touches.Remove(touch.Index);
            _lastTouch = Now();
        }
        else if (input is InputEventScreenDrag) _lastTouch = Now();
    }

    private void OnNodeAdded(Node node)
    {
        if (node is NSettingsScreen screen)
            screen.Connect(Node.SignalName.Ready, Callable.From(() => AddRows(screen)), (uint)ConnectFlags.OneShot);
    }

    private void AddRows(NSettingsScreen screen)
    {
        try
        {
            var panel = screen.GetNode<NSettingsPanel>("%GraphicsSettings");
            VBoxContainer content = panel.Content;
            foreach (string name in DesktopRows)
                if (content.GetNodeOrNull<CanvasItem>(name) is { } row) row.Visible = false;
            var template = content.GetNode<Control>("Msaa");
            var divider = content.GetNode<Control>("Line6");
            int index = divider.GetIndex() + 1;
            _rows.RemoveAll(row => !IsInstanceValid(row));
            index = AddRow(content, template, divider, index, "Battery Saver",
                () => _settings.Preset == BatteryPreset.Custom ? PresetChoicesWithCustom : PresetChoices,
                () => (int)_settings.Preset, choice => Store(_settings.WithPreset((BatteryPreset)choice)), out _);
            index = AddRow(content, template, divider, index, ResolutionTitle(), () => ResolutionChoices,
                () => (int)_settings.Resolution, choice => Store(_settings with { Resolution = (RenderResolution)choice }), out _resolutionLabel);
            index = AddRow(content, template, divider, index, "Lower FPS When Idle", () => OffOn,
                () => _settings.LowerIdleFrameRate ? 1 : 0, choice => Store(_settings with { LowerIdleFrameRate = choice == 1 }), out _);
            AddRow(content, template, divider, index, "Cool Down When Hot", () => OffOn,
                () => _settings.CoolDownWhenHot ? 1 : 0, choice => Store(_settings with { CoolDownWhenHot = choice == 1 }), out _);
            // The panel sizes its scroll area and builds controller navigation in its own _Ready.
            panel.Call(NSettingsPanel.MethodName.RefreshSize);
            panel.Call(NSettingsPanel.MethodName.UpdateNavigation);
        }
        catch (Exception error)
        {
            // The settings screen keeps working without the phone rows.
            GD.PushError("Phone graphics rows not added: " + error.GetType().Name + ": " + error.Message);
        }
    }

    private int AddRow(VBoxContainer content, Control template, Control divider, int index, string title, Func<string[]> choices,
        Func<int> read, Action<int> write, out MegaRichTextLabel label)
    {
        var row = new MarginContainer { Name = "Phone" + title.Replace(" ", ""), CustomMinimumSize = template.CustomMinimumSize };
        foreach (string side in new[] { "left", "top", "right", "bottom" })
            row.AddThemeConstantOverride("margin_" + side, template.GetThemeConstant("margin_" + side));
        label = (MegaRichTextLabel)template.GetNode<MegaRichTextLabel>("Label").Duplicate();
        label.Text = title;
        row.AddChild(label);
        var paginator = PhoneOptionPaginator.Create(choices, read, write);
        row.AddChild(paginator);
        _rows.Add(paginator);
        content.AddChild(row);
        content.MoveChild(row, index);
        var line = (Control)divider.Duplicate();
        content.AddChild(line);
        content.MoveChild(line, index + 1);
        return index + 2;
    }

    private void Store(PhoneGraphicsSettings settings)
    {
        if (settings == _settings) return;
        _settings = settings;
        try
        {
            _settings.Save(_path);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            GD.PushError("Phone graphics not saved: " + error.Message);
        }
        GD.Print("Phone graphics: ", _settings.Describe(_appliedFactor));
        // A preset moves several rows at once; show every row as it now stands.
        foreach (PhoneOptionPaginator row in _rows)
            if (IsInstanceValid(row) && row.IsInsideTree()) row.SetFromSettings();
        if (_resolutionLabel != null && IsInstanceValid(_resolutionLabel)) _resolutionLabel.Text = ResolutionTitle();
    }

    /// <summary>The resolution is chosen before the engine starts, so a new choice needs a restart.</summary>
    private string ResolutionTitle() =>
        Math.Abs(_settings.ResolutionFactor - _appliedFactor) < 0.001 ? "Render Resolution" : "Render Resolution (after restart)";

    private double Now() => Stopwatch.GetElapsedTime(_origin).TotalSeconds;

    private void Disable(Exception error)
    {
        GD.PushError("Phone graphics disabled: " + error.GetType().Name + ": " + error.Message);
        _gameRunning = false;
        SetProcess(false);
    }
}
