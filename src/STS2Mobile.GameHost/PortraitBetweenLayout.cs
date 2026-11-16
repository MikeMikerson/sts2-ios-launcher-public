using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Portrait layout for the between-fight screens: the Spire Map, spoils and card rewards, card grids (deck, piles and
/// card selection), events, the shop, the rest site and the treasure room. Every edit is captured first so turning Portrait Mode
/// off restores the desktop layout live.
/// </summary>
internal sealed class PortraitBetweenLayout
{
    public const string MapScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen";
    public const string ProceedButtonTypeName = "MegaCrit.Sts2.Core.Nodes.CommonUi.NProceedButton";
    public const string RewardsScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen";
    public const string CardRewardTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen";
    public const string CardGridTypeName = "MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid";
    public const string MerchantInventoryTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory";
    public const string FakeMerchantInventoryTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NFakeMerchantInventory";
    public const string EventLayoutTypeName = "MegaCrit.Sts2.Core.Nodes.Events.NEventLayout";
    public const string MapFtueTypeName = "MegaCrit.Sts2.Core.Nodes.Ftue.NMapSelectFtue";
    public const string AncientLayoutTypeName = "MegaCrit.Sts2.Core.Nodes.Events.NAncientEventLayout";
    public const string BackButtonTypeName = "MegaCrit.Sts2.Core.Nodes.CommonUi.NBackButton";
    public const string EventOptionTypeName = "MegaCrit.Sts2.Core.Nodes.Events.NEventOptionButton";
    public const string RestSiteRoomTypeName = "MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom";
    public const string AncientDialogueLineTypeName = "MegaCrit.Sts2.Core.Nodes.Events.NAncientDialogueLine";

    public static readonly HashSet<string> WatchedTypeNames = new(StringComparer.Ordinal)
    {
        MapScreenTypeName, ProceedButtonTypeName, RewardsScreenTypeName, CardRewardTypeName, CardGridTypeName,
        MerchantInventoryTypeName, FakeMerchantInventoryTypeName, EventLayoutTypeName, MapFtueTypeName, AncientLayoutTypeName,
        BackButtonTypeName, EventOptionTypeName, RestSiteRoomTypeName, AncientDialogueLineTypeName,
    };

    // Card grids in portrait: two cards to a row at 1.8, each card 432 units (150 points) wide. The grid's
    // cards are 240 units with 40 between, so a 540 unit scroll area holds two, 40 units from each side: a selected card grows
    // to 300 and its cost badge still stays inside; the scrollbar sits in the right margin.
    private const float GridScrollInsetLeft = 30f;
    private const float GridScrollInsetRight = 30f;
    private const float GridScrollbarLeft = -38f;
    private const float GridScrollbarRight = -8f;
    // The deck's sort buttons stand in one row above the cards (which start 180 units down the scroll area), 75 units tall.
    private const float SortRowTop = 98f;
    private const float SortRowHeight = 75f;
    private const float SortRowGap = 6f;
    private const float SortButtonPad = 4f;
    private const float AncientContentLift = 150f;
    private const float AncientNextEdgeMargin = 40f;
    // The ancients' dialogue lines are 800 wide in a 1000 wide column; in portrait they take the column's width.
    private const float AncientDialogueWiden = 200f;

    // The shop's slot panel is 1747 wide, but its slots sit between about x 300 and 1600 (panel-local) from y 150.
    private const float ShopContentLeft = 210f;
    private const float ShopContentRight = 1580f;
    private const float ShopContentTop = 150f;
    private const float ShopOpenY = 80f;

    // Event art keeps its subject left of centre on the desktop; this brings it to the middle of the portrait canvas.
    private const float EventArtShiftX = 320f;
    private const float EventArtCentreY = 44f;

    private const string LegendPillName = "PortraitLegendPill";

    // Sizes for a finger. 127 units are 44 points and a font of 32 units is 11 points (PortraitBands).
    private const string HitPadHolderName = PhoneHitArea.HolderName;
    private const float LegendPillWidth = 230f;
    private const int LegendPillFontSize = 34;
    private const float MapPointScale = 1.25f;
    private const float DrawingToolsScale = PortraitBands.MinTarget / 60f;
    private const float RewardsScale = 1.5f;
    private const float RewardsPivotY = 400f;
    private const int EventTextFontSize = 32;
    private const float EventBottomMargin = 60f;
    private const float EventMaxLift = 400f;
    private const float ShopItemPadWidth = 112f;
    private const float ShopItemPadHeight = 108f;
    // A shop price's number on the screen, in canvas units: 32 is 11 points on a 375 point wide phone.
    private const float ShopPriceUnits = 32f;
    private const float RestChoicesMaxScale = 1.35f;
    private const float RestChoicesMinScale = 0.6f;
    private const float RestChoicesPivotY = 80f;
    private static readonly string[] RichFontSizeNames =
        ["normal_font_size", "bold_font_size", "bold_italics_font_size", "italics_font_size", "mono_font_size"];
    private const float CardRowSpacing = 350f;
    private const float RewardCardWidth = 300f;
    private const float RewardCardHeight = 422f;
    private const float RewardMaxCardScale = 1.5f;
    private const float GridScale = 1.8f;
    // The shared banner ("Choose a Card") is 162 units tall, 172 to 334 above the screen's middle; in portrait it rises to just
    // under the relic strip so the cards under it can grow.
    internal const float BannerTopOffset = 334f;
    internal const float BannerBottomOffset = 172f;
    private const float BannerContentGap = 16f;

    private static readonly Color PillFillColor = new(24f / 255f, 37f / 255f, 45f / 255f, 0.94f);
    private static readonly Color PillBorderColor = new(118f / 255f, 146f / 255f, 162f / 255f, 0.9f);
    private static readonly Color PillTextColor = new(239f / 255f, 200f / 255f, 81f / 255f, 1f);

    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private readonly HashSet<ulong> _maps = new();
    private readonly Dictionary<ulong, (ulong First, int Count, float Width, float Shift)> _mapShifts = new();
    private readonly HashSet<ulong> _proceedButtons = new();
    private readonly HashSet<ulong> _cardRewards = new();
    private readonly Dictionary<ulong, List<ulong>> _laidOutRewardHolders = new();
    private readonly HashSet<ulong> _mapFtues = new();
    private readonly HashSet<ulong> _grids = new();
    private readonly Dictionary<ulong, TextureRect.StretchModeEnum> _stretchModes = new();
    private readonly HashSet<ulong> _hitAreaTargets = new();
    private readonly HashSet<ulong> _liftedAncientContents = new();
    private readonly HashSet<ulong> _liftedBanners = new();
    private readonly HashSet<ulong> _shops = new();
    private readonly HashSet<ulong> _fakeShops = new();
    private readonly HashSet<ulong> _hitPadHolders = new();
    private readonly Dictionary<ulong, (ulong First, int Count)> _mapPointLayouts = new();
    private readonly HashSet<ulong> _scaledMapPoints = new();
    private readonly HashSet<ulong> _events = new();
    private readonly HashSet<ulong> _restSites = new();
    private readonly Dictionary<ulong, int> _labelMaxFontSizes = new();
    private readonly Dictionary<ulong, int> _labelMinFontSizes = new();
    private readonly HashSet<ulong> _ancients = new();
    private readonly Dictionary<ulong, (bool Had, int Size)> _labelFontSizes = new();
    private readonly Dictionary<ulong, (bool Had, int Size)[]> _richFontSizes = new();
    private bool _legendOpen;

    // ---- entry points from PortraitLayout -------------------------------------------------------------------------------

    private readonly HashSet<ulong> _loggedNodes = new();

    public void ApplyNode(Control control, string typeName, float capHeight, float canvasHeight, float canvasWidth)
    {
        if (_loggedNodes.Add(control.GetInstanceId()))
            GD.Print($"Portrait between layout: {control.GetType().Name} {control.GetPath()}");
        switch (typeName)
        {
            case MapScreenTypeName:
                LayoutMapPortrait(control, canvasHeight, canvasWidth);
                _maps.Add(control.GetInstanceId());
                break;
            case ProceedButtonTypeName:
                _proceedButtons.Add(control.GetInstanceId());
                EnsureHitPad(control, 0f, PortraitBands.MinTarget);
                break;
            case BackButtonTypeName:
                EnsureHitPad(control, 0f, PortraitBands.MinTarget);
                break;
            case EventOptionTypeName:
                LayoutEventOptionPortrait(control);
                break;
            case AncientDialogueLineTypeName:
                LayoutAncientDialogueLinePortrait(control);
                break;
            case RestSiteRoomTypeName:
                _restSites.Add(control.GetInstanceId());
                break;
            case RewardsScreenTypeName:
                LayoutRewardsPortrait(control);
                break;
            case CardRewardTypeName:
                _cardRewards.Add(control.GetInstanceId());
                break;
            case CardGridTypeName:
                LayoutCardGridPortrait(control, capHeight, canvasHeight, canvasWidth);
                if (control.GetType().FullName == CardGridTypeName) _grids.Add(control.GetInstanceId());
                break;
            case AncientLayoutTypeName:
                LayoutAncientPortrait(control, canvasWidth);
                _ancients.Add(control.GetInstanceId());
                break;
            case MerchantInventoryTypeName:
                _shops.Add(control.GetInstanceId());
                UpdateShopPortrait(control, capHeight, new Vector2(canvasWidth, canvasHeight));
                break;
            case FakeMerchantInventoryTypeName:
                LayoutMerchantInventoryPortrait(control, capHeight, canvasWidth);
                _fakeShops.Add(control.GetInstanceId());
                break;
            case EventLayoutTypeName:
                LayoutEventPortrait(control, capHeight, canvasHeight, canvasWidth);
                break;
            case MapFtueTypeName:
                _mapFtues.Add(control.GetInstanceId());
                break;
        }
    }

    /// <summary>
    /// Called as a node is added, before the game sets its screen up: an ancient's option rows and dialogue lines get their
    /// portrait size here, because the game measures them for the column's slide in the frame they are added.
    /// Returns whether it changed anything.
    /// </summary>
    public bool LayoutOnAdd(Control control)
    {
        string? typeName = control.GetType().FullName;
        if (typeName == AncientDialogueLineTypeName)
        {
            LayoutAncientDialogueLinePortrait(control);
            return true;
        }
        if (typeName == EventOptionTypeName && IsInLayout(control, AncientLayoutTypeName))
        {
            LayoutAncientOptionPortrait(control);
            return true;
        }
        return false;
    }

    private static bool IsInLayout(Node node, string layoutTypeName)
    {
        for (Node? parent = node.GetParent(); parent != null; parent = parent.GetParent())
            if (parent.GetType().FullName == layoutTypeName) return true;
        return false;
    }

    public void OnProcessFrame(Window root, float capHeight)
    {
        Vector2 canvas = root.GetVisibleRect().Size;
        foreach (ulong id in _maps)
            if (GodotObject.InstanceFromId(id) is Control map && GodotObject.IsInstanceValid(map) && map.IsVisibleInTree())
                UpdateMapPortrait(map, root, canvas);
        foreach (ulong id in _proceedButtons)
            if (GodotObject.InstanceFromId(id) is Control button && GodotObject.IsInstanceValid(button) && button.IsVisibleInTree())
                UpdateProceedButtonPortrait(button, canvas);
        foreach (ulong id in _cardRewards)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen) && screen.IsInsideTree())
                UpdateCardRewardPortrait(screen, canvas, capHeight);
        foreach (ulong id in _grids)
            if (GodotObject.InstanceFromId(id) is Control grid && GodotObject.IsInstanceValid(grid) && grid.IsVisibleInTree())
                KeepGridColumnsPortrait(grid);
        foreach (ulong id in _mapFtues)
            if (GodotObject.InstanceFromId(id) is Control ftue && GodotObject.IsInstanceValid(ftue) && ftue.IsVisibleInTree())
                KeepMapFtueOnScreen(ftue, canvas);
        foreach (ulong id in _events)
            if (GodotObject.InstanceFromId(id) is Control layout && GodotObject.IsInstanceValid(layout) && layout.IsVisibleInTree())
                KeepEventTextOnScreen(layout, capHeight, canvas);
        foreach (ulong id in _restSites)
            if (GodotObject.InstanceFromId(id) is Control room && GodotObject.IsInstanceValid(room) && room.IsVisibleInTree())
                UpdateRestSitePortrait(room, canvas);
        foreach (ulong id in _ancients)
            if (GodotObject.InstanceFromId(id) is Control ancient && GodotObject.IsInstanceValid(ancient) && ancient.IsVisibleInTree())
                KeepAncientOptionsInside(ancient);
        foreach (ulong id in _shops)
            if (GodotObject.InstanceFromId(id) is Control shop && GodotObject.IsInstanceValid(shop) && shop.IsVisibleInTree())
                UpdateShopPortrait(shop, capHeight, canvas);
        foreach (ulong id in _fakeShops)
            if (GodotObject.InstanceFromId(id) is Control shop && GodotObject.IsInstanceValid(shop) && shop.IsInsideTree() &&
                shop.GetNodeOrNull<Control>("%SlotsContainer") is { } slots)
                KeepClosedShopOutOfSight(shop, slots);
    }

    public void RestoreLandscape()
    {
        foreach (ulong id in _cardRewards)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen))
                RestoreCardRewardLandscape(screen);
        _laidOutRewardHolders.Clear();

        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
        foreach ((ulong id, TextureRect.StretchModeEnum mode) in _stretchModes)
            if (GodotObject.InstanceFromId(id) is TextureRect rect && GodotObject.IsInstanceValid(rect))
                rect.StretchMode = mode;

        foreach (ulong id in _maps)
        {
            if (GodotObject.InstanceFromId(id) is not Control map || !GodotObject.IsInstanceValid(map)) continue;
            if (map.GetNodeOrNull<CanvasItem>(LegendPillName) is { } pill) pill.Visible = false;
            if (map.GetType().GetField("_targetDragPos", BindingFlags.Instance | BindingFlags.NonPublic) is { } targetField &&
                targetField.GetValue(map) is Vector2 target)
                targetField.SetValue(map, new Vector2(0f, target.Y));
            if (map.GetNodeOrNull<Control>("TheMap") is { } theMap)
                theMap.Position = new Vector2(0f, theMap.Position.Y);
            if (map.GetNodeOrNull<Control>("%MapLegend") is { } legend)
            {
                legend.Visible = true;
                legend.Position = new Vector2(map.Size.X * 0.8f, legend.Position.Y);
            }
        }

        foreach (ulong id in _proceedButtons)
            if (GodotObject.InstanceFromId(id) is Control button && GodotObject.IsInstanceValid(button))
                RestoreProceedButtonLandscape(button);

        // The larger hit areas, the map points' size and the larger text go back to the game's own.
        foreach (ulong id in _hitAreaTargets)
            if (GodotObject.InstanceFromId(id) is Control target && GodotObject.IsInstanceValid(target))
                PhoneHitArea.Remove(target);
        _hitAreaTargets.Clear();
        RestoreBanners(_liftedBanners);
        foreach (ulong id in _shops.Concat(_fakeShops))
            if (GodotObject.InstanceFromId(id) is Control shop && GodotObject.IsInstanceValid(shop))
                RestoreShopLandscape(shop);
        foreach (ulong id in _liftedAncientContents)
            if (GodotObject.InstanceFromId(id) is Control content && GodotObject.IsInstanceValid(content))
                content.Position += new Vector2(0f, AncientContentLift);
        _liftedAncientContents.Clear();
        foreach (ulong id in _hitPadHolders)
        {
            if (GodotObject.InstanceFromId(id) is not Control holder || !GodotObject.IsInstanceValid(holder)) continue;
            holder.GetParent()?.RemoveChild(holder);
            holder.QueueFree();
        }
        _hitPadHolders.Clear();
        foreach (ulong id in _scaledMapPoints)
            if (GodotObject.InstanceFromId(id) is Control point && GodotObject.IsInstanceValid(point))
                point.Scale = Vector2.One;
        _scaledMapPoints.Clear();
        _mapPointLayouts.Clear();
        foreach ((ulong id, int size) in _labelMaxFontSizes)
            if (GodotObject.InstanceFromId(id) is Control label && GodotObject.IsInstanceValid(label))
                label.Set("MaxFontSize", size);
        _labelMaxFontSizes.Clear();
        foreach ((ulong id, int size) in _labelMinFontSizes)
            if (GodotObject.InstanceFromId(id) is Control label && GodotObject.IsInstanceValid(label))
                label.Set("MinFontSize", size);
        _labelMinFontSizes.Clear();
        foreach ((ulong id, (bool had, int size)) in _labelFontSizes)
        {
            if (GodotObject.InstanceFromId(id) is not Control label || !GodotObject.IsInstanceValid(label)) continue;
            if (had) label.AddThemeFontSizeOverride("font_size", size);
            else label.RemoveThemeFontSizeOverride("font_size");
        }
        _labelFontSizes.Clear();
        foreach ((ulong id, (bool Had, int Size)[] sizes) in _richFontSizes)
        {
            if (GodotObject.InstanceFromId(id) is not Control label || !GodotObject.IsInstanceValid(label)) continue;
            for (int i = 0; i < RichFontSizeNames.Length; i++)
            {
                if (sizes[i].Had) label.AddThemeFontSizeOverride(RichFontSizeNames[i], sizes[i].Size);
                else label.RemoveThemeFontSizeOverride(RichFontSizeNames[i]);
            }
        }
        _richFontSizes.Clear();
    }

    public void PruneDead()
    {
        foreach (ulong id in _snapshots.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _snapshots.Remove(id);
        _maps.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in _mapShifts.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _mapShifts.Remove(id);
        _proceedButtons.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _cardRewards.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _mapFtues.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _grids.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in _stretchModes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _stretchModes.Remove(id);
        foreach (ulong id in _laidOutRewardHolders.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList())
            _laidOutRewardHolders.Remove(id);
        _hitAreaTargets.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _liftedAncientContents.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _liftedBanners.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _shops.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _fakeShops.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in _shopModes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _shopModes.Remove(id);
        _hitPadHolders.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _scaledMapPoints.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _events.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _restSites.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _ancients.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in _mapPointLayouts.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _mapPointLayouts.Remove(id);
        foreach (ulong id in _labelMaxFontSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _labelMaxFontSizes.Remove(id);
        foreach (ulong id in _labelMinFontSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _labelMinFontSizes.Remove(id);
        foreach (ulong id in _labelFontSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _labelFontSizes.Remove(id);
        foreach (ulong id in _richFontSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _richFontSizes.Remove(id);
    }

    // ---- Spire Map ------------------------------------------------------------------------------------------------------

    private void LayoutMapPortrait(Control map, float canvasHeight, float canvasWidth)
    {
        float shiftY = PortraitBetweenMath.MapBackgroundShift(canvasHeight);
        if (map.GetNodeOrNull<Control>("TheMap/MapBg") is { } bg)
        {
            PortraitControlSnapshot snap = Capture(bg, captureVisible: false);
            bg.OffsetTop = snap.InitialOffsetTop + shiftY;
            bg.OffsetBottom = snap.InitialOffsetBottom + shiftY;
            // Wider on both sides, so the parchment still covers the screen when the map is moved sideways to centre it and
            // its centre (what multiplayer map positions are measured from) stays where it was.
            bg.OffsetLeft = snap.InitialOffsetLeft - PortraitBetweenMath.MaxMapCentreShift;
            bg.OffsetRight = snap.InitialOffsetRight + PortraitBetweenMath.MaxMapCentreShift;
            // Each parchment tile is a 1080-tall slice of desktop-wide art: cover the narrow tile instead of fitting inside it,
            // which would leave black bands between the tiles.
            foreach (TextureRect tile in bg.GetChildren().OfType<TextureRect>())
            {
                _stretchModes.TryAdd(tile.GetInstanceId(), tile.StretchMode);
                tile.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
            }
        }
        if (map.GetNodeOrNull<Control>("TheMap/Drawings") is { } drawings)
        {
            PortraitControlSnapshot snap = Capture(drawings, captureVisible: false);
            float shiftX = (canvasWidth - 1920f) / 2f;
            drawings.OffsetLeft = snap.InitialOffsetLeft + shiftX;
            drawings.OffsetRight = snap.InitialOffsetRight + shiftX;
            drawings.OffsetTop = snap.InitialOffsetTop + shiftY;
            drawings.OffsetBottom = snap.InitialOffsetBottom + shiftY;
        }
        if (map.GetNodeOrNull<Control>("%MapLegend") is { } legend)
        {
            Capture(legend, captureVisible: true);
            legend.PivotOffset = Vector2.Zero;
            legend.Scale = new Vector2(0.8f, 0.8f);
            legend.Visible = _legendOpen;
        }
        EnsureLegendPill(map).Visible = true;

        // The draw, erase and clear buttons are 60 units (21 points) in a bar at the bottom left, with nothing beside it
        // up to the Share button: the whole bar grows from its bottom left corner until each button is 44 points.
        if (map.GetNodeOrNull<Control>("%DrawingTools") is { } tools)
        {
            Capture(tools, captureVisible: false);
            tools.PivotOffset = new Vector2(0f, tools.Size.Y);
            tools.Scale = new Vector2(DrawingToolsScale, DrawingToolsScale);
        }
    }

    private void UpdateMapPortrait(Control map, Window root, Vector2 canvas)
    {
        // The game's scroll limits (-600..1800) were made for a 1080-tall canvas; keep its target inside the portrait range.
        MapScrollRange range = MapScrollLimits(map, root);
        Type type = map.GetType();
        bool dragging = type.GetField("_isDragging", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(map) is true;
        FieldInfo? targetField = type.GetField("_targetDragPos", BindingFlags.Instance | BindingFlags.NonPublic);
        // The game keeps the map container at x = 0 and writes that back whenever it opens or scrolls the map; it follows this
        // target on both axes, so the sideways shift that centres the room points is held in the target as well.
        float shiftX = MapCentreShift(map, canvas.X);
        if (targetField?.GetValue(map) is Vector2 target)
        {
            float targetY = target.Y;
            if (!dragging)
            {
                float clamped = PortraitBetweenMath.ClampMapScroll(target.Y, range);
                if (!Mathf.IsEqualApprox(clamped, target.Y))
                    targetY = Mathf.Lerp(target.Y, clamped, (float)Math.Min(1.0, map.GetProcessDeltaTime() * 12.0));
            }
            if (targetY != target.Y || target.X != shiftX)
                targetField.SetValue(map, new Vector2(shiftX, targetY));
        }
        if (map.GetNodeOrNull<Control>("TheMap") is { } theMap && theMap.Position.X != shiftX)
            theMap.Position = new Vector2(shiftX, theMap.Position.Y);
        UpdateMapPointsPortrait(map);
        // The Share button sets its own scale whenever it is touched, so it keeps its picture and gets a taller hit area.
        // Done here and not when the map is laid out: the layout runs as the map enters the tree, before its children have.
        if (map.GetNodeOrNull<Control>("%ShareButton") is { } share && share.IsVisibleInTree())
            EnsureHitPad(share, 0f, PortraitBands.MinTarget);

        (float bandTop, _) = VisibleBand(root);
        Button pill = EnsureLegendPill(map);
        pill.Visible = true;
        pill.Position = new Vector2(canvas.X - pill.Size.X - 20f, bandTop - 40f);
        if (map.GetNodeOrNull<Control>("%MapLegend") is { } legend)
        {
            legend.Visible = _legendOpen;
            legend.Scale = new Vector2(0.8f, 0.8f);
            legend.Position = new Vector2(canvas.X - legend.Size.X * 0.8f - 16f, pill.Position.Y + pill.Size.Y + 13f);
        }
    }

    /// <summary>
    /// Room points for a finger, done once for each act's points. A room point is 56 units (19 points) with a
    /// picture of 96, and the points sit about 150 units apart with some jitter. Each point is drawn a quarter larger around
    /// its own centre, which leaves the centres, the paths and the centring measure where they were, and gets a square hit
    /// area of up to 44 points: never more than half the way to the nearest other room point (measured along the longer
    /// axis, so two hit areas cannot overlap) and never reaching a boss or the starting point.
    /// </summary>
    private void UpdateMapPointsPortrait(Control map)
    {
        if (map.GetNodeOrNull<Control>("TheMap/Points") is not { } points) return;
        int count = points.GetChildCount();
        ulong first = count > 0 ? points.GetChild(0).GetInstanceId() : 0;
        ulong id = map.GetInstanceId();
        if (_mapPointLayouts.TryGetValue(id, out (ulong First, int Count) known) && known.First == first && known.Count == count)
            return;
        _mapPointLayouts[id] = (first, count);

        var rooms = new List<Control>();
        var others = new List<Rect2>();
        foreach (Control point in points.GetChildren().OfType<Control>())
        {
            if (point.IsQueuedForDeletion()) continue;
            string name = point.GetType().Name;
            if (name == "NNormalMapPoint") rooms.Add(point);
            else if (name.EndsWith("MapPoint", StringComparison.Ordinal)) others.Add(point.GetRect());
        }
        var centres = rooms.Select(p => p.Position + p.PivotOffset).ToList();
        for (int i = 0; i < rooms.Count; i++)
        {
            Control point = rooms[i];
            Vector2 centre = centres[i];
            float half = PortraitBands.MinTarget / 2f;
            for (int j = 0; j < rooms.Count; j++)
            {
                if (j == i) continue;
                Vector2 apart = (centres[j] - centre).Abs();
                half = Math.Min(half, Math.Max(apart.X, apart.Y) / 2f);
            }
            foreach (Rect2 other in others)
            {
                float gap = Math.Max(
                    Math.Max(other.Position.X - centre.X, centre.X - other.End.X),
                    Math.Max(other.Position.Y - centre.Y, centre.Y - other.End.Y));
                half = Math.Min(half, gap);
            }
            point.Scale = new Vector2(MapPointScale, MapPointScale);
            _scaledMapPoints.Add(point.GetInstanceId());
            if (half * 2f > Math.Max(point.Size.X, point.Size.Y) * MapPointScale + 1f)
                EnsureHitPad(point, half * 2f, half * 2f);
        }
    }

    /// <summary>
    /// The sideways shift that centres this map's room points, measured again only when the points change (a new act).
    /// </summary>
    private float MapCentreShift(Control map, float canvasWidth)
    {
        if (map.GetNodeOrNull<Control>("TheMap/Points") is not { } points) return 0f;
        int count = points.GetChildCount();
        ulong first = count > 0 ? points.GetChild(0).GetInstanceId() : 0;
        ulong id = map.GetInstanceId();
        if (_mapShifts.TryGetValue(id, out (ulong First, int Count, float Width, float Shift) known) &&
            known.First == first && known.Count == count && known.Width == canvasWidth)
            return known.Shift;

        float left = float.MaxValue, right = float.MinValue;
        foreach (Control point in points.GetChildren().OfType<Control>())
        {
            if (point.GetType().Name != "NNormalMapPoint" || point.IsQueuedForDeletion()) continue;
            left = Math.Min(left, point.Position.X);
            right = Math.Max(right, point.Position.X + point.Size.X);
        }
        float shift = left <= right ? PortraitBetweenMath.MapCentreShift(left, right, canvasWidth) : 0f;
        _mapShifts[id] = (first, count, canvasWidth, shift);
        return shift;
    }

    private Button EnsureLegendPill(Control map)
    {
        if (map.GetNodeOrNull<Button>(LegendPillName) is { } existing) return existing;
        var style = new StyleBoxFlat
        {
            BgColor = PillFillColor,
            BorderColor = PillBorderColor,
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 63, CornerRadiusTopRight = 63, CornerRadiusBottomLeft = 63, CornerRadiusBottomRight = 63,
        };
        var pill = new Button
        {
            Name = LegendPillName,
            CustomMinimumSize = new Vector2(LegendPillWidth, PortraitBands.MinTarget),
            Size = new Vector2(LegendPillWidth, PortraitBands.MinTarget),
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        foreach (string state in new[] { "normal", "hover", "pressed", "focus", "disabled" })
            pill.AddThemeStyleboxOverride(state, style);
        string header = map.GetNodeOrNull<Label>("MapLegend/Header")?.Text ?? "Legend";
        var label = new Label
        {
            Name = "PillLabel",
            Text = header + " ?",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        label.AddThemeColorOverride("font_color", PillTextColor);
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.65f));
        label.AddThemeConstantOverride("outline_size", 8);
        label.AddThemeFontSizeOverride("font_size", LegendPillFontSize);
        if (ResourceLoader.Exists("res://themes/kreon_bold_glyph_space_one.tres") &&
            ResourceLoader.Load<Font>("res://themes/kreon_bold_glyph_space_one.tres") is { } kreon)
            label.AddThemeFontOverride("font", kreon);
        pill.AddChild(label);
        pill.Pressed += () => _legendOpen = !_legendOpen;
        map.AddChild(pill);
        return pill;
    }

    // ---- Proceed button (rewards, shop, rest site, treasure) ------------------------------------------------------------

    private static void UpdateProceedButtonPortrait(Control button, Vector2 canvas)
    {
        // ShowPos is a fraction of the canvas (1583/1920 across), which leaves the button partly off the narrow portrait edge.
        if (button.GetType().GetField("_animTween", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(button) is Tween { } tween &&
            tween.IsValid() && tween.IsRunning())
            return;
        float width = button.Size.X * button.Scale.X;
        if (button.Position.X >= canvas.X - 1f || button.Position.X + width <= canvas.X - 16f) return;
        button.Position = new Vector2(canvas.X - width - 24f, button.Position.Y);
    }

    private static void RestoreProceedButtonLandscape(Control button)
    {
        bool enabled = button.GetType().GetProperty("IsEnabled")?.GetValue(button) is true;
        PropertyInfo? pos = button.GetType().GetProperty(enabled ? "ShowPos" : "HidePos", BindingFlags.Instance | BindingFlags.NonPublic);
        if (pos?.GetValue(button) is Vector2 target)
            button.Position = target;
    }

    // ---- Spoils screen --------------------------------------------------------------------------------------------------

    private void LayoutRewardsPortrait(Control screen)
    {
        if (screen.GetNodeOrNull<Control>("Rewards") is not { } window) return;
        Capture(window, captureVisible: false);
        // The Loot panel is 526 wide with a 652 wide banner and rows of 402 x 86 units: at 1.5 the banner is 978 of the
        // canvas's 1080 and a row is 129 units (44 points) with 12 point text. The list is part of the panel, so as many
        // rows show as before and the rest scroll. The pivot is below the middle so the panel grows more up than down and
        // its list stays clear of the Proceed button.
        window.PivotOffset = new Vector2(window.Size.X / 2f, RewardsPivotY);
        window.Scale = new Vector2(RewardsScale, RewardsScale);
    }

    // ---- Card reward: 2 x 2 grid ----------------------------------------------------------------------------------------

    private void UpdateCardRewardPortrait(Control screen, Vector2 canvas, float capHeight)
    {
        Type type = screen.GetType();
        if (type.GetField("_completionSource", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(screen) is { } source &&
            source.GetType().GetProperty("Task")?.GetValue(source) is System.Threading.Tasks.Task { IsCompleted: true })
            return;
        if (screen.GetNodeOrNull<Control>("UI/CardRow") is not { } row) return;
        List<Control> holders = row.GetChildren().OfType<Control>().Where(h => !h.IsQueuedForDeletion()).ToList();
        if (holders.Count == 0) return;

        float centreY = canvas.Y / 2f;
        float lift = BannerLift(canvas, capHeight);
        LiftBanner(screen.GetNodeOrNull<Control>("UI/Banner"), canvas, lift, _liftedBanners);
        float bannerBottom = centreY - BannerBottomOffset - lift;
        // Choose and Skip are 44 points tall in portrait (PortraitOverlayLayout grows them).
        float altHeight = PortraitBands.MinTarget;
        float bottomLimit = canvas.Y - 120f - altHeight - 30f;
        int rows = holders.Count <= 2 ? 1 : (holders.Count + 1) / 2;
        const float gapX = 48f, gapY = 40f;
        float maxScaleByHeight = ((bottomLimit - (bannerBottom + 30f)) - (rows - 1) * gapY) / (rows * RewardCardHeight);
        float maxScaleByWidth = (canvas.X - 60f - gapX) / (2f * RewardCardWidth);
        float cardScale = Math.Min(RewardMaxCardScale, Math.Min(maxScaleByHeight, maxScaleByWidth));
        float holderScale = 0.8f;
        float rowScale = cardScale / holderScale;

        float blockHeight = rows * RewardCardHeight * cardScale + (rows - 1) * gapY;
        float blockCentreY = bannerBottom + 30f + blockHeight / 2f;

        Capture(row, captureVisible: false);
        row.Scale = new Vector2(rowScale, rowScale);
        row.OffsetTop = blockCentreY - centreY;
        row.OffsetBottom = row.OffsetTop;

        if (screen.GetNodeOrNull<Control>("UI/RewardAlternatives") is { } alts)
        {
            Capture(alts, captureVisible: false);
            float top = blockCentreY + blockHeight / 2f + 30f - centreY;
            alts.OffsetTop = top;
            alts.OffsetBottom = top + altHeight;
        }

        // A card keeps its slot: the game moves a hovered card to the end of the row so it draws on top, so the
        // row's child order is not the cards' order. Slots follow the game's options (the card each holder shows), and a holder
        // the options do not name keeps the place it was first seen in.
        ulong screenId = screen.GetInstanceId();
        if (!_laidOutRewardHolders.TryGetValue(screenId, out List<ulong>? order))
            _laidOutRewardHolders[screenId] = order = new List<ulong>();
        order.RemoveAll(id => !holders.Any(h => h.GetInstanceId() == id));
        bool fresh = false;
        foreach (Control holder in holders
                     .Select(h => (Holder: h, Option: PhoneCardRewardChoice.OptionIndexOf(screen, h)))
                     .OrderBy(p => p.Option < 0 ? int.MaxValue : p.Option)
                     .ThenBy(p => p.Holder.GetInstanceId())
                     .Select(p => p.Holder))
        {
            if (order.Contains(holder.GetInstanceId())) continue;
            order.Add(holder.GetInstanceId());
            fresh = true;
        }
        IReadOnlyList<GridSlot> slots = PortraitBetweenMath.RewardSlots(
            order.Count, RewardCardWidth * cardScale, RewardCardHeight * cardScale, gapX, gapY);
        Vector2 SlotOf(Control holder)
        {
            GridSlot slot = slots[order.IndexOf(holder.GetInstanceId())];
            return new Vector2(slot.CenterX / rowScale, slot.CenterY / rowScale);
        }

        if (fresh)
        {
            // New cards: replace the game's slide to its single row with a slide to the portrait grid, keeping its fade in.
            FieldInfo? tweenField = type.GetField("_cardTween", BindingFlags.Instance | BindingFlags.NonPublic);
            if (tweenField?.GetValue(screen) is Tween { } old && old.IsValid()) old.Kill();
            Tween tween = screen.CreateTween().SetParallel(true);
            foreach (Control holder in holders)
            {
                tween.TweenProperty(holder, "position", SlotOf(holder), 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
                tween.TweenProperty(holder, "modulate", Colors.White, 1.0).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quart);
            }
            tweenField?.SetValue(screen, tween);
            return;
        }

        if (type.GetField("_cardTween", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(screen) is Tween { } running &&
            running.IsValid() && running.IsRunning())
            return;
        foreach (Control holder in holders)
        {
            Vector2 position = SlotOf(holder);
            if (holder.Position != position) holder.Position = position;
        }
    }

    private static void RestoreCardRewardLandscape(Control screen)
    {
        if (screen.GetNodeOrNull<Control>("UI/CardRow") is not { } row) return;
        List<Control> holders = row.GetChildren().OfType<Control>().Where(h => !h.IsQueuedForDeletion()).ToList();
        for (int i = 0; i < holders.Count; i++)
            holders[i].Position = new Vector2(-(holders.Count - 1) * CardRowSpacing / 2f + CardRowSpacing * i, 0f);
    }

    // ---- The shared banner -----------------------------------------------------------------------------------------------

    /// <summary>How far the shared banner rises in portrait: its top comes to just under the top bar and relic strip.</summary>
    internal static float BannerLift(Vector2 canvas, float capHeight) =>
        Math.Max(0f, canvas.Y / 2f - BannerTopOffset - (capHeight + PortraitBands.RunContentTop + BannerContentGap));

    /// <summary>
    /// Moves a screen's banner (NCommonBanner) up by <paramref name="lift"/>. The banner keeps where it shows and where it slides
    /// in from, measured from the middle of the screen, and goes back there when the window changes; so both are set here, and a
    /// slide that is under way is started again towards the new place.
    /// </summary>
    internal static void LiftBanner(Control? banner, Vector2 canvas, float lift, HashSet<ulong> lifted)
    {
        // Before its _Ready the banner has not measured where it stands (its image offset), and moving it then would be
        // measured as the game's own place (the choose a card banner ended off the screen).
        if (banner == null || !GodotObject.IsInstanceValid(banner) || !banner.IsNodeReady()) return;
        Type type = banner.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (type.GetField("_imgOffset", flags)?.GetValue(banner) is not Vector2 imgOffset ||
            type.GetField("_showPos", flags) is not { } showField || type.GetField("_hidePos", flags) is not { } hideField ||
            showField.GetValue(banner) is not Vector2 shown)
            return;
        Vector2 target = canvas / 2f - imgOffset - new Vector2(0f, lift);
        if (shown.DistanceSquaredTo(target) < 0.25f) return;
        showField.SetValue(banner, target);
        hideField.SetValue(banner, target + new Vector2(0f, 50f));
        lifted.Add(banner.GetInstanceId());
        if (type.GetField("_tween", flags)?.GetValue(banner) is Tween { } tween && tween.IsValid() && tween.IsRunning())
            banner.Call("AnimateIn");
        else
            banner.Position = target;
    }

    /// <summary>Puts lifted banners back where the game shows them (it works that out again from the window).</summary>
    internal static void RestoreBanners(HashSet<ulong> lifted)
    {
        foreach (ulong id in lifted)
            if (GodotObject.InstanceFromId(id) is Control banner && GodotObject.IsInstanceValid(banner) && banner.HasMethod("OnWindowChange"))
                banner.Call("OnWindowChange");
        lifted.Clear();
    }

    // ---- Card grids: deck, piles, card selection --------------------------------------------------------------------------

    private void LayoutCardGridPortrait(Control grid, float capHeight, float canvasHeight, float canvasWidth)
    {
        if (grid.GetType().FullName != CardGridTypeName) return;
        float top = capHeight + PortraitBands.TopBarHeight + 8f;
        PortraitControlSnapshot snap = Capture(grid, captureVisible: false);
        float parentTop = grid.GetParent() is Control parent ? parent.GlobalPosition.Y : 0f;
        float localTop = Math.Max(top - parentTop, snap.InitialOffsetTop);
        grid.AnchorLeft = 0f;
        grid.AnchorTop = 0f;
        grid.AnchorRight = 0f;
        grid.AnchorBottom = 0f;
        grid.PivotOffset = Vector2.Zero;
        grid.Scale = new Vector2(GridScale, GridScale);
        grid.OffsetLeft = 0f;
        grid.OffsetTop = localTop;
        grid.OffsetRight = canvasWidth / GridScale;
        grid.OffsetBottom = localTop + (canvasHeight - parentTop - localTop) / GridScale;

        // The deck view instances the grid with editable children, so the unique name does not resolve from the grid there.
        if (GridScroll(grid) is { } scroll)
        {
            Capture(scroll, captureVisible: false);
            scroll.OffsetLeft = GridScrollInsetLeft;
            scroll.OffsetRight = -GridScrollInsetRight;
            if (scroll.GetNodeOrNull<Control>("SortingOptions") is { } sorting)
                LayoutSortRowPortrait(sorting, canvasWidth / GridScale - GridScrollInsetLeft - GridScrollInsetRight);
        }
        if (grid.GetNodeOrNull<Control>("Scrollbar") is { } scrollbar)
        {
            Capture(scrollbar, captureVisible: false);
            scrollbar.OffsetLeft = GridScrollbarLeft;
            scrollbar.OffsetRight = GridScrollbarRight;
        }
    }

    /// <summary>
    /// The deck's four sort buttons (Obtained, Card Type, Cost, A - Z) are 250 x 42 units with 22 unit text, 50 apart: at the
    /// grid's portrait scale they would be 1150 units wide, so the game's row was shrunk to 0.66 (11.6 points tall). In portrait
    /// the row is the scroll area's width and 75 units tall (47 points at 1.8, its text 13.8 points), each button as wide as its
    /// own text and arrow need and the spare width shared out; if the texts do not fit, their font is made smaller to fit.
    /// </summary>
    private void LayoutSortRowPortrait(Control sorting, float width)
    {
        Capture(sorting, captureVisible: false);
        sorting.Scale = Vector2.One;
        sorting.PivotOffset = Vector2.Zero;
        sorting.OffsetLeft = 0f;
        sorting.OffsetRight = 0f;
        sorting.OffsetTop = SortRowTop;
        sorting.OffsetBottom = SortRowTop + SortRowHeight;
        if (sorting.GetNodeOrNull<Control>("HBoxContainer") is not { } row) return;
        Capture(row, captureVisible: false);
        row.OffsetTop = -SortRowHeight / 2f;
        row.OffsetBottom = SortRowHeight / 2f;
        row.AddThemeConstantOverride("separation", (int)SortRowGap);

        List<Control> buttons = row.GetChildren().OfType<Control>().Where(b => b.Visible).ToList();
        if (buttons.Count == 0) return;
        // A button is its text, 12 units, a 36 unit arrow and 8 units of inset (deck_view_sort_button.tscn), plus some padding.
        float Need(Control button, int font)
        {
            if (button.GetNodeOrNull<Label>("%Label") is not { } label) return 120f;
            Font? face = label.GetThemeFont("font");
            float text = face?.GetStringSize(label.Text, HorizontalAlignment.Left, -1, font).X ?? label.Text.Length * font * 0.55f;
            return text + 12f + 36f + 8f + SortButtonPad;
        }
        float room = width - SortRowGap * (buttons.Count - 1);
        int gameFont = buttons[0].GetNodeOrNull<Label>("%Label") is { } first ? first.GetThemeFontSize("font_size") : 22;
        if (_labelFontSizes.TryGetValue(buttons[0].GetNodeOrNull<Label>("%Label")?.GetInstanceId() ?? 0, out (bool Had, int Size) kept))
            gameFont = kept.Size;
        int fontSize = gameFont;
        while (fontSize > 14 && buttons.Sum(b => Need(b, fontSize)) > room) fontSize--;
        float spare = Math.Max(0f, room - buttons.Sum(b => Need(b, fontSize))) / buttons.Count;
        foreach (Control button in buttons)
        {
            Capture(button, captureVisible: false);
            button.CustomMinimumSize = new Vector2(MathF.Floor(Need(button, fontSize) + spare), SortRowHeight);
            if (button.GetNodeOrNull<Label>("%Label") is { } label && fontSize != gameFont) SetLabelFontSize(label, fontSize);
        }
        GD.Print($"Portrait between layout: deck sort row {buttons.Count} buttons in {width:F0} units, text {fontSize}.");
    }

    private static Control? GridScroll(Control grid) =>
        grid.GetNodeOrNull<Control>("%ScrollContainer") ?? grid.GetNodeOrNull<Control>("ScrollContainer");

    private static void KeepGridColumnsPortrait(Control grid)
    {
        // The grid sizes its scroll container itself after laying out cards, which can put back the old width. Columns follow
        // that width, so hold the portrait width and ask the grid to lay out again when it drifts.
        if (GridScroll(grid) is not { } scroll) return;
        float target = grid.Size.X - GridScrollInsetLeft - GridScrollInsetRight;
        if (Math.Abs(scroll.Size.X - target) < 1f) return;
        scroll.OffsetLeft = GridScrollInsetLeft;
        scroll.OffsetRight = -GridScrollInsetRight;
        scroll.Size = new Vector2(target, scroll.Size.Y);
        grid.GetType().GetField("_needsReinit", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(grid, true);
    }

    // ---- Ancient events (Neow) -----------------------------------------------------------------------------------------

    private void LayoutAncientPortrait(Control layout, float canvasWidth)
    {
        // The dialogue's "Continue" hint is placed 268 to 663 units right of the centre, for a desktop-wide screen; on the
        // narrow canvas its text ran past the right edge. Keep its right end inside the screen.
        if (layout.GetNodeOrNull<Control>("%FakeNextButtonContainer/FakeNextButton") is { } next)
        {
            PortraitControlSnapshot nextSnap = Capture(next, captureVisible: false);
            float shift = Math.Max(0f, nextSnap.InitialOffsetRight - (canvasWidth / 2f - AncientNextEdgeMargin));
            next.OffsetLeft = nextSnap.InitialOffsetLeft - shift;
            next.OffsetRight = nextSnap.InitialOffsetRight - shift;
        }

        // Lift the dialogue and options above the ancient's name plate in the bottom-left corner. Once only, and by moving
        // the box as it is: the game changes this box's height itself (short while the dialogue runs, so it ends above the
        // "Continue" hint, and full again for the options), so writing a height kept from an earlier moment here cut the
        // third option off when a later layout pass came after the options were shown.
        if (layout.GetNodeOrNull<Control>("%ContentContainer") is not { } content) return;
        if (_liftedAncientContents.Add(content.GetInstanceId()))
            content.Position -= new Vector2(0f, AncientContentLift);
    }

    /// <summary>
    /// When the last dialogue line shows, the game slides the column so the options end at the bottom of its clipped box,
    /// measuring the options as they are at that moment; rows that grew after (portrait's 44 point rows, laid out a frame
    /// later) ran past the box and the last option was cut off. Once the slide has ended, the column is held
    /// with its end at the box's bottom. Earlier lines are left as the game places them (the lines to come wait below).
    /// </summary>
    private void KeepAncientOptionsInside(Control layout)
    {
        Type type = layout.GetType();
        object? lastLine = type.GetProperty("IsDialogueOnLastLine", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(layout);
        // No slide yet: the game has not set the screen up, and puts the column below the box when it does.
        if (type.GetField("_contentTween", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(layout) is not Tween tween) return;
        bool sliding = tween.IsValid() && tween.IsRunning();
        Control? box = layout.GetNodeOrNull<Control>("%ContentContainer");
        Control? content = box?.GetNodeOrNull<Control>("Content");
        if (lastLine is not true || sliding || box == null || content == null) return;
        float overflow = content.Position.Y + content.Size.Y - box.Size.Y;
        if (overflow <= 0.5f) return;
        content.Position -= new Vector2(0f, overflow);
        GD.Print($"Portrait ancient: options lifted {overflow:F0} into the box.");
    }

    // ---- Map tutorial tip ------------------------------------------------------------------------------------------------

    private static void KeepMapFtueOnScreen(Control ftue, Vector2 canvas)
    {
        // The tip opens to the right of the room it points at; on the narrow canvas that runs off the edge. Slide only the panel.
        if (ftue.GetNodeOrNull<Control>("%Header") is not { } header || ftue.GetNodeOrNull<Control>("%Positioner") is not { } positioner)
            return;
        Control panel = header;
        while (panel.GetParent() is Control parent && parent != positioner) panel = parent;
        Rect2 rect = panel.GetGlobalRect();
        float overflow = rect.End.X - (canvas.X - 16f);
        if (overflow > 0.5f) panel.Position -= new Vector2(overflow, 0f);
        else if (rect.Position.X < 16f) panel.Position += new Vector2(16f - rect.Position.X, 0f);
    }

    // ---- Shop inventory ------------------------------------------------------------------------------------------------

    // The shop in rows. In the panel's own units a card slot is drawn at 0.65: 195 x 274 about its origin with its
    // price under it to 181 below the origin. A relic's icon reaches 52 above its origin and its price 69 below (a potion's
    // less); their touch boxes are the icon's size (the game sets them so), about 83 units, so six stand in a row of 1048 canvas
    // units. The slots keep the game's 0.65: it tweens them back to that after a touch.
    private const float ShopCardWidth = 300f * 0.65f;
    private const float ShopCardHalfHeight = 211f * 0.65f;
    private const float ShopCardPriceBottom = 278f * 0.65f;
    private const float ShopCardGapX = 12f;
    private const float ShopRowGap = 16f;
    private const float ShopSmallItemTop = 56f;
    private const float ShopSmallRowHeight = ShopSmallItemTop + 69f + ShopRowGap;
    private const float ShopRemovalLift = 10f;
    private const float ShopSideMargin = 16f;
    private const float ShopBottomMargin = 110f;
    // The game slides the closed panel to y -1000, which leaves the bottom of the taller portrait panel on the screen.
    private const float ShopClosedY = -1000f;
    private const float ShopHiddenY = -3000f;
    private const float ShopDialogueLeftReach = 182f;
    private const float ShopDialogueRightReach = 98f;

    private readonly Dictionary<ulong, (bool Rows, Vector2 Canvas)> _shopModes = new();

    /// <summary>
    /// The shop in portrait, laid out again whenever it changes mode or the canvas changes size. A blitz shop keeps the game's
    /// panel scaled to the width (its slots are placed for a wide screen); the standard one stands in rows: three character
    /// cards; two more and card removal; the relics and potions in one row of six; and the two colorless cards in the middle of
    /// the last row, where the back button keeps the bottom left corner. The panel is drawn as large as the width and the
    /// height allow (about 1.63 on an iPhone 11 Pro: cards 110 points wide, twice the size they had). While the shop is closed
    /// the panel waits far enough up to be out of sight.
    /// </summary>
    private void UpdateShopPortrait(Control inventory, float capHeight, Vector2 canvas)
    {
        if (inventory.GetNodeOrNull<Control>("%SlotsContainer") is not { } slots) return;
        bool blitz = slots.GetNodeOrNull<Control>("BlitzSlots") is { Visible: true };
        ulong id = inventory.GetInstanceId();
        bool known = _shopModes.TryGetValue(id, out (bool Rows, Vector2 Canvas) mode);
        if (!known || mode.Rows == blitz || mode.Canvas != canvas)
        {
            if (blitz)
            {
                RestoreShopItems(slots);
                LayoutMerchantInventoryPortrait(inventory, capHeight, canvas.X);
            }
            else
            {
                LayoutShopRows(slots, capHeight, canvas);
            }
            _shopModes[id] = (!blitz, canvas);
        }

        KeepClosedShopOutOfSight(inventory, slots);

        // The merchant's speech bubble stands at a random x between 450 and 1450 (a desktop's width), so in portrait it can be
        // off the screen; its text box reaches 182 units left of that point and 98 right.
        if (inventory.GetNodeOrNull<Node2D>("%Dialogue") is { } dialogue)
        {
            float x = Math.Clamp(dialogue.Position.X, ShopDialogueLeftReach + ShopSideMargin, canvas.X - ShopDialogueRightReach - ShopSideMargin);
            if (Math.Abs(x - dialogue.Position.X) > 0.5f) dialogue.Position = new Vector2(x, dialogue.Position.Y);
        }
    }

    /// <summary>
    /// The game slides a closed shop's panel to y -1000, which leaves the bottom of the taller portrait panel on the screen;
    /// once it is closed and still, it waits further up. The fake merchant's panel (a subclass) too.
    /// </summary>
    private static void KeepClosedShopOutOfSight(Control inventory, Control slots)
    {
        if (slots.Position.Y <= ShopClosedY - 1f || slots.Position.Y >= ShopClosedY + 1f) return;
        if (inventory.GetType().GetProperty("IsOpen")?.GetValue(inventory) is true) return;
        for (Type? type = inventory.GetType(); type != null; type = type.BaseType)
        {
            if (type.GetField("_inventoryTween", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is not { } field) continue;
            if (field.GetValue(inventory) is Tween { } tween && tween.IsValid() && tween.IsRunning()) return;
            break;
        }
        slots.Position = new Vector2(slots.Position.X, ShopHiddenY);
    }

    private void LayoutShopRows(Control slots, float capHeight, Vector2 canvas)
    {
        PortraitControlSnapshot snap = Capture(slots, captureVisible: false);
        float width = snap.InitialOffsetRight - snap.InitialOffsetLeft;
        float cx = (ShopContentLeft + ShopContentRight) / 2f;
        float top = ShopContentTop;
        float cardRow = ShopCardHalfHeight + ShopCardPriceBottom + ShopRowGap;
        float contentHeight = 2f * cardRow + ShopSmallRowHeight + ShopCardHalfHeight + ShopCardPriceBottom;
        // Under the whole relic strip: it stays in view over the shop, and its second row (ten relics or more) covered the
        // titles of the first row of cards.
        float visualTop = capHeight + PortraitBands.RunContentTop + 8f;
        float byWidth = (canvas.X - 2f * ShopSideMargin) / (3f * ShopCardWidth + 2f * ShopCardGapX);
        float byHeight = (canvas.Y - ShopBottomMargin - visualTop) / contentHeight;
        float scale = Math.Max(0.5f, Math.Min(byWidth, byHeight));

        slots.OffsetLeft = -cx;
        slots.OffsetRight = -cx + width;
        slots.PivotOffset = new Vector2(cx, PortraitBetweenMath.PivotForVisualTop(ShopOpenY, top, visualTop, scale));
        slots.Scale = new Vector2(scale, scale);

        float pitch = ShopCardWidth + ShopCardGapX;
        float row0 = top + ShopCardHalfHeight;
        float row1 = row0 + cardRow;
        float smallY = top + 2f * cardRow + ShopSmallItemTop;
        float row3 = top + 2f * cardRow + ShopSmallRowHeight + ShopCardHalfHeight;
        var places = new List<(Control Item, Vector2 At)>();
        List<Control> Items(string group) =>
            slots.GetNodeOrNull<Control>(group)?.GetChildren().OfType<Control>().ToList() ?? new List<Control>();
        List<Control> character = Items("CharacterCards");
        for (int i = 0; i < character.Count; i++)
            places.Add((character[i], i < 3 ? new Vector2(cx + (i - 1) * pitch, row0) : new Vector2(cx + (i - 4) * pitch, row1)));
        if (slots.GetNodeOrNull<Control>("MerchantCardRemoval") is { } removal)
            places.Add((removal, new Vector2(cx + pitch, row1 - ShopRemovalLift)));
        List<Control> colorless = Items("ColorlessCards");
        for (int i = 0; i < colorless.Count; i++)
            places.Add((colorless[i], new Vector2(cx + (i - (colorless.Count - 1) / 2f) * pitch, row3)));
        List<Control> small = Items("Relics").Concat(Items("Potions")).ToList();
        float smallPitch = (canvas.X - 2f * ShopSideMargin) / scale / Math.Max(6, small.Count);
        for (int i = 0; i < small.Count; i++)
            places.Add((small[i], new Vector2(cx + (i - (small.Count - 1) / 2f) * smallPitch, smallY)));

        foreach ((Control item, Vector2 at) in places)
        {
            Capture(item, captureVisible: false);
            Vector2 groupAt = item.GetParent() is Control group && group != slots ? group.Position : Vector2.Zero;
            item.Position = at - groupAt;
        }
        float padWidth = Math.Min(PortraitBands.MinTarget, smallPitch * scale - 6f);
        foreach (Control item in small)
            if (item.GetNodeOrNull<Control>("Hitbox") is { } hitbox)
                EnsureHitPad(hitbox, padWidth, PortraitBands.MinTarget);
        foreach (Node cost in slots.FindChildren("Cost", "HBoxContainer", recursive: true, owned: false))
            EnlargeShopPrice((Control)cost);
        GD.Print($"Portrait between layout: shop in rows at {scale:F2} (width allows {byWidth:F2}, height {byHeight:F2}), {places.Count} items.");
    }

    /// <summary>Puts the shop's items back where the scene has them (the blitz shop, and landscape through the snapshots).</summary>
    private void RestoreShopItems(Control slots)
    {
        foreach (string group in new[] { "CharacterCards", "ColorlessCards", "Relics", "Potions" })
            foreach (Control item in slots.GetNodeOrNull<Control>(group)?.GetChildren().OfType<Control>() ?? Enumerable.Empty<Control>())
                if (_snapshots.TryGetValue(item.GetInstanceId(), out PortraitControlSnapshot? snap)) snap.Restore(item);
        if (slots.GetNodeOrNull<Control>("MerchantCardRemoval") is { } removal &&
            _snapshots.TryGetValue(removal.GetInstanceId(), out PortraitControlSnapshot? removalSnap))
            removalSnap.Restore(removal);
    }

    /// <summary>
    /// After the snapshots put the panel back, its height is the game's again: open at 80, closed at -1000 (the snapshot holds
    /// the place it had when it was first laid out, which may be the other one).
    /// </summary>
    private void RestoreShopLandscape(Control inventory)
    {
        _shopModes.Remove(inventory.GetInstanceId());
        if (inventory.GetNodeOrNull<Control>("%SlotsContainer") is not { } slots) return;
        bool open = inventory.GetType().GetProperty("IsOpen")?.GetValue(inventory) is true;
        bool sliding = inventory.GetType().GetField("_inventoryTween", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(inventory) is Tween { } tween &&
                       tween.IsValid() && tween.IsRunning();
        if (!sliding) slots.Position = new Vector2(slots.Position.X, open ? ShopOpenY : ShopClosedY);
    }

    private void LayoutMerchantInventoryPortrait(Control inventory, float capHeight, float canvasWidth)
    {
        if (inventory.GetNodeOrNull<Control>("%SlotsContainer") is not { } slots) return;
        PortraitControlSnapshot snap = Capture(slots, captureVisible: false);
        float width = snap.InitialOffsetRight - snap.InitialOffsetLeft;
        float scale = PortraitBetweenMath.FitScale(ShopContentRight - ShopContentLeft, canvasWidth - 20f);
        float contentCentre = (ShopContentLeft + ShopContentRight) / 2f;
        float visualTop = capHeight + PortraitBands.RunContentTop + 8f;
        // The game slides the panel's y between -1000 and 80; only x follows the anchors, so centre the content with offsets
        // and lift it under the top bar with the pivot.
        slots.OffsetLeft = -contentCentre;
        slots.OffsetRight = -contentCentre + width;
        slots.PivotOffset = new Vector2(contentCentre, PortraitBetweenMath.PivotForVisualTop(ShopOpenY, ShopContentTop, visualTop, scale));
        slots.Scale = new Vector2(scale, scale);

        // Relics and potions are three to a row, 116 units apart on the canvas with 111 between the two rows, and their
        // hit boxes are 97 and 56 units: each gets the largest hit area that cannot reach its neighbours.
        foreach (string group in new[] { "Relics", "Potions" })
        {
            if (slots.GetNodeOrNull<Control>(group) is not { } items) continue;
            foreach (Control item in items.GetChildren().OfType<Control>())
                if (item.GetNodeOrNull<Control>("Hitbox") is { } hitbox)
                    EnsureHitPad(hitbox, ShopItemPadWidth, ShopItemPadHeight);
        }

        foreach (Node cost in slots.FindChildren("Cost", "HBoxContainer", recursive: true, owned: false))
            EnlargeShopPrice((Control)cost);
    }

    /// <summary>
    /// A shop price is a gold icon and a 39 unit number in a slot drawn at 0.65, in a shop scaled down to the screen's width:
    /// 6.8 points. In portrait the number is set to about 11 points on the screen whatever the scales, and the icon
    /// grows with it. The price row is centred under its item, and the items are far enough apart for the wider row.
    /// </summary>
    private void EnlargeShopPrice(Control cost)
    {
        if (cost.GetNodeOrNull<Label>("CostLabel") is not { } label || !label.IsInsideTree()) return;
        float scale = label.GetGlobalTransformWithCanvas().Scale.Y;
        if (scale < 0.01f) return;
        int gameFont = GameLabelFontSize(label);
        int font = (int)MathF.Ceiling(ShopPriceUnits / scale);
        if (font <= gameFont) return;
        SetLabelFontSize(label, font);
        foreach (string limit in new[] { "MinFontSize", "MaxFontSize" })
        {
            Dictionary<ulong, int> kept = limit == "MinFontSize" ? _labelMinFontSizes : _labelMaxFontSizes;
            ulong id = label.GetInstanceId();
            if (label.Get(limit).VariantType != Variant.Type.Int) continue;
            if (!kept.ContainsKey(id)) kept[id] = label.Get(limit).AsInt32();
            label.Set(limit, font);
        }
        foreach (TextureRect icon in cost.GetChildren().OfType<TextureRect>())
        {
            PortraitControlSnapshot snap = Capture(icon, captureVisible: false);
            icon.CustomMinimumSize = snap.InitialCustomMinimumSize * ((float)font / gameFont);
        }
    }

    /// <summary>Sets a label's font size in portrait, keeping the game's own to put back in landscape.</summary>
    private void SetLabelFontSize(Control label, int size)
    {
        ulong id = label.GetInstanceId();
        if (!_labelFontSizes.ContainsKey(id))
            _labelFontSizes[id] = (label.HasThemeFontSizeOverride("font_size"), label.GetThemeFontSize("font_size"));
        if (label.GetThemeFontSize("font_size") != size) label.AddThemeFontSizeOverride("font_size", size);
    }

    private int GameLabelFontSize(Control label) =>
        _labelFontSizes.TryGetValue(label.GetInstanceId(), out (bool Had, int Size) kept) ? kept.Size : label.GetThemeFontSize("font_size");

    // ---- Events --------------------------------------------------------------------------------------------------------

    private void LayoutEventPortrait(Control layout, float capHeight, float canvasHeight, float canvasWidth)
    {
        if (layout.GetType().FullName != EventLayoutTypeName) return;
        float bandTop = capHeight + PortraitBands.TopBarHeight;
        float artCentre = bandTop + 540f;
        float shiftY = artCentre - (canvasHeight / 2f + EventArtCentreY);
        foreach (string name in new[] { "%Portrait", "%VfxContainer" })
        {
            if (layout.GetNodeOrNull<Control>(name) is not { } art) continue;
            PortraitControlSnapshot snap = Capture(art, captureVisible: false);
            art.OffsetLeft = snap.InitialOffsetLeft + EventArtShiftX;
            art.OffsetRight = snap.InitialOffsetRight + EventArtShiftX;
            art.OffsetTop = snap.InitialOffsetTop + shiftY;
            art.OffsetBottom = snap.InitialOffsetBottom + shiftY;
        }
        if (layout.GetNodeOrNull<Control>("VBoxContainer") is { } text)
        {
            Capture(text, captureVisible: false);
            float rootTop = layout.OffsetTop;
            float top = bandTop + 1080f - 40f - rootTop;
            text.OffsetLeft = -(canvasWidth / 2f) + 40f;
            text.OffsetRight = canvasWidth / 2f - 40f;
            text.OffsetTop = top;
        }
        // The description is drawn at 26 units (9 points); it does not size itself, so its font sizes are set to 11 points.
        if (layout.GetNodeOrNull<Control>("%EventDescription") is { } description)
            SetRichFontSize(description, EventTextFontSize);
        _events.Add(layout.GetInstanceId());
        KeepEventTextOnScreen(layout, capHeight, new Vector2(canvasWidth, canvasHeight));
    }

    /// <summary>
    /// With larger text and option rows a long event can be taller than the room under the art. The text block is anchored
    /// at its top and grows down, so when its bottom would pass the screen's bottom it is lifted over the art's lower edge
    /// by what is missing.
    /// </summary>
    private static void KeepEventTextOnScreen(Control layout, float capHeight, Vector2 canvas)
    {
        if (layout.GetNodeOrNull<Control>("VBoxContainer") is not { } text) return;
        float baseTop = capHeight + PortraitBands.TopBarHeight + 1080f - 40f - layout.OffsetTop;
        float bottom = layout.GlobalPosition.Y + baseTop + text.GetCombinedMinimumSize().Y;
        float lift = Math.Clamp(bottom - (canvas.Y - EventBottomMargin), 0f, EventMaxLift);
        float top = baseTop - lift;
        if (Math.Abs(text.OffsetTop - top) > 0.5f) text.OffsetTop = top;
    }

    /// <summary>
    /// An event's option row is 100 units tall (35 points) with text of up to 24 units (8 points). In the default event
    /// layout the row becomes 44 points tall, its frame follows, and its text gets the row's full width and may size itself
    /// up to 11 points. The ancients' options keep their own layout.
    /// </summary>
    private void LayoutEventOptionPortrait(Control option)
    {
        bool inDefaultLayout = false, inAncientLayout = false;
        for (Node? node = option.GetParent(); node != null && !inDefaultLayout && !inAncientLayout; node = node.GetParent())
        {
            inDefaultLayout = node.GetType().FullName == EventLayoutTypeName;
            inAncientLayout = node.GetType().FullName == AncientLayoutTypeName;
        }
        if (inAncientLayout) LayoutAncientOptionPortrait(option);
        if (!inDefaultLayout) return;

        PortraitControlSnapshot snap = Capture(option, captureVisible: false);
        option.CustomMinimumSize = new Vector2(snap.InitialCustomMinimumSize.X, PortraitBands.MinTarget);
        foreach (string name in new[] { "Shadow", "Outline" })
        {
            // Both are centred on the row and 100 units tall whatever the row's height.
            if (option.GetNodeOrNull<Control>(name) is not { } frame) continue;
            Capture(frame, captureVisible: false);
            frame.OffsetTop = -PortraitBands.MinTarget / 2f;
            frame.OffsetBottom = PortraitBands.MinTarget / 2f;
        }
        if (option.GetNodeOrNull<Control>("%Text") is { } label)
        {
            // The text sizes itself between its smallest and largest font when its box changes, so the limit goes up first.
            ulong id = label.GetInstanceId();
            if (!_labelMaxFontSizes.ContainsKey(id) && label.Get("MaxFontSize").VariantType == Variant.Type.Int)
            {
                _labelMaxFontSizes[id] = label.Get("MaxFontSize").AsInt32();
                label.Set("MaxFontSize", EventTextFontSize);
            }
            PortraitControlSnapshot textSnap = Capture(label, captureVisible: false);
            label.AnchorRight = 1f;
            label.AnchorBottom = 1f;
            label.OffsetLeft = textSnap.InitialOffsetLeft;
            label.OffsetTop = textSnap.InitialOffsetTop;
            label.OffsetRight = -36f;
            label.OffsetBottom = -13f;
        }
    }

    /// <summary>
    /// An ancient's option row (Neow's and the others') is 1000 x 100 units, 34 points tall, with text of up to 24 units (8
    /// points). In portrait the row is 44 points tall, its outline and its icon and text row grow with it, and the text may
    /// size itself up to 11 points. The game places the column from the rows' sizes when the options
    /// show, so the taller rows stay inside the screen.
    /// </summary>
    private void LayoutAncientOptionPortrait(Control option)
    {
        PortraitControlSnapshot snap = Capture(option, captureVisible: false);
        float grow = PortraitBands.MinTarget - snap.InitialCustomMinimumSize.Y;
        if (grow <= 0f) return;
        option.CustomMinimumSize = new Vector2(snap.InitialCustomMinimumSize.X, PortraitBands.MinTarget);
        if (option.GetNodeOrNull<Control>("Outline") is { } outline)
        {
            // Centred on the row, a little taller than it.
            PortraitControlSnapshot outlineSnap = Capture(outline, captureVisible: false);
            outline.OffsetTop = outlineSnap.InitialOffsetTop - grow / 2f;
            outline.OffsetBottom = outlineSnap.InitialOffsetBottom + grow / 2f;
        }
        if (option.GetNodeOrNull<Control>("HBoxContainer") is { } row)
        {
            // Placed by its offsets inside the row, 13 units from its top and bottom.
            PortraitControlSnapshot rowSnap = Capture(row, captureVisible: false);
            row.OffsetBottom = rowSnap.InitialOffsetBottom + grow;
        }
        if (option.GetNodeOrNull<Control>("%Text") is { } label)
        {
            ulong id = label.GetInstanceId();
            if (!_labelMaxFontSizes.ContainsKey(id) && label.Get("MaxFontSize").VariantType == Variant.Type.Int)
            {
                _labelMaxFontSizes[id] = label.Get("MaxFontSize").AsInt32();
                label.Set("MaxFontSize", EventTextFontSize);
            }
        }
    }

    /// <summary>
    /// An ancient's dialogue line is a speech bubble 800 units wide with its text at a fixed 24 units (8 points). In portrait
    /// it takes the column's full width and its text is drawn at 11 points; the bubble grows with the text. The line is not a
    /// touch target of its own: a touch anywhere on the screen moves the dialogue on.
    /// </summary>
    private void LayoutAncientDialogueLinePortrait(Control line)
    {
        foreach (string path in new[] { ".", "DialogueContainer", "DialogueContainer/TextContainer/Text" })
        {
            if (line.GetNodeOrNull<Control>(path) is not { } part) continue;
            PortraitControlSnapshot snap = Capture(part, captureVisible: false);
            part.CustomMinimumSize = new Vector2(snap.InitialCustomMinimumSize.X + AncientDialogueWiden, snap.InitialCustomMinimumSize.Y);
        }
        if (line.GetNodeOrNull<Control>("DialogueContainer/TextContainer/Text") is { } text)
        {
            ulong id = text.GetInstanceId();
            if (!_labelMaxFontSizes.ContainsKey(id) && text.Get("MaxFontSize").VariantType == Variant.Type.Int)
            {
                _labelMaxFontSizes[id] = text.Get("MaxFontSize").AsInt32();
                text.Set("MaxFontSize", EventTextFontSize);
            }
            if (!_labelMinFontSizes.ContainsKey(id) && text.Get("MinFontSize").VariantType == Variant.Type.Int)
            {
                _labelMinFontSizes[id] = text.Get("MinFontSize").AsInt32();
                text.Set("MinFontSize", EventTextFontSize);
            }
            SetRichFontSize(text, EventTextFontSize);
        }
    }

    // ---- Rest site -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The rest site's options are a row of 247 x 163 buttons, 100 apart, with a 28 unit (10 point) label under each, in
    /// the middle of an empty screen. The row is drawn as large as its buttons fit the width, up to a third larger (two
    /// options), and smaller when four or more would pass the screen's edges. The pivot is above the row's middle, so
    /// the row stays under the question above it and its labels stay above the description below.
    /// </summary>
    private void UpdateRestSitePortrait(Control room, Vector2 canvas)
    {
        if (room.GetNodeOrNull<Control>("%ChoicesContainer") is not { } choices) return;
        float width = choices.GetCombinedMinimumSize().X;
        float scale = Math.Clamp(
            PortraitBetweenMath.FitScale(width, canvas.X - 40f, RestChoicesMaxScale), RestChoicesMinScale, RestChoicesMaxScale);
        var pivot = new Vector2(choices.Size.X / 2f, RestChoicesPivotY);
        if (Mathf.IsEqualApprox(choices.Scale.X, scale) && choices.PivotOffset.IsEqualApprox(pivot)) return;
        Capture(choices, captureVisible: false);
        choices.PivotOffset = pivot;
        choices.Scale = new Vector2(scale, scale);
    }

    // ---- shared --------------------------------------------------------------------------------------------------------

    private PortraitControlSnapshot Capture(Control control, bool captureVisible)
    {
        ulong id = control.GetInstanceId();
        if (_snapshots.TryGetValue(id, out PortraitControlSnapshot? existing))
        {
            if (captureVisible && !existing.CaptureVisible) existing.RecordInitialVisibility(control.Visible);
            return existing;
        }
        var created = new PortraitControlSnapshot(control, captureVisible);
        _snapshots[id] = created;
        return created;
    }

    /// <summary>
    /// Gives a control a hit area of at least this size on the canvas, centred on it, without changing its picture (row
    /// 21h; PhoneHitArea). A container would lay the area out as one of its own children, so there it hangs from a child
    /// the container may size as it likes. Taken away again in landscape.
    /// </summary>
    private void EnsureHitPad(Control target, float canvasWidth, float canvasHeight)
    {
        if (!target.IsInsideTree()) return;
        Vector2 scale = target.GetGlobalTransformWithCanvas().Scale.Abs();
        if (scale.X < 0.01f || scale.Y < 0.01f) return;
        float growX = Math.Max(0f, canvasWidth / scale.X - target.Size.X) / 2f;
        float growY = Math.Max(0f, canvasHeight / scale.Y - target.Size.Y) / 2f;
        if (!PhoneHitArea.IsContainer(target))
        {
            PhoneHitArea.Grow(target, growX, growY, growX, growY);
            _hitAreaTargets.Add(target.GetInstanceId());
            return;
        }

        Control? holder = target.GetNodeOrNull<Control>(HitPadHolderName);
        if (holder == null)
        {
            holder = new Control { Name = HitPadHolderName, MouseFilter = Control.MouseFilterEnum.Ignore };
            var pad = new Control
            {
                Name = PhoneHitArea.NodeName,
                MouseFilter = Control.MouseFilterEnum.Pass,
                FocusMode = Control.FocusModeEnum.None,
            };
            pad.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            holder.AddChild(pad);
            target.AddChild(holder);
            _hitPadHolders.Add(holder.GetInstanceId());
        }
        if (holder.GetNodeOrNull<Control>(PhoneHitArea.NodeName) is { } area)
        {
            area.OffsetLeft = -growX;
            area.OffsetTop = -growY;
            area.OffsetRight = growX;
            area.OffsetBottom = growY;
        }
    }

    /// <summary>Sets every font size of a rich text label that does not size itself, keeping what it had for landscape.</summary>
    private void SetRichFontSize(Control label, int size)
    {
        ulong id = label.GetInstanceId();
        if (!_richFontSizes.ContainsKey(id))
            _richFontSizes[id] = RichFontSizeNames
                .Select(name => (label.HasThemeFontSizeOverride(name), label.HasThemeFontSizeOverride(name) ? label.GetThemeFontSize(name) : 0))
                .ToArray();
        foreach (string name in RichFontSizeNames)
            if (!label.HasThemeFontSizeOverride(name) || label.GetThemeFontSize(name) != size)
                label.AddThemeFontSizeOverride(name, size);
    }

    /// <summary>Top of the band below the top bar and relic strip, and the bottom of the band above the back button.</summary>
    public static (float Top, float Bottom) VisibleBand(Window root)
    {
        float canvasHeight = Math.Max(root.GetVisibleRect().Size.Y, SafeAreaCanvas.MinPortraitHeight);
        double inset = SafeAreaCanvas.TopInset();
        float cap = inset > 0 ? MathF.Ceiling((float)(inset * canvasHeight)) : 162f;
        cap = Math.Max(cap, 136f);
        return (cap + PortraitBands.TopBarHeight + 200f, canvasHeight - 150f);
    }

    public static MapScrollRange MapScrollLimits(Control map, Window root)
    {
        (float top, float bottom) = VisibleBand(root);
        Type type = map.GetType();
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (map.GetNodeOrNull<Control>("TheMap") is not { } theMap ||
            type.GetField("_bossPointNode", Flags)?.GetValue(map) is not Control boss ||
            type.GetField("_startingPointNode", Flags)?.GetValue(map) is not Control start)
            return new MapScrollRange(-600f, 1800f);
        Control topBoss = type.GetField("_secondBossPointNode", Flags)?.GetValue(map) is Control second && second.IsVisibleInTree() &&
            second.GlobalPosition.Y < boss.GlobalPosition.Y ? second : boss;
        float bossTop = topBoss.GetGlobalRect().Position.Y - theMap.GlobalPosition.Y;
        float startBottom = start.GetGlobalRect().End.Y - theMap.GlobalPosition.Y;
        return PortraitBetweenMath.MapScrollRange(bossTop - 20f, startBottom + 20f, top, bottom);
    }

    /// <summary>One line naming the visible between-fight screens and their rectangles, for the proof log.</summary>
    public static string DescribeForProof(Node root)
    {
        var parts = new List<string>();
        foreach (Node node in Descendants(root))
        {
            if (node is not Control control || !control.IsVisibleInTree()) continue;
            string? name = control.GetType().FullName;
            if (name == null || !name.StartsWith("MegaCrit.Sts2.Core.Nodes.", StringComparison.Ordinal)) continue;
            string shortName = control.GetType().Name;
            if (shortName is "NMapScreen" or "NRewardsScreen" or "NCardRewardSelectionScreen" or "NDeckViewScreen" or "NCardGrid"
                or "NEventRoom" or "NEventLayout" or "NAncientEventLayout" or "NMerchantRoom" or "NStandardMerchantRoom"
                or "NMerchantInventory" or "NRestSiteRoom" or "NTreasureRoom" or "NProceedButton")
            {
                Rect2 r = control.GetGlobalRect();
                parts.Add($"{shortName}[{r.Position.X:F0},{r.Position.Y:F0} {r.Size.X:F0}x{r.Size.Y:F0}]");
                if (shortName == "NMapScreen" && control.GetNodeOrNull<Control>("TheMap") is { } theMap &&
                    control.GetType().GetField("_bossPointNode", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(control) is Control boss)
                    parts.Add($"mapY={theMap.Position.Y:F0} boss=[{boss.GetGlobalRect().Position.Y:F0}..{boss.GetGlobalRect().End.Y:F0}]");
                if (shortName == "NMerchantInventory" && control.GetNodeOrNull<Control>("%SlotsContainer") is { } slotsNode)
                    parts.Add($"slots=[{slotsNode.GetGlobalRect().Position.X:F0},{slotsNode.GetGlobalRect().Position.Y:F0} scale={slotsNode.Scale.X:F2} pivot={slotsNode.PivotOffset.Y:F0} off={slotsNode.OffsetLeft:F0}]");
                if (shortName == "NCardGrid" && (control.GetNodeOrNull<Control>("%ScrollContainer") ?? control.GetNodeOrNull<Control>("ScrollContainer")) is { } scrollNode)
                    parts.Add($"scroll=[{scrollNode.Size.X:F0}x{scrollNode.Size.Y:F0} off={scrollNode.OffsetLeft:F0},{scrollNode.OffsetRight:F0} anchors={scrollNode.AnchorLeft},{scrollNode.AnchorRight} gridSize={control.Size.X:F0}]");
                if (shortName == "NCardGrid" && control.GetType().GetProperty("Columns", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(control) is int columns)
                    parts.Add($"columns={columns}");
            }
        }
        return "screens=" + string.Join(" ", parts.Distinct());
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (Node below in Descendants(child)) yield return below;
        }
    }
}
