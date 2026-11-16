using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Live iOS orientation switching and portrait reflow for the game's main menu, settings screen, top bar and 2-row relic strip
/// Every control touched in portrait has its desktop layout captured before the first change so turning Portrait Mode
/// off restores the original landscape layout without restarting the game.
/// </summary>
internal sealed class PortraitLayout
{
    private const string SafeCapName = "PortraitSafeCap";
    private const string ExpandSpacerName = "PortraitExpandSpacer";
    private const string RelicPagerName = "PortraitRelicPager";
    private const string TopBarTypeName = "MegaCrit.Sts2.Core.Nodes.CommonUi.NTopBar";
    private const string RelicInventoryTypeName = "MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventory";
    private const string PotionTypeName = "MegaCrit.Sts2.Core.Nodes.Potions.NPotion";
    private const string EarlyAccessDisclaimerTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NEarlyAccessDisclaimer";
    private const float DefaultCapHeight = 162f;
    private const float MinCapHeight = 136f;
    private const float TopBarRowHeight = PortraitBands.TopBarRowHeight;
    // The right margin the game's scene gives the top bar's boss icon.
    private const int BossIconMarginRight = 4;
    // The top bar's items were small: relics 96 units (33 points, were 78), ten to a row, so a page of
    // two rows holds 18 with the pager taking two places.
    private const float PortraitRelicSize = 96f;
    private const float DesktopRelicSize = 68f;
    private const int PortraitRelicsPerPage = 18;
    // Sizes for a finger. A potion slot is 44 points (127 units) in its own row under the top bar, and the map,
    // deck and settings buttons are 44 points wide, their touch area reaching up into the safe cap to be 44 points tall.
    private const string PotionRowName = "PotionMarginifier";
    private const float PotionSlotSize = PortraitBands.MinTarget;
    private const float PotionPictureScale = 1.5f;
    private const float TopBarButtonWidth = PortraitBands.MinTarget;
    private const float TopBarRowInset = 4f;
    private const float TopBarTouchRise = PortraitBands.MinTarget - (TopBarRowHeight - TopBarRowInset);
    // Health, gold and floor numbers at 38 units (13 points; the game's 32 is 11) and their icons 48 wide (were
    // 40), as much as the row's width allows with three digit health.
    private const int TopBarNumberFontSize = 38;
    private const float TopBarIconWidth = 48f;
    // The main menu's entries, 44 points tall and 208 wide, text 50 units (about 17 points), the column's box
    // from a little above the screen's middle to near the city at the bottom (the entries are centred in it).
    private const float MenuButtonWidth = 600f;
    private const int MenuFontSize = 50;
    private const float MenuButtonsTop = -150f;
    private const float MenuButtonsBottom = 850f;
    // Settings' panels drawn 1.55 times larger, so a row of 64 units with its divider and gaps (82)
    // is 127 units (44 points) from the next and text of 28 units reads at 15 points; 1000 units wide as drawn, from 24
    // units in, with the scrollbar beside them. The tabs are 110 units tall with 44 point touch areas and 13 point text.
    private const float SettingsScale = 1.55f;
    private const float SettingsRowsLeft = 24f;
    private const float SettingsRowsWidth = 1000f;
    private const float SettingsTabHeight = 110f;
    private const int SettingsTabFontSize = 38;
    // The rows end this far above the canvas's lower edge, clear of the back button in the lower left corner.
    private const float SettingsBottomGap = 380f;
    private const string SettingsClipperPath = "ScrollContainer/Mask/Clipper";
    private const string SettingsSpacerName = "PortraitScaleSpacer";

    // Neutral slate stone cap (#2C444F = RGB 44, 68, 79) matching top_bar.tres with neutral white balance.
    private static readonly Color SafeCapColor = new(44f / 255f, 68f / 255f, 79f / 255f, 1f);
    private static readonly Color PagerFillColor = new(24f / 255f, 37f / 255f, 45f / 255f, 0.94f);
    private static readonly Color PagerBorderColor = new(118f / 255f, 146f / 255f, 162f / 255f, 0.9f);
    private static readonly Color PagerTextColor = new(239f / 255f, 200f / 255f, 81f / 255f, 1f);

    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private readonly Dictionary<ulong, Vector2> _relicOriginalPos = new();
    private readonly Dictionary<ulong, int> _relicPages = new();
    private readonly HashSet<ulong> _wiredTopBars = new();
    private readonly HashSet<ulong> _wiredInventories = new();
    private readonly HashSet<ulong> _wiredPotionHolders = new();
    // Labels whose font portrait enlarges (top bar numbers, the deck count, the main menu's entries), with the game's own
    // font size to put back in landscape.
    private readonly Dictionary<ulong, (bool HadOverride, int Size)> _fontSizes = new();
    private readonly HashSet<ulong> _wiredMenuLabels = new();
    private readonly PortraitCombatLayout _combat = new();
    private readonly PortraitBetweenLayout _between = new();
    private readonly PortraitScreenFit _fit = new();
    private readonly PortraitMenuLayout _menus = new();
    private readonly PortraitOverlayLayout _overlays = new();
    private SceneTree? _tree;
    private FieldInfo? _topBarHideTween;
    // The run's top bar and relic strip, as last laid out in portrait (see KeepRunBarsInPlace).
    private ulong _runTopBarId;
    private ulong _runRelicsId;
    private bool _applyQueued;
    private int _switchGeneration;

    // True from the first portrait layout until landscape has been put back after Portrait Mode is turned off. While it is
    // false nothing here writes to the game, so landscape with Portrait Mode never on is the game's own.
    private bool _needsRestore;

    public void Attach(SceneTree tree, bool initialPortrait)
    {
        _tree = tree;
        SafeAreaCanvas.SetPortrait(initialPortrait);
        tree.Root.SizeChanged += OnRootSizeChanged;
        tree.NodeAdded += OnNodeAdded;
        tree.ProcessFrame += OnProcessFrame;
        QueueApply();
    }

    public void Detach()
    {
        if (_tree == null) return;
        _tree.Root.SizeChanged -= OnRootSizeChanged;
        _tree.NodeAdded -= OnNodeAdded;
        _tree.ProcessFrame -= OnProcessFrame;
        _tree = null;
    }

    public void Apply(SceneTree tree, bool portrait)
    {
        _tree = tree;
        SafeAreaCanvas.SetPortrait(portrait);
        ScreenOrientation.SetDeferEdges(true);
        ScreenOrientation.Set(landscape: !portrait);
        UpdateWindowCanvas(tree.Root);
        ApplyImmediate();
        _ = FollowGeometryRotationAsync(tree, portrait, ++_switchGeneration);
    }

    private async Task FollowGeometryRotationAsync(SceneTree tree, bool portrait, int generation)
    {
        for (int frame = 0; frame < 60; frame++)
        {
            if (_switchGeneration != generation || !GodotObject.IsInstanceValid(tree.Root)) return;
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (_switchGeneration != generation || !GodotObject.IsInstanceValid(tree.Root)) return;
            Vector2I size = DisplayServer.WindowGetSize();
            bool rotated = portrait ? size.Y >= size.X : size.X >= size.Y;
            if (rotated && frame >= 2)
            {
                UpdateWindowCanvas(tree.Root);
                ApplyImmediate();
                FinishRestore(portrait);
                GD.Print($"Orientation switched live to {(portrait ? "portrait" : "landscape")}: window {size.X}x{size.Y}, canvas {tree.Root.ContentScaleSize.X}x{tree.Root.ContentScaleSize.Y}.");
                if (ScreenOrientation.GestureState() is { } gestures) GD.Print($"Edge gestures: {gestures}.");
                return;
            }
        }
        UpdateWindowCanvas(tree.Root);
        ApplyImmediate();
        FinishRestore(portrait);
    }

    /// <summary>The window has turned back to landscape and the layout was put back at its final size: nothing is left to restore.</summary>
    private void FinishRestore(bool portrait)
    {
        if (portrait || SafeAreaCanvas.IsPortrait || !_needsRestore) return;
        _needsRestore = false;
        GD.Print("Portrait layout: landscape restored; the game's own layout from here.");
    }

    private static void UpdateWindowCanvas(Window root)
    {
        Vector2I size = DisplayServer.WindowGetSize();
        long aspect = SafeAreaCanvas.Layout(size.X, size.Y, out int cw, out int ch);
        root.ContentScaleAspect = (Window.ContentScaleAspectEnum)aspect;
        root.ContentScaleSize = new Vector2I(cw, ch);
    }

    private void OnRootSizeChanged() => QueueApply();

    private void OnNodeAdded(Node node)
    {
        _overlays.Track(node);
        // An ancient's rows are sized as they are added: the game measures them for its slide in the same frame.
        if (SafeAreaCanvas.IsPortrait && node is Control added && _between.LayoutOnAdd(added)) _needsRestore = true;
        if (node is NMainMenu or NSettingsScreen)
        {
            QueueApply();
            return;
        }
        string? fullName = node.GetType().FullName;
        if (fullName is TopBarTypeName or RelicInventoryTypeName or EarlyAccessDisclaimerTypeName or PortraitCombatLayout.CombatRoomTypeName ||
            (fullName != null && (PortraitBetweenLayout.WatchedTypeNames.Contains(fullName) || PortraitScreenFit.Handles(fullName) ||
             PortraitMenuLayout.WatchedTypeNames.Contains(fullName) || PortraitOverlayLayout.WatchedTypeNames.Contains(fullName))))
            QueueApply();
    }

    private void OnProcessFrame()
    {
        if (_tree == null || !SafeAreaCanvas.IsPortrait || !GodotObject.IsInstanceValid(_tree.Root))
            return;
        _needsRestore = true;
        float capHeight = ComputeCapHeight(_tree.Root);
        KeepRunBarsInPlace();
        _combat.OnProcessFrame(_tree, capHeight);
        _between.OnProcessFrame(_tree.Root, capHeight);
        _fit.OnProcessFrame(_tree.Root, capHeight);
        _menus.OnProcessFrame(_tree.Root, capHeight);
        _overlays.OnProcessFrame(_tree.Root);
    }

    internal static float ComputeCapHeight(Window root)
    {
        float canvasHeight = Math.Max(root.GetVisibleRect().Size.Y, SafeAreaCanvas.MinPortraitHeight);
        double insetFraction = SafeAreaCanvas.TopInset();
        float capHeight = insetFraction > 0 ? MathF.Ceiling((float)(insetFraction * canvasHeight)) : DefaultCapHeight;
        return Math.Max(capHeight, MinCapHeight);
    }

    /// <summary>In a run the top bar stays over fitted screens such as the pause menu, so their band starts below it.</summary>
    private static float FitBandTop(string typeName, float capHeight) =>
        PortraitScreenFit.InRunScreens.Contains(typeName) ? capHeight + PortraitBands.TopBarHeight : capHeight;

    private void QueueApply()
    {
        if (_applyQueued || _tree == null) return;
        _applyQueued = true;
        Callable.From(ApplyDeferred).CallDeferred();
    }

    private void ApplyDeferred()
    {
        _applyQueued = false;
        ApplyImmediate();
    }

    public void ApplyImmediate()
    {
        if (_tree == null || !GodotObject.IsInstanceValid(_tree.Root)) return;
        PruneDeadInstances();
        bool portrait = SafeAreaCanvas.IsPortrait;
        if (!portrait)
        {
            // Landscape that was never laid out in portrait (or has been put back since) is left to the game.
            if (_needsRestore) RestoreLandscape(_tree.Root);
            return;
        }
        _needsRestore = true;
        float canvasHeight = Math.Max(_tree.Root.GetVisibleRect().Size.Y, SafeAreaCanvas.MinPortraitHeight);
        float capHeight = ComputeCapHeight(_tree.Root);

        foreach (Node node in Traverse(_tree.Root))
        {
            if (node is NMainMenu mainMenu) LayoutMainMenuPortrait(mainMenu, capHeight);
            else if (node is NSettingsScreen settings) LayoutSettingsPortrait(settings, capHeight);
            else if (node is Control control)
            {
                string? typeName = control.GetType().FullName;
                if (typeName == TopBarTypeName)
                {
                    _runTopBarId = control.GetInstanceId();
                    LayoutTopBarPortrait(control, capHeight);
                }
                else if (typeName == RelicInventoryTypeName && control is FlowContainer flow)
                {
                    _runRelicsId = flow.GetInstanceId();
                    LayoutRelicInventoryPortrait(flow, capHeight);
                }
                else if (typeName == EarlyAccessDisclaimerTypeName) LayoutEarlyAccessPortrait(control);
                else if (typeName == PortraitCombatLayout.CombatRoomTypeName) _combat.ApplyRoomImmediate(control, capHeight, canvasHeight);
                else if (typeName != null && PortraitBetweenLayout.WatchedTypeNames.Contains(typeName))
                    _between.ApplyNode(control, typeName, capHeight, canvasHeight, _tree.Root.GetVisibleRect().Size.X);
                else if (typeName != null && PortraitScreenFit.Handles(typeName))
                    _fit.ApplyNode(control, typeName, FitBandTop(typeName, capHeight), _tree.Root.GetVisibleRect().Size);
                else if (typeName != null && PortraitMenuLayout.WatchedTypeNames.Contains(typeName))
                    _menus.ApplyNode(control, typeName, capHeight, _tree.Root.GetVisibleRect().Size);
                else if (typeName != null && PortraitOverlayLayout.WatchedTypeNames.Contains(typeName))
                    _overlays.ApplyNode(control, typeName, capHeight, _tree.Root.GetVisibleRect().Size);
            }
        }
    }

    private void RestoreLandscape(Node root)
    {
        _combat.RestoreLandscape(root);
        _between.RestoreLandscape();
        _fit.RestoreLandscape();
        _menus.RestoreLandscape();
        _overlays.RestoreLandscape();

        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
        {
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
        }

        foreach (Node node in Traverse(root))
        {
            if (node is NMainMenu mainMenu)
            {
                if (mainMenu.GetNodeOrNull<Control>("%MainMenuBg/BgContainer") is { } bg)
                    bg.Scale = Vector2.One;
                if (mainMenu.GetNodeOrNull<Control>("%ContinueRunInfo") is { } runInfo &&
                    _snapshots.TryGetValue(runInfo.GetInstanceId(), out PortraitControlSnapshot? infoSnap))
                    SetRunInfoHome(runInfo, new Vector2(infoSnap.InitialOffsetLeft, infoSnap.InitialOffsetTop));
                foreach (string name in new[] { "ChangeProfileButton", "PatchNotesButton" })
                    if (mainMenu.GetNodeOrNull<Control>(name) is { } topControl) PhoneHitArea.Remove(topControl);
            }
            else if (node is NSettingsScreen settings)
            {
                RemoveSettingsTouchAreas(settings);
                RefreshSettingsPanels(settings);
            }
            else if (node is Control control)
            {
                string? typeName = control.GetType().FullName;
                if (typeName == TopBarTypeName)
                {
                    if (control.GetNodeOrNull<CanvasItem>(SafeCapName) is { } cap) cap.Visible = false;
                    if (control.GetNodeOrNull<CanvasItem>("LeftAlignedStuff/" + ExpandSpacerName) is { } spacer) spacer.Visible = false;
                    if (control.GetNodeOrNull<Node>("%TimerContainer") is { } timer && timer.HasMethod("RefreshVisibility"))
                        timer.Call("RefreshVisibility");
                    if (control.GetNodeOrNull<Control>(PotionRowName) is { } potionRow &&
                        control.GetNodeOrNull<Control>("LeftAlignedStuff") is { } leftBox)
                    {
                        // Back into the top bar's row, after the gold, where the game's scene has it.
                        potionRow.Reparent(leftBox, keepGlobalTransform: false);
                        int after = leftBox.GetNodeOrNull<Node>("TopBarGold")?.GetIndex() ?? 2;
                        leftBox.MoveChild(potionRow, Math.Min(after + 1, leftBox.GetChildCount() - 1));
                    }
                    if (control.GetNodeOrNull<Control>("%TopBarPortraitTip") is { } portraitTip)
                        PhoneHitArea.Remove(portraitTip);
                    RestorePotionHoldersLandscape(control);
                }
                else if (typeName == RelicInventoryTypeName && control is FlowContainer inventory)
                {
                    RestoreRelicInventoryLandscape(inventory);
                }
            }
        }

        foreach ((ulong id, (bool hadOverride, int size)) in _fontSizes)
        {
            if (GodotObject.InstanceFromId(id) is not Control label || !GodotObject.IsInstanceValid(label)) continue;
            if (hadOverride) label.AddThemeFontSizeOverride("font_size", size);
            else label.RemoveThemeFontSizeOverride("font_size");
        }
        _fontSizes.Clear();
    }

    /// <summary>Sets a label's font size in portrait, keeping the game's own to put back in landscape.</summary>
    private void SetFontSize(Control label, int size)
    {
        ulong id = label.GetInstanceId();
        if (!_fontSizes.ContainsKey(id))
        {
            bool had = label.HasThemeFontSizeOverride("font_size");
            _fontSizes[id] = (had, label.GetThemeFontSize("font_size"));
        }
        label.AddThemeFontSizeOverride("font_size", size);
    }

    /// <summary>The game's own font size of a label portrait may have changed.</summary>
    private int GameFontSize(Control label) =>
        _fontSizes.TryGetValue(label.GetInstanceId(), out (bool HadOverride, int Size) font) ? font.Size : label.GetThemeFontSize("font_size");

    private void LayoutMainMenuPortrait(NMainMenu mainMenu, float capHeight)
    {
        if (mainMenu.GetNodeOrNull<Control>("%MainMenuBg/BgContainer") is { } bgContainer)
        {
            Capture(bgContainer, captureVisible: false);
            const float bgScale = 2.05f;
            bgContainer.Scale = new Vector2(bgScale, bgScale);
            if (bgContainer.GetNodeOrNull<Control>("Control") is { } logoHolder)
            {
                Capture(logoHolder, captureVisible: false);
                logoHolder.Scale = new Vector2(1f / bgScale, 1f / bgScale);
                logoHolder.OffsetLeft = 98.5f;
                logoHolder.OffsetRight = 98.5f;
                logoHolder.OffsetTop = -226f;
                logoHolder.OffsetBottom = -226f;
            }
        }

        if (mainMenu.GetNodeOrNull<Control>("MainMenuTextButtons") is { } buttons)
        {
            Capture(buttons, captureVisible: false);
            buttons.AnchorLeft = 0.5f;
            buttons.AnchorRight = 0.5f;
            buttons.AnchorTop = 0.5f;
            buttons.AnchorBottom = 0.5f;
            buttons.OffsetLeft = -MenuButtonWidth / 2f;
            buttons.OffsetRight = MenuButtonWidth / 2f;
            buttons.OffsetTop = MenuButtonsTop;
            buttons.OffsetBottom = MenuButtonsBottom;
            // The main menu's buttons were tiny: each entry is a 44 point row across the middle of the
            // screen with its text at about 17 points (the game's 32 is 11 points on a 375 point wide phone).
            foreach (Control button in buttons.GetChildren().OfType<Control>())
            {
                Capture(button, captureVisible: false);
                button.CustomMinimumSize = new Vector2(MenuButtonWidth, PortraitBands.MinTarget);
                if (button.GetChildCount() > 0 && button.GetChild(0) is Label label) EnlargeMenuLabel(label);
            }
        }

        // The run's details show beside Continue while it is hovered; beside a row this wide they would run past the right
        // edge, so in portrait they show centred above it, between the logo and the buttons.
        if (mainMenu.GetNodeOrNull<Control>("%ContinueRunInfo") is { } runInfo)
        {
            Capture(runInfo, captureVisible: false);
            float width = runInfo.Size.X > 0f ? runInfo.Size.X : runInfo.OffsetRight - runInfo.OffsetLeft;
            float height = runInfo.Size.Y > 0f ? runInfo.Size.Y : runInfo.OffsetBottom - runInfo.OffsetTop;
            SetRunInfoHome(runInfo, new Vector2((MenuButtonWidth - width) / 2f, -height - 16f));
        }

        foreach (string topControlName in new[] { "ChangeProfileButton", "PatchNotesButton", "ReleaseInfo" })
        {
            if (mainMenu.GetNodeOrNull<Control>(topControlName) is { } topControl)
            {
                PortraitControlSnapshot snap = Capture(topControl, captureVisible: false);
                topControl.OffsetTop = snap.InitialOffsetTop + capHeight;
                topControl.OffsetBottom = snap.InitialOffsetBottom + capHeight;
                if (topControlName != "ReleaseInfo") GrowToMinTarget(topControl);
            }
        }
    }

    private void EnlargeMenuLabel(Label label)
    {
        ulong id = label.GetInstanceId();
        SetFontSize(label, MenuFontSize);
        // The game turns the label around its centre on hover and sets that centre once, for its own font size: keep it the
        // centre as the label grows and shrinks (the same value the game gives it, so landscape is unchanged).
        if (_wiredMenuLabels.Add(id))
            label.Resized += () =>
            {
                if (GodotObject.IsInstanceValid(label)) label.PivotOffset = label.Size * 0.5f;
            };
        label.PivotOffset = label.Size * 0.5f;
    }

    /// <summary>Grows a control's touch area to 44 points each way around its centre, when its picture is smaller.</summary>
    private static void GrowToMinTarget(Control control)
    {
        Vector2 size = control.Size;
        if (size.X <= 0f || size.Y <= 0f) return;
        float padX = Math.Max(0f, (PortraitBands.MinTarget - size.X) / 2f);
        float padY = Math.Max(0f, (PortraitBands.MinTarget - size.Y) / 2f);
        if (padX > 0f || padY > 0f) PhoneHitArea.Grow(control, padX, padY, padX, padY);
    }

    /// <summary>
    /// Places the continue button's run details and the spot their show and hide animation starts from (the game keeps it
    /// in <c>_initPosition</c>, taken once when the node is ready).
    /// </summary>
    private static void SetRunInfoHome(Control runInfo, Vector2 position)
    {
        runInfo.GetType().GetField("_initPosition", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(runInfo, position);
        float width = runInfo.OffsetRight - runInfo.OffsetLeft;
        float height = runInfo.OffsetBottom - runInfo.OffsetTop;
        runInfo.OffsetLeft = position.X;
        runInfo.OffsetTop = position.Y;
        runInfo.OffsetRight = position.X + width;
        runInfo.OffsetBottom = position.Y + height;
    }

    private void LayoutEarlyAccessPortrait(Control disclaimer)
    {
        if (disclaimer.GetNodeOrNull<Control>("Image") is { } image)
        {
            Capture(image, captureVisible: false);
            image.AnchorLeft = 0.5f;
            image.AnchorRight = 0.5f;
            image.AnchorTop = 0.5f;
            image.AnchorBottom = 0.5f;
            image.OffsetLeft = -364f;
            image.OffsetRight = 364f;
            image.OffsetTop = -415f;
            image.OffsetBottom = 415f;
        }
    }

    /// <summary>
    /// Settings. The tabs sit under the notch (under the top bar, potions and relics in a run, which
    /// stay in view: see <see cref="KeepRunBarsInPlace"/>) and the rows scroll below them, never under them. Each panel but
    /// Input is drawn <see cref="SettingsScale"/> times larger at a width that fits the screen, so its rows, text,
    /// tickboxes, page arrows, sliders and the language list grow together; its small controls get 44 point touch areas.
    /// Input, the keyboard and controller table, keeps the game's size: it already spans the width.
    /// </summary>
    private void LayoutSettingsPortrait(NSettingsScreen settings, float capHeight)
    {
        bool inRun = settings.GetType().GetField("_isInRun", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(settings) is true;
        float tabsTop = inRun ? capHeight + PortraitBands.RunContentTop + 12f : capHeight + 24f;
        if (settings.GetNodeOrNull<Control>("SettingsTabManager") is { } tabManager)
        {
            Capture(tabManager, captureVisible: false);
            tabManager.AnchorLeft = 0.5f;
            tabManager.AnchorRight = 0.5f;
            tabManager.OffsetLeft = -504f;
            tabManager.OffsetRight = 504f;
            tabManager.OffsetTop = tabsTop;
            tabManager.OffsetBottom = tabsTop + SettingsTabHeight;

            foreach (string iconName in new[] { "%LeftTabIcon", "%RightTabIcon" })
            {
                if (settings.GetNodeOrNull<Control>(iconName) is { } icon)
                {
                    Capture(icon, captureVisible: true);
                    icon.Visible = false;
                }
            }

            foreach (string tabName in new[] { "General", "Graphics", "Sound", "Input" })
            {
                if (tabManager.GetNodeOrNull<Control>(tabName) is { } tab)
                {
                    Capture(tab, captureVisible: false);
                    tab.CustomMinimumSize = new Vector2(244f, SettingsTabHeight);
                    float pad = (PortraitBands.MinTarget - SettingsTabHeight) / 2f;
                    PhoneHitArea.Grow(tab, 0f, pad, 0f, pad);
                    if (tab.GetNodeOrNull<Control>("Label") is { } tabLabel) SetFontSize(tabLabel, SettingsTabFontSize);
                }
            }
        }

        float rowsTop = tabsTop + SettingsTabHeight + 16f;
        if (settings.GetNodeOrNull<Control>(SettingsClipperPath) is { } clipper)
        {
            // The scroll area: its top edge is where the rows start, its bottom edge stays clear of the back button.
            Capture(clipper, captureVisible: false);
            clipper.OffsetTop = rowsTop;
            clipper.OffsetBottom = -SettingsBottomGap;

            float panelWidth = SettingsRowsWidth / SettingsScale;
            foreach (Node child in clipper.GetChildren())
            {
                if (child is not NSettingsPanel panel) continue;
                if (IsInputPanel(panel))
                {
                    // Input is wider than the screen (a 1100 unit table): drawn a little smaller so it fits.
                    Capture(panel, captureVisible: false);
                    float inputWidth = Math.Max(panel.Size.X, 1100f);
                    panel.AnchorLeft = 0.5f;
                    panel.AnchorRight = 0.5f;
                    panel.OffsetLeft = SettingsRowsLeft - 540f;
                    panel.OffsetRight = SettingsRowsLeft - 540f + inputWidth;
                    panel.PivotOffset = Vector2.Zero;
                    float fit = SettingsRowsWidth / inputWidth;
                    panel.Scale = new Vector2(fit, fit);
                    continue;
                }
                Capture(panel, captureVisible: false);
                panel.AnchorLeft = 0.5f;
                panel.AnchorRight = 0.5f;
                panel.OffsetLeft = SettingsRowsLeft - 540f;
                panel.OffsetRight = SettingsRowsLeft - 540f + panelWidth;
                panel.PivotOffset = Vector2.Zero;
                panel.Scale = new Vector2(SettingsScale, SettingsScale);
                if (panel.GetNodeOrNull<Control>("VBoxContainer") is { } content)
                {
                    Capture(content, captureVisible: false);
                    content.CustomMinimumSize = new Vector2(panelWidth, 0f);
                }
                GrowSettingsTargets(panel);
            }
        }

        // The mask fades the rows out at the tabs' lower edge, measured from where the tabs are when it is resized.
        if (settings.GetNodeOrNull<Control>("ScrollContainer/Mask") is { } mask && mask.HasMethod("OnResized"))
            mask.Call("OnResized");

        if (settings.GetNodeOrNull<Control>("ScrollContainer/Scrollbar") is { } scrollbar)
        {
            Capture(scrollbar, captureVisible: false);
            scrollbar.AnchorLeft = 0.5f;
            scrollbar.AnchorRight = 0.5f;
            scrollbar.AnchorTop = 0f;
            scrollbar.AnchorBottom = 1f;
            scrollbar.OffsetLeft = SettingsRowsLeft + SettingsRowsWidth - 540f + 4f;
            scrollbar.OffsetRight = scrollbar.OffsetLeft + 48f;
            scrollbar.OffsetTop = rowsTop;
            scrollbar.OffsetBottom = -SettingsBottomGap;
        }

        UpdateSettingsScrollRoom(settings);
    }

    /// <summary>
    /// The game scrolls a panel by its own height, not its drawn one: a spacer at the end of a scaled panel's rows makes the
    /// panel as tall to the scroll as its rows are drawn, so the last row can be scrolled into view.
    /// </summary>
    private static void UpdateSettingsScrollRoom(NSettingsScreen settings)
    {
        if (settings.GetNodeOrNull<Control>(SettingsClipperPath) is not { } clipper) return;
        foreach (Node child in clipper.GetChildren())
        {
            if (child is not NSettingsPanel panel || IsInputPanel(panel) || !panel.IsNodeReady()) continue;
            if (panel.GetNodeOrNull<VBoxContainer>("VBoxContainer") is not { } content) continue;
            Control? spacer = content.GetNodeOrNull<Control>(SettingsSpacerName);
            if (spacer == null)
            {
                spacer = new Control { Name = SettingsSpacerName, MouseFilter = Control.MouseFilterEnum.Ignore, FocusMode = Control.FocusModeEnum.None };
                content.AddChild(spacer);
            }
            content.MoveChild(spacer, content.GetChildCount() - 1);
            spacer.Visible = true;
            float rows = 0f;
            int count = 0;
            foreach (Node row in content.GetChildren())
            {
                if (row == spacer || row is not Control control || !control.Visible) continue;
                rows += control.GetCombinedMinimumSize().Y;
                count++;
            }
            rows += Math.Max(0, count - 1) * content.GetThemeConstant("separation");
            float room = MathF.Ceiling(rows * (SettingsScale - 1f));
            if (Math.Abs(spacer.CustomMinimumSize.Y - room) > 0.5f) spacer.CustomMinimumSize = new Vector2(0f, room);
            panel.Call(NSettingsPanel.MethodName.RefreshSize);
        }
    }

    /// <summary>Gives each of a scaled panel's touch targets a touch area of 44 points as drawn, where its picture is smaller.</summary>
    private static void GrowSettingsTargets(Control panel)
    {
        float min = PortraitBands.MinTarget / SettingsScale;
        foreach (Node node in Traverse(panel))
        {
            if (node is not Control control || !PortraitAudit.IsTouchTarget(control) || PhoneHitArea.IsContainer(control)) continue;
            Vector2 size = control.Size;
            if (size.X <= 0f || size.Y <= 0f) continue;
            float padX = Math.Max(0f, (min - size.X) / 2f);
            float padY = Math.Max(0f, (min - size.Y) / 2f);
            if (padX > 0f || padY > 0f) PhoneHitArea.Grow(control, padX, padY, padX, padY);
        }
    }

    private static void RefreshSettingsPanels(NSettingsScreen settings)
    {
        if (settings.GetNodeOrNull<Control>(SettingsClipperPath) is not { } clipper) return;
        foreach (Node child in clipper.GetChildren())
        {
            if (child is not NSettingsPanel panel) continue;
            if (panel.GetNodeOrNull<Control>("VBoxContainer/" + SettingsSpacerName) is { } spacer) spacer.Visible = false;
            if (panel.IsNodeReady()) panel.Call(NSettingsPanel.MethodName.RefreshSize);
        }
        if (settings.GetNodeOrNull<Control>("ScrollContainer/Mask") is { } mask && mask.HasMethod("OnResized"))
            mask.Call("OnResized");
    }

    private static void RemoveSettingsTouchAreas(NSettingsScreen settings)
    {
        foreach (Node node in Traverse(settings))
            if (node is Control control && control.Name == PhoneHitArea.NodeName && control.GetParent() is Control owner)
                PhoneHitArea.Remove(owner);
    }

    /// <summary>
    /// In a run the game slides the top bar and the relics up out of view while the pause menu, Settings or the compendium is
    /// open (and while a player's details are open in a lobby). The top bar's slide is a desktop bar's height, so in portrait it
    /// stopped in the notch's band over the relics. Portrait keeps both where the fight puts them on
    /// every screen; their buttons close the open screen first, as they do in a fight.
    /// </summary>
    private void KeepRunBarsInPlace()
    {
        if (_runTopBarId != 0 && GodotObject.InstanceFromId(_runTopBarId) is Control topBar && GodotObject.IsInstanceValid(topBar) &&
            topBar.IsInsideTree() && topBar.Position.Y < -0.5f)
        {
            _topBarHideTween ??= topBar.GetType().GetField("_hideTween", BindingFlags.Instance | BindingFlags.NonPublic);
            (_topBarHideTween?.GetValue(topBar) as Tween)?.Kill();
            topBar.Position = new Vector2(topBar.Position.X, 0f);
        }
        if (_runRelicsId != 0 && GodotObject.InstanceFromId(_runRelicsId) is Control relics && GodotObject.IsInstanceValid(relics) &&
            relics.IsInsideTree() && relics.Position.Y < RelicHomeY(relics) - 0.5f)
            relics.GetType().GetMethod("ShowImmediately", Type.EmptyTypes)?.Invoke(relics, null);
    }

    private static float RelicHomeY(Control relics) =>
        relics.GetType().GetField("_originalPos", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(relics) is Vector2 home ? home.Y : relics.Position.Y;

    private static bool IsInputPanel(Control panel) => panel.GetType().Name == "NInputSettingsPanel";

    private void LayoutTopBarPortrait(Control topBar, float capHeight)
    {
        WireTopBarSignals(topBar);

        var safeCap = topBar.GetNodeOrNull<ColorRect>(SafeCapName);
        if (safeCap == null)
        {
            safeCap = new ColorRect
            {
                Name = SafeCapName,
                Color = SafeCapColor,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            topBar.AddChild(safeCap);
            topBar.MoveChild(safeCap, 0);
        }
        safeCap.AnchorLeft = 0f;
        safeCap.AnchorRight = 1f;
        safeCap.AnchorTop = 0f;
        safeCap.AnchorBottom = 0f;
        safeCap.OffsetLeft = 0f;
        safeCap.OffsetRight = 0f;
        safeCap.OffsetTop = -60f;
        safeCap.OffsetBottom = capHeight + 4f;
        safeCap.Visible = true;

        if (topBar.GetNodeOrNull<Control>("%BgImage") is { } bgImage)
        {
            Capture(bgImage, captureVisible: false);
            // Keep desktop OffsetLeft=-1280 and OffsetRight=1280 so top_bar.tres keeps its 1:1 horizontal stone grain.
            bgImage.OffsetTop = capHeight - 12f;
            bgImage.OffsetBottom = capHeight + TopBarRowHeight + 12f;
        }

        // The row's boxes start above the row, in the safe cap, so what they hold is 44 points tall to a finger. Each
        // picture is kept in the row itself by a top margin or by sitting at the bottom of its box.
        float rowTop = capHeight + TopBarRowInset - TopBarTouchRise;
        float rowBottom = capHeight + TopBarRowHeight;
        int rise = (int)TopBarTouchRise;
        float rightWidth = 3f * TopBarButtonWidth;

        if (topBar.GetNodeOrNull<HBoxContainer>("RightAlignedStuff") is { } rightBox)
        {
            Capture(rightBox, captureVisible: false);
            rightBox.AnchorLeft = 1f;
            rightBox.AnchorRight = 1f;
            rightBox.OffsetLeft = -(rightWidth + 10f);
            rightBox.OffsetRight = -10f;
            rightBox.OffsetTop = rowTop;
            rightBox.OffsetBottom = rowBottom;
            rightBox.AddThemeConstantOverride("separation", 0);

            foreach (string hiddenName in new[] { "Padding", "SaveIndicator", "TimerContainer" })
            {
                if (rightBox.GetNodeOrNull<Control>(hiddenName) is { } hidden)
                {
                    Capture(hidden, captureVisible: true);
                    hidden.Visible = false;
                }
            }

            foreach (string btnName in new[] { "%Map", "%Deck", "%PauseButton" })
            {
                if (topBar.GetNodeOrNull<Control>(btnName) is not { } btn) continue;
                Capture(btn, captureVisible: false);
                btn.CustomMinimumSize = new Vector2(TopBarButtonWidth, 80f);
                if (btn.IsClass("MarginContainer"))
                {
                    // Map and settings are margin containers to the engine (their script class derives from a plain
                    // control, so a C# type test would miss them): the picture fills the box inside the margins. The box
                    // stays 64 units wide, as the game has it: the game turns and scales the picture around a pivot fixed
                    // for that width, so a wider box swings the picture off its place (the gear spins while it is touched).
                    int spare = (int)TopBarButtonWidth - 64;
                    btn.AddThemeConstantOverride("margin_left", spare / 2);
                    btn.AddThemeConstantOverride("margin_right", spare - spare / 2);
                    btn.AddThemeConstantOverride("margin_top", 8 + rise);
                    btn.AddThemeConstantOverride("margin_bottom", 8);
                }
                else if (btn.GetNodeOrNull<Control>("Control") is { } pictureBox)
                {
                    // Deck: the picture is centred in a box that fills the button.
                    Capture(pictureBox, captureVisible: false);
                    pictureBox.OffsetTop = rise;
                    if (btn.GetNodeOrNull<Control>("DeckCardCount") is { } deckCount)
                    {
                        // The deck's card count is under 10 points. By its font, not its scale: the game animates
                        // the label's scale whenever the count changes, which would put a scale of ours back to 1.
                        SetFontSize(deckCount, (int)MathF.Round(GameFontSize(deckCount) * 1.2f));
                    }
                }
            }
        }

        if (topBar.GetNodeOrNull<HBoxContainer>("LeftAlignedStuff") is { } leftBox)
        {
            Capture(leftBox, captureVisible: false);
            leftBox.AnchorLeft = 0f;
            leftBox.AnchorRight = 1f;
            leftBox.OffsetLeft = 10f;
            leftBox.OffsetRight = -(rightWidth + 18f);
            leftBox.OffsetTop = rowTop;
            leftBox.OffsetBottom = rowBottom;
            leftBox.AddThemeConstantOverride("separation", 8);

            if (leftBox.GetNodeOrNull<Control>("PortraitContainer") is { } portraitContainer)
            {
                Capture(portraitContainer, captureVisible: false);
                portraitContainer.AddThemeConstantOverride("margin_left", 2);
                portraitContainer.AddThemeConstantOverride("margin_top", 4 + rise);
                // Room on the right for the character tip's touch area (below).
                portraitContainer.AddThemeConstantOverride("margin_right", 38);
                portraitContainer.AddThemeConstantOverride("margin_bottom", 4);
            }

            if (topBar.GetNodeOrNull<Control>("%TopBarPortraitTip") is { } portraitTip)
            {
                Capture(portraitTip, captureVisible: false);
                portraitTip.CustomMinimumSize = new Vector2(72f, 72f);
                PhoneHitArea.Grow(portraitTip, 2f, 4f + TopBarTouchRise, 36f, 4f);
                if (topBar.GetNodeOrNull<Control>("%AscensionIcon") is { } ascensionIcon)
                {
                    // The ascension number is 7 points: as large as the badge can be without covering the character.
                    Capture(ascensionIcon, captureVisible: false);
                    ascensionIcon.PivotOffset = ascensionIcon.Size;
                    ascensionIcon.Scale = new Vector2(1.3f, 1.3f);
                }
            }

            if (topBar.GetNodeOrNull<Control>("%TopBarHp") is { } hpBox)
            {
                Capture(hpBox, captureVisible: false);
                hpBox.AddThemeConstantOverride("separation", 2);
                if (hpBox.GetNodeOrNull<Control>("HpIcon") is { } hpIcon)
                {
                    Capture(hpIcon, captureVisible: false);
                    hpIcon.CustomMinimumSize = new Vector2(TopBarIconWidth, 80f);
                    hpIcon.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
                }
                if (topBar.GetNodeOrNull<Control>("%HpLabel") is { } hpLabel)
                {
                    Capture(hpLabel, captureVisible: false);
                    hpLabel.CustomMinimumSize = new Vector2(0f, 80f);
                    hpLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
                    SetFontSize(hpLabel, TopBarNumberFontSize);
                }
            }

            if (topBar.GetNodeOrNull<Control>("%TopBarGold") is { } goldBox)
            {
                Capture(goldBox, captureVisible: false);
                goldBox.AddThemeConstantOverride("separation", 2);
                goldBox.CustomMinimumSize = new Vector2(PortraitBands.MinTarget, 80f);
                if (goldBox.GetNodeOrNull<Control>("Spacer") is { } spacer)
                {
                    Capture(spacer, captureVisible: true);
                    spacer.Visible = false;
                }
                if (goldBox.GetNodeOrNull<Control>("GoldIcon") is { } goldIcon)
                {
                    Capture(goldIcon, captureVisible: false);
                    goldIcon.CustomMinimumSize = new Vector2(TopBarIconWidth, 80f);
                    goldIcon.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
                }
                if (topBar.GetNodeOrNull<Control>("%GoldLabel") is { } goldLabel)
                {
                    Capture(goldLabel, captureVisible: false);
                    goldLabel.CustomMinimumSize = new Vector2(0f, 80f);
                    goldLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
                    SetFontSize(goldLabel, TopBarNumberFontSize);
                }
            }

            // The potion tray leaves the row for its own row under it (see LayoutPotionRowPortrait).
            Control? potionRow = topBar.GetNodeOrNull<Control>(PotionRowName) ?? leftBox.GetNodeOrNull<Control>(PotionRowName);
            if (potionRow != null)
            {
                Capture(potionRow, captureVisible: false);
                if (potionRow.GetParent() != topBar)
                    potionRow.Reparent(topBar, keepGlobalTransform: false);
                potionRow.AddThemeConstantOverride("margin_top", 0);
                potionRow.AddThemeConstantOverride("margin_bottom", 0);
            }

            if (topBar.GetNodeOrNull<Control>("%PotionContainer") is { } potionContainer)
            {
                Capture(potionContainer, captureVisible: false);
                potionContainer.CustomMinimumSize = new Vector2(0f, 72f);
                potionContainer.AddThemeConstantOverride("margin_top", 2);
                potionContainer.AddThemeConstantOverride("margin_bottom", 2);
            }

            if (topBar.GetNodeOrNull<Control>("%PotionContainer/MarginContainer") is { } trayMargin)
            {
                Capture(trayMargin, captureVisible: false);
                trayMargin.AddThemeConstantOverride("margin_left", 10);
                trayMargin.AddThemeConstantOverride("margin_right", 10);
                trayMargin.AddThemeConstantOverride("margin_top", 2);
                trayMargin.AddThemeConstantOverride("margin_bottom", 2);
            }

            RefreshPotionHoldersPortrait(topBar);

            if (potionRow != null)
            {
                potionRow.AnchorLeft = 0f;
                potionRow.AnchorRight = 0f;
                potionRow.AnchorTop = 0f;
                potionRow.AnchorBottom = 0f;
                potionRow.Position = new Vector2(10f, capHeight + TopBarRowHeight + 2f);
                // A control outside a container never shrinks by itself: after a slot is removed, take the row's own size.
                potionRow.ResetSize();
            }

            if (topBar.GetNodeOrNull<Control>("%RoomIcons") is { } roomIcons)
            {
                var expandSpacer = leftBox.GetNodeOrNull<Control>(ExpandSpacerName);
                if (expandSpacer == null)
                {
                    expandSpacer = new Control
                    {
                        Name = ExpandSpacerName,
                        MouseFilter = Control.MouseFilterEnum.Ignore,
                        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    };
                    leftBox.AddChild(expandSpacer);
                }
                expandSpacer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                expandSpacer.Visible = true;
                int roomIndex = roomIcons.GetIndex();
                if (expandSpacer.GetIndex() != roomIndex - 1)
                    leftBox.MoveChild(expandSpacer, roomIndex);

                Capture(roomIcons, captureVisible: false);
                roomIcons.AddThemeConstantOverride("separation", 4);

                if (roomIcons.GetNodeOrNull<Control>("RoomIconResizer") is { } roomIconResizer)
                {
                    Capture(roomIconResizer, captureVisible: true);
                    roomIconResizer.Visible = false;
                }
                if (topBar.GetNodeOrNull<Control>("%BossIcon") is { } bossIcon)
                {
                    // The game shows and hides this icon itself on every room, so it is left to the game. At a double boss it
                    // hangs the second icon behind the first, reaching past the icon's own box, which at the end of this row
                    // put it under the map button: give the box that room.
                    Capture(bossIcon, captureVisible: false);
                    bossIcon.AddThemeConstantOverride("margin_left", 12);
                    bossIcon.AddThemeConstantOverride("margin_right", BossIconMarginRight + 12 + (int)MathF.Ceiling(SecondBossIconOverhang(bossIcon)));
                    bossIcon.AddThemeConstantOverride("margin_top", -4 + rise);
                }
                if (topBar.GetNodeOrNull<Control>("%FloorIcon") is { } floorIcon)
                {
                    Capture(floorIcon, captureVisible: false);
                    floorIcon.AddThemeConstantOverride("separation", 2);
                    floorIcon.CustomMinimumSize = new Vector2(96f, 0f);
                    if (floorIcon.GetNodeOrNull<Control>("FloorIconPositioner") is { } floorPicture)
                    {
                        Capture(floorPicture, captureVisible: false);
                        floorPicture.AddThemeConstantOverride("margin_top", -3 + rise);
                    }
                    if (topBar.GetNodeOrNull<Control>("%FloorNumLabel") is { } floorLabel)
                    {
                        Capture(floorLabel, captureVisible: false);
                        floorLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
                        SetFontSize(floorLabel, TopBarNumberFontSize);
                    }
                }
            }

            if (topBar.GetNodeOrNull<Control>("%Modifiers") is { } modifiers)
            {
                Capture(modifiers, captureVisible: true);
                modifiers.Visible = false;
            }
        }

        if (topBar.GetNodeOrNull<Control>("GoldPopup") is { } goldPopup)
        {
            PortraitControlSnapshot snap = Capture(goldPopup, captureVisible: false);
            goldPopup.OffsetTop = snap.InitialOffsetTop + capHeight;
            goldPopup.OffsetBottom = snap.InitialOffsetBottom + capHeight;
        }
    }

    private void WireTopBarSignals(Control topBar)
    {
        ulong id = topBar.GetInstanceId();
        if (!_wiredTopBars.Add(id)) return;
        if (topBar.GetNodeOrNull<Control>("%PotionContainer/MarginContainer/PotionHolders") is { } holdersBox)
            holdersBox.ChildOrderChanged += QueueApply;
        if (topBar.GetNodeOrNull<Control>("%TimerContainer") is { } timer)
        {
            timer.VisibilityChanged += () =>
            {
                if (SafeAreaCanvas.IsPortrait && timer.Visible)
                    timer.Visible = false;
            };
        }
        if (topBar.GetNodeOrNull<Control>("%BossIcon/Icon") is { } bossTexture)
        {
            // The second boss icon is added when an act with two bosses starts, and hidden again at the first of them.
            foreach (CanvasItem existing in bossTexture.GetChildren().OfType<CanvasItem>())
                existing.VisibilityChanged += QueueApply;
            bossTexture.ChildEnteredTree += child =>
            {
                if (child is CanvasItem item) item.VisibilityChanged += QueueApply;
                QueueApply();
            };
        }
    }

    /// <summary>How far a shown second boss icon reaches past the right edge of the first one, zero when there is none.</summary>
    private static float SecondBossIconOverhang(Control bossIcon)
    {
        if (bossIcon.GetNodeOrNull<Control>("Icon") is not { } first) return 0f;
        float overhang = 0f;
        foreach (Control child in first.GetChildren().OfType<Control>())
        {
            if (child.Name.ToString() == "Outline" || !child.Visible || child.IsQueuedForDeletion()) continue;
            overhang = Math.Max(overhang, child.Position.X + child.Size.X * child.Scale.X - first.Size.X);
        }
        return overhang;
    }

    private void RefreshPotionHoldersPortrait(Control topBar)
    {
        if (topBar.GetNodeOrNull<HBoxContainer>("%PotionContainer/MarginContainer/PotionHolders") is not { } holdersBox) return;
        Capture(holdersBox, captureVisible: false);

        var holders = new List<Control>();
        foreach (Node child in holdersBox.GetChildren())
        {
            if (child is Control holder && !holder.IsQueuedForDeletion())
                holders.Add(holder);
        }

        float holderSize = PotionSlotSize;
        holdersBox.AddThemeConstantOverride("separation", 4);

        foreach (Control holder in holders)
        {
            Capture(holder, captureVisible: false);
            holder.CustomMinimumSize = new Vector2(holderSize, holderSize);
            holder.Size = new Vector2(holderSize, holderSize);
            WirePotionHolder(holder);
            if (holder.GetNodeOrNull<Control>("%EmptyIcon") is { } emptyIcon)
            {
                // The game scales the empty slot's picture and the potion by one number, so give them one size: the
                // potion is 60 units centred on the slot, and the empty picture fills the slot unless it is told otherwise.
                Capture(emptyIcon, captureVisible: false);
                emptyIcon.AnchorLeft = emptyIcon.AnchorRight = emptyIcon.AnchorTop = emptyIcon.AnchorBottom = 0.5f;
                emptyIcon.OffsetLeft = emptyIcon.OffsetTop = -30f;
                emptyIcon.OffsetRight = emptyIcon.OffsetBottom = 30f;
            }
            ScaleChildPotions(holder, PotionPictureScale / 0.9f);
        }
    }

    private void WirePotionHolder(Control holder)
    {
        ulong id = holder.GetInstanceId();
        if (!_wiredPotionHolders.Add(id)) return;
        holder.ChildOrderChanged += QueueApply;
    }

    private void RestorePotionHoldersLandscape(Control topBar)
    {
        if (topBar.GetNodeOrNull<HBoxContainer>("%PotionContainer/MarginContainer/PotionHolders") is not { } holdersBox) return;
        foreach (Node child in holdersBox.GetChildren())
        {
            if (child is not Control holder) continue;
            holder.CustomMinimumSize = new Vector2(60f, 60f);
            ScaleChildPotions(holder, 1f);
        }
    }

    private static void ScaleChildPotions(Control holder, float scale)
    {
        Vector2 potionScale = new(0.9f * scale, 0.9f * scale);
        FieldInfo? scaleField = holder.GetType().GetField("_potionScale", BindingFlags.Instance | BindingFlags.NonPublic);
        scaleField?.SetValue(holder, potionScale);

        if (holder.GetNodeOrNull<Control>("%EmptyIcon") is { } emptyIcon)
        {
            emptyIcon.PivotOffset = new Vector2(30f, 30f);
            emptyIcon.Scale = potionScale;
        }

        foreach (Node child in holder.GetChildren())
        {
            if (child is Control control && control.GetType().FullName == PotionTypeName)
            {
                control.PivotOffset = new Vector2(30f, 30f);
                control.Scale = potionScale;
            }
        }
    }

    private void LayoutRelicInventoryPortrait(FlowContainer inventory, float capHeight)
    {
        WireRelicInventorySignals(inventory);
        Capture(inventory, captureVisible: false);
        SaveRelicOriginalPos(inventory);

        float topY = capHeight + PortraitBands.TopBarHeight + 14f;
        inventory.AnchorLeft = 0f;
        inventory.AnchorRight = 1f;
        inventory.AnchorTop = 0f;
        inventory.AnchorBottom = 0f;
        inventory.OffsetLeft = 20f;
        inventory.OffsetRight = -20f;
        inventory.OffsetTop = topY;
        inventory.OffsetBottom = topY + 2f * PortraitRelicSize + 16f;
        inventory.AddThemeConstantOverride("h_separation", 8);
        inventory.AddThemeConstantOverride("v_separation", 8);

        var portraitOrigin = new Vector2(20f, topY);
        SetRelicOriginalPos(inventory, portraitOrigin);
        if (inventory.Position.Y >= 0f)
            inventory.Position = portraitOrigin;

        RefreshRelicPagingPortrait(inventory);
    }

    private void WireRelicInventorySignals(FlowContainer inventory)
    {
        ulong id = inventory.GetInstanceId();
        if (!_wiredInventories.Add(id)) return;
        inventory.ChildOrderChanged += QueueApply;
        if (inventory.HasSignal("RelicsChanged"))
            inventory.Connect("RelicsChanged", Callable.From(QueueApply));
    }

    private void RefreshRelicPagingPortrait(FlowContainer inventory)
    {
        var holders = GetRelicHolders(inventory);
        float scale = PortraitRelicSize / DesktopRelicSize;

        foreach (Control holder in holders)
        {
            holder.CustomMinimumSize = new Vector2(PortraitRelicSize, PortraitRelicSize);
            if ((holder.GetNodeOrNull<Control>("%Relic") ?? holder.GetNodeOrNull<Control>("Relic")) is { } relicNode)
            {
                relicNode.PivotOffset = Vector2.Zero;
                relicNode.Scale = new Vector2(scale, scale);
            }
        }

        ulong invId = inventory.GetInstanceId();
        if (holders.Count <= PortraitRelicsPerPage)
        {
            _relicPages.Remove(invId);
            foreach (Control holder in holders) holder.Visible = true;
            if (inventory.GetNodeOrNull<CanvasItem>(RelicPagerName) is { } existingPager)
                existingPager.Visible = false;
            return;
        }

        int pageCount = (holders.Count + PortraitRelicsPerPage - 1) / PortraitRelicsPerPage;
        int page = _relicPages.TryGetValue(invId, out int storedPage) ? Math.Clamp(storedPage, 0, pageCount - 1) : 0;
        _relicPages[invId] = page;

        int start = page * PortraitRelicsPerPage;
        int end = Math.Min(start + PortraitRelicsPerPage, holders.Count);
        for (int i = 0; i < holders.Count; i++)
            holders[i].Visible = i >= start && i < end;

        Button pager = EnsureRelicPager(inventory);
        var label = pager.GetNode<Label>("PagerLabel");
        label.Text = page == 0
            ? $"+{holders.Count - PortraitRelicsPerPage} ({holders.Count})"
            : $"{start + 1}-{end} ({holders.Count})";
        pager.Visible = true;
        int lastIndex = inventory.GetChildCount() - 1;
        if (pager.GetIndex() != lastIndex)
            inventory.MoveChild(pager, lastIndex);
    }

    private Button EnsureRelicPager(FlowContainer inventory)
    {
        if (inventory.GetNodeOrNull<Button>(RelicPagerName) is { } existing)
            return existing;

        var style = new StyleBoxFlat
        {
            BgColor = PagerFillColor,
            BorderColor = PagerBorderColor,
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 36,
            CornerRadiusTopRight = 36,
            CornerRadiusBottomLeft = 36,
            CornerRadiusBottomRight = 36,
        };

        var pager = new Button
        {
            Name = RelicPagerName,
            CustomMinimumSize = new Vector2(164f, PortraitRelicSize),
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        foreach (string state in new[] { "normal", "hover", "pressed", "focus", "disabled" })
            pager.AddThemeStyleboxOverride(state, style);

        var label = new Label
        {
            Name = "PagerLabel",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        label.AddThemeColorOverride("font_color", PagerTextColor);
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.65f));
        label.AddThemeConstantOverride("outline_size", 8);
        label.AddThemeFontSizeOverride("font_size", 26);
        if (ResourceLoader.Exists("res://themes/kreon_bold_glyph_space_one.tres") &&
            ResourceLoader.Load<Font>("res://themes/kreon_bold_glyph_space_one.tres") is { } kreon)
        {
            label.AddThemeFontOverride("font", kreon);
        }
        pager.AddChild(label);

        ulong invId = inventory.GetInstanceId();
        pager.Pressed += () =>
        {
            if (GodotObject.InstanceFromId(invId) is not FlowContainer inv || !GodotObject.IsInstanceValid(inv)) return;
            int total = GetRelicHolders(inv).Count;
            if (total <= PortraitRelicsPerPage) return;
            int pages = (total + PortraitRelicsPerPage - 1) / PortraitRelicsPerPage;
            int current = _relicPages.TryGetValue(invId, out int p) ? p : 0;
            _relicPages[invId] = (current + 1) % pages;
            RefreshRelicPagingPortrait(inv);
        };

        inventory.AddChild(pager);
        return pager;
    }

    private void RestoreRelicInventoryLandscape(FlowContainer inventory)
    {
        ulong invId = inventory.GetInstanceId();
        if (_relicOriginalPos.TryGetValue(invId, out Vector2 origPos))
        {
            SetRelicOriginalPos(inventory, origPos);
            if (inventory.Position.Y >= 0f)
                inventory.Position = origPos;
        }

        foreach (Control holder in GetRelicHolders(inventory))
        {
            holder.CustomMinimumSize = new Vector2(DesktopRelicSize, DesktopRelicSize);
            holder.Visible = true;
            if ((holder.GetNodeOrNull<Control>("%Relic") ?? holder.GetNodeOrNull<Control>("Relic")) is { } relicNode)
            {
                relicNode.PivotOffset = Vector2.Zero;
                relicNode.Scale = Vector2.One;
            }
        }

        if (inventory.GetNodeOrNull<CanvasItem>(RelicPagerName) is { } pager)
            pager.Visible = false;
    }

    private static List<Control> GetRelicHolders(FlowContainer inventory)
    {
        var list = new List<Control>();
        foreach (Node child in inventory.GetChildren())
        {
            if (child is Control control && !control.IsQueuedForDeletion() && control.Name != RelicPagerName)
                list.Add(control);
        }
        return list;
    }

    private void SaveRelicOriginalPos(FlowContainer inventory)
    {
        ulong id = inventory.GetInstanceId();
        if (_relicOriginalPos.ContainsKey(id)) return;
        FieldInfo? field = inventory.GetType().GetField("_originalPos", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(inventory) is Vector2 pos)
            _relicOriginalPos[id] = pos;
    }

    private static void SetRelicOriginalPos(FlowContainer inventory, Vector2 pos)
    {
        FieldInfo? field = inventory.GetType().GetField("_originalPos", BindingFlags.Instance | BindingFlags.NonPublic);
        field?.SetValue(inventory, pos);
    }

    private PortraitControlSnapshot Capture(Control control, bool captureVisible)
    {
        ulong id = control.GetInstanceId();
        if (_snapshots.TryGetValue(id, out PortraitControlSnapshot? existing))
        {
            if (captureVisible && !existing.CaptureVisible)
                existing.RecordInitialVisibility(control.Visible);
            return existing;
        }
        var created = new PortraitControlSnapshot(control, captureVisible);
        _snapshots[id] = created;
        return created;
    }

    private void PruneDeadInstances()
    {
        var dead = new List<ulong>();
        foreach (ulong id in _snapshots.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _snapshots.Remove(id);
        foreach (ulong id in _fontSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _fontSizes.Remove(id);
        _wiredMenuLabels.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));

        dead.Clear();
        foreach (ulong id in _relicOriginalPos.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead)
        {
            _relicOriginalPos.Remove(id);
            _relicPages.Remove(id);
        }
        _combat.PruneDead();
        _between.PruneDead();
        _fit.PruneDead();
        _menus.PruneDead();
        _overlays.PruneDead();
    }

    private static IEnumerable<Node> Traverse(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Node current = stack.Pop();
            yield return current;
            int count = current.GetChildCount();
            for (int i = count - 1; i >= 0; i--)
                stack.Push(current.GetChild(i));
        }
    }
}
