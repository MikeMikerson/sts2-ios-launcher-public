using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// A scroll that starts on a card should scroll. The game's scroll views (the card grids of the
/// deck, the piles and the select, upgrade and transform screens, <c>NCardGrid</c>; and the lists, <c>NScrollableContainer</c>)
/// scroll a mouse drag only when the press lands on their own background: a press on a card goes to the card, so on a phone,
/// where the cards fill the screen, most drags did nothing. Here, in portrait only, a touch that moves mostly up or down by more
/// than <see cref="StartDistance"/> scrolls the view under it wherever it started: the press on the card or button under the
/// finger is let go (it will not pick, select or press when the finger lifts), and the view follows the finger as it does for a
/// drag on its background, springing back past its ends as the game's own drag does. A drag on the background is left to the
/// game. In landscape nothing changes.
/// </summary>
internal sealed class PhoneTouchScroll
{
    private const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string GridTypeName = "MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid";
    private const string ListTypeName = "MegaCrit.Sts2.Core.Nodes.GodotExtensions.NScrollableContainer";
    private const string HolderTypeName = "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder";
    private const string ClickableTypeName = "MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl";

    /// <summary>How far a touch moves, in canvas units, before it is a scroll: 24 units are about 8 points across a 375-point phone.</summary>
    public const float StartDistance = 24f;

    /// <summary>How many touches became a scroll since launch (for the proof runs).</summary>
    public static int ScrollsStarted { get; private set; }

    /// <summary>How many presses a scroll let go since launch (for the proof runs).</summary>
    public static int PressesReleased { get; private set; }

    private readonly HashSet<ulong> _views = new();
    private readonly Type? _gridType, _listType, _holderType, _clickableType;
    private readonly FieldInfo? _gridTarget, _gridDragging, _listTarget, _listDragging, _holderPress, _clickablePressed, _listContent;
    private readonly PropertyInfo? _gridCanScroll, _listScrollbar;
    private readonly bool _failed;
    private SceneTree? _tree;

    private bool _down;
    private bool _scrolling;
    private ulong _view;
    private Vector2 _start;

    public PhoneTouchScroll()
    {
        try
        {
            Assembly game = typeof(NGame).Assembly;
            _gridType = game.GetType(GridTypeName);
            _listType = game.GetType(ListTypeName);
            _holderType = game.GetType(HolderTypeName);
            _clickableType = game.GetType(ClickableTypeName);
            _gridTarget = _gridType?.GetField("_targetDrag", InstAll);
            _gridDragging = _gridType?.GetField("_isDragging", InstAll);
            _gridCanScroll = _gridType?.GetProperty("CanScroll", InstAll);
            _listTarget = _listType?.GetField("_targetDragPosY", InstAll);
            _listDragging = _listType?.GetField("_isDragging", InstAll);
            _listContent = _listType?.GetField("_content", InstAll);
            _listScrollbar = _listType?.GetProperty("Scrollbar", InstAll);
            _holderPress = _holderType?.GetField("_currentPressedAction", InstAll);
            _clickablePressed = _clickableType?.GetField("_isPressed", InstAll);
            if (_gridTarget == null || _gridDragging == null || _listTarget == null || _listDragging == null ||
                _holderPress == null || _clickablePressed == null)
            {
                _failed = true;
                GD.PrintErr("Phone touch scroll: the game's scroll views no longer have the expected members; scrolling is left to the game.");
            }
        }
        catch (Exception error)
        {
            _failed = true;
            GD.PrintErr("Phone touch scroll: off: ", error.Message);
        }
    }

    public void Attach(SceneTree tree)
    {
        if (_failed) return;
        _tree = tree;
        tree.NodeAdded += OnNodeAdded;
        tree.Root.WindowInput += OnWindowInput;
    }

    public void Detach()
    {
        if (_tree != null && GodotObject.IsInstanceValid(_tree.Root))
        {
            _tree.NodeAdded -= OnNodeAdded;
            _tree.Root.WindowInput -= OnWindowInput;
        }
        _tree = null;
    }

    private void OnNodeAdded(Node node)
    {
        Type type = node.GetType();
        if ((_gridType != null && _gridType.IsAssignableFrom(type)) || (_listType != null && _listType.IsAssignableFrom(type)))
            _views.Add(node.GetInstanceId());
    }

    private void OnWindowInput(InputEvent input)
    {
        if (_tree == null || !GodotObject.IsInstanceValid(_tree.Root)) return;
        if (!SafeAreaCanvas.IsPortrait)
        {
            End();
            return;
        }
        try
        {
            Transform2D toCanvas = _tree.Root.GetFinalTransform().AffineInverse();
            if (input is InputEventScreenTouch touch && touch.Index == 0)
            {
                if (touch.Pressed)
                {
                    End();
                    _down = true;
                    _start = toCanvas * touch.Position;
                    _view = ViewAt(_start);
                }
                else
                {
                    End();
                }
            }
            else if (input is InputEventScreenDrag drag && drag.Index == 0 && _down && _view != 0)
            {
                if (GodotObject.InstanceFromId(_view) is not Control view || !GodotObject.IsInstanceValid(view) || !view.IsVisibleInTree())
                {
                    _view = 0;
                    return;
                }
                if (_scrolling)
                {
                    Scroll(view, toCanvas.BasisXform(drag.Relative).Y);
                    return;
                }
                Vector2 moved = toCanvas * drag.Position - _start;
                if (MathF.Abs(moved.Y) < StartDistance || MathF.Abs(moved.Y) < MathF.Abs(moved.X) * 1.2f)
                    return;
                if (Dragging(view) || !Covers(view))
                {
                    // A drag on the view's background is the game's own; a view under another screen is not scrolled.
                    _view = 0;
                    return;
                }
                _scrolling = true;
                ScrollsStarted++;
                ReleasePresses(view);
                SetDragging(view, true);
                Scroll(view, moved.Y);
            }
        }
        catch (Exception error)
        {
            GD.PrintErr("Phone touch scroll: ", error.Message);
            End();
        }
    }

    /// <summary>The touch is over: the view springs back inside its ends, as after the game's own drag.</summary>
    private void End()
    {
        if (_scrolling && GodotObject.InstanceFromId(_view) is Control view && GodotObject.IsInstanceValid(view))
            SetDragging(view, false);
        _down = false;
        _scrolling = false;
        _view = 0;
    }

    /// <summary>The innermost scroll view that can scroll and holds the point, or 0.</summary>
    private ulong ViewAt(Vector2 point)
    {
        ulong best = 0;
        float bestArea = float.MaxValue;
        var dead = new List<ulong>();
        foreach (ulong id in _views)
        {
            if (GodotObject.InstanceFromId(id) is not Control view || !GodotObject.IsInstanceValid(view))
            {
                dead.Add(id);
                continue;
            }
            if (!view.IsInsideTree() || !view.IsVisibleInTree() || !CanScroll(view)) continue;
            Vector2 local = view.GetGlobalTransformWithCanvas().AffineInverse() * point;
            if (!new Rect2(Vector2.Zero, view.Size).HasPoint(local)) continue;
            float area = view.Size.X * view.Size.Y;
            if (area < bestArea)
            {
                best = id;
                bestArea = area;
            }
        }
        foreach (ulong id in dead) _views.Remove(id);
        return best;
    }

    private bool IsGrid(Control view) => _gridType != null && _gridType.IsInstanceOfType(view);

    private bool CanScroll(Control view)
    {
        if (IsGrid(view))
            return _gridCanScroll?.GetValue(view) is not false;
        // A list scrolls when its content is taller than its window, which is when the game shows its scroll bar.
        return _listContent?.GetValue(view) is Control && _listScrollbar?.GetValue(view) is CanvasItem { Visible: true };
    }

    private bool Dragging(Control view) =>
        (IsGrid(view) ? _gridDragging : _listDragging)?.GetValue(view) is true;

    private void SetDragging(Control view, bool dragging) =>
        (IsGrid(view) ? _gridDragging : _listDragging)?.SetValue(view, dragging);

    private void Scroll(Control view, float dy)
    {
        FieldInfo? target = IsGrid(view) ? _gridTarget : _listTarget;
        if (target?.GetValue(view) is float y)
            target.SetValue(view, y + dy);
    }

    /// <summary>The control under the finger is the view or inside it: no other screen covers the view there.</summary>
    private bool Covers(Control view)
    {
        Control? under = _tree?.Root.GuiGetHoveredControl();
        return under == null || under == view || view.IsAncestorOf(under);
    }

    /// <summary>Lets go of every press inside the view, so the card or button under the finger does nothing when it lifts.</summary>
    private void ReleasePresses(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            Type type = child.GetType();
            if (_holderType != null && _holderType.IsAssignableFrom(type) && _holderPress!.GetValue(child) != null)
            {
                _holderPress.SetValue(child, null);
                PressesReleased++;
            }
            if (_clickableType != null && _clickableType.IsAssignableFrom(type) && _clickablePressed!.GetValue(child) is true)
            {
                _clickablePressed.SetValue(child, false);
                PressesReleased++;
            }
            ReleasePresses(child);
        }
    }
}
