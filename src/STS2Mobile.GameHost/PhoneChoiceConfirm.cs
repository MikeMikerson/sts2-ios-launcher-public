using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.GameHost;

/// <summary>
/// No single tap takes a card or a relic, and no single tap buys, on screens that open in portrait (landscape plays as the desktop game does).
/// The card reward screen has its own pass (<see cref="PhoneCardRewardChoice"/>); this one covers the rest:
/// <list type="bullet">
/// <item>Choose a card, relic choices and the treasure chest's relics: a press selects (outlined), a press on another moves the
/// selection, and a Choose button (the card reward's own, beside Skip or under the relics) takes it. The game takes these on the
/// release of a press, so the press here also clears the game's record of it and the release does nothing.</item>
/// <item>The simple and combat pile card grids: when the game would close as soon as enough cards are picked, it waits for its own
/// confirm button instead, and a tap on another card when the selection is full moves it. Bundles and the deck's select, upgrade,
/// transform and enchant screens already preview the pick with a confirm button.</item>
/// <item>The shop (cards, relics, potions, card removal): a tap selects an item (outlined, with its tip), a second tap on the same
/// item buys it, and a tap anywhere else lets it go.</item>
/// </list>
/// Rewards list rows, event options and map nodes stay one tap. Selecting happens on the press: under touch on the simulator
/// a card holder never got its release. Cards are matched by holder, never by their place in a row (the game moves a
/// hovered card to the end). The game's types are reached by name, as the host compiles against a stub that names only what
/// it must.
/// </summary>
internal sealed class PhoneChoiceConfirm
{
    private const string Cs = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.";
    private const string ChooseCardTypeName = Cs + "NChooseACardSelectionScreen";
    private const string ChooseRelicTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.NChooseARelicSelection";
    private const string TreasureTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic.NTreasureRoomRelicCollection";
    private const string SimpleGridTypeName = Cs + "NSimpleCardSelectScreen";
    private const string CombatPileGridTypeName = Cs + "NCombatPileCardSelectScreen";
    private const string MerchantSlotTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantSlot";
    private const string OutlineName = "PhoneSelectedOutline";
    private const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal const float ButtonGap = 24f;

    private static readonly Color OutlineColor = new(0f, 0.957f, 0.988f, 0.98f);
    private static readonly Color Dimmed = new(0.5f, 0.5f, 0.5f, 0.7f);

    private enum Kind { ChooseCard, ChooseRelic, Treasure, Grid }

    private sealed class ScreenState
    {
        public Kind Kind;
        public ulong Selected;
        public Control? Choose;
        public bool Placed;
        public Vector2 Home;
        public bool Taken;
        public ulong LoweredFor;
        public float LoweredY;
    }

    private readonly Dictionary<ulong, ScreenState> _screens = new();
    private readonly HashSet<ulong> _wired = new();
    private readonly HashSet<ulong> _wiredSlots = new();
    private readonly Type? _cardHolderType;
    private readonly Type? _clickableType;
    private readonly Type? _merchantSlotType;
    private readonly FieldInfo? _currentPressedAction;
    private readonly FieldInfo? _isPressed;
    private readonly FieldInfo? _isHovered;
    private readonly FieldInfo? _ignoreMouseRelease;
    private readonly MethodInfo? _slotOnSelected;
    private readonly MethodInfo? _createButton;
    private readonly MethodInfo? _enable;
    private readonly MethodInfo? _disable;
    private readonly PropertyInfo? _isEnabled;
    private bool _failed;

    // The shop: the selected slot, and whether a press this frame landed on a slot (a press elsewhere lets the selection go).
    private ulong _selectedSlot;
    private bool _pressSeen;
    private bool _slotPressed;

    internal static PhoneChoiceConfirm? Instance { get; private set; }

    /// <summary>Whether choices wait for a confirm (false once the game's choice screens no longer match, or after an error).</summary>
    internal static bool Active => Instance is { _failed: false };

    public PhoneChoiceConfirm()
    {
        Instance = this;
        Assembly sts2 = typeof(NGame).Assembly;
        _cardHolderType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder");
        _clickableType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl");
        _merchantSlotType = sts2.GetType(MerchantSlotTypeName);
        _currentPressedAction = _cardHolderType?.GetField("_currentPressedAction", InstAll);
        _isHovered = _cardHolderType?.GetField("_isHovered", InstAll);
        _isPressed = _clickableType?.GetField("_isPressed", InstAll);
        _ignoreMouseRelease = _merchantSlotType?.GetField("_ignoreMouseRelease", InstAll);
        _slotOnSelected = _merchantSlotType?.GetMethod("OnSelected", InstAll, Type.EmptyTypes);
        _createButton = sts2.GetType(Cs + "NCardRewardAlternativeButton")?.GetMethod("Create", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, [typeof(string), typeof(string)]);
        _enable = _clickableType?.GetMethod("Enable", InstAll, Type.EmptyTypes);
        _disable = _clickableType?.GetMethod("Disable", InstAll, Type.EmptyTypes);
        _isEnabled = _clickableType?.GetProperty("IsEnabled", InstAll);
        if (_currentPressedAction == null || _isPressed == null || _ignoreMouseRelease == null || _slotOnSelected == null ||
            _createButton == null || _enable == null || _disable == null || _isEnabled == null)
        {
            _failed = true;
            GD.PrintErr("Phone choice confirm: the game's choice screens changed; one tap takes a card, relic or shop item as in the game.");
        }
    }

    public static string? SelectedName(Control screen) =>
        Instance?._screens.TryGetValue(screen.GetInstanceId(), out ScreenState? s) == true && GodotObject.InstanceFromId(s.Selected) is Node n ? n.Name : null;

    /// <summary>
    /// Choice screens and controls, and shop slots, now under the tap-then-confirm behaviour (for the proof runs): both are 0 in
    /// a launch that stayed in landscape.
    /// </summary>
    public static string TrackedForProof() =>
        Instance == null ? "none" : $"{Instance._screens.Count + Instance._wired.Count}+{Instance._wiredSlots.Count}slots";

    /// <summary>The selected shop slot, or null (for the proof runs).</summary>
    public static Control? SelectedSlot() => Instance != null && GodotObject.InstanceFromId(Instance._selectedSlot) is Control c && GodotObject.IsInstanceValid(c) ? c : null;

    public void OnNodeAdded(Node node)
    {
        if (_failed) return;
        string? name = node.GetType().FullName;
        Kind? kind = name switch
        {
            ChooseCardTypeName => Kind.ChooseCard,
            ChooseRelicTypeName => Kind.ChooseRelic,
            TreasureTypeName => Kind.Treasure,
            SimpleGridTypeName or CombatPileGridTypeName => Kind.Grid,
            _ => null,
        };
        try
        {
            if (kind is { } k)
            {
                ulong id = node.GetInstanceId();
                if (_screens.ContainsKey(id)) return;
                var state = new ScreenState { Kind = k };
                _screens[id] = state;
                node.TreeExiting += () => _screens.Remove(id);
                if (k == Kind.Grid) RequireConfirm(node);
                else if (k != Kind.Treasure && node is Control screen) PrepareSkip(screen, state);
            }
            else if (_merchantSlotType != null && _merchantSlotType.IsInstanceOfType(node))
            {
                // A slot already in the shop when Portrait Mode is turned on is ready; a new one is not yet.
                if (node.IsNodeReady()) WireSlot((Control)node);
                else node.Connect(Node.SignalName.Ready, Callable.From(() => WireSlot((Control)node)), (uint)GodotObject.ConnectFlags.OneShot);
            }
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    /// <summary>Every press, before the game's controls see it: a shop press that lands on no slot lets the selection go.</summary>
    public void OnWindowInput(InputEvent input)
    {
        if (input is InputEventScreenTouch { Pressed: true } || input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            _pressSeen = true;
    }

    public void OnProcessFrame()
    {
        if (_failed) return;
        try
        {
            if (_pressSeen && !_slotPressed) ClearSlot();
            _pressSeen = _slotPressed = false;
            if (_selectedSlot != 0 && !(GodotObject.InstanceFromId(_selectedSlot) is Control slot && GodotObject.IsInstanceValid(slot) &&
                    slot.IsVisibleInTree()))
                ClearSlot();
            if (_screens.Count == 0) return;
            List<ulong>? dead = null;
            foreach ((ulong id, ScreenState state) in _screens)
            {
                if (GodotObject.InstanceFromId(id) is not Control screen || !GodotObject.IsInstanceValid(screen))
                {
                    (dead ??= new List<ulong>()).Add(id);
                    continue;
                }
                if (!screen.IsInsideTree()) continue;
                if (state.Kind == Kind.Grid) WireGrid(screen);
                else StepRow(screen, state);
            }
            if (dead != null) foreach (ulong id in dead) _screens.Remove(id);
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private void Fail(Exception error)
    {
        _failed = true;
        GD.PrintErr("Phone choice confirm: turned off after an error: ", error);
    }

    // ---- Choose a card, relic choices, the treasure chest ---------------------------------------------------------------

    private void StepRow(Control screen, ScreenState state)
    {
        List<Control> items = Items(screen, state.Kind);
        ulong screenId = screen.GetInstanceId();
        foreach (Control item in items)
        {
            Control press = PressTarget(item, state.Kind);
            ulong pressId = press.GetInstanceId();
            if (!_wired.Add(pressId)) continue;
            ulong itemId = item.GetInstanceId();
            press.Connect("MousePressed", Callable.From((InputEvent e) => OnItemPressed(screenId, itemId, e)));
            press.TreeExiting += () => _wired.Remove(pressId);
        }
        if (state.Selected != 0 && !items.Any(i => i.GetInstanceId() == state.Selected)) ClearSelection(state);
        if (state.Taken) return;
        EnsureChoose(screen, state);
        PlaceChoose(screen, state, items);
        RefreshChoose(state);
    }

    private static List<Control> Items(Control screen, Kind kind)
    {
        IEnumerable<Control> found = kind switch
        {
            Kind.ChooseCard => screen.GetNodeOrNull<Control>("CardRow")?.GetChildren().OfType<Control>() ?? [],
            Kind.ChooseRelic => screen.GetNodeOrNull<Control>("RelicRow")?.GetChildren().OfType<Control>() ?? [],
            Kind.Treasure => (screen.GetType().GetField("_holdersInUse", InstAll)?.GetValue(screen) as IEnumerable)?.OfType<Control>() ?? [],
            _ => [],
        };
        return found.Where(c => GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion() && c.IsInsideTree()).ToList();
    }

    private static Control PressTarget(Control item, Kind kind) =>
        kind == Kind.ChooseCard ? item.GetNodeOrNull<Control>("%Hitbox") ?? item : item;

    private void OnItemPressed(ulong screenId, ulong itemId, InputEvent e)
    {
        if (_failed || e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) return;
        if (!_screens.TryGetValue(screenId, out ScreenState? state) || state.Taken) return;
        if (GodotObject.InstanceFromId(itemId) is not Control item || !GodotObject.IsInstanceValid(item)) return;
        try
        {
            // The game takes the item on the release of this press: forget the press so the release does nothing.
            if (state.Kind == Kind.ChooseCard) _currentPressedAction!.SetValue(item, null);
            else _isPressed!.SetValue(item, false);
            if (state.Kind == Kind.ChooseCard) HoverOnly(item);
            if (state.Selected == itemId) return;
            ClearSelection(state);
            state.Selected = itemId;
            item.AddChild(state.Kind == Kind.ChooseCard ? PhoneCardRewardChoice.MakeOutline() : MakeRectOutline(item.Size, 6f, 4f));
            GD.Print($"Phone choice confirm: {state.Kind} {item.Name} selected.");
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    /// <summary>The pressed card hovered (the game's enlarged look and tips), every other card in the row not.</summary>
    private void HoverOnly(Control pressed)
    {
        if (_isHovered == null || pressed.GetParent() is not Node row) return;
        foreach (Node child in row.GetChildren())
        {
            if (child is not Control holder || holder.GetNodeOrNull<Control>("%Hitbox") is not { } hitbox) continue;
            bool hovered = _isHovered.GetValue(holder) is true;
            bool isPressed = holder == pressed;
            if (isPressed && !hovered) hitbox.EmitSignal("Focused", hitbox);
            else if (!isPressed && hovered) hitbox.EmitSignal("Unfocused", hitbox);
        }
    }

    private void OnChoosePressed(ulong screenId)
    {
        if (!_screens.TryGetValue(screenId, out ScreenState? state) || state.Taken) return;
        if (GodotObject.InstanceFromId(screenId) is not Control screen || !GodotObject.IsInstanceValid(screen)) return;
        if (GodotObject.InstanceFromId(state.Selected) is not Control item || !GodotObject.IsInstanceValid(item)) return;
        string method = state.Kind == Kind.Treasure ? "PickRelic" : "SelectHolder";
        MethodInfo? take = screen.GetType().GetMethods(InstAll).FirstOrDefault(m => m.Name == method && m.GetParameters().Length == 1 &&
            m.GetParameters()[0].ParameterType.IsInstanceOfType(item));
        if (take == null)
        {
            GD.PrintErr($"Phone choice confirm: {screen.GetType().Name}.{method} not found.");
            return;
        }
        GD.Print($"Phone choice confirm: {state.Kind} {item.Name} taken with Choose.");
        state.Taken = true;
        ClearSelection(state);
        if (state.Choose is { } choose && GodotObject.IsInstanceValid(choose))
        {
            _disable!.Invoke(choose, null);
            choose.Visible = false;
        }
        take.Invoke(screen, [item]);
    }

    private void ClearSelection(ScreenState state)
    {
        if (GodotObject.InstanceFromId(state.Selected) is Control item && GodotObject.IsInstanceValid(item) &&
            item.GetNodeOrNull<Node>(OutlineName) is { } outline)
            outline.QueueFree();
        state.Selected = 0;
    }

    private void EnsureChoose(Control screen, ScreenState state)
    {
        if (state.Choose != null && GodotObject.IsInstanceValid(state.Choose)) return;
        if (_createButton!.Invoke(null, [PhoneCardRewardChoice.ChooseText(), "ui_select"]) is not Control choose) return;
        choose.Name = PhoneCardRewardChoice.ChooseButtonName;
        choose.Visible = false;
        screen.AddChild(choose);
        ulong screenId = screen.GetInstanceId();
        choose.Connect("Released", Callable.From((Control _) => OnChoosePressed(screenId)));
        state.Choose = choose;
        _disable!.Invoke(choose, null);
        choose.Modulate = Dimmed;
        // The peek button hides the screen's controls to show the fight; Choose goes with them.
        if (screen.GetType().GetField("_peekButton", InstAll)?.GetValue(screen) is Control peek)
            peek.GetType().GetMethod("AddTargets", InstAll)?.Invoke(peek, [new[] { choose }]);
        GD.Print($"Phone choice confirm: Choose button added to {screen.GetType().Name}.");
    }

    /// <summary>The on-screen rects of the hover tips the game shows now (every tip set lives in NGame.HoverTipsContainer).</summary>
    private static IEnumerable<Rect2> VisibleTipRects()
    {
        PropertyInfo? containerProp = typeof(NGame).GetProperty("HoverTipsContainer", InstAll);
        PropertyInfo? instanceProp = typeof(NGame).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (instanceProp?.GetValue(null) is not Node game || !GodotObject.IsInstanceValid(game)) yield break;
        if (containerProp?.GetValue(game) is not Node container || !GodotObject.IsInstanceValid(container)) yield break;
        foreach (Node set in container.GetChildren())
        {
            if (set is not Control setControl || !setControl.IsVisibleInTree()) continue;
            foreach (Node part in set.GetChildren())
            {
                if (part is Control c && c.IsVisibleInTree() && c.Size.X > 1f && c.Size.Y > 1f) yield return c.GetGlobalRect();
            }
        }
    }

    /// <summary>
    /// Where the game put Skip is the middle of the row: remembered before anything here moves it. In portrait a screen that
    /// is only now opening also gets Skip in its portrait place and size at once: Skip slides in to wherever it
    /// stands when the game readies it, so it must already stand right of the middle, where Choose will leave it.
    /// </summary>
    private static void PrepareSkip(Control screen, ScreenState state)
    {
        if (screen.GetNodeOrNull<Control>("SkipButton") is not { } skip) return;
        state.Home = new Vector2((skip.OffsetLeft + skip.OffsetRight) / 2f, (skip.OffsetTop + skip.OffsetBottom) / 2f);
        if (screen.IsNodeReady() || PortraitOverlayLayout.ChoiceButtonRow(screen) is not { } row) return;
        Vector2 size = PortraitOverlayLayout.GrowChoiceButtonNow(skip) ?? ButtonSize(skip);
        CentreAt(skip, new Vector2(row.Centre.X + (size.X + ButtonGap) / 2f, row.Centre.Y), size);
    }

    /// <summary>
    /// The size a Choose or Skip button is meant to have: its minimum size, which is the game's 276 x 73 in landscape and the
    /// 44 point one <see cref="PortraitOverlayLayout"/> gives it in portrait.
    /// </summary>
    private static Vector2 ButtonSize(Control button) =>
        button.CustomMinimumSize is { X: > 1f, Y: > 1f } minimum ? minimum : new Vector2(276f, 73f);

    /// <summary>A button of this size with its middle here, measured from its anchor point (the middle of the screen).</summary>
    private static void CentreAt(Control button, Vector2 centre, Vector2 size)
    {
        float left = centre.X - size.X / 2f, top = centre.Y - size.Y / 2f;
        if (Math.Abs(button.OffsetLeft - left) < 0.25f && Math.Abs(button.OffsetTop - top) < 0.25f &&
            Math.Abs(button.OffsetRight - (left + size.X)) < 0.25f && Math.Abs(button.OffsetBottom - (top + size.Y)) < 0.25f)
            return;
        button.OffsetLeft = left;
        button.OffsetTop = top;
        button.OffsetRight = left + size.X;
        button.OffsetBottom = top + size.Y;
    }

    /// <summary>
    /// Choose sits beside Skip, the pair centred on the row: where the game put Skip, or in portrait the row the screen's portrait
    /// layout gives (<see cref="PortraitOverlayLayout.ChoiceButtonRow"/>). With no Skip, Choose takes the middle. The places are
    /// worked out again every frame from the buttons' sizes, so they follow a switch between portrait and landscape. On the
    /// treasure chest Choose sits under the relics.
    /// </summary>
    private void PlaceChoose(Control screen, ScreenState state, List<Control> items)
    {
        if (state.Choose is not { } choose) return;
        Vector2 size = ButtonSize(choose);
        if (state.Kind == Kind.Treasure)
        {
            List<Control> shown = items.Where(i => i.IsVisibleInTree()).ToList();
            if (shown.Count == 0)
            {
                choose.Visible = false;
                return;
            }
            Rect2 rect = shown.Select(i => i.GetGlobalRect()).Aggregate((a, b) => a.Merge(b));
            float scale = choose.GetGlobalTransform().Scale.X;
            Vector2 placed = new(rect.GetCenter().X - size.X * scale / 2f, rect.End.Y + 30f * scale);
            // The selected relic's tips open beside it and can cover the spot under the relics: Choose then goes under the tips,
            // or above everything when there is no room below.
            Rect2 screenRect = choose.GetViewportRect();
            foreach (Rect2 tip in VisibleTipRects().OrderBy(r => r.Position.Y))
            {
                Rect2 button = new(placed, size * scale);
                if (!button.Intersects(tip)) continue;
                placed.Y = tip.End.Y + 24f * scale;
                if (placed.Y + size.Y * scale > screenRect.End.Y - 24f * scale)
                {
                    float top = Math.Min(rect.Position.Y, VisibleTipRects().Select(r => r.Position.Y).DefaultIfEmpty(rect.Position.Y).Min());
                    placed.Y = Math.Max(24f * scale, top - (24f + size.Y) * scale);
                    break;
                }
            }
            // The tips close when a finger lands on Choose: it keeps its lowered place until the selection changes, so it does not
            // jump away between the press and the release.
            if (state.LoweredFor == state.Selected && state.Selected != 0)
            {
                Rect2 held = new(new Vector2(placed.X, state.LoweredY), size * scale);
                if (!VisibleTipRects().Any(t => held.Intersects(t))) placed.Y = state.LoweredY;
            }
            state.LoweredFor = state.Selected;
            state.LoweredY = placed.Y;
            choose.GlobalPosition = placed;
            choose.Visible = true;
            return;
        }
        if (screen.GetNodeOrNull<Control>("SkipButton") is not { } skip) return;
        if (!state.Placed)
        {
            // Wait for Skip's slide-in, which writes its position every frame.
            if (skip.Visible && skip.GetType().GetField("_animInTween", InstAll)?.GetValue(skip) is Tween { } tween && tween.IsValid() &&
                tween.IsRunning())
                return;
            state.Placed = true;
            choose.Visible = true;
        }
        choose.AnchorLeft = choose.AnchorRight = skip.AnchorLeft;
        choose.AnchorTop = choose.AnchorBottom = skip.AnchorTop;
        Vector2 centre = PortraitOverlayLayout.ChoiceButtonRow(screen)?.Centre ?? state.Home;
        if (!skip.Visible)
        {
            CentreAt(choose, centre, size);
            return;
        }
        Vector2 skipSize = ButtonSize(skip);
        float total = size.X + ButtonGap + skipSize.X;
        CentreAt(choose, new Vector2(centre.X - total / 2f + size.X / 2f, centre.Y), size);
        CentreAt(skip, new Vector2(centre.X + total / 2f - skipSize.X / 2f, centre.Y), skipSize);
    }

    private void RefreshChoose(ScreenState state)
    {
        if (state.Choose is not { } choose || !GodotObject.IsInstanceValid(choose)) return;
        bool on = state.Selected != 0;
        bool enabled = _isEnabled!.GetValue(choose) is true;
        if (on && !enabled)
        {
            _enable!.Invoke(choose, null);
            choose.Modulate = Colors.White;
        }
        else if (!on && enabled)
        {
            _disable!.Invoke(choose, null);
            choose.Modulate = Dimmed;
        }
    }

    // ---- The simple and combat pile card grids --------------------------------------------------------------------------

    /// <summary>The grid waits for its own confirm button rather than closing as soon as enough cards are picked.</summary>
    private static void RequireConfirm(Node screen)
    {
        FieldInfo? prefsField = screen.GetType().GetField("_prefs", InstAll);
        if (prefsField?.GetValue(screen) is not { } prefs) return;
        PropertyInfo? manual = prefs.GetType().GetProperty("RequireManualConfirmation", InstAll);
        if (manual == null || manual.GetValue(prefs) is true) return;
        manual.SetValue(prefs, true);
        prefsField.SetValue(screen, prefs);
        GD.Print($"Phone choice confirm: {screen.GetType().Name} waits for its confirm button.");
    }

    private void WireGrid(Control screen)
    {
        if (screen.GetType().GetField("_grid", InstAll)?.GetValue(screen) is not Control grid) return;
        ulong gridId = grid.GetInstanceId();
        if (!_wired.Add(gridId)) return;
        ulong screenId = screen.GetInstanceId();
        grid.Connect("HolderPressed", Callable.From((Control holder) => OnGridHolderPressed(screenId, holder)));
        grid.TreeExiting += () => _wired.Remove(gridId);
    }

    /// <summary>
    /// After the game's own handler: a card tapped while the selection is full (the game ignores it) replaces a selected card, so
    /// a second tap moves a one-card selection.
    /// </summary>
    private void OnGridHolderPressed(ulong screenId, Control holder)
    {
        if (_failed || GodotObject.InstanceFromId(screenId) is not Control screen || !GodotObject.IsInstanceValid(screen)) return;
        try
        {
            Type type = screen.GetType();
            object? card = holder.GetType().GetProperty("CardModel", InstAll)?.GetValue(holder);
            if (card == null || type.GetField("_selectedCards", InstAll)?.GetValue(screen) is not IEnumerable selected) return;
            List<object> picked = selected.Cast<object>().ToList();
            if (picked.Any(c => ReferenceEquals(c, card))) return;
            object? prefs = type.GetField("_prefs", InstAll)?.GetValue(screen);
            if (prefs?.GetType().GetProperty("MaxSelect", InstAll)?.GetValue(prefs) is not int max || max < 1 || picked.Count < max) return;
            MethodInfo? click = type.GetMethod("OnCardClicked", InstAll);
            if (click == null) return;
            click.Invoke(screen, [picked[^1]]);
            click.Invoke(screen, [card]);
            GD.Print($"Phone choice confirm: {type.Name} selection moved to {card.GetType().Name}.");
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    // ---- The shop ------------------------------------------------------------------------------------------------------

    private void WireSlot(Control slot)
    {
        if (_failed || !GodotObject.IsInstanceValid(slot) || slot.GetNodeOrNull<Control>("%Hitbox") is not { } hitbox) return;
        ulong slotId = slot.GetInstanceId();
        if (!_wiredSlots.Add(slotId)) return;
        hitbox.Connect("MousePressed", Callable.From((InputEvent e) => OnSlotPressed(slotId, e)));
        slot.TreeExiting += () => _wiredSlots.Remove(slotId);
    }

    private void OnSlotPressed(ulong slotId, InputEvent e)
    {
        if (_failed || e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) return;
        if (GodotObject.InstanceFromId(slotId) is not Control slot || !GodotObject.IsInstanceValid(slot)) return;
        try
        {
            _slotPressed = true;
            // The game buys on the release of a press on a hovered slot: never here.
            _ignoreMouseRelease!.SetValue(slot, true);
            if (_selectedSlot == slotId)
            {
                ClearSlot();
                GD.Print($"Phone choice confirm: {slot.GetType().Name} bought with a second tap.");
                if (_slotOnSelected!.Invoke(slot, null) is Task buying)
                    buying.ContinueWith(t => GD.PrintErr("Phone choice confirm: purchase failed: ", t.Exception), TaskContinuationOptions.OnlyOnFaulted);
                return;
            }
            ClearSlot();
            if (slot.GetNodeOrNull<Control>("%Hitbox") is { } hitbox)
            {
                hitbox.AddChild(MakeRectOutline(hitbox.Size, 10f, 6f));
                _selectedSlot = slotId;
                GD.Print($"Phone choice confirm: {slot.GetType().Name} selected; a second tap buys it.");
            }
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private void ClearSlot()
    {
        if (GodotObject.InstanceFromId(_selectedSlot) is Control slot && GodotObject.IsInstanceValid(slot) &&
            slot.GetNodeOrNull<Control>("%Hitbox")?.GetNodeOrNull<Node>(OutlineName) is { } outline)
            outline.QueueFree();
        _selectedSlot = 0;
    }

    // ---- shared --------------------------------------------------------------------------------------------------------

    /// <summary>A border around a control's rectangle, in the card's own outline colour; nothing is laid over the item.</summary>
    private static Panel MakeRectOutline(Vector2 size, float pad, float border)
    {
        var style = new StyleBoxFlat
        {
            DrawCenter = false,
            BorderColor = OutlineColor,
            BorderWidthLeft = (int)border, BorderWidthTop = (int)border, BorderWidthRight = (int)border, BorderWidthBottom = (int)border,
            CornerRadiusTopLeft = 14, CornerRadiusTopRight = 14, CornerRadiusBottomLeft = 14, CornerRadiusBottomRight = 14,
        };
        var panel = new Panel
        {
            Name = OutlineName,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(-pad, -pad),
            Size = size + new Vector2(2f * pad, 2f * pad),
        };
        panel.AddThemeStyleboxOverride("panel", style);
        return panel;
    }
}
