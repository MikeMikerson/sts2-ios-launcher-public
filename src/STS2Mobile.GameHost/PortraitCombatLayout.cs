using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Portrait-mode combat room layout, Adaptive Two-Row Theater Fan (<c>NPlayerHand</c>), mid-screen HUD bar, pinned corner piles,
/// and high-contrast card playability. Captures initial desktop layout before any portrait edit so toggling Portrait Mode
/// off mid-fight restores the original landscape combat layout cleanly. The End Turn and pile buttons are 44 points
/// across, and each creature's health display and intents are drawn at one size on the canvas so their text is 11 points.
/// </summary>
internal sealed class PortraitCombatLayout
{
    public const string CombatRoomTypeName = "MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom";

    private static readonly Color PlayableHighlightColor = new(
        PortraitCombatMath.PlayableHighlightRgba.R,
        PortraitCombatMath.PlayableHighlightRgba.G,
        PortraitCombatMath.PlayableHighlightRgba.B,
        PortraitCombatMath.PlayableHighlightRgba.A);

    private static readonly Color UnaffordableCostColor = new(
        PortraitCombatMath.UnaffordableCostRgba.R,
        PortraitCombatMath.UnaffordableCostRgba.G,
        PortraitCombatMath.UnaffordableCostRgba.B,
        PortraitCombatMath.UnaffordableCostRgba.A);

    private static readonly HashSet<string> SkippedCardBodyChildren = new(StringComparer.Ordinal)
    {
        "Shadow",
        "Highlight",
        "EnergyIcon",
        "StarIcon",
        "CardVfxContainer",
        "CardSparkles",
    };

    // Touch targets: the End Turn button (220 by 90 in the scene) and the pile buttons (80 by 80) are drawn large
    // enough for the 44-point side a finger needs (PortraitBands.MinTarget), in place of the HUD math's 1.24 and 1.32.
    private const float EndTurnTargetScale = 1.42f;
    private const float PileTargetScale = 1.6f;
    private const float HudEdgeMargin = 16f;

    // Text: a creature is drawn at 0.42 to 0.94 of its size to fit the stage, which left its health, block and
    // name text (24 units), power amounts (18) and intent values (22) at about 6 points. The health display and the
    // intents are drawn at one fixed size on the canvas instead, whatever the creature's own scale: 24 units become 11
    // points at 1.33, 22 units at 1.45, and power amounts are set in 24 units.
    private const float StateDisplayCanvasScale = 1.33f;
    private const float IntentCanvasScale = 1.45f;
    private const int PowerAmountFontSize = 24;
    private const int HandIndexFontSize = 40;

    /// <summary>What an enemy's intents take above its head at <see cref="IntentCanvasScale"/> (64 units each, and a gap).</summary>
    private const float IntentBandHeight = 64f * IntentCanvasScale + 12f;

    // A lone enemy may use the stage from just right of the player to the canvas's edge, kept a
    // fifth of the width from the player (PortraitCombatMath.EnemyStageLeft), never left of this. With enemies on both sides the player stands in the middle and keeps this much either side of it.
    private const float LoneEnemyLeft = 360f;
    private const float SurroundedPlayerHalf = 160f;
    private const float SurroundedSideGap = 100f;

    private const float MinHealthBarWidth = 150f;
    private const float MinNameplateWidth = 250f;
    private const float StageSideMargin = 8f;
    private const float PowerIconPitch = 48f;

    private readonly Dictionary<ulong, CombatRoomState> _rooms = new();
    private readonly Dictionary<ulong, CreatureDesktopState> _creatures = new();
    private readonly Dictionary<ulong, int> _enlargedFonts = new();
    private readonly Dictionary<ulong, Dictionary<ulong, Color>> _dimmedCardChildren = new();
    private readonly HashSet<ulong> _boostedHighlights = new();
    private readonly HashSet<ulong> _wiredHolders = new();
    private readonly HandStillness _stillness = new();
    private readonly Dictionary<ulong, Control.MouseFilterEnum> _endTurnFilters = new();
    private readonly Dictionary<ulong, (Vector2 Position, Vector2 Scale)> _bossArmOriginals = new();
    private readonly Dictionary<ulong, BossArmFit> _bossArmFits = new();
    private CombatReflection? _reflection;

    private const string KaiserCrabBackgroundTypeName = "MegaCrit.Sts2.Core.Nodes.Vfx.Backgrounds.NKaiserCrabBossBackground";

    public void OnProcessFrame(SceneTree tree, float capHeight)
    {
        if (!SafeAreaCanvas.IsPortrait || !GodotObject.IsInstanceValid(tree.Root))
            return;

        CombatReflection refl = EnsureReflection();
        Node? activeRoom = refl.GetActiveCombatRoom();
        if (activeRoom is not Control roomControl || !GodotObject.IsInstanceValid(roomControl) || !roomControl.IsInsideTree())
            return;

        float canvasHeight = Math.Max(tree.Root.GetVisibleRect().Size.Y, SafeAreaCanvas.MinPortraitHeight);
        float delta = (float)roomControl.GetProcessDeltaTime();
        ApplyCombatRoomPortrait(roomControl, refl, capHeight, canvasHeight, delta);
        FitCombatFtueModal(tree.Root, portrait: true);
    }

    public void ApplyRoomImmediate(Control roomControl, float capHeight, float canvasHeight)
    {
        CombatReflection refl = EnsureReflection();
        ApplyCombatRoomPortrait(roomControl, refl, capHeight, canvasHeight, delta: 1f);
    }

    public void RestoreLandscape(Node root)
    {
        CombatReflection refl = EnsureReflection();

        FitCombatFtueModal(root, portrait: false);
        RestoreAllDimmedCards(refl);
        RestoreEndTurnFilters();
        RestoreBackgroundBosses();

        foreach ((ulong labelId, int fontSize) in _enlargedFonts)
        {
            if (GodotObject.InstanceFromId(labelId) is Label label && GodotObject.IsInstanceValid(label))
                label.AddThemeFontSizeOverride("font_size", fontSize);
        }
        _enlargedFonts.Clear();

        foreach ((ulong creatureId, CreatureDesktopState state) in _creatures)
        {
            if (GodotObject.InstanceFromId(creatureId) is Control creature && GodotObject.IsInstanceValid(creature))
                state.Restore(creature, refl);
        }
        _creatures.Clear();

        foreach ((ulong roomId, CombatRoomState roomState) in _rooms)
        {
            if (GodotObject.InstanceFromId(roomId) is Control room && GodotObject.IsInstanceValid(room))
                roomState.Restore(room, refl);
        }
        _rooms.Clear();
    }

    public void PruneDead()
    {
        var dead = new List<ulong>();
        foreach (ulong id in _rooms.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _rooms.Remove(id);

        dead.Clear();
        foreach (ulong id in _creatures.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _creatures.Remove(id);

        dead.Clear();
        foreach (ulong id in _dimmedCardChildren.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _dimmedCardChildren.Remove(id);

        dead.Clear();
        foreach (ulong id in _wiredHolders)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _wiredHolders.Remove(id);

        dead.Clear();
        foreach (ulong id in _boostedHighlights)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _boostedHighlights.Remove(id);

        dead.Clear();
        foreach (ulong id in _enlargedFonts.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _enlargedFonts.Remove(id);

        dead.Clear();
        foreach (ulong id in _endTurnFilters.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _endTurnFilters.Remove(id);
    }

    /// <summary>
    /// Sets a label's font size up to <paramref name="fontSize"/>, keeping the size it had so landscape gets it back.
    /// For labels the game does not size itself (auto size off): it would put its own size back on the next text change.
    /// </summary>
    private void EnlargeLabelFont(Label? label, int fontSize)
    {
        if (label == null || !GodotObject.IsInstanceValid(label)) return;
        int current = label.GetThemeFontSize("font_size");
        if (current >= fontSize) return;
        _enlargedFonts.TryAdd(label.GetInstanceId(), current);
        label.AddThemeFontSizeOverride("font_size", fontSize);
    }

    private static void FitCombatFtueModal(Node root, bool portrait)
    {
        if (!GodotObject.IsInstanceValid(root)) return;
        Control? ftue = FindCombatRulesFtue(root);
        if (ftue == null) return;
        if (portrait)
        {
            ftue.PivotOffset = ftue.Size * 0.5f;
            ftue.Scale = new Vector2(0.68f, 0.68f);
        }
        else
        {
            ftue.Scale = Vector2.One;
        }
    }

    private static Control? FindCombatRulesFtue(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is Control c && c.GetType().FullName == "MegaCrit.Sts2.Core.Nodes.Ftue.NCombatRulesFtue")
                return c;
            if (FindCombatRulesFtue(child) is { } nested)
                return nested;
        }
        return null;
    }

    private void ApplyCombatRoomPortrait(
        Control room,
        CombatReflection refl,
        float capHeight,
        float canvasHeight,
        float delta)
    {
        ulong roomId = room.GetInstanceId();
        if (!_rooms.TryGetValue(roomId, out CombatRoomState? state))
        {
            state = CombatRoomState.Capture(room, refl);
            _rooms[roomId] = state;
        }

        Control? ui = room.GetNodeOrNull<Control>("%CombatUi") ?? room.GetNodeOrNull<Control>("CombatUi");
        if (ui != null)
        {
            ui.AnchorLeft = 0f;
            ui.AnchorTop = 0f;
            ui.AnchorRight = 1f;
            ui.AnchorBottom = 1f;
            ui.OffsetLeft = 0f;
            ui.OffsetTop = 0f;
            ui.OffsetRight = 0f;
            ui.OffsetBottom = 0f;
        }

        Control? hand = ui?.GetNodeOrNull<Control>("%Hand") ?? ui?.GetNodeOrNull<Control>("Hand");
        bool twoRows = SyncHandAndPlayabilityPortrait(hand, refl, out bool hasStarCounter);
        if (ui != null && refl.StarCounterField?.GetValue(ui) is CanvasItem starItem && starItem.IsVisibleInTree())
            hasStarCounter = true;

        float stageTopY = capHeight + PortraitBands.RunContentTop;
        PortraitHudLayout targetHud = PortraitCombatMath.ComputeHudLayout(canvasHeight, stageTopY, twoRows, hasStarCounter);

        if (!state.HasSmoothedHud)
        {
            state.SmoothedHudTopY = targetHud.HudTopY;
            state.HasSmoothedHud = true;
        }
        else
        {
            float alpha = delta >= 0.5f ? 1f : 1f - MathF.Exp(-14f * Math.Max(0.001f, delta));
            state.SmoothedHudTopY = Mathf.Lerp(state.SmoothedHudTopY, targetHud.HudTopY, alpha);
            if (MathF.Abs(state.SmoothedHudTopY - targetHud.HudTopY) < 0.5f)
                state.SmoothedHudTopY = targetHud.HudTopY;
        }

        float hudDeltaY = state.SmoothedHudTopY - targetHud.HudTopY;
        if (ui != null)
        {
            LayoutCombatHudPortrait(ui, room, refl, targetHud, hudDeltaY, state, delta);
            KeepEndTurnUnlitWhileHolding(ui, hand, refl);
        }

        LayoutCombatStagePortrait(room, refl, state, capHeight, stageTopY, state.SmoothedHudTopY, canvasHeight, twoRows);
    }

    /// <summary>
    /// In portrait End Turn sits in the hand's band, so a card carried toward an enemy passes
    /// over it, and the button lit up (its hover: brighter, its label red, the playable cards flashed) as if letting go there
    /// would end the turn, which it never does while a card is held. While a card is held the button takes no pointer, so it
    /// cannot light; its filter is put back as soon as the card is played or put back, and when portrait is left.
    /// </summary>
    private void KeepEndTurnUnlitWhileHolding(Control ui, Control? hand, CombatReflection refl)
    {
        if ((ui.GetNodeOrNull<Control>("%EndTurnButton") ?? ui.GetNodeOrNull<Control>("EndTurnButton")) is not { } endTurn)
            return;
        bool holding = hand != null && (bool)(refl.HandInCardPlayProp?.GetValue(hand) ?? false);
        ulong id = endTurn.GetInstanceId();
        if (holding)
        {
            if (endTurn.MouseFilter != Control.MouseFilterEnum.Ignore)
            {
                _endTurnFilters[id] = endTurn.MouseFilter;
                endTurn.MouseFilter = Control.MouseFilterEnum.Ignore;
            }
        }
        else if (_endTurnFilters.Remove(id, out Control.MouseFilterEnum filter))
        {
            endTurn.MouseFilter = filter;
        }
    }

    private void RestoreEndTurnFilters()
    {
        foreach ((ulong id, Control.MouseFilterEnum filter) in _endTurnFilters)
        {
            if (GodotObject.InstanceFromId(id) is Control endTurn && GodotObject.IsInstanceValid(endTurn))
                endTurn.MouseFilter = filter;
        }
        _endTurnFilters.Clear();
    }

    private bool SyncHandAndPlayabilityPortrait(Control? hand, CombatReflection refl, out bool hasStarCounter)
    {
        hasStarCounter = false;
        if (hand == null)
            return false;

        Control? container = hand.GetNodeOrNull<Control>("%CardHolderContainer") ?? hand.GetNodeOrNull<Control>("CardHolderContainer");
        if (container == null)
            return false;

        bool combatInProgress = refl.IsCombatInProgress();
        bool inCardPlay = (bool)(refl.HandInCardPlayProp?.GetValue(hand) ?? false);
        bool isDisabled = (bool)(refl.HandIsDisabledField?.GetValue(hand) ?? false);
        object? focusedObj = refl.HandFocusedHolderProp?.GetValue(hand);

        var activeHolders = new List<Control>();
        var allHoldersInHand = new List<Control>();
        foreach (Node child in container.GetChildren())
        {
            if (child is Control holder && !holder.IsQueuedForDeletion() &&
                holder.GetType().FullName == "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder")
            {
                allHoldersInHand.Add(holder);
                if (holder.IsVisibleInTree())
                    activeHolders.Add(holder);
            }
        }

        // Also check any holder temporarily reparented onto NPlayerHand during drag.
        Control? draggedHolder = null;
        foreach (Node child in hand.GetChildren())
        {
            if (child is Control holder && !holder.IsQueuedForDeletion() &&
                holder.GetType().FullName == "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder")
            {
                draggedHolder = holder;
                break;
            }
        }

        bool holding = inCardPlay && draggedHolder != null;
        int effectiveCount = PortraitCombatMath.EffectiveCardCount(activeHolders.Count, holding);
        bool twoRows = PortraitCombatMath.UsesTwoCardRows(effectiveCount);
        // A held card keeps its slot empty, so the rest of the hand stays still: every touch picks a card up, and closing the gap
        // then opening it again when the card went back made the whole hand bounce.
        int heldIndex = holding && refl.HandDraggedIndexField?.GetValue(hand) is int dragged ? Math.Clamp(dragged, 0, activeHolders.Count) : -1;

        int focusedIndex = -1;
        if (focusedObj is Control focusedControl)
            focusedIndex = activeHolders.IndexOf(focusedControl);

        var activeDimmedCardIds = new HashSet<ulong>();

        foreach (Control holder in allHoldersInHand)
        {
            WireHolderFocus(holder, hand);
            // The card's number in the hand: 36 units are 10.3 points on a two-row card (drawn at 0.824).
            EnlargeLabelFont(holder.GetNodeOrNull<Label>("%HandIndex") ?? holder.GetNodeOrNull<Label>("HandIndex"), HandIndexFontSize);
        }

        for (int i = 0; i < activeHolders.Count; i++)
        {
            Control holder = activeHolders[i];
            Control? cardNode = refl.CardHolderCardNodeProp?.GetValue(holder) as Control;
            bool playable = !combatInProgress || refl.CanCardPlay(cardNode);

            int slotIndex = PortraitCombatMath.SlotIndexBesideHeldCard(i, heldIndex);
            PortraitCardSlot slot = PortraitCombatMath.ComputeHandCardSlot(
                heldIndex >= 0 ? effectiveCount : activeHolders.Count,
                slotIndex,
                focusedIndex >= 0 ? PortraitCombatMath.SlotIndexBesideHeldCard(focusedIndex, heldIndex) : -1,
                playable,
                isDisabled,
                forceTwoRows: twoRows);

            bool isFocused = focusedIndex == i;
            var slotPosition = new Vector2(slot.LocalX, slot.LocalY);
            if (!isFocused)
                _stillness.RestoreIfRetargeted(holder, slotPosition, (Vector2)(refl.HolderTargetPosField?.GetValue(holder) ?? holder.Position));
            ApplyHolderTargets(holder, refl, slot, isFocused);
            if (isFocused) _stillness.Forget(holder);
            else _stillness.Record(holder, slotPosition);

            if (cardNode != null)
            {
                ApplyCardPlayabilityVisuals(holder, cardNode, refl, combatInProgress, playable, activeDimmedCardIds);
            }
        }

        if (draggedHolder != null && refl.CardHolderCardNodeProp?.GetValue(draggedHolder) is Control draggedCard)
        {
            // Keep a dragged card at full brightness while held/targeted so art and preview numbers read clearly.
            RestoreSingleCardVisuals(draggedCard, refl);
        }

        // Undim any previously dimmed card that left the active unplayable hand set (played, discarded, pooled, or now affordable).
        if (_dimmedCardChildren.Count > 0)
        {
            var restoreIds = new List<ulong>();
            foreach (ulong cardId in _dimmedCardChildren.Keys)
            {
                if (!activeDimmedCardIds.Contains(cardId))
                    restoreIds.Add(cardId);
            }
            foreach (ulong cardId in restoreIds)
            {
                if (GodotObject.InstanceFromId(cardId) is Control card && GodotObject.IsInstanceValid(card))
                    RestoreSingleCardVisuals(card, refl);
                else
                    _dimmedCardChildren.Remove(cardId);
            }
        }

        return twoRows;
    }

    /// <summary>
    /// Under a finger the hover and the press come in one event. Hovering a card runs the game's desktop hand
    /// layout at once (<c>NPlayerHand.RefreshLayout</c>), which in portrait drops the focused card about 330 units, out from
    /// under the finger, so the press picked the card beside it or nothing. Connected after the hand's own handler, this puts
    /// the portrait layout back in the same moment, before the press is delivered.
    /// </summary>
    private void WireHolderFocus(Control holder, Control hand)
    {
        if (!_wiredHolders.Add(holder.GetInstanceId())) return;
        ulong handId = hand.GetInstanceId();
        Callable relayout = Callable.From((Control _) => RelayoutHandNow(handId));
        if (holder.HasSignal("HolderFocused")) holder.Connect("HolderFocused", relayout);
        if (holder.HasSignal("HolderUnfocused")) holder.Connect("HolderUnfocused", relayout);
    }

    private void RelayoutHandNow(ulong handId)
    {
        if (!SafeAreaCanvas.IsPortrait || GodotObject.InstanceFromId(handId) is not Control hand || !GodotObject.IsInstanceValid(hand))
            return;
        try
        {
            SyncHandAndPlayabilityPortrait(hand, EnsureReflection(), out _);
        }
        catch (Exception error)
        {
            GD.PrintErr("Portrait hand relayout on focus failed: ", error.Message);
        }
    }

    private static void ApplyHolderTargets(Control holder, CombatReflection refl, PortraitCardSlot slot, bool isFocused)
    {
        var targetPos = new Vector2(slot.LocalX, slot.LocalY);
        var targetScale = new Vector2(slot.Scale, slot.Scale);

        Vector2 currentTargetPos = (Vector2)(refl.HolderTargetPosField?.GetValue(holder) ?? holder.Position);
        Vector2 currentTargetScale = (Vector2)(refl.HolderTargetScaleField?.GetValue(holder) ?? holder.Scale);
        float currentTargetAngle = (float)(refl.HolderTargetAngleField?.GetValue(holder) ?? holder.RotationDegrees);

        if (isFocused)
        {
            // The focused card snaps to its spot (the game's own focus layout snaps it elsewhere first), so it stays under a finger.
            if (MathF.Abs(holder.Position.Y - targetPos.Y) > 0.5f)
                holder.Position = new Vector2(holder.Position.X, targetPos.Y);
            if ((currentTargetPos - targetPos).LengthSquared() > 0.25f)
                refl.HolderSetTargetPosMethod?.Invoke(holder, [targetPos]);
            if (MathF.Abs(holder.Scale.X - targetScale.X) > 0.002f || MathF.Abs(currentTargetScale.X - targetScale.X) > 0.002f)
            {
                refl.HolderTargetScaleField?.SetValue(holder, targetScale);
                refl.HolderSetScaleInstantlyMethod?.Invoke(holder, [targetScale]);
            }
            if (MathF.Abs(holder.RotationDegrees) > 0.05f || MathF.Abs(currentTargetAngle) > 0.05f)
            {
                refl.HolderTargetAngleField?.SetValue(holder, 0f);
                refl.HolderSetAngleInstantlyMethod?.Invoke(holder, [0f]);
            }
        }
        else
        {
            if ((currentTargetPos - targetPos).LengthSquared() > 0.25f)
                refl.HolderSetTargetPosMethod?.Invoke(holder, [targetPos]);
            if (MathF.Abs(currentTargetScale.X - targetScale.X) > 0.002f)
                refl.HolderSetTargetScaleMethod?.Invoke(holder, [targetScale]);
            if (MathF.Abs(currentTargetAngle - slot.AngleDegrees) > 0.05f)
                refl.HolderSetTargetAngleMethod?.Invoke(holder, [slot.AngleDegrees]);
        }

        if (isFocused)
            holder.ZIndex = 2;
        else if (holder.ZIndex > 1)
            holder.ZIndex = 0;
    }

    private void ApplyCardPlayabilityVisuals(
        Control holder,
        Control cardNode,
        CombatReflection refl,
        bool combatInProgress,
        bool playable,
        HashSet<ulong> activeDimmedCardIds)
    {
        ulong cardId = cardNode.GetInstanceId();
        Control? body = refl.CardBodyProp?.GetValue(cardNode) as Control ?? cardNode.GetNodeOrNull<Control>("%CardContainer");
        Control? highlight = refl.CardHighlightProp?.GetValue(cardNode) as Control ?? body?.GetNodeOrNull<Control>("Highlight");

        if (!combatInProgress || playable)
        {
            RestoreSingleCardVisuals(cardNode, refl);
            if (combatInProgress && highlight != null)
            {
                bool glowGold = (bool)(refl.HolderGlowGoldProp?.GetValue(holder) ?? false);
                bool glowRed = (bool)(refl.HolderGlowRedProp?.GetValue(holder) ?? false);
                if (!glowGold && !glowRed)
                    highlight.Modulate = PlayableHighlightColor;

                if (highlight.Material is ShaderMaterial mat)
                {
                    Tween? tween = refl.HighlightTweenField?.GetValue(highlight) as Tween;
                    if (tween == null || !tween.IsRunning())
                    {
                        mat.SetShaderParameter("width", 0.095f);
                        _boostedHighlights.Add(highlight.GetInstanceId());
                    }
                }
            }
            return;
        }

        activeDimmedCardIds.Add(cardId);
        if (body != null)
        {
            if (!_dimmedCardChildren.TryGetValue(cardId, out Dictionary<ulong, Color>? saved))
            {
                saved = new Dictionary<ulong, Color>();
                _dimmedCardChildren[cardId] = saved;
            }

            float dim = PortraitCombatMath.UnplayableBodyBrightness;
            foreach (Node child in body.GetChildren())
            {
                if (child is not CanvasItem item || SkippedCardBodyChildren.Contains(item.Name.ToString()))
                    continue;
                ulong childId = item.GetInstanceId();
                if (!saved.TryGetValue(childId, out Color orig))
                {
                    orig = item.Modulate;
                    saved[childId] = orig;
                }
                item.Modulate = new Color(orig.R * dim, orig.G * dim, orig.B * dim, orig.A);
            }
        }

        // Keep energy and star icons at 100% brightness and sharpen unaffordable red cost digits (#FF5252).
        if (body?.GetNodeOrNull<CanvasItem>("EnergyIcon") is { } energyIcon)
            energyIcon.Modulate = Colors.White;
        if (body?.GetNodeOrNull<CanvasItem>("StarIcon") is { } starIcon)
            starIcon.Modulate = Colors.White;

        if (refl.CardEnergyLabelField?.GetValue(cardNode) is Control energyLabel)
        {
            CanvasItem? slashIcon = refl.CardUnplayableEnergyIconField?.GetValue(cardNode) as CanvasItem;
            bool hasNonResourceSlash = slashIcon != null && slashIcon.Visible;
            if (!hasNonResourceSlash && energyLabel.HasThemeColorOverride("font_color"))
            {
                Color c = energyLabel.GetThemeColor("font_color");
                if (c.R > 0.85f && c.G < 0.45f)
                    energyLabel.AddThemeColorOverride("font_color", UnaffordableCostColor);
            }
        }
    }

    private void RestoreSingleCardVisuals(Control cardNode, CombatReflection refl)
    {
        ulong cardId = cardNode.GetInstanceId();
        if (_dimmedCardChildren.TryGetValue(cardId, out Dictionary<ulong, Color>? saved))
        {
            foreach ((ulong childId, Color orig) in saved)
            {
                if (GodotObject.InstanceFromId(childId) is CanvasItem item && GodotObject.IsInstanceValid(item))
                    item.Modulate = orig;
            }
            _dimmedCardChildren.Remove(cardId);
        }

        if ((refl.CardHighlightProp?.GetValue(cardNode) as Control) is { } highlight &&
            _boostedHighlights.Remove(highlight.GetInstanceId()) &&
            highlight.Material is ShaderMaterial mat)
        {
            float current = mat.GetShaderParameter("width").AsSingle();
            if (current > 0.08f)
                mat.SetShaderParameter("width", 0.075f);
        }
    }

    private void RestoreAllDimmedCards(CombatReflection refl)
    {
        foreach ((ulong cardId, Dictionary<ulong, Color> saved) in _dimmedCardChildren)
        {
            foreach ((ulong childId, Color orig) in saved)
            {
                if (GodotObject.InstanceFromId(childId) is CanvasItem item && GodotObject.IsInstanceValid(item))
                    item.Modulate = orig;
            }
            if (GodotObject.InstanceFromId(cardId) is Control card && GodotObject.IsInstanceValid(card) &&
                refl.CardDisplayingPileProp?.GetValue(card) is { } pileEnum)
            {
                refl.CardUpdateEnergyCostMethod?.Invoke(card, [pileEnum]);
            }
        }
        _dimmedCardChildren.Clear();

        foreach (ulong hlId in _boostedHighlights)
        {
            if (GodotObject.InstanceFromId(hlId) is Control hl && GodotObject.IsInstanceValid(hl) && hl.Material is ShaderMaterial mat)
            {
                float current = mat.GetShaderParameter("width").AsSingle();
                if (current > 0.08f)
                    mat.SetShaderParameter("width", 0.075f);
                if (MathF.Abs(hl.Modulate.R - PlayableHighlightColor.R) < 0.02f)
                    hl.Modulate = new Color(0.298f, 0.980f, 1.0f, hl.Modulate.A);
            }
        }
        _boostedHighlights.Clear();
    }

    private static void LayoutCombatHudPortrait(
        Control ui,
        Control room,
        CombatReflection refl,
        PortraitHudLayout hud,
        float hudDeltaY,
        CombatRoomState state,
        float delta)
    {
        if ((ui.GetNodeOrNull<Control>("%EnergyCounterContainer") ?? ui.GetNodeOrNull<Control>("EnergyCounterContainer")) is { } energyContainer)
        {
            energyContainer.AnchorLeft = 0f;
            energyContainer.AnchorRight = 0f;
            energyContainer.AnchorTop = 0f;
            energyContainer.AnchorBottom = 0f;
            Vector2 energyPos = new(hud.EnergyContainerX, hud.EnergyContainerY + hudDeltaY);
            energyContainer.Position = energyPos;

            if (refl.EnergyCounterField?.GetValue(ui) is Control energyCounter)
            {
                energyCounter.PivotOffset = new Vector2(64f, 64f);
                energyCounter.Scale = new Vector2(hud.EnergyScale, hud.EnergyScale);
            }
        }

        if (refl.StarCounterField?.GetValue(ui) is Control starCounter && starCounter.IsVisibleInTree())
        {
            starCounter.Scale = new Vector2(hud.StarScale, hud.StarScale);
            if (starCounter.GetParent()?.Name == "CombatUi")
            {
                starCounter.AnchorLeft = 0f;
                starCounter.AnchorRight = 0f;
                starCounter.AnchorTop = 0f;
                starCounter.AnchorBottom = 0f;
                starCounter.Position = new Vector2(hud.StarX, hud.StarY + hudDeltaY);
            }
            else
            {
                // Reparented under _energyCounter during NCombatUi.Activate.
                starCounter.Position = new Vector2(108f, 4f);
            }
        }

        if (refl.FoolCombatUiField?.GetValue(ui) is Control foolUi && foolUi.IsVisibleInTree())
        {
            foolUi.AnchorLeft = 0f;
            foolUi.AnchorRight = 0f;
            foolUi.AnchorTop = 0f;
            foolUi.AnchorBottom = 0f;
            foolUi.Scale = new Vector2(0.72f, 0.72f);
            foolUi.Position = new Vector2(14f, hud.HudTopY + hudDeltaY - 190f);
        }

        // End Turn button: kill desktop ShowPos/HidePos tween and glide horizontally from right edge of the mid-bar.
        // It grows about its centre (44 points tall), so its place is moved left to keep its right edge on the canvas.
        float endTurnShowX = Math.Min(
            hud.EndTurnShowX, SafeAreaCanvas.PortraitWidth - HudEdgeMargin - 110f * (1f + EndTurnTargetScale));
        float endTurnLeftEdge = endTurnShowX + 110f * (1f - EndTurnTargetScale);
        if ((ui.GetNodeOrNull<Control>("%EndTurnButton") ?? ui.GetNodeOrNull<Control>("EndTurnButton")) is { } endTurn)
        {
            endTurn.PivotOffset = new Vector2(110f, 45f);
            endTurn.Scale = new Vector2(EndTurnTargetScale, EndTurnTargetScale);

            if (refl.EndTurnPosTweenField?.GetValue(endTurn) is Tween posTween)
            {
                if (posTween.IsRunning()) posTween.Kill();
                refl.EndTurnPosTweenField.SetValue(endTurn, null);
            }

            int btnState = Convert.ToInt32(refl.EndTurnStateField?.GetValue(endTurn) ?? 2);
            bool hidden = btnState == 2;
            Vector2 targetEndTurn = new(hidden ? hud.EndTurnHideX : endTurnShowX, hud.EndTurnShowY + hudDeltaY);
            if (!state.EndTurnInitialized || delta >= 0.5f)
            {
                endTurn.Position = targetEndTurn;
                state.EndTurnInitialized = true;
            }
            else
            {
                float a = 1f - MathF.Exp(-14f * Math.Max(0.001f, delta));
                endTurn.Position = new Vector2(Mathf.Lerp(endTurn.Position.X, targetEndTurn.X, a), targetEndTurn.Y);
            }
        }

        if ((ui.GetNodeOrNull<Control>("%PingButton") ?? ui.GetNodeOrNull<Control>("PingButton")) is { } pingBtn && pingBtn.Visible)
        {
            if (refl.PingPosTweenField?.GetValue(pingBtn) is Tween pingTween)
            {
                if (pingTween.IsRunning()) pingTween.Kill();
                refl.PingPosTweenField.SetValue(pingBtn, null);
            }
            int pingState = Convert.ToInt32(refl.PingStateField?.GetValue(pingBtn) ?? 2);
            bool pingHidden = pingState == 2;
            pingBtn.Scale = new Vector2(0.85f, 0.85f);
            // 150 by 70 about its centre (75, 35): its right edge stops short of the End Turn button, and its touch area
            // reaches 40 units above and below the picture (44 points tall, the picture stays as it was).
            float pingShowX = endTurnLeftEdge - 8f - 75f * (1f + 0.85f);
            pingBtn.Position = new Vector2(pingHidden ? 1140f : pingShowX, hud.EndTurnShowY + hudDeltaY + 10f);
            PhoneHitArea.Grow(pingBtn, 0f, 40f, 0f, 40f);
        }

        // Proceed button at end of fight: keep inside 1080-wide canvas instead of clipping at desktop X=890..1130.
        if ((room.GetNodeOrNull<Control>("%ProceedButton") ?? room.GetNodeOrNull<Control>("ProceedButton")) is { } proceedBtn)
        {
            bool enabled = (bool)(refl.ButtonIsEnabledProp?.GetValue(proceedBtn) ?? false);
            if (refl.ProceedAnimTweenField?.GetValue(proceedBtn) is Tween procTween && procTween.IsRunning())
            {
                procTween.Kill();
                refl.ProceedAnimTweenField.SetValue(proceedBtn, null);
            }
            float procW = Math.Max(200f, proceedBtn.Size.X);
            float targetX = enabled ? 1080f - procW - 24f : 1140f;
            float targetY = hud.EndTurnShowY + hudDeltaY - 6f;
            if (delta >= 0.5f)
                proceedBtn.Position = new Vector2(targetX, targetY);
            else
            {
                float a = 1f - MathF.Exp(-12f * Math.Max(0.001f, delta));
                proceedBtn.Position = new Vector2(Mathf.Lerp(proceedBtn.Position.X, targetX, a), targetY);
            }
        }

        // Draw, Discard, and Exhaust piles: 80 by 80 about their centre, drawn 44 points across.
        if ((ui.GetNodeOrNull<Control>("%CombatPileContainer") ?? ui.GetNodeOrNull<Control>("CombatPileContainer")) is { } pileContainer)
        {
            pileContainer.AnchorLeft = 0f;
            pileContainer.AnchorTop = 0f;
            pileContainer.AnchorRight = 1f;
            pileContainer.AnchorBottom = 1f;
            pileContainer.OffsetLeft = 0f;
            pileContainer.OffsetTop = 0f;
            pileContainer.OffsetRight = 0f;
            pileContainer.OffsetBottom = 0f;

            if ((pileContainer.GetNodeOrNull<Control>("%DrawPile") ?? pileContainer.GetNodeOrNull<Control>("DrawPile")) is { } drawPile)
            {
                LayoutPileButton(
                    drawPile,
                    refl,
                    new Vector2(hud.DrawShowX, hud.DrawShowY),
                    new Vector2(hud.DrawHideX, hud.DrawHideY),
                    PileTargetScale);
            }

            if ((pileContainer.GetNodeOrNull<Control>("%DiscardPile") ?? pileContainer.GetNodeOrNull<Control>("DiscardPile")) is { } discardPile)
            {
                LayoutPileButton(
                    discardPile,
                    refl,
                    new Vector2(hud.DiscardShowX, hud.DiscardShowY),
                    new Vector2(hud.DiscardHideX, hud.DiscardHideY),
                    PileTargetScale);
            }

            if ((pileContainer.GetNodeOrNull<Control>("%ExhaustPile") ?? pileContainer.GetNodeOrNull<Control>("ExhaustPile")) is { } exhaustPile)
            {
                LayoutPileButton(
                    exhaustPile,
                    refl,
                    new Vector2(hud.ExhaustShowX, hud.ExhaustShowY + hudDeltaY),
                    new Vector2(hud.ExhaustHideX, hud.ExhaustShowY + hudDeltaY),
                    PileTargetScale);
            }
        }
    }

    private static void LayoutPileButton(Control pile, CombatReflection refl, Vector2 showPos, Vector2 hidePos, float scale)
    {
        pile.PivotOffset = new Vector2(40f, 40f);
        pile.Scale = new Vector2(scale, scale);

        refl.PileShowPosField?.SetValue(pile, showPos);
        refl.PileHidePosField?.SetValue(pile, hidePos);

        Tween? tween = refl.PilePosTweenField?.GetValue(pile) as Tween;
        if (tween != null && tween.IsRunning())
            return;

        // When combat is active and the pile is visible, keep it anchored cleanly on showPos.
        if (pile.Visible && refl.IsCombatInProgress())
            pile.Position = showPos;
    }

    private void LayoutCombatStagePortrait(
        Control room,
        CombatReflection refl,
        CombatRoomState state,
        float capHeight,
        float stageTopY,
        float hudTopY,
        float canvasHeight,
        bool twoRows)
    {
        Control? sceneContainer = room.GetNodeOrNull<Control>("%CombatSceneContainer") ?? room.GetNodeOrNull<Control>("CombatSceneContainer");
        if (sceneContainer == null)
            return;

        sceneContainer.Scale = Vector2.One;
        sceneContainer.PivotOffset = sceneContainer.Size * 0.5f;

        float frontFeetY = hudTopY - (twoRows ? 165f : 220f);
        LayoutStageBackgroundPortrait(sceneContainer, capHeight, frontFeetY, canvasHeight);

        Control? allyContainer = sceneContainer.GetNodeOrNull<Control>("%AllyContainer") ?? sceneContainer.GetNodeOrNull<Control>("AllyContainer");
        Control? enemyContainer = sceneContainer.GetNodeOrNull<Control>("%EnemyContainer") ?? sceneContainer.GetNodeOrNull<Control>("EnemyContainer");
        Vector2 centerOrigin = new(540f, canvasHeight * 0.5f);

        if (allyContainer != null)
        {
            allyContainer.Scale = Vector2.One;
            allyContainer.Position = centerOrigin;
        }
        if (enemyContainer != null)
        {
            enemyContainer.Scale = Vector2.One;
            enemyContainer.Position = centerOrigin;
        }

        // Partition active creature nodes into players, pets, and enemies.
        var players = new List<Control>();
        var pets = new List<Control>();
        var currentEnemies = new List<Control>();
        bool hasOrbs = false;

        if (refl.RoomCreatureNodesField?.GetValue(room) is IEnumerable creatureList)
        {
            foreach (object? item in creatureList)
            {
                if (item is not Control creature || !GodotObject.IsInstanceValid(creature) || creature.IsQueuedForDeletion())
                    continue;

                CaptureCreatureIfNeeded(creature, refl);
                object? entity = refl.CreatureEntityProp?.GetValue(creature);
                if (entity == null) continue;

                bool isPlayerSide = refl.IsPlayerSide(entity);
                if (isPlayerSide)
                {
                    bool isPlayer = (bool)(refl.EntityIsPlayerProp?.GetValue(entity) ?? false);
                    if (isPlayer)
                    {
                        players.Add(creature);
                        if (refl.CreatureHasOrbs(creature))
                            hasOrbs = true;
                    }
                    else
                    {
                        pets.Add(creature);
                    }
                }
                else
                {
                    currentEnemies.Add(creature);
                }
            }
        }

        // Keep enemy slot indices stable when one enemy dies mid-fight; re-index only if summons increase or replace the list.
        state.SyncEnemySlotOrder(currentEnemies);

        PortraitCreatureSlot[] allySlots = PortraitCombatMath.ComputeAllyStageSlots(
            players.Count, pets.Count, hasOrbs, stageTopY, hudTopY, twoRows);

        // The game stands some enemies on the player's left (the Kaiser Crab's Crusher, with the
        // player in the middle and the Rocket on the right; its Surrounded power makes the side the player faces matter).
        // Those keep their side: the player moves to the middle and the enemies the game has on the left stand left of it.
        var leftEnemies = new List<Control>();
        if (players.Count > 0 && _creatures.TryGetValue(players[0].GetInstanceId(), out CreatureDesktopState? playerDesktop))
        {
            foreach (Control enemy in currentEnemies)
            {
                if (_creatures.TryGetValue(enemy.GetInstanceId(), out CreatureDesktopState? enemyDesktop) &&
                    enemyDesktop.DesktopX < playerDesktop.DesktopX - SurroundedSideGap)
                    leftEnemies.Add(enemy);
            }
        }
        bool surrounded = leftEnemies.Count > 0;
        if (surrounded && allySlots.Length > 0)
        {
            // The player's hit box in the middle, not its feet: the hit box sits a little right of them.
            (float lo, float hi) = HitboxLocalExtent(players[0], refl);
            float shift = SafeAreaCanvas.PortraitWidth * 0.5f - (allySlots[0].CanvasX + (lo + hi) * 0.5f * allySlots[0].BaseScale);
            for (int i = 0; i < allySlots.Length; i++)
                allySlots[i] = allySlots[i] with { CanvasX = allySlots[i].CanvasX + shift };
        }

        var orderedAllies = new List<Control>(players.Count + pets.Count);
        orderedAllies.AddRange(players);
        orderedAllies.AddRange(pets);

        float maxAllyW = orderedAllies.Count > 1 ? 210f : 270f;
        float maxAllyH = orderedAllies.Count > 1 ? 260f : 360f;
        // The widest health bar a creature may have, so the bars of neighbours in one row stay apart.
        float maxAllyBar = orderedAllies.Count > 1 ? 170f : 320f;
        for (int i = 0; i < orderedAllies.Count && i < allySlots.Length; i++)
        {
            ApplyCreatureSlot(orderedAllies[i], refl, allySlots[i], centerOrigin, maxAllyW, maxAllyH, stageTopY, maxAllyBar, isAlly: true);
        }

        if (surrounded)
        {
            LayoutSurroundingEnemies(refl, state, sceneContainer, leftEnemies, currentEnemies, centerOrigin, stageTopY, hudTopY, twoRows);
            return;
        }

        // The enemy stood much closer to the player than on a desktop, so the enemies' hit boxes start a fifth
        // of the width right of the player's (half of it from pets and other players), measured from where the allies
        // stand now.
        float playersRight = 0f, alliesRight = 0f;
        foreach (Control ally in orderedAllies)
        {
            if (HitboxCanvasRight(ally) is not float right) continue;
            alliesRight = Math.Max(alliesRight, right);
            if (players.Contains(ally)) playersRight = Math.Max(playersRight, right);
        }
        float enemyLeft = PortraitCombatMath.EnemyStageLeft(playersRight, alliesRight, LoneEnemyLeft);

        int slotCount = state.EnemySlotOrder.Count;
        PortraitCreatureSlot[] enemySlots = PortraitCombatMath.ComputeEnemyStageSlots(slotCount, stageTopY, hudTopY, twoRows);
        // Elites and bosses looked small, not threatening, so a lone enemy may take the whole stage right of the
        // player (it was held to 380 by 410 units, so the Waterfall Giant stood no taller than the player), and enemies in a
        // group may stand taller; the intents above their heads still stay under the relic strip (ApplyCreatureSlot).
        float maxEnemyW = slotCount >= 4 ? 225f : (slotCount >= 2 ? 290f : PortraitCombatMath.EnemyStageRight - enemyLeft);
        float maxEnemyH = slotCount >= 4 ? 300f : (slotCount == 3 ? 380f : (slotCount == 2 ? 420f : 600f));
        float maxEnemyBar = slotCount switch
        {
            <= 1 => 420f,
            2 => 262f,
            3 => 300f,
            4 => 264f,
            5 => 204f,
            _ => 170f,
        };

        var enemies = new Control?[Math.Min(slotCount, enemySlots.Length)];
        for (int i = 0; i < enemies.Length; i++)
        {
            ulong cid = state.EnemySlotOrder[i];
            if (cid != 0 && GodotObject.InstanceFromId(cid) is Control enemy && GodotObject.IsInstanceValid(enemy) && !enemy.IsQueuedForDeletion())
                enemies[i] = enemy;
        }

        // A group whose leftmost hit box would start nearer the player than enemyLeft is drawn smaller and nearer the right
        // edge, the same factor for every slot and size limit, so it keeps its arrangement.
        if (slotCount > 1)
        {
            float leftmost = float.MaxValue;
            for (int i = 0; i < enemies.Length; i++)
            {
                if (enemies[i] is not Control enemy) continue;
                float scale = FittedCreatureScale(enemy, refl, enemySlots[i], maxEnemyW, maxEnemyH, stageTopY, isAlly: false);
                (float lo, _) = HitboxLocalExtent(enemy, refl);
                leftmost = Math.Min(leftmost, enemySlots[i].CanvasX + lo * scale);
            }
            float k = leftmost < float.MaxValue ? PortraitCombatMath.EnemyGroupSqueeze(leftmost, enemyLeft) : 1f;
            if (k < 1f)
            {
                for (int i = 0; i < enemySlots.Length; i++)
                    enemySlots[i] = PortraitCombatMath.SqueezeEnemySlot(enemySlots[i], k);
                maxEnemyW *= k;
                maxEnemyH *= k;
                maxEnemyBar = Math.Max(MinHealthBarWidth, maxEnemyBar * k);
            }
        }

        for (int i = 0; i < enemies.Length; i++)
        {
            if (enemies[i] is not Control enemy) continue;
            ApplyCreatureSlot(enemy, refl, enemySlots[i], centerOrigin, maxEnemyW, maxEnemyH, stageTopY, maxEnemyBar, isAlly: false,
                minLeft: enemyLeft, maxRight: PortraitCombatMath.EnemyStageRight);
        }
    }

    /// <summary>Where a creature's hit box ends on the right, on the canvas; null before it has one.</summary>
    private float? HitboxCanvasRight(Control creature)
    {
        if (!_creatures.TryGetValue(creature.GetInstanceId(), out CreatureDesktopState? st) || st.Hitbox is not { } hit ||
            !GodotObject.IsInstanceValid(hit) || hit.Size.X < 1f)
            return null;
        Transform2D t = hit.GetGlobalTransformWithCanvas();
        return Math.Max((t * Vector2.Zero).X, (t * new Vector2(hit.Size.X, 0f)).X);
    }

    /// <summary>
    /// Where a creature's hit box starts and ends across it, in its own units from its feet (scale 1): the hit box of
    /// some creatures is not centred on the feet (the Waterfall Giant's is 30 units right of them). Without a hit box,
    /// its bounds centred on the feet.
    /// </summary>
    private (float Left, float Right) HitboxLocalExtent(Control creature, CombatReflection refl)
    {
        if (_creatures.TryGetValue(creature.GetInstanceId(), out CreatureDesktopState? st) && st.Hitbox is { } hit &&
            GodotObject.IsInstanceValid(hit) && hit.Size.X >= 1f && MathF.Abs(creature.Scale.X) > 0.05f)
        {
            Transform2D toLocal = creature.GetGlobalTransformWithCanvas().AffineInverse() * hit.GetGlobalTransformWithCanvas();
            float a = (toLocal * Vector2.Zero).X, b = (toLocal * new Vector2(hit.Size.X, 0f)).X;
            return (Math.Min(a, b), Math.Max(a, b));
        }
        float half = refl.GetCreatureRawBoundsSize(creature).X * 0.5f;
        return (-half, half);
    }

    /// <summary>The scale <see cref="ApplyCreatureSlot"/> draws a creature at in a slot.</summary>
    private static float FittedCreatureScale(
        Control creature, CombatReflection refl, PortraitCreatureSlot slot, float maxW, float maxH, float stageTopY, bool isAlly)
    {
        // An enemy's intents stand above its head at a fixed size: an enemy tall enough to push them over the
        // stage's top, into the relics, is drawn smaller. On an iPhone 11 Pro this bites while the hand has two rows: a
        // lone enemy over 372 units tall, and the back row over 272 (two enemies), 210 (three) or 185 (four or five).
        if (!isAlly)
            maxH = Math.Min(maxH, Math.Max(140f, slot.CanvasFeetY - stageTopY - IntentBandHeight));
        Vector2 rawBounds = refl.GetCreatureRawBoundsSize(creature);
        return PortraitCombatMath.FitCreatureScale(slot.BaseScale, rawBounds.X, rawBounds.Y, maxW, maxH);
    }

    /// <summary>
    /// Enemies on both sides of a player in the middle (the Kaiser Crab): each side gets the stage between the
    /// player and its edge, its enemies side by side in the order the game has them, at the lone enemy's size rules.
    /// </summary>
    private void LayoutSurroundingEnemies(
        CombatReflection refl,
        CombatRoomState state,
        Control sceneContainer,
        List<Control> leftEnemies,
        List<Control> currentEnemies,
        Vector2 centerOrigin,
        float stageTopY,
        float hudTopY,
        bool twoRows)
    {
        float frontFeetY = hudTopY - (twoRows ? 165f : 220f);
        float middle = SafeAreaCanvas.PortraitWidth * 0.5f;
        var right = new List<Control>();
        foreach (Control enemy in currentEnemies)
            if (!leftEnemies.Contains(enemy)) right.Add(enemy);
        // The game's slot order, so an enemy keeps its place on its side when another dies.
        int Order(Control c) => state.EnemySlotOrder.IndexOf(c.GetInstanceId());
        leftEnemies.Sort((a, b) => Order(a).CompareTo(Order(b)));
        right.Sort((a, b) => Order(a).CompareTo(Order(b)));

        void Side(List<Control> side, float from, float to)
        {
            if (side.Count == 0) return;
            float width = (to - from) / side.Count;
            for (int i = 0; i < side.Count; i++)
            {
                var slot = new PortraitCreatureSlot(from + (i + 0.5f) * width, frontFeetY - 18f, twoRows ? 0.84f : 0.88f, 1);
                ApplyCreatureSlot(side[i], refl, slot, centerOrigin, width, 600f, stageTopY, Math.Min(width - 16f, 360f), isAlly: false,
                    minLeft: from + i * width, maxRight: from + (i + 1) * width);
            }
        }

        Side(leftEnemies, StageSideMargin, middle - SurroundedPlayerHalf);
        Side(right, middle + SurroundedPlayerHalf, SafeAreaCanvas.PortraitWidth - StageSideMargin);
        if (leftEnemies.Count == 1 && right.Count == 1)
            FitBackgroundBossArms(sceneContainer, leftEnemies[0], right[0]);
    }

    /// <summary>
    /// The Kaiser Crab is one skeleton drawn in the fight's background (<c>NKaiserCrabBossBackground</c>): its body and both
    /// arms; the Crusher and the Rocket are invisible hit areas the game stands over the claws. The portrait background is
    /// drawn larger and cut at the sides, so both arms were off the screen. Where the game stood the two hit areas gives
    /// where the claws are in the background; the skeleton is drawn smaller and moved so those places land on the hit areas'
    /// portrait places (outer edge to outer edge), the whole crab across the width as the game shows it. Fitted once, and
    /// again only when the hit areas move (the hand's rows change the stage); landscape gets the game's own place back.
    /// </summary>
    private void FitBackgroundBossArms(Control sceneContainer, Control left, Control right)
    {
        Control? bgContainer = sceneContainer.GetNodeOrNull<Control>("%BgContainer") ?? sceneContainer.GetNodeOrNull<Control>("BgContainer");
        if (FindByTypeName(bgContainer, KaiserCrabBackgroundTypeName, 6) is not Node2D body || body.GetParent() is not CanvasItem parent) return;
        if (!_creatures.TryGetValue(left.GetInstanceId(), out CreatureDesktopState? l) || l.Hitbox is not { } hitL ||
            !_creatures.TryGetValue(right.GetInstanceId(), out CreatureDesktopState? r) || r.Hitbox is not { } hitR)
            return;

        // The portrait places: the hit areas' outer edges, at their middle height.
        Vector2 targetL = hitL.GetGlobalTransformWithCanvas() * new Vector2(0f, hitL.Size.Y * 0.5f);
        Vector2 targetR = hitR.GetGlobalTransformWithCanvas() * new Vector2(hitR.Size.X, hitR.Size.Y * 0.5f);
        ulong id = body.GetInstanceId();
        if (_bossArmFits.TryGetValue(id, out BossArmFit fit) &&
            (fit.TargetL - targetL).LengthSquared() < 400f && (fit.TargetR - targetR).LengthSquared() < 400f)
            return;

        // The same points where the game laid them out: in the enemies' container, whose middle is the stage's middle, as
        // the background's container (23 units right of it in the combat room's scene, both unscaled on a desktop).
        Vector2 inLeft = left.GetGlobalTransformWithCanvas().AffineInverse() * targetL;
        Vector2 inRight = right.GetGlobalTransformWithCanvas().AffineInverse() * targetR;
        Vector2 bgOffset = new(DesktopBackgroundOffsetX, 0f);
        Transform2D bgCanvas = bgContainer!.GetGlobalTransformWithCanvas();
        Vector2 clawL = bgCanvas * (l.DesktopPoint(inLeft) - bgOffset);
        Vector2 clawR = bgCanvas * (r.DesktopPoint(inRight) - bgOffset);
        float span = clawR.X - clawL.X;
        if (span < 100f || targetR.X - targetL.X < 100f) return;

        if (!_bossArmOriginals.TryGetValue(id, out (Vector2 Position, Vector2 Scale) original))
            _bossArmOriginals[id] = original = (body.Position, body.Scale);
        Transform2D parentCanvas = parent.GetGlobalTransformWithCanvas();
        Vector2 origin = parentCanvas * original.Position;
        float f = (targetR.X - targetL.X) / span;
        Vector2 newOrigin = (targetL + targetR) * 0.5f + f * (origin - (clawL + clawR) * 0.5f);
        body.Position = parentCanvas.AffineInverse() * newOrigin;
        body.Scale = original.Scale * f;
        _bossArmFits[id] = new BossArmFit(targetL, targetR);
        GD.Print($"Portrait combat: the Kaiser Crab drawn at {body.Scale.X:F2} with its arms on the Crusher ({targetL.X:F0}) and the Rocket ({targetR.X:F0}).");
    }

    /// <summary>The combat room's background container sits this far right of the stage's middle (<c>offset_left</c>).</summary>
    private const float DesktopBackgroundOffsetX = 23f;

    private static Node? FindByTypeName(Node? node, string typeName, int depth)
    {
        if (node == null || depth < 0) return null;
        if (node.GetType().FullName == typeName) return node;
        foreach (Node child in node.GetChildren())
            if (FindByTypeName(child, typeName, depth - 1) is { } found) return found;
        return null;
    }

    private void RestoreBackgroundBosses()
    {
        foreach ((ulong id, (Vector2 position, Vector2 scale)) in _bossArmOriginals)
        {
            if (GodotObject.InstanceFromId(id) is Node2D body && GodotObject.IsInstanceValid(body))
            {
                body.Position = position;
                body.Scale = scale;
            }
        }
        _bossArmOriginals.Clear();
        _bossArmFits.Clear();
    }

    private readonly record struct BossArmFit(Vector2 TargetL, Vector2 TargetR);

    private void ApplyCreatureSlot(
        Control creature,
        CombatReflection refl,
        PortraitCreatureSlot slot,
        Vector2 containerOrigin,
        float maxW,
        float maxH,
        float stageTopY,
        float maxBarWidth,
        bool isAlly,
        float minLeft = 0f,
        float maxRight = SafeAreaCanvas.PortraitWidth)
    {
        float fittedScale = FittedCreatureScale(creature, refl, slot, maxW, maxH, stageTopY, isAlly);

        // A wide creature moves in so its hit box stays between minLeft and maxRight (the hit box itself,
        // which is not always centred on the feet).
        (float lo, float hi) = HitboxLocalExtent(creature, refl);
        float fromX = minLeft - lo * fittedScale;
        float canvasX = Math.Clamp(slot.CanvasX, fromX, Math.Max(fromX, maxRight - hi * fittedScale));
        Vector2 targetLocalPos = new(canvasX - containerOrigin.X, slot.CanvasFeetY - containerOrigin.Y);
        Vector2 targetScale = new(fittedScale, fittedScale);

        creature.PivotOffset = Vector2.Zero;
        creature.ZIndex = slot.RowDepth;

        bool posChanged = (creature.Position - targetLocalPos).LengthSquared() > 0.25f;
        bool scaleChanged = MathF.Abs(creature.Scale.X - fittedScale) > 0.003f;

        if (posChanged) creature.Position = targetLocalPos;
        if (scaleChanged) creature.Scale = targetScale;

        if (isAlly && refl.CreatureOrbManagerProp?.GetValue(creature) is Control orbMgr)
        {
            var orbScale = new Vector2(0.68f, 0.68f);
            if ((orbMgr.Scale - orbScale).LengthSquared() > 0.001f)
                orbMgr.Scale = orbScale;
        }

        Vector2 visualsScale = refl.GetCreatureVisualsScale(creature);
        ulong id = creature.GetInstanceId();
        bool visualsChanged = false;
        if (_creatures.TryGetValue(id, out CreatureDesktopState? cState))
        {
            if ((cState.LastSyncedVisualsScale - visualsScale).LengthSquared() > 0.0004f)
            {
                cState.LastSyncedVisualsScale = visualsScale;
                visualsChanged = true;
            }
        }

        if (posChanged || scaleChanged || visualsChanged)
            refl.InvokeUpdateBounds(creature);

        if (cState != null)
            ApplyCreatureLabelsPortrait(creature, cState, refl, maxBarWidth, isAlly);
    }

    /// <summary>
    /// A creature's intents and its health display (bar, block, name, powers) are drawn at one size on the canvas
    /// (<see cref="IntentCanvasScale"/>, <see cref="StateDisplayCanvasScale"/>) whatever scale the creature has, so their
    /// text is 11 points. The game lays the health display out from the creature's hit box, as wide as the creature;
    /// scaled up, that would be up to twice as wide and start at the hit box's left edge. So the display is laid out again
    /// from a stand-in for the hit box (a hidden control) sized for a bar of the creature's width on the canvas, centred
    /// under it, no wider than <paramref name="maxBarWidth"/> and inside the canvas. This runs after the game's own layout
    /// (<c>NCreature.UpdateBounds</c>), and again whenever the game has laid the display out since.
    /// </summary>
    private void ApplyCreatureLabelsPortrait(
        Control creature,
        CreatureDesktopState cState,
        CombatReflection refl,
        float maxBarWidth,
        bool isAlly)
    {
        float creatureScale = creature.GetGlobalTransform().Scale.X;
        if (creatureScale < 0.05f)
            return;

        if (!isAlly && cState.Intents is { } intents && GodotObject.IsInstanceValid(intents))
        {
            // Grown from the bottom centre, so the intents rise above the head instead of sinking into it. The game owns
            // the row's position, so a row of several intents that would pass a side of the canvas is moved back in
            // through the pivot: a pivot d units right of the centre moves the scaled row d * (scale - 1) left.
            float intentScale = IntentCanvasScale / creatureScale;
            float pivotX = intents.Size.X * 0.5f;
            int shown = 0;
            foreach (Node intent in intents.GetChildren())
            {
                if (intent is Control { Visible: true } && !intent.IsQueuedForDeletion())
                    shown++;
            }
            if (shown > 1 && intentScale > 1.05f)
            {
                float rowHalf = (shown * 64f - (shown - 1) * 10f) * IntentCanvasScale * 0.5f;
                float center = creature.GlobalPosition.X + (intents.Position.X + intents.Size.X * 0.5f) * creatureScale;
                float clamped = LeftEdgeOnCanvas(center, rowHalf * 2f) + rowHalf;
                pivotX += (center - clamped) / (creatureScale * (intentScale - 1f));
            }
            var intentPivot = new Vector2(pivotX, intents.Size.Y);
            if ((intents.PivotOffset - intentPivot).LengthSquared() > 0.25f)
                intents.PivotOffset = intentPivot;
            if (MathF.Abs(intents.Scale.X - intentScale) > 0.002f)
                intents.Scale = new Vector2(intentScale, intentScale);
        }

        if (!refl.CanLayOutStateDisplay || cState.StateDisplayLayoutFailed ||
            cState.Display is not { } display || !GodotObject.IsInstanceValid(display) ||
            cState.Hitbox is not { } hitbox || !GodotObject.IsInstanceValid(hitbox) ||
            cState.Nameplate is not { } nameplate || !GodotObject.IsInstanceValid(nameplate) ||
            cState.HpBar is not { } hpBar || !GodotObject.IsInstanceValid(hpBar))
            return;

        float displayScale = StateDisplayCanvasScale / creatureScale;
        if (display.PivotOffset != Vector2.Zero)
            display.PivotOffset = Vector2.Zero;
        if (MathF.Abs(display.Scale.X - displayScale) > 0.002f)
            display.Scale = new Vector2(displayScale, displayScale);

        Control? powerContainer = cState.PowerContainer is { } p && GodotObject.IsInstanceValid(p) ? p : null;
        if (powerContainer != null)
        {
            foreach (Node power in powerContainer.GetChildren())
                EnlargeLabelFont(power.GetNodeOrNull<Label>("%AmountLabel") ?? power.GetNodeOrNull<Label>("AmountLabel"), PowerAmountFontSize);
        }

        // One unit of the display on the canvas, and the bar's place there: as wide as the game draws it (the hit box and
        // what the game adds to it), centred under the hit box.
        float unit = creatureScale * display.Scale.X;
        float hitWidth = hitbox.Size.X * creatureScale;
        float barWidth = Math.Clamp(
            hitWidth + cState.BarExtraWidth * creatureScale, Math.Min(MinHealthBarWidth, maxBarWidth), maxBarWidth);
        float barLeft = LeftEdgeOnCanvas(hitbox.GlobalPosition.X + hitWidth * 0.5f, barWidth);
        // The name may be wider than the bar: the game shrinks a name that does not fit, and only one name shows at a time.
        float nameWidth = Math.Max(barWidth, MinNameplateWidth);
        float nameLeft = LeftEdgeOnCanvas(barLeft + barWidth * 0.5f, nameWidth);
        float nameLocalX = (nameLeft - display.GlobalPosition.X) / unit;
        float nameLocalWidth = nameWidth / unit;

        // The name plate is the marker: the game's layout puts it at the hit box, this one puts it here.
        if (MathF.Abs(nameplate.Position.X - nameLocalX) <= 0.75f && MathF.Abs(nameplate.Size.X - nameLocalWidth) <= 0.75f)
            return;

        Control? bounds = cState.EnsureBoundsStandIn(creature);
        if (bounds == null)
            return;

        // The bar: the game makes it wider than the bounds by its extra width in display units, and starts it half that
        // many canvas units left of them.
        float boundsY = hitbox.GlobalPosition.Y;
        bounds.Size = new Vector2(Math.Max(8f, barWidth / unit - cState.BarExtraWidth), 8f);
        bounds.GlobalPosition = new Vector2(barLeft + cState.BarExtraWidth * 0.5f, boundsY);
        if (!refl.LayOutStateDisplay(display, bounds))
        {
            // Scaled up with the game's own layout the display would be far wider than its creature: this creature keeps
            // the game's size and layout from here on.
            cState.GiveUpStateDisplay(creature, refl);
            return;
        }

        // The powers: rows of as many icons as fit under the bar, from its left end (the game adds 25 units to the width
        // and rounds the icons in a row up).
        if (powerContainer != null)
        {
            int perRow = Math.Max(1, (int)MathF.Floor(barWidth / (PowerIconPitch * unit)));
            bounds.Size = new Vector2(perRow * PowerIconPitch - 26f, 8f);
            bounds.GlobalPosition = new Vector2(barLeft, boundsY);
            refl.LayOutPowerContainer(powerContainer, bounds);
        }

        nameplate.Position = new Vector2(nameLocalX, nameplate.Position.Y);
        nameplate.Size = new Vector2(nameLocalWidth, nameplate.Size.Y);

        // The bar's touch area (258 units wide in the scene) follows the bar, so it does not reach over a neighbour's bar.
        if (cState.HpBarHitbox is { } barHitbox && GodotObject.IsInstanceValid(barHitbox))
        {
            barHitbox.OffsetLeft = hpBar.Position.X - 8f;
            barHitbox.OffsetRight = hpBar.Position.X + barWidth / unit + 8f;
        }
    }

    /// <summary>The left edge of something <paramref name="width"/> wide centred on <paramref name="centerX"/>, kept on the canvas.</summary>
    private static float LeftEdgeOnCanvas(float centerX, float width)
    {
        float half = width * 0.5f;
        float min = StageSideMargin + half;
        float max = SafeAreaCanvas.PortraitWidth - StageSideMargin - half;
        return (max < min ? SafeAreaCanvas.PortraitWidth * 0.5f : Math.Clamp(centerX, min, max)) - half;
    }

    private static void LayoutStageBackgroundPortrait(
        Control sceneContainer,
        float capHeight,
        float frontFeetY,
        float canvasHeight)
    {
        Control? bgContainer = sceneContainer.GetNodeOrNull<Control>("%BgContainer") ?? sceneContainer.GetNodeOrNull<Control>("BgContainer");
        if (bgContainer == null)
            return;

        Control? bgRoot = null;
        foreach (Node child in bgContainer.GetChildren())
        {
            if (child is Control c)
            {
                bgRoot = c;
                break;
            }
        }

        int populatedLayers = 0;
        Control? layer00 = null;
        Control? foreground = null;
        if (bgRoot != null)
        {
            foreach (Node child in bgRoot.GetChildren())
            {
                if (child is not Control layer) continue;
                string name = layer.Name.ToString();
                if (name.StartsWith("Layer_", StringComparison.Ordinal) && layer.GetChildCount() > 0)
                {
                    populatedLayers++;
                    if (name == "Layer_00") layer00 = layer;
                }
                else if (name == "Foreground" && layer.GetChildCount() > 0)
                {
                    foreground = layer;
                }
            }
        }

        if (populatedLayers >= 2 && layer00 != null)
        {
            const float wallScale = 1.28f;
            // Place wall/floor horizon (+185 unscaled in BgContainer) right at frontFeetY so zero empty floor gap sits above the hand.
            float desiredCenterY = frontFeetY - 185f * wallScale;
            float maxCenterY = 648f * wallScale; // keeps top of wall <= 0
            float bgCenterY = Math.Min(desiredCenterY, maxCenterY);
            float offsetY = bgCenterY - canvasHeight * 0.5f;

            bgContainer.Scale = new Vector2(wallScale, wallScale);
            bgContainer.OffsetLeft = 0f;
            bgContainer.OffsetRight = 0f;
            bgContainer.OffsetTop = offsetY;
            bgContainer.OffsetBottom = offsetY;

            // Extend the base stone/moss floor layer seamlessly down behind the hand and bottom piles.
            layer00.Scale = new Vector2(1.55f, 2.35f);
            layer00.Position = new Vector2(0f, 380f);

            if (foreground != null)
            {
                float bottomDeltaLocal = (canvasHeight - (bgCenterY + 648f * wallScale)) / wallScale;
                foreground.Position = new Vector2(0f, Math.Max(0f, bottomDeltaLocal));
            }
        }
        else
        {
            float fullCoverScale = Math.Max(1.92f, (canvasHeight + 220f) / 1296f);
            bgContainer.Scale = new Vector2(fullCoverScale, fullCoverScale);
            bgContainer.OffsetLeft = 0f;
            bgContainer.OffsetRight = 0f;
            bgContainer.OffsetTop = 0f;
            bgContainer.OffsetBottom = 0f;
        }
    }

    private void CaptureCreatureIfNeeded(Control creature, CombatReflection refl)
    {
        ulong id = creature.GetInstanceId();
        if (_creatures.ContainsKey(id))
            return;
        _creatures[id] = CreatureDesktopState.Capture(creature, refl);
    }

    private CombatReflection EnsureReflection() => _reflection ??= new CombatReflection(typeof(NGame).Assembly);

    private sealed class CreatureDesktopState
    {
        private const string BoundsStandInName = "PortraitStateBounds";

        private readonly Vector2 _position;
        private readonly Vector2 _scale;
        private readonly Vector2 _pivotOffset;
        private readonly int _zIndex;
        private readonly Vector2 _orbScale;
        private readonly Vector2 _displayScale, _displayPivot;
        private readonly Vector2 _intentScale, _intentPivot;
        private readonly float _barHitboxLeft, _barHitboxRight;
        private Control? _boundsStandIn;
        public Vector2 LastSyncedVisualsScale;

        /// <summary>
        /// What the game adds to the hit box's width for this creature's health bar (<c>NHealthBar.UpdateLayoutForCreatureBounds</c>):
        /// nothing for a player, 24 for a monster less the monster's own reduction.
        /// </summary>
        public readonly float BarExtraWidth;

        /// <summary>The game's layout call failed for this creature: its health display stays as the game draws it.</summary>
        public bool StateDisplayLayoutFailed { get; private set; }

        /// <summary>The creature's parts drawn larger in portrait: its health display and what that holds, its hit box and its intents.</summary>
        public readonly Control? Display, Hitbox, Intents, Nameplate, HpBar, HpBarHitbox, PowerContainer;

        private CreatureDesktopState(Control creature, CombatReflection refl)
        {
            _position = creature.Position;
            _scale = creature.Scale;
            _pivotOffset = creature.PivotOffset;
            _zIndex = creature.ZIndex;
            _orbScale = refl.CreatureOrbManagerProp?.GetValue(creature) is Control orbMgr ? orbMgr.Scale : Vector2.One;

            // In the creature's scene the health display (NCreatureStateDisplay) is the node named HealthBar; inside it the
            // bar itself (NHealthBar) has that name again.
            Display = creature.GetNodeOrNull<Control>("%HealthBar") ?? creature.GetNodeOrNull<Control>("HealthBar");
            Hitbox = creature.GetNodeOrNull<Control>("%Hitbox") ?? creature.GetNodeOrNull<Control>("Hitbox");
            Intents = creature.GetNodeOrNull<Control>("%Intents") ?? creature.GetNodeOrNull<Control>("Intents");
            Control? healthBar = Display?.GetNodeOrNull<Control>("%HealthBar") ?? Display?.GetNodeOrNull<Control>("HealthBar");
            HpBar = healthBar?.GetNodeOrNull<Control>("%HpBarContainer") ?? healthBar?.GetNodeOrNull<Control>("HpBarContainer");
            HpBarHitbox = Display?.GetNodeOrNull<Control>("%HpBarHitbox") ?? healthBar?.GetNodeOrNull<Control>("HpBarHitbox");
            Nameplate = Display?.GetNodeOrNull<Control>("%NameplateContainer") ?? Display?.GetNodeOrNull<Control>("NameplateContainer");
            PowerContainer = Display?.GetNodeOrNull<Control>("%PowerContainer") ?? Display?.GetNodeOrNull<Control>("PowerContainer");

            _displayScale = Display?.Scale ?? Vector2.One;
            _displayPivot = Display?.PivotOffset ?? Vector2.Zero;
            _intentScale = Intents?.Scale ?? Vector2.One;
            _intentPivot = Intents?.PivotOffset ?? new Vector2(500f, 20f);
            _barHitboxLeft = HpBarHitbox?.OffsetLeft ?? -138f;
            _barHitboxRight = HpBarHitbox?.OffsetRight ?? 120f;
            BarExtraWidth = refl.GetHealthBarExtraWidth(creature);
        }

        public static CreatureDesktopState Capture(Control creature, CombatReflection refl) => new(creature, refl);

        /// <summary>Where the game stood the creature across the stage (its container's units; the stage's middle is 0).</summary>
        public float DesktopX => _position.X;

        /// <summary>Where a point of the creature (its own units) was in its container when the game laid it out.</summary>
        public Vector2 DesktopPoint(Vector2 local) => _position + _pivotOffset + _scale * (local - _pivotOffset);

        /// <summary>
        /// The hidden control that stands in for the creature's hit box when the health display is laid out again: the
        /// game reads its left edge on the canvas and its width. A child of the creature, so it goes when the creature
        /// does; null while the creature cannot take a child yet.
        /// </summary>
        public Control? EnsureBoundsStandIn(Control creature)
        {
            if (_boundsStandIn is { } known && GodotObject.IsInstanceValid(known) && known.IsInsideTree())
                return known;
            if (!creature.IsInsideTree() || !creature.IsNodeReady())
                return null;

            var standIn = new Control
            {
                Name = BoundsStandInName,
                Visible = false,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                FocusMode = Control.FocusModeEnum.None,
            };
            creature.AddChild(standIn);
            if (!standIn.IsInsideTree())
            {
                standIn.Free();
                return null;
            }
            _boundsStandIn = standIn;
            return standIn;
        }

        public void GiveUpStateDisplay(Control creature, CombatReflection refl)
        {
            StateDisplayLayoutFailed = true;
            RestoreStateDisplay();
            try
            {
                refl.InvokeUpdateBounds(creature);
            }
            catch (Exception)
            {
                // The game's own layout fails for this creature too: nothing more to put back.
            }
        }

        private void RestoreStateDisplay()
        {
            if (Display != null && GodotObject.IsInstanceValid(Display))
            {
                Display.Scale = _displayScale;
                Display.PivotOffset = _displayPivot;
            }
            if (HpBarHitbox != null && GodotObject.IsInstanceValid(HpBarHitbox))
            {
                HpBarHitbox.OffsetLeft = _barHitboxLeft;
                HpBarHitbox.OffsetRight = _barHitboxRight;
            }
            if (_boundsStandIn != null && GodotObject.IsInstanceValid(_boundsStandIn))
                _boundsStandIn.QueueFree();
            _boundsStandIn = null;
        }

        public void Restore(Control creature, CombatReflection refl)
        {
            // The larger health display and intents first: the game lays the display out again from the hit box below.
            RestoreStateDisplay();
            if (Intents != null && GodotObject.IsInstanceValid(Intents))
            {
                Intents.Scale = _intentScale;
                Intents.PivotOffset = _intentPivot;
            }

            creature.PivotOffset = _pivotOffset;
            creature.Scale = _scale;
            creature.Position = _position;
            creature.ZIndex = _zIndex;
            if (refl.CreatureOrbManagerProp?.GetValue(creature) is Control orbMgr)
                orbMgr.Scale = _orbScale;
            refl.InvokeUpdateBounds(creature);
        }
    }

    private sealed class CombatRoomState
    {
        public bool HasSmoothedHud;
        public float SmoothedHudTopY;
        public bool EndTurnInitialized;
        public readonly List<ulong> EnemySlotOrder = new();

        private readonly Vector2 _sceneScale, _scenePivot, _scenePos;
        private readonly Vector2 _bgScale;
        private readonly float _bgOffLeft, _bgOffTop, _bgOffRight, _bgOffBottom;
        private readonly Vector2 _layer00Scale, _layer00Pos;
        private readonly Vector2 _fgPos;
        private readonly float _energyAnchorTop, _energyAnchorBottom, _energyOffLeft, _energyOffTop, _energyOffRight, _energyOffBottom;
        private readonly Vector2 _energyScale;
        private readonly Vector2 _starScale, _starPos;
        private readonly Vector2 _foolScale, _foolPos;
        private readonly Vector2 _drawShow, _drawHide, _drawScale;
        private readonly Vector2 _discardShow, _discardHide, _discardScale;
        private readonly Vector2 _exhaustShow, _exhaustHide, _exhaustScale;

        private CombatRoomState(
            Vector2 sceneScale,
            Vector2 scenePivot,
            Vector2 scenePos,
            Vector2 bgScale,
            float bgOffLeft,
            float bgOffTop,
            float bgOffRight,
            float bgOffBottom,
            Vector2 layer00Scale,
            Vector2 layer00Pos,
            Vector2 fgPos,
            float energyAnchorTop,
            float energyAnchorBottom,
            float energyOffLeft,
            float energyOffTop,
            float energyOffRight,
            float energyOffBottom,
            Vector2 energyScale,
            Vector2 starScale,
            Vector2 starPos,
            Vector2 foolScale,
            Vector2 foolPos,
            Vector2 drawShow,
            Vector2 drawHide,
            Vector2 drawScale,
            Vector2 discardShow,
            Vector2 discardHide,
            Vector2 discardScale,
            Vector2 exhaustShow,
            Vector2 exhaustHide,
            Vector2 exhaustScale)
        {
            _sceneScale = sceneScale;
            _scenePivot = scenePivot;
            _scenePos = scenePos;
            _bgScale = bgScale;
            _bgOffLeft = bgOffLeft;
            _bgOffTop = bgOffTop;
            _bgOffRight = bgOffRight;
            _bgOffBottom = bgOffBottom;
            _layer00Scale = layer00Scale;
            _layer00Pos = layer00Pos;
            _fgPos = fgPos;
            _energyAnchorTop = energyAnchorTop;
            _energyAnchorBottom = energyAnchorBottom;
            _energyOffLeft = energyOffLeft;
            _energyOffTop = energyOffTop;
            _energyOffRight = energyOffRight;
            _energyOffBottom = energyOffBottom;
            _energyScale = energyScale;
            _starScale = starScale;
            _starPos = starPos;
            _foolScale = foolScale;
            _foolPos = foolPos;
            _drawShow = drawShow;
            _drawHide = drawHide;
            _drawScale = drawScale;
            _discardShow = discardShow;
            _discardHide = discardHide;
            _discardScale = discardScale;
            _exhaustShow = exhaustShow;
            _exhaustHide = exhaustHide;
            _exhaustScale = exhaustScale;
        }

        public static CombatRoomState Capture(Control room, CombatReflection refl)
        {
            Control? scene = room.GetNodeOrNull<Control>("%CombatSceneContainer") ?? room.GetNodeOrNull<Control>("CombatSceneContainer");
            Control? bg = scene?.GetNodeOrNull<Control>("%BgContainer") ?? scene?.GetNodeOrNull<Control>("BgContainer");
            Control? bgRoot = null;
            if (bg != null)
            {
                foreach (Node child in bg.GetChildren())
                {
                    if (child is Control c) { bgRoot = c; break; }
                }
            }
            Control? layer00 = bgRoot?.GetNodeOrNull<Control>("Layer_00");
            Control? fg = bgRoot?.GetNodeOrNull<Control>("Foreground");

            Control? ui = room.GetNodeOrNull<Control>("%CombatUi") ?? room.GetNodeOrNull<Control>("CombatUi");
            Control? energyCont = ui?.GetNodeOrNull<Control>("%EnergyCounterContainer") ?? ui?.GetNodeOrNull<Control>("EnergyCounterContainer");
            Control? energy = (ui != null ? refl.EnergyCounterField?.GetValue(ui) as Control : null);
            Control? star = (ui != null ? refl.StarCounterField?.GetValue(ui) as Control : null);
            Control? fool = (ui != null ? refl.FoolCombatUiField?.GetValue(ui) as Control : null);
            Control? piles = ui?.GetNodeOrNull<Control>("%CombatPileContainer") ?? ui?.GetNodeOrNull<Control>("CombatPileContainer");
            Control? draw = piles?.GetNodeOrNull<Control>("%DrawPile") ?? piles?.GetNodeOrNull<Control>("DrawPile");
            Control? discard = piles?.GetNodeOrNull<Control>("%DiscardPile") ?? piles?.GetNodeOrNull<Control>("DiscardPile");
            Control? exhaust = piles?.GetNodeOrNull<Control>("%ExhaustPile") ?? piles?.GetNodeOrNull<Control>("ExhaustPile");

            return new CombatRoomState(
                scene?.Scale ?? Vector2.One,
                scene?.PivotOffset ?? new Vector2(960f, 540f),
                scene?.Position ?? Vector2.Zero,
                bg?.Scale ?? Vector2.One,
                bg?.OffsetLeft ?? 23f,
                bg?.OffsetTop ?? 0f,
                bg?.OffsetRight ?? 23f,
                bg?.OffsetBottom ?? 0f,
                layer00?.Scale ?? Vector2.One,
                layer00?.Position ?? Vector2.Zero,
                fg?.Position ?? Vector2.Zero,
                energyCont?.AnchorTop ?? 1f,
                energyCont?.AnchorBottom ?? 1f,
                energyCont?.OffsetLeft ?? 100f,
                energyCont?.OffsetTop ?? -252f,
                energyCont?.OffsetRight ?? 100f,
                energyCont?.OffsetBottom ?? -252f,
                energy?.Scale ?? Vector2.One,
                star?.Scale ?? new Vector2(0.8f, 0.8f),
                star?.Position ?? Vector2.Zero,
                fool?.Scale ?? Vector2.One,
                fool?.Position ?? Vector2.Zero,
                ReadVec(refl.PileShowPosField, draw, new Vector2(15f, 985f)),
                ReadVec(refl.PileHidePosField, draw, new Vector2(-135f, 1085f)),
                draw?.Scale ?? Vector2.One,
                ReadVec(refl.PileShowPosField, discard, new Vector2(1586f, 985f)),
                ReadVec(refl.PileHidePosField, discard, new Vector2(1736f, 1085f)),
                discard?.Scale ?? Vector2.One,
                ReadVec(refl.PileShowPosField, exhaust, new Vector2(1590f, 790f)),
                ReadVec(refl.PileHidePosField, exhaust, new Vector2(1740f, 790f)),
                exhaust?.Scale ?? Vector2.One);
        }

        public void SyncEnemySlotOrder(List<Control> currentEnemies)
        {
            var currentIds = new HashSet<ulong>(currentEnemies.Count);
            foreach (Control c in currentEnemies)
                currentIds.Add(c.GetInstanceId());

            bool newSummon = false;
            foreach (ulong id in currentIds)
            {
                if (!EnemySlotOrder.Contains(id))
                {
                    newSummon = true;
                    break;
                }
            }

            if (EnemySlotOrder.Count == 0 || newSummon)
            {
                EnemySlotOrder.Clear();
                foreach (Control c in currentEnemies)
                    EnemySlotOrder.Add(c.GetInstanceId());
                return;
            }

            // Mark freed slots as 0 so surviving enemies stay on their assigned formation coordinates.
            for (int i = 0; i < EnemySlotOrder.Count; i++)
            {
                ulong id = EnemySlotOrder[i];
                if (id != 0 && !currentIds.Contains(id))
                    EnemySlotOrder[i] = 0;
            }
        }

        public void Restore(Control room, CombatReflection refl)
        {
            Control? scene = room.GetNodeOrNull<Control>("%CombatSceneContainer") ?? room.GetNodeOrNull<Control>("CombatSceneContainer");
            if (scene != null)
            {
                scene.PivotOffset = _scenePivot;
                scene.Scale = _sceneScale;
                scene.Position = _scenePos;

                if ((scene.GetNodeOrNull<Control>("%BgContainer") ?? scene.GetNodeOrNull<Control>("BgContainer")) is { } bg)
                {
                    bg.Scale = _bgScale;
                    bg.OffsetLeft = _bgOffLeft;
                    bg.OffsetTop = _bgOffTop;
                    bg.OffsetRight = _bgOffRight;
                    bg.OffsetBottom = _bgOffBottom;
                    foreach (Node child in bg.GetChildren())
                    {
                        if (child is not Control bgRoot) continue;
                        if (bgRoot.GetNodeOrNull<Control>("Layer_00") is { } l0)
                        {
                            l0.Scale = _layer00Scale;
                            l0.Position = _layer00Pos;
                        }
                        if (bgRoot.GetNodeOrNull<Control>("Foreground") is { } fg)
                            fg.Position = _fgPos;
                        break;
                    }
                    scene.Call("OnWindowChange");
                }

                if ((scene.GetNodeOrNull<Control>("%AllyContainer") ?? scene.GetNodeOrNull<Control>("AllyContainer")) is { } ally)
                    ally.Position = scene.Size * 0.5f;
                if ((scene.GetNodeOrNull<Control>("%EnemyContainer") ?? scene.GetNodeOrNull<Control>("EnemyContainer")) is { } enemy)
                    enemy.Position = scene.Size * 0.5f;
            }

            refl.RoomAdjustScaleMethod?.Invoke(room, null);

            if (refl.RoomCreatureNodesField?.GetValue(room) is IEnumerable creatures)
            {
                foreach (object? item in creatures)
                {
                    if (item is Control c && GodotObject.IsInstanceValid(c))
                        refl.InvokeUpdateBounds(c);
                }
            }

            Control? ui = room.GetNodeOrNull<Control>("%CombatUi") ?? room.GetNodeOrNull<Control>("CombatUi");
            if (ui != null)
            {
                if ((ui.GetNodeOrNull<Control>("%EnergyCounterContainer") ?? ui.GetNodeOrNull<Control>("EnergyCounterContainer")) is { } energyCont)
                {
                    energyCont.AnchorLeft = 0f;
                    energyCont.AnchorRight = 0f;
                    energyCont.AnchorTop = _energyAnchorTop;
                    energyCont.AnchorBottom = _energyAnchorBottom;
                    energyCont.OffsetLeft = _energyOffLeft;
                    energyCont.OffsetTop = _energyOffTop;
                    energyCont.OffsetRight = _energyOffRight;
                    energyCont.OffsetBottom = _energyOffBottom;
                }
                if (refl.EnergyCounterField?.GetValue(ui) is Control energy)
                    energy.Scale = _energyScale;
                if (refl.StarCounterField?.GetValue(ui) is Control star)
                {
                    star.Scale = _starScale;
                    star.Position = _starPos;
                }
                if (refl.FoolCombatUiField?.GetValue(ui) is Control fool)
                {
                    fool.Scale = _foolScale;
                    fool.Position = _foolPos;
                }

                if ((ui.GetNodeOrNull<Control>("%EndTurnButton") ?? ui.GetNodeOrNull<Control>("EndTurnButton")) is { } endTurn)
                {
                    endTurn.Scale = Vector2.One;
                    int btnState = Convert.ToInt32(refl.EndTurnStateField?.GetValue(endTurn) ?? 2);
                    PropertyInfo? posProp = btnState == 2 ? refl.EndTurnHidePosProp : refl.EndTurnShowPosProp;
                    if (posProp?.GetValue(endTurn) is Vector2 landscapePos)
                        endTurn.Position = landscapePos;
                }

                if ((ui.GetNodeOrNull<Control>("%PingButton") ?? ui.GetNodeOrNull<Control>("PingButton")) is { } pingBtn)
                {
                    PhoneHitArea.Remove(pingBtn);
                    pingBtn.Scale = Vector2.One;
                    int pState = Convert.ToInt32(refl.PingStateField?.GetValue(pingBtn) ?? 2);
                    PropertyInfo? pPos = pState == 2 ? refl.PingHidePosProp : refl.PingShowPosProp;
                    if (pPos?.GetValue(pingBtn) is Vector2 lp)
                        pingBtn.Position = lp;
                }

                if ((ui.GetNodeOrNull<Control>("%CombatPileContainer") ?? ui.GetNodeOrNull<Control>("CombatPileContainer")) is { } piles)
                {
                    Vector2 vpSize = room.GetViewportRect().Size;
                    if ((piles.GetNodeOrNull<Control>("%DrawPile") ?? piles.GetNodeOrNull<Control>("DrawPile")) is { } draw)
                    {
                        draw.Scale = _drawScale;
                        Vector2 show = new(15f, vpSize.Y - 95f);
                        Vector2 hide = show + new Vector2(-150f, 100f);
                        refl.PileShowPosField?.SetValue(draw, show);
                        refl.PileHidePosField?.SetValue(draw, hide);
                        if (draw.Visible) draw.Position = show;
                    }
                    if ((piles.GetNodeOrNull<Control>("%DiscardPile") ?? piles.GetNodeOrNull<Control>("DiscardPile")) is { } discard)
                    {
                        discard.Scale = _discardScale;
                        Vector2 show = new(vpSize.X - 94f, vpSize.Y - 95f);
                        Vector2 hide = show + new Vector2(150f, 100f);
                        refl.PileShowPosField?.SetValue(discard, show);
                        refl.PileHidePosField?.SetValue(discard, hide);
                        if (discard.Visible) discard.Position = show;
                    }
                    if ((piles.GetNodeOrNull<Control>("%ExhaustPile") ?? piles.GetNodeOrNull<Control>("ExhaustPile")) is { } exhaust)
                    {
                        exhaust.Scale = _exhaustScale;
                        refl.ExhaustSetAnimPositionsMethod?.Invoke(exhaust, null);
                        if (exhaust.Visible && refl.PileShowPosField?.GetValue(exhaust) is Vector2 show)
                            exhaust.Position = show;
                    }
                }

                if ((ui.GetNodeOrNull<Control>("%Hand") ?? ui.GetNodeOrNull<Control>("Hand")) is { } hand)
                {
                    refl.HandForceRefreshMethod?.Invoke(hand, null);
                }
            }

            if ((room.GetNodeOrNull<Control>("%ProceedButton") ?? room.GetNodeOrNull<Control>("ProceedButton")) is { } proceedBtn)
            {
                bool enabled = (bool)(refl.ButtonIsEnabledProp?.GetValue(proceedBtn) ?? false);
                PropertyInfo? prop = enabled ? refl.ProceedShowPosProp : refl.ProceedHidePosProp;
                if (prop?.GetValue(proceedBtn) is Vector2 p)
                    proceedBtn.Position = p;
            }
        }

        private static Vector2 ReadVec(FieldInfo? field, object? target, Vector2 fallback) =>
            target != null && field?.GetValue(target) is Vector2 v ? v : fallback;
    }

    private sealed class CombatReflection
    {
        private readonly PropertyInfo? _combatRoomInstanceProp;
        private readonly PropertyInfo? _combatMgrInstanceProp;
        private readonly PropertyInfo? _combatMgrInProgressProp;

        public readonly FieldInfo? RoomCreatureNodesField;
        public readonly MethodInfo? RoomAdjustScaleMethod;

        public readonly FieldInfo? EnergyCounterField;
        public readonly FieldInfo? StarCounterField;
        public readonly FieldInfo? FoolCombatUiField;

        public readonly PropertyInfo? HandInCardPlayProp;
        public readonly FieldInfo? HandIsDisabledField;
        public readonly FieldInfo? HandDraggedIndexField;
        public readonly PropertyInfo? HandFocusedHolderProp;
        public readonly MethodInfo? HandForceRefreshMethod;

        public readonly PropertyInfo? CardHolderCardNodeProp;
        public readonly FieldInfo? HolderTargetPosField;
        public readonly FieldInfo? HolderTargetScaleField;
        public readonly FieldInfo? HolderTargetAngleField;
        public readonly MethodInfo? HolderSetTargetPosMethod;
        public readonly MethodInfo? HolderSetTargetScaleMethod;
        public readonly MethodInfo? HolderSetTargetAngleMethod;
        public readonly MethodInfo? HolderSetScaleInstantlyMethod;
        public readonly MethodInfo? HolderSetAngleInstantlyMethod;
        public readonly PropertyInfo? HolderGlowGoldProp;
        public readonly PropertyInfo? HolderGlowRedProp;

        public readonly PropertyInfo? CardBodyProp;
        public readonly PropertyInfo? CardHighlightProp;
        public readonly PropertyInfo? CardModelProp;
        public readonly PropertyInfo? CardDisplayingPileProp;
        public readonly FieldInfo? CardEnergyLabelField;
        public readonly FieldInfo? CardUnplayableEnergyIconField;
        public readonly MethodInfo? CardUpdateEnergyCostMethod;
        public readonly MethodInfo? CardModelCanPlayMethod;
        public readonly FieldInfo? HighlightTweenField;

        public readonly FieldInfo? EndTurnStateField;
        public readonly FieldInfo? EndTurnPosTweenField;
        public readonly PropertyInfo? EndTurnShowPosProp;
        public readonly PropertyInfo? EndTurnHidePosProp;

        public readonly FieldInfo? PingStateField;
        public readonly FieldInfo? PingPosTweenField;
        public readonly PropertyInfo? PingShowPosProp;
        public readonly PropertyInfo? PingHidePosProp;

        public readonly PropertyInfo? ButtonIsEnabledProp;
        public readonly FieldInfo? ProceedAnimTweenField;
        public readonly PropertyInfo? ProceedShowPosProp;
        public readonly PropertyInfo? ProceedHidePosProp;

        public readonly FieldInfo? PileShowPosField;
        public readonly FieldInfo? PileHidePosField;
        public readonly FieldInfo? PilePosTweenField;
        public readonly MethodInfo? ExhaustSetAnimPositionsMethod;

        public readonly PropertyInfo? CreatureEntityProp;
        public readonly PropertyInfo? CreatureVisualsProp;
        public readonly PropertyInfo? CreatureOrbManagerProp;
        private readonly MethodInfo? _creatureUpdateBoundsMethod;
        private readonly MethodInfo? _stateDisplaySetBoundsMethod;
        private readonly MethodInfo? _powerContainerSetBoundsMethod;
        private bool _stateDisplayFailureLogged;
        public readonly PropertyInfo? EntityIsPlayerProp;
        private readonly PropertyInfo? _entitySideProp;
        private readonly PropertyInfo? _entityPlayerProp;
        private readonly PropertyInfo? _entityMonsterProp;
        private readonly PropertyInfo? _monsterBarReductionProp;
        private readonly PropertyInfo? _playerCombatStateProp;
        private readonly PropertyInfo? _orbQueueProp;
        private readonly PropertyInfo? _orbCapacityProp;
        private readonly PropertyInfo? _visualsBoundsProp;

        public CombatReflection(Assembly sts2)
        {
            const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags StaticAll = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            Type? combatRoomType = sts2.GetType(CombatRoomTypeName);
            _combatRoomInstanceProp = combatRoomType?.GetProperty("Instance", StaticAll);
            RoomCreatureNodesField = FindField(combatRoomType, "_creatureNodes");
            RoomAdjustScaleMethod = combatRoomType?.GetMethod("AdjustCreatureScaleForAspectRatio", InstAll);

            Type? combatMgrType = sts2.GetType("MegaCrit.Sts2.Core.Combat.CombatManager");
            _combatMgrInstanceProp = combatMgrType?.GetProperty("Instance", StaticAll);
            _combatMgrInProgressProp = combatMgrType?.GetProperty("IsInProgress", InstAll);

            Type? combatUiType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NCombatUi");
            EnergyCounterField = FindField(combatUiType, "_energyCounter");
            StarCounterField = FindField(combatUiType, "_starCounter");
            FoolCombatUiField = FindField(combatUiType, "_foolCombatUi");

            Type? handType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand");
            HandInCardPlayProp = handType?.GetProperty("InCardPlay", InstAll);
            HandIsDisabledField = FindField(handType, "_isDisabled");
            HandDraggedIndexField = FindField(handType, "_draggedHolderIndex");
            HandFocusedHolderProp = handType?.GetProperty("FocusedHolder", InstAll);
            HandForceRefreshMethod = handType?.GetMethod("ForceRefreshCardIndices", InstAll);

            Type? handHolderType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder");
            CardHolderCardNodeProp = FindProperty(handHolderType, "CardNode");
            HolderTargetPosField = FindField(handHolderType, "_targetPosition");
            HolderTargetScaleField = FindField(handHolderType, "_targetScale");
            HolderTargetAngleField = FindField(handHolderType, "_targetAngle");
            HolderSetTargetPosMethod = handHolderType?.GetMethod("SetTargetPosition", InstAll);
            HolderSetTargetScaleMethod = handHolderType?.GetMethod("SetTargetScale", InstAll);
            HolderSetTargetAngleMethod = handHolderType?.GetMethod("SetTargetAngle", InstAll);
            HolderSetScaleInstantlyMethod = handHolderType?.GetMethod("SetScaleInstantly", InstAll);
            HolderSetAngleInstantlyMethod = handHolderType?.GetMethod("SetAngleInstantly", InstAll);
            HolderGlowGoldProp = FindProperty(handHolderType, "ShouldGlowGold");
            HolderGlowRedProp = FindProperty(handHolderType, "ShouldGlowRed");

            Type? cardType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Cards.NCard");
            CardBodyProp = FindProperty(cardType, "Body");
            CardHighlightProp = FindProperty(cardType, "CardHighlight");
            CardModelProp = FindProperty(cardType, "Model");
            CardDisplayingPileProp = FindProperty(cardType, "DisplayingPile");
            CardEnergyLabelField = FindField(cardType, "_energyLabel");
            CardUnplayableEnergyIconField = FindField(cardType, "_unplayableEnergyIcon");
            CardUpdateEnergyCostMethod = cardType?.GetMethod("UpdateEnergyCostColor", InstAll);

            Type? cardModelType = sts2.GetType("MegaCrit.Sts2.Core.Models.CardModel");
            CardModelCanPlayMethod = cardModelType?.GetMethod("CanPlay", InstAll, null, Type.EmptyTypes, null);

            Type? highlightType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Cards.NCardHighlight");
            HighlightTweenField = FindField(highlightType, "_curTween");

            Type? endTurnType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NEndTurnButton");
            EndTurnStateField = FindField(endTurnType, "_state");
            EndTurnPosTweenField = FindField(endTurnType, "_positionTween");
            EndTurnShowPosProp = FindProperty(endTurnType, "ShowPos");
            EndTurnHidePosProp = FindProperty(endTurnType, "HidePos");

            Type? pingType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NPingButton");
            PingStateField = FindField(pingType, "_state");
            PingPosTweenField = FindField(pingType, "_positionTween");
            PingShowPosProp = FindProperty(pingType, "ShowPos");
            PingHidePosProp = FindProperty(pingType, "HidePos");

            Type? proceedType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.CommonUi.NProceedButton");
            ButtonIsEnabledProp = FindProperty(proceedType, "IsEnabled");
            ProceedAnimTweenField = FindField(proceedType, "_animTween");
            ProceedShowPosProp = FindProperty(proceedType, "ShowPos");
            ProceedHidePosProp = FindProperty(proceedType, "HidePos");

            Type? pileBaseType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NCombatCardPile");
            PileShowPosField = FindField(pileBaseType, "_showPosition");
            PileHidePosField = FindField(pileBaseType, "_hidePosition");
            PilePosTweenField = FindField(pileBaseType, "_positionTween");

            Type? exhaustType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NExhaustPileButton");
            ExhaustSetAnimPositionsMethod = exhaustType?.GetMethod("SetAnimInOutPositions", InstAll);

            Type? creatureNodeType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NCreature");
            CreatureEntityProp = FindProperty(creatureNodeType, "Entity");
            CreatureVisualsProp = FindProperty(creatureNodeType, "Visuals");
            CreatureOrbManagerProp = FindProperty(creatureNodeType, "OrbManager");
            _creatureUpdateBoundsMethod = creatureNodeType?.GetMethod("UpdateBounds", InstAll, null, [typeof(Node)], null);

            Type? stateDisplayType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NCreatureStateDisplay");
            _stateDisplaySetBoundsMethod = stateDisplayType?.GetMethod("SetCreatureBounds", InstAll, null, [typeof(Control)], null);
            Type? powerContainerType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NPowerContainer");
            _powerContainerSetBoundsMethod = powerContainerType?.GetMethod("SetCreatureBounds", InstAll, null, [typeof(Control)], null);

            Type? entityType = sts2.GetType("MegaCrit.Sts2.Core.Entities.Creatures.Creature");
            EntityIsPlayerProp = FindProperty(entityType, "IsPlayer");
            _entitySideProp = FindProperty(entityType, "Side");
            _entityPlayerProp = FindProperty(entityType, "Player");
            _entityMonsterProp = FindProperty(entityType, "Monster");
            _monsterBarReductionProp = FindProperty(sts2.GetType("MegaCrit.Sts2.Core.Models.MonsterModel"), "HpBarSizeReduction");

            Type? playerType = sts2.GetType("MegaCrit.Sts2.Core.Entities.Players.Player");
            _playerCombatStateProp = FindProperty(playerType, "PlayerCombatState");

            Type? pcsType = sts2.GetType("MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState");
            _orbQueueProp = FindProperty(pcsType, "OrbQueue");

            Type? orbQueueType = sts2.GetType("MegaCrit.Sts2.Core.Entities.Orbs.OrbQueue");
            _orbCapacityProp = FindProperty(orbQueueType, "Capacity");

            Type? visualsType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NCreatureVisuals");
            _visualsBoundsProp = FindProperty(visualsType, "Bounds");
        }

        public Node? GetActiveCombatRoom() => _combatRoomInstanceProp?.GetValue(null) as Node;

        public bool IsCombatInProgress()
        {
            object? inst = _combatMgrInstanceProp?.GetValue(null);
            return inst != null && (bool)(_combatMgrInProgressProp?.GetValue(inst) ?? false);
        }

        public bool CanCardPlay(Control? cardNode)
        {
            if (cardNode == null) return true;
            object? model = CardModelProp?.GetValue(cardNode);
            if (model == null) return true;
            return (bool)(CardModelCanPlayMethod?.Invoke(model, null) ?? true);
        }

        public bool IsPlayerSide(object entity)
        {
            object? side = _entitySideProp?.GetValue(entity);
            return side != null && Convert.ToInt32(side) == 1; // CombatSide.Player == 1 (check below or fallback to IsPlayer/PetOwner)
        }

        public bool CreatureHasOrbs(Control creature)
        {
            object? entity = CreatureEntityProp?.GetValue(creature);
            object? player = entity != null ? _entityPlayerProp?.GetValue(entity) : null;
            object? pcs = player != null ? _playerCombatStateProp?.GetValue(player) : null;
            object? orbQueue = pcs != null ? _orbQueueProp?.GetValue(pcs) : null;
            int cap = orbQueue != null ? Convert.ToInt32(_orbCapacityProp?.GetValue(orbQueue) ?? 0) : 0;
            return cap > 0;
        }

        public Vector2 GetCreatureRawBoundsSize(Control creature)
        {
            if (CreatureVisualsProp?.GetValue(creature) is Node visuals &&
                _visualsBoundsProp?.GetValue(visuals) is Control bounds)
            {
                return bounds.Size;
            }
            return new Vector2(220f, 220f);
        }

        public Vector2 GetCreatureVisualsScale(Control creature) =>
            CreatureVisualsProp?.GetValue(creature) is Node2D visuals ? visuals.Scale : Vector2.One;

        public void InvokeUpdateBounds(Control creature)
        {
            if (CreatureVisualsProp?.GetValue(creature) is Node visuals)
                _creatureUpdateBoundsMethod?.Invoke(creature, [visuals]);
        }

        /// <summary>The width the game adds to a creature's hit box for its health bar: 24 less the monster's reduction, none for a player.</summary>
        public float GetHealthBarExtraWidth(Control creature)
        {
            object? entity = CreatureEntityProp?.GetValue(creature);
            object? monster = entity != null ? _entityMonsterProp?.GetValue(entity) : null;
            if (monster == null)
                return 0f;
            return 24f - (_monsterBarReductionProp?.GetValue(monster) is float reduction ? reduction : 0f);
        }

        /// <summary>
        /// Whether a creature's health display can be laid out for other bounds than its hit box. Without that the display
        /// keeps the game's scale: scaled up with the game's own layout it would be far wider than its creature.
        /// </summary>
        public bool CanLayOutStateDisplay => _stateDisplaySetBoundsMethod != null;

        /// <summary><c>NCreatureStateDisplay.SetCreatureBounds</c>: places the bar, block, name and powers for these bounds.</summary>
        public bool LayOutStateDisplay(Control display, Control bounds)
        {
            if (_stateDisplaySetBoundsMethod == null)
                return false;
            try
            {
                _stateDisplaySetBoundsMethod.Invoke(display, [bounds]);
                return true;
            }
            catch (Exception error)
            {
                if (!_stateDisplayFailureLogged)
                    GD.PrintErr("Portrait health display layout failed: ", (error.InnerException ?? error).Message);
                _stateDisplayFailureLogged = true;
                return false;
            }
        }

        /// <summary><c>NPowerContainer.SetCreatureBounds</c>: wraps the power icons to the width of these bounds.</summary>
        public void LayOutPowerContainer(Control powerContainer, Control bounds)
        {
            try
            {
                _powerContainerSetBoundsMethod?.Invoke(powerContainer, [bounds]);
            }
            catch (Exception error)
            {
                GD.PrintErr("Portrait power layout failed: ", (error.InnerException ?? error).Message);
            }
        }

        private static FieldInfo? FindField(Type? type, string name)
        {
            for (Type? t = type; t != null; t = t.BaseType)
            {
                FieldInfo? f = t.GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        private static PropertyInfo? FindProperty(Type? type, string name)
        {
            for (Type? t = type; t != null; t = t.BaseType)
            {
                PropertyInfo? p = t.GetProperty(
                    name,
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (p != null) return p;
            }
            return null;
        }
    }
}
