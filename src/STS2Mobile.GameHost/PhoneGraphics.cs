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
    private readonly PortraitLayout _portraitLayout = new();
    private readonly PhoneTouchCards _touchCards = new();
    private readonly PhoneTouchScroll _touchScroll = new();
    private readonly PhoneHoverTips _hoverTips = new();
    private readonly PhoneCardRewardChoice _cardRewardChoice = new();
    private readonly PhoneRewardTapGuard _rewardTapGuard = new();
    private readonly PhoneChoiceConfirm _choiceConfirm = new();
    private readonly long _origin = Stopwatch.GetTimestamp();
    private PhoneGraphicsSettings _settings = PhoneGraphicsSettings.Default;
    private string _path = "";
    private double _appliedFactor = 1;
    private double _lastTouch;
    private bool _gameRunning;
    private bool _focused = true;
    private MegaRichTextLabel? _resolutionLabel;
    private MegaRichTextLabel? _fillScreenLabel;
    private NSettingsPanel? _activeGraphicsPanel;
    private Control? _portraitDefaultRow;
    private Control? _portraitDefaultDivider;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        try
        {
            _path = ProjectSettings.GlobalizePath(SettingsPath);
            _settings = PhoneGraphicsSettings.Load(_path);
            SafeAreaCanvas.FillScreen = _settings.FillScreen;
            // Landscape is locked initially unless both Portrait Mode and Set Portrait as Default were chosen.
            if (!_settings.StartInPortrait && _settings.PortraitMode)
                _settings = _settings.WithPortraitMode(false);
            _appliedFactor = DevicePower.RenderScale();
            GD.Print("Phone graphics: ", _settings.Describe(_appliedFactor));
            GetTree().Root.WindowInput += OnWindowInput;
            GetTree().NodeAdded += OnNodeAdded;
            _portraitLayout.Attach(GetTree(), _settings.StartInPortrait);
            _touchCards.Attach(GetTree());
            _touchScroll.Attach(GetTree());
            GetTree().ProcessFrame += OnTouchFrame;
        }
        catch (Exception error)
        {
            Disable(error);
        }
    }

    public override void _ExitTree()
    {
        _portraitLayout.Detach();
        _touchCards.Detach();
        _touchScroll.Detach();
        GetTree().ProcessFrame -= OnTouchFrame;
    }

    // Hand cards under a finger and hover tips kept on screen; cards, relics and shop items taken only after a
    // confirm (Choose, the game's own confirm, or a second tap in the shop). Portrait only: in landscape the game plays as it
    // does on a desktop, with a touch as the mouse. The three screen passes below still run in landscape,
    // where they do nothing unless a screen opened in portrait is still open: such a screen keeps its confirm until it closes,
    // so turning Portrait Mode off over an open choice leaves it working.
    private void OnTouchFrame()
    {
        if (!_gameRunning) return;
        if (SafeAreaCanvas.IsPortrait)
        {
            SceneTree tree = GetTree();
            _touchCards.OnProcessFrame(tree);
            _hoverTips.OnProcessFrame(tree);
        }
        _cardRewardChoice.OnProcessFrame();
        _rewardTapGuard.OnProcessFrame();
        _choiceConfirm.OnProcessFrame();
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
            else if (PhoneGraphicsSettings.Load(_path) != _settings)
            {
                // Persist normalized Landscape start when Portrait Mode had been left on without Set Portrait as Default.
                _settings.Save(_path);
            }
            _gameRunning = true;
            _portraitLayout.ApplyImmediate();
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
        _choiceConfirm.OnWindowInput(input);
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
        // Choice screens, reward lists and shop slots get the tap-then-confirm behaviour only in portrait (see AdoptOpenScreens
        // for the ones already open when Portrait Mode is turned on).
        if (SafeAreaCanvas.IsPortrait)
        {
            _cardRewardChoice.OnNodeAdded(node);
            _rewardTapGuard.OnNodeAdded(node);
            _choiceConfirm.OnNodeAdded(node);
        }
        if (node is NSettingsScreen screen)
            screen.Connect(Node.SignalName.Ready, Callable.From(() => AddRows(screen)), (uint)ConnectFlags.OneShot);
    }

    /// <summary>
    /// Portrait Mode was turned on with the game running: choice screens, reward lists and shop slots that are already open
    /// get the portrait behaviour too. They need it: in the portrait layout the game's own take on release does not fire for a
    /// still tap on a reward card, so a screen left as it was could only be skipped (<c>switch-mid-choice</c>).
    /// </summary>
    private void AdoptOpenScreens()
    {
        var pending = new Stack<Node>();
        pending.Push(GetTree().Root);
        while (pending.Count > 0)
        {
            Node node = pending.Pop();
            _cardRewardChoice.OnNodeAdded(node);
            _rewardTapGuard.OnNodeAdded(node);
            _choiceConfirm.OnNodeAdded(node);
            foreach (Node child in node.GetChildren()) pending.Push(child);
        }
    }

    private void AddRows(NSettingsScreen screen)
    {
        try
        {
            var panel = screen.GetNode<NSettingsPanel>("%GraphicsSettings");
            VBoxContainer content = panel.Content;
            foreach (string name in DesktopRows)
            {
                if (content.GetNodeOrNull<CanvasItem>(name) is not { } row) continue;
                row.Visible = false;
                // A row's dropdown is drawn by the panel over the row, shown while the row's anchor is: with
                // only the row hidden it was left over the first row shown. Hiding the anchor hides it for good.
                foreach (Node child in row.GetChildren())
                    if (child is CanvasItem anchor && child.GetType().Name == "NDropdownPositioner") anchor.Visible = false;
            }
            var template = content.GetNode<Control>("Msaa");
            var divider = content.GetNode<Control>("Line6");
            int index = divider.GetIndex() + 1;
            _rows.RemoveAll(row => !IsInstanceValid(row));
            _activeGraphicsPanel = panel;
            index = AddRow(content, template, divider, index, "Portrait Mode", () => OffOn,
                () => _settings.PortraitMode ? 1 : 0,
                choice =>
                {
                    Store(_settings.WithPortraitMode(choice == 1));
                    RefreshPortraitDefaultRow();
                    if (!_settings.PortraitMode) _hoverTips.Release();
                    _portraitLayout.Apply(GetTree(), _settings.PortraitMode);
                    if (_settings.PortraitMode) AdoptOpenScreens();
                },
                out _, out _, out _, subtitle: "Landscape is locked initially. Toggle to play in Portrait.");
            index = AddRow(content, template, divider, index, "Set Portrait as Default", () => OffOn,
                () => _settings.PortraitDefault ? 1 : 0,
                choice => Store(_settings.WithPortraitDefault(choice == 1)),
                out _, out _portraitDefaultRow, out _portraitDefaultDivider,
                indentLeft: 44, subtitle: "Launch directly in Portrait when opening the game.");
            index = AddRow(content, template, divider, index, "Battery Saver",
                () => _settings.Preset == BatteryPreset.Custom ? PresetChoicesWithCustom : PresetChoices,
                () => (int)_settings.Preset, choice => Store(_settings.WithPreset((BatteryPreset)choice)), out _, out _, out _);
            index = AddRow(content, template, divider, index, ResolutionTitle(), () => ResolutionChoices,
                () => (int)_settings.Resolution, choice => Store(_settings with { Resolution = (RenderResolution)choice }), out _resolutionLabel, out _, out _);
            index = AddRow(content, template, divider, index, FillScreenTitle(), () => OffOn,
                () => _settings.FillScreen ? 1 : 0, choice => Store(_settings with { FillScreen = choice == 1 }), out _fillScreenLabel, out _, out _,
                subtitle: "Landscape only. May place controls behind the notch or rounded corners. Applies after restart.");
            index = AddRow(content, template, divider, index, "Lower FPS When Idle", () => OffOn,
                () => _settings.LowerIdleFrameRate ? 1 : 0, choice => Store(_settings with { LowerIdleFrameRate = choice == 1 }), out _, out _, out _);
            AddRow(content, template, divider, index, "Cool Down When Hot", () => OffOn,
                () => _settings.CoolDownWhenHot ? 1 : 0, choice => Store(_settings with { CoolDownWhenHot = choice == 1 }), out _, out _, out _);
            RefreshPortraitDefaultRow();
            _portraitLayout.ApplyImmediate();
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

    private void RefreshPortraitDefaultRow()
    {
        bool show = _settings.PortraitMode;
        if (_portraitDefaultRow != null && IsInstanceValid(_portraitDefaultRow)) _portraitDefaultRow.Visible = show;
        if (_portraitDefaultDivider != null && IsInstanceValid(_portraitDefaultDivider)) _portraitDefaultDivider.Visible = show;
        if (_activeGraphicsPanel != null && IsInstanceValid(_activeGraphicsPanel) && _activeGraphicsPanel.IsNodeReady())
        {
            _activeGraphicsPanel.Call(NSettingsPanel.MethodName.RefreshSize);
            _activeGraphicsPanel.Call(NSettingsPanel.MethodName.UpdateNavigation);
        }
    }

    private int AddRow(VBoxContainer content, Control template, Control divider, int index, string title, Func<string[]> choices,
        Func<int> read, Action<int> write, out MegaRichTextLabel label, out Control rowNode, out Control lineNode,
        int indentLeft = 0, string? subtitle = null)
    {
        var row = new MarginContainer
        {
            Name = "Phone" + title.Replace(" ", ""),
            CustomMinimumSize = subtitle == null
                ? template.CustomMinimumSize
                : new Vector2(template.CustomMinimumSize.X, Math.Max(template.CustomMinimumSize.Y, 88f)),
        };
        foreach (string side in new[] { "left", "top", "right", "bottom" })
        {
            int margin = template.GetThemeConstant("margin_" + side) + (side == "left" ? indentLeft : 0);
            row.AddThemeConstantOverride("margin_" + side, margin);
        }
        label = (MegaRichTextLabel)template.GetNode<MegaRichTextLabel>("Label").Duplicate();
        label.Text = title;
        if (subtitle == null)
        {
            row.AddChild(label);
        }
        else
        {
            label.CustomMinimumSize = new Vector2(0f, 38f);
            var textMargin = new MarginContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            textMargin.AddThemeConstantOverride("margin_right", 336);
            var textStack = new VBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            textStack.AddThemeConstantOverride("separation", 2);
            textStack.AddChild(label);
            var sub = new Label
            {
                Text = subtitle,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            sub.AddThemeColorOverride("font_color", new Color(0.66f, 0.75f, 0.80f, 1f));
            sub.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.45f));
            sub.AddThemeConstantOverride("outline_size", 6);
            sub.AddThemeFontSizeOverride("font_size", 18);
            if (label.GetThemeFont("normal_font") is { } font)
                sub.AddThemeFontOverride("font", font);
            textStack.AddChild(sub);
            textMargin.AddChild(textStack);
            row.AddChild(textMargin);
        }
        var paginator = PhoneOptionPaginator.Create(choices, read, write);
        row.AddChild(paginator);
        _rows.Add(paginator);
        content.AddChild(row);
        content.MoveChild(row, index);
        var line = (Control)divider.Duplicate();
        content.AddChild(line);
        content.MoveChild(line, index + 1);
        rowNode = row;
        lineNode = line;
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
        if (_fillScreenLabel != null && IsInstanceValid(_fillScreenLabel)) _fillScreenLabel.Text = FillScreenTitle();
    }

    /// <summary>The resolution is chosen before the engine starts, so a new choice needs a restart.</summary>
    private string ResolutionTitle() =>
        Math.Abs(_settings.ResolutionFactor - _appliedFactor) < 0.001 ? "Render Resolution" : "Render Resolution (after restart)";

    private string FillScreenTitle() =>
        _settings.FillScreen == SafeAreaCanvas.FillScreen ? "Fill Screen" : "Fill Screen (after restart)";

    private double Now() => Stopwatch.GetElapsedTime(_origin).TotalSeconds;

    private void Disable(Exception error)
    {
        GD.PushError("Phone graphics disabled: " + error.GetType().Name + ": " + error.Message);
        _gameRunning = false;
        SetProcess(false);
    }
}
