using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Makes the hand work under a finger, in portrait only: in landscape the hand is the game's own, as on a desktop.
/// The game's card play is built for a mouse: a press picks the card up and it follows the pointer until it
/// reaches the play zone, so on a phone a tap or a short drag left the card held at the finger's last spot, and while one card
/// is held every other card ignores touches, so a card sometimes would not drag. Here:
/// <list type="bullet">
/// <item>Lifting the finger below the play zone, or off every target while aiming, puts the card back in the hand.</item>
/// <item>A tap on a hand card, or a long press, opens the game's card inspect screen on that card: the card large, its keyword
/// tips and its upgrade. A tap anywhere closes it. Dragging still plays cards as before.</item>
/// </list>
/// Keeping the other cards still under a touch is the portrait hand layout's job (<see cref="PortraitCombatLayout"/>).
/// </summary>
internal sealed class PhoneTouchCards
{
    private const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticAll = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>How many times a tap or long press opened the inspect screen since launch (for the proof runs): 0 in landscape.</summary>
    public static int InspectsOpened { get; private set; }

    private readonly Reflection? _r;
    private bool _failed;
    private SceneTree? _tree;

    private bool _down;
    private bool _pendingRelease;
    private bool _decided;
    private ulong _releaseFrame;
    private double _downTime;
    private double _upTime;
    private Vector2 _downWindowPos;
    private float _maxMoveWindow;
    private Vector2? _downCanvasPos;
    private Control? _holder;

    public PhoneTouchCards()
    {
        try
        {
            _r = new Reflection(typeof(NGame).Assembly);
            if (!_r.Complete)
            {
                _failed = true;
                GD.PrintErr("Phone touch cards: the game's hand no longer has the expected members; card touches are left as the game handles them.");
            }
        }
        catch (Exception error)
        {
            _failed = true;
            GD.PrintErr("Phone touch cards: off: ", error.Message);
        }
    }

    public void Attach(SceneTree tree)
    {
        _tree = tree;
        tree.Root.WindowInput += OnWindowInput;
    }

    public void Detach()
    {
        if (_tree != null && GodotObject.IsInstanceValid(_tree.Root)) _tree.Root.WindowInput -= OnWindowInput;
        _tree = null;
    }

    private static double Now() => Time.GetTicksMsec() / 1000.0;

    private void OnWindowInput(InputEvent input)
    {
        if (_failed) return;
        if (!SafeAreaCanvas.IsPortrait)
        {
            // Landscape is the game's own hand. A touch that began before Portrait Mode was turned off is dropped.
            _down = false;
            Reset();
            return;
        }
        if (input is InputEventScreenTouch touch && touch.Index == 0)
        {
            if (touch.Pressed)
            {
                _down = true;
                _pendingRelease = false;
                _decided = false;
                _holder = null;
                _downCanvasPos = null;
                _downTime = Now();
                _downWindowPos = touch.Position;
                _maxMoveWindow = 0f;
            }
            else if (_down)
            {
                _down = false;
                _upTime = Now();
                _maxMoveWindow = Math.Max(_maxMoveWindow, touch.Position.DistanceTo(_downWindowPos));
                _pendingRelease = true;
                _releaseFrame = Engine.GetProcessFrames();
            }
        }
        else if (input is InputEventScreenDrag drag && drag.Index == 0 && _down)
        {
            _maxMoveWindow = Math.Max(_maxMoveWindow, drag.Position.DistanceTo(_downWindowPos));
        }
    }

    /// <summary>Called each frame in portrait only.</summary>
    public void OnProcessFrame(SceneTree tree)
    {
        if (_failed || _r == null || !GodotObject.IsInstanceValid(tree.Root)) return;
        try
        {
            if (!_down && !_pendingRelease) return;
            Step(tree);
        }
        catch (Exception error)
        {
            _failed = true;
            GD.PrintErr("Phone touch cards: turned off after an error: ", error);
        }
    }

    private void Step(SceneTree tree)
    {
        Reflection r = _r!;
        Control? hand = r.Hand();
        if (hand == null || !GodotObject.IsInstanceValid(hand) || !hand.IsVisibleInTree())
        {
            Reset();
            return;
        }
        _downCanvasPos ??= tree.Root.GetMousePosition();
        object? play = r.CurrentCardPlay(hand);
        if (_holder == null && play != null && r.IsMouseCardPlay(play))
            _holder = r.PlayHolder(play);

        float moved = _maxMoveWindow * CanvasPerWindowPixel(tree.Root);

        if (_down)
        {
            // A long press: the finger rests on a held card (or on a card the game does not pick up, such as an unplayable one).
            // Put it back and show it large.
            if (!_decided && TouchCardMath.IsLongPress(moved, Now() - _downTime))
            {
                Control? holder = _holder ?? (r.OverlayCount() == 0 && r.InPlayMode(hand) ? HolderAt(r, hand, _downCanvasPos) : null);
                if (holder == null) return;
                _decided = true;
                if (play != null) r.CancelAllCardPlay(hand);
                Inspect(r, hand, holder, "long press");
            }
            return;
        }

        // Released: wait two frames so the game's own release handling (play, aim, cancel) runs first.
        if (Engine.GetProcessFrames() < _releaseFrame + 2) return;
        if (!_decided)
        {
            bool tap = TouchCardMath.IsTap(moved, _upTime - _downTime);
            play = r.CurrentCardPlay(hand);
            if (play != null && r.IsMouseCardPlay(play) && r.PlayHolder(play) is { } held && (_holder == null || held == _holder))
            {
                bool aiming = r.IsTargeting();
                float mouseY = tree.Root.GetMousePosition().Y;
                bool inPlayZone = r.PlayZoneThreshold(play) is float threshold && mouseY < threshold;
                if (aiming || !inPlayZone)
                {
                    r.CancelAllCardPlay(hand);
                    GD.Print($"Phone touch cards: card put back ({(aiming ? "released off every target" : "released below the play zone")}).");
                }
            }
            // Only in play mode: a tap that picks cards from the hand (discard, upgrade) stays the game's.
            if (tap && r.OverlayCount() == 0 && r.InPlayMode(hand))
            {
                Control? tapped = _holder ?? HolderAt(r, hand, _downCanvasPos);
                if (tapped != null) Inspect(r, hand, tapped, "tap");
            }
        }
        Reset();
    }

    private void Reset()
    {
        _pendingRelease = false;
        _decided = false;
        _holder = null;
        _downCanvasPos = null;
    }

    private static float CanvasPerWindowPixel(Window root)
    {
        Vector2I window = DisplayServer.WindowGetSize();
        float canvasWidth = root.GetVisibleRect().Size.X;
        return window.X <= 0 ? 1f : canvasWidth / window.X;
    }

    private static Control? HolderAt(Reflection r, Control hand, Vector2? point)
    {
        if (point is not { } p) return null;
        IList? holders = r.ActiveHolders(hand);
        if (holders == null) return null;
        // The last holder is drawn on top and gets the touch first, as the game's own input does.
        for (int i = holders.Count - 1; i >= 0; i--)
        {
            if (holders[i] is not Control holder) continue;
            Control? hitbox = holder.GetNodeOrNull<Control>("%Hitbox") ?? holder.GetNodeOrNull<Control>("Hitbox");
            if (hitbox == null) continue;
            Vector2 local = hitbox.GetGlobalTransformWithCanvas().AffineInverse() * p;
            // The card must be the one under the finger, not one covered by a pile view or another screen.
            if (new Rect2(Vector2.Zero, hitbox.Size).HasPoint(local)) return r.HolderHovered(holder) ? holder : null;
        }
        return null;
    }

    private static void Inspect(Reflection r, Control hand, Control holder, string how)
    {
        if (!GodotObject.IsInstanceValid(holder) || r.OverlayCount() > 0) return;
        IList? holders = r.ActiveHolders(hand);
        object? card = r.HolderCardModel(holder);
        if (holders == null || card == null) return;
        IList cards = r.NewCardList();
        int index = 0;
        foreach (object? h in holders)
        {
            if (h is not Control other || r.HolderCardModel(other) is not { } model) continue;
            if (other == holder) index = cards.Count;
            cards.Add(model);
        }
        if (cards.Count == 0) cards.Add(card);
        if (r.OpenInspect(cards, index))
        {
            InspectsOpened++;
            GD.Print($"Phone touch cards: {how} opened card {index + 1} of {cards.Count} large.");
        }
    }

    private sealed class Reflection
    {
        private readonly PropertyInfo? _roomInstance;
        private readonly PropertyInfo? _roomUi;
        private readonly PropertyInfo? _uiHand;
        private readonly FieldInfo? _currentCardPlay;
        private readonly MethodInfo? _cancelAll;
        private readonly PropertyInfo? _activeHolders;
        private readonly PropertyInfo? _playHolder;
        private readonly Type? _mouseCardPlayType;
        private readonly PropertyInfo? _playZoneThreshold;
        private readonly PropertyInfo? _targetManagerInstance;
        private readonly PropertyInfo? _isInSelection;
        private readonly PropertyInfo? _holderCardModel;
        private readonly PropertyInfo? _overlayInstance;
        private readonly PropertyInfo? _overlayCount;
        private readonly PropertyInfo? _gameInstance;
        private readonly MethodInfo? _getInspectScreen;
        private readonly Type? _cardModelType;
        private readonly PropertyInfo? _currentMode;
        private readonly FieldInfo? _holderHovered;

        public Reflection(Assembly sts2)
        {
            Type? roomType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom");
            _roomInstance = roomType?.GetProperty("Instance", StaticAll);
            _roomUi = roomType?.GetProperty("Ui", InstAll);
            _uiHand = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NCombatUi")?.GetProperty("Hand", InstAll);
            Type? handType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand");
            _currentCardPlay = handType?.GetField("_currentCardPlay", InstAll);
            _cancelAll = handType?.GetMethod("CancelAllCardPlay", InstAll, Type.EmptyTypes);
            _activeHolders = handType?.GetProperty("ActiveHolders", InstAll);
            _playHolder = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NCardPlay")?.GetProperty("Holder", InstAll);
            _mouseCardPlayType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NMouseCardPlay");
            _playZoneThreshold = _mouseCardPlayType?.GetProperty("PlayZoneThreshold", InstAll);
            Type? targetType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NTargetManager");
            _targetManagerInstance = targetType?.GetProperty("Instance", StaticAll);
            _isInSelection = targetType?.GetProperty("IsInSelection", InstAll);
            _holderCardModel = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder")?.GetProperty("CardModel", InstAll);
            Type? overlayType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack");
            _overlayInstance = overlayType?.GetProperty("Instance", StaticAll);
            _overlayCount = overlayType?.GetProperty("ScreenCount", InstAll);
            _gameInstance = typeof(NGame).GetProperty("Instance", StaticAll);
            _getInspectScreen = typeof(NGame).GetMethod("GetInspectCardScreen", InstAll, Type.EmptyTypes);
            _cardModelType = sts2.GetType("MegaCrit.Sts2.Core.Models.CardModel");
            _currentMode = handType?.GetProperty("CurrentMode", InstAll);
            _holderHovered = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder")?.GetField("_isHovered", InstAll);
        }

        public bool InPlayMode(Control hand) => _currentMode?.GetValue(hand)?.ToString() == "Play";

        public bool HolderHovered(Control holder) => _holderHovered == null || (bool)(_holderHovered.GetValue(holder) ?? false);

        public bool Complete =>
            _roomInstance != null && _roomUi != null && _uiHand != null && _currentCardPlay != null && _cancelAll != null &&
            _activeHolders != null && _playHolder != null && _mouseCardPlayType != null && _playZoneThreshold != null &&
            _targetManagerInstance != null && _isInSelection != null && _holderCardModel != null && _gameInstance != null &&
            _getInspectScreen != null && _cardModelType != null;

        public Control? Hand()
        {
            object? room = _roomInstance!.GetValue(null);
            if (room is not Node node || !GodotObject.IsInstanceValid(node) || !node.IsInsideTree()) return null;
            object? ui = _roomUi!.GetValue(room);
            return ui == null ? null : _uiHand!.GetValue(ui) as Control;
        }

        public object? CurrentCardPlay(Control hand) =>
            _currentCardPlay!.GetValue(hand) is Node play && GodotObject.IsInstanceValid(play) && play.IsInsideTree() ? play : null;

        public bool IsMouseCardPlay(object play) => _mouseCardPlayType!.IsInstanceOfType(play);

        public Control? PlayHolder(object play) => _playHolder!.GetValue(play) as Control;

        public float? PlayZoneThreshold(object play) => _playZoneThreshold!.GetValue(play) as float?;

        public void CancelAllCardPlay(Control hand) => _cancelAll!.Invoke(hand, null);

        public IList? ActiveHolders(Control hand) => _activeHolders!.GetValue(hand) as IList;

        public object? HolderCardModel(Control holder) => _holderCardModel!.GetValue(holder);

        public bool IsTargeting()
        {
            try
            {
                object? manager = _targetManagerInstance!.GetValue(null);
                return manager != null && (bool)(_isInSelection!.GetValue(manager) ?? false);
            }
            catch (TargetInvocationException)
            {
                return false;
            }
        }

        public int OverlayCount()
        {
            object? stack = _overlayInstance?.GetValue(null);
            return stack == null ? 0 : (int)(_overlayCount?.GetValue(stack) ?? 0);
        }

        public IList NewCardList() => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_cardModelType!))!;

        public bool OpenInspect(IList cards, int index)
        {
            object? game = _gameInstance!.GetValue(null);
            if (game == null) return false;
            object? screen = _getInspectScreen!.Invoke(game, null);
            MethodInfo? open = screen?.GetType().GetMethod("Open", InstAll);
            if (screen == null || open == null) return false;
            open.Invoke(screen, [cards, index, false]);
            return true;
        }
    }
}
