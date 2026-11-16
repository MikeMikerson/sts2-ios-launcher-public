using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.GameHost;

/// <summary>
/// Keeps every hover tip inside the screen in portrait; in landscape tips are the game's own, as on a desktop. The game places
/// a tip set beside its card or relic and only corrects the right edge, which runs tips off the narrow portrait canvas on
/// either side. Each portrait frame this pass looks at every tip set the
/// game shows (all of them live in <c>NGame.HoverTipsContainer</c>), enlarges its text and card columns so they can be read on
/// a phone, grows them away from the item they describe, and moves each column inside the canvas, above or below the item when
/// beside it there is no room. One pass for all tips, not one per screen.
/// <para>A long press on Ascender's Bane put some tips past the edge: the game sizes the text column from each
/// tip's height before its text has wrapped, and the column is a flow container, so tips that do not fit flow into a second column
/// beside the first, outside the size this pass fitted. The text column is now made one column as tall as its tips, and a column
/// with no room above or below its item is shrunk, down to <see cref="MinFitScale"/>, to fit the larger side.</para>
/// </summary>
internal sealed class PhoneHoverTips
{
    private const string TipSetTypeName = "MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet";
    private const float Margin = 10f;
    private const float Gap = 8f;

    /// <summary>Tips drawn at 1.4 times the game's size in portrait: about 11 pt text on a 375 pt wide phone.</summary>
    public const float PortraitTipScale = 1.4f;

    /// <summary>The smallest scale a column is shrunk to so it fits above or below its item; below that it stays readable over it.</summary>
    public const float MinFitScale = 0.9f;

    private readonly Dictionary<ulong, ColumnState> _columns = new();
    private FieldInfo? _ownerField;
    private FieldInfo? _textField;
    private FieldInfo? _cardField;
    private PropertyInfo? _containerProp;
    private PropertyInfo? _gameInstance;
    private bool _resolved;
    private bool _failed;
    private int _errors;
    private int _frame;

    private sealed class ColumnState
    {
        public Vector2 Original;
        public Vector2 Written;
        public Vector2 WrittenScale = Vector2.One;
    }

    /// <summary>
    /// Portrait Mode was turned off: a tip still on screen goes back to where the game put it, at the game's size, and nothing
    /// is tracked any more.
    /// </summary>
    public void Release()
    {
        foreach ((ulong id, ColumnState state) in _columns)
        {
            if (GodotObject.InstanceFromId(id) is not Control column || !GodotObject.IsInstanceValid(column)) continue;
            // A column the game has placed again since the last pass is already the game's.
            if ((column.Position - state.Written).LengthSquared() <= 0.25f) column.Position = state.Original;
            column.Scale = Vector2.One;
        }
        _columns.Clear();
    }

    /// <summary>Called each frame in portrait only.</summary>
    public void OnProcessFrame(SceneTree tree)
    {
        if (_failed || !GodotObject.IsInstanceValid(tree.Root)) return;
        try
        {
            Node? container = TipsContainer();
            if (container == null || container.GetChildCount() == 0)
            {
                if (_columns.Count > 0 && ++_frame % 120 == 0) _columns.Clear();
                return;
            }
            Rect2 canvas = tree.Root.GetVisibleRect();
            foreach (Node child in container.GetChildren())
            {
                if (child is not Control set || set.GetType().FullName != TipSetTypeName || set.IsQueuedForDeletion() || !set.Visible) continue;
                try
                {
                    FitSet(set, canvas, PortraitTipScale);
                }
                catch (Exception error)
                {
                    // One tip set that cannot be fitted is left as the game placed it; the others are still fitted.
                    if (++_errors <= 3) GD.PrintErr("Phone hover tips: a tip set was left as the game placed it: ", error);
                    if (_errors >= 50) throw;
                }
            }
            if (++_frame % 600 == 0) Prune();
        }
        catch (Exception error)
        {
            _failed = true;
            GD.PrintErr("Phone hover tips: turned off after repeated errors: ", error.Message);
        }
    }

    private Node? TipsContainer()
    {
        if (!_resolved)
        {
            _resolved = true;
            Type? setType = typeof(NGame).Assembly.GetType(TipSetTypeName);
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _ownerField = setType?.GetField("_owner", inst);
            _textField = setType?.GetField("_textHoverTipContainer", inst);
            _cardField = setType?.GetField("_cardHoverTipContainer", inst);
            _containerProp = typeof(NGame).GetProperty("HoverTipsContainer", inst);
            _gameInstance = typeof(NGame).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (_textField == null || _cardField == null || _containerProp == null || _gameInstance == null)
            {
                _failed = true;
                GD.PrintErr("Phone hover tips: the game's tip set no longer has the expected members; tips are left as the game places them.");
                return null;
            }
        }
        return _gameInstance!.GetValue(null) is Node game && GodotObject.IsInstanceValid(game) ? _containerProp!.GetValue(game) as Node : null;
    }

    private void FitSet(Control set, Rect2 canvas, float scale)
    {
        Rect2? owner = _ownerField?.GetValue(set) is Control ownerControl && GodotObject.IsInstanceValid(ownerControl) && ownerControl.IsInsideTree()
            ? ItemRect(ownerControl, canvas)
            : null;
        Transform2D toCanvas = set.GetGlobalTransformWithCanvas();
        Rect2? placedText = null;
        if (_textField!.GetValue(set) is Control text && GodotObject.IsInstanceValid(text))
        {
            if (text is FlowContainer flow) MakeOneColumn(flow);
            placedText = FitColumn(text, toCanvas, canvas, owner, scale, avoid: null);
        }
        if (_cardField!.GetValue(set) is Control cards && GodotObject.IsInstanceValid(cards))
            FitColumn(cards, toCanvas, canvas, owner, scale, placedText);
    }

    /// <summary>
    /// Sizes the game's text tip column to one column holding every tip. The game sets its height from tips measured before their
    /// text wrapped, so the flow container moved the last tips into a second column, past the size this pass fits on the screen.
    /// <para>Touch of Orobas with The Fool ran past the right edge: the sum of the tips' heights came out a
    /// hair under the flow's need (358.9998 for 359), so the last tip still wrapped, and the width, kept from the wrapped flow,
    /// stayed two columns wide. The column is now as wide as its widest tip, with a unit to spare in height, and fills left
    /// to right: a tip set left of its item fills right to left, which in a column wider than its tips drew them past its
    /// right edge.</para>
    /// </summary>
    private static void MakeOneColumn(FlowContainer flow)
    {
        float height = 0f, width = 0f;
        int count = 0;
        foreach (Node child in flow.GetChildren())
        {
            if (child is not Control tip || !tip.Visible) continue;
            Vector2 size = new(Math.Max(tip.Size.X, tip.GetCombinedMinimumSize().X), Math.Max(tip.Size.Y, tip.GetCombinedMinimumSize().Y));
            height += size.Y;
            width = Math.Max(width, size.X);
            count++;
        }
        if (count == 0) return;
        height += flow.GetThemeConstant("v_separation") * (count - 1) + 1f;
        if (flow.ReverseFill) flow.ReverseFill = false;
        if (Math.Abs(flow.Size.Y - height) > 0.5f || Math.Abs(flow.Size.X - width) > 0.5f)
            flow.Size = new Vector2(width, height);
    }

    private Rect2? FitColumn(Control column, Transform2D toCanvas, Rect2 canvas, Rect2? owner, float scale, Rect2? avoid)
    {
        if (!column.Visible || column.Size.X < 2f || column.Size.Y < 2f || column.GetChildCount() == 0) return null;
        ulong id = column.GetInstanceId();
        if (!_columns.TryGetValue(id, out ColumnState? state))
        {
            state = new ColumnState { Original = column.Position, Written = column.Position };
            _columns[id] = state;
        }
        else if ((column.Position - state.Written).LengthSquared() > 0.25f)
        {
            // The game placed this column again (a new alignment): that is the new starting point.
            state.Original = column.Position;
        }

        Vector2 origin = toCanvas * state.Original;
        var desktop = new Rect2(origin, column.Size);
        float s = Math.Min(scale, (canvas.Size.X - 2f * Margin) / column.Size.X);
        s = Math.Min(s, Math.Max(0.5f, (canvas.Size.Y - 2f * Margin) / column.Size.Y));
        Vector2 size = column.Size * s;

        // Grow away from the item: a column left of it keeps its right edge, one above it keeps its bottom edge.
        float x = desktop.Position.X, y = desktop.Position.Y;
        if (owner is { } o)
        {
            if (desktop.End.X <= o.GetCenter().X) x = desktop.End.X - size.X;
            if (desktop.End.Y <= o.Position.Y + 1f) y = desktop.End.Y - size.Y;
        }
        var placed = new Rect2(ClampX(x, size.X, canvas), ClampY(y, size.Y, canvas), size);

        // Pushed back over the item it describes: put it above or below the item instead, whichever has room.
        if (owner is { } item && OverlapShare(placed, item) > 0.25f && AboveOrBelow(item, column.Size, s, canvas) is { } moved)
            (placed, s) = moved;
        if (avoid is { } other && placed.Intersects(other) && AboveOrBelow(other, column.Size, s, canvas) is { } apart)
            (placed, s) = apart;

        Vector2 local = toCanvas.AffineInverse() * placed.Position;
        column.Scale = new Vector2(s, s);
        column.Position = local;
        state.Written = local;
        state.WrittenScale = column.Scale;
        return placed;
    }

    /// <summary>
    /// The column above or below the item, at scale <paramref name="s"/>; when neither side has room, the larger side with the
    /// column shrunk to fit it, if that keeps at least <see cref="MinFitScale"/>. Null when it fits nowhere.
    /// </summary>
    private static (Rect2 Rect, float Scale)? AboveOrBelow(Rect2 item, Vector2 baseSize, float s, Rect2 canvas)
    {
        float roomAbove = item.Position.Y - Gap - (canvas.Position.Y + Margin);
        float roomBelow = canvas.End.Y - Margin - (item.End.Y + Gap);
        Vector2 size = baseSize * s;
        bool aboveFits = size.Y <= roomAbove;
        bool belowFits = size.Y <= roomBelow;
        // Above the item unless it starts near the top of the screen (the top bar, relics): there the tip goes below.
        bool preferAbove = item.Position.Y > canvas.Position.Y + canvas.Size.Y * 0.25f;
        bool above;
        if (preferAbove ? aboveFits : !belowFits && aboveFits) above = true;
        else if (belowFits) above = false;
        else
        {
            above = roomAbove > roomBelow;
            float shrunk = Math.Min(s, Math.Max(roomAbove, roomBelow) / baseSize.Y);
            if (shrunk < MinFitScale || shrunk <= 0f) return null;
            s = shrunk;
            size = baseSize * s;
        }
        float x = ClampX(item.GetCenter().X - size.X * 0.5f, size.X, canvas);
        float y = above ? item.Position.Y - Gap - size.Y : item.End.Y + Gap;
        return (new Rect2(x, y, size), s);
    }

    private static float ClampX(float x, float width, Rect2 canvas) =>
        Math.Clamp(x, canvas.Position.X + Margin, Math.Max(canvas.Position.X + Margin, canvas.End.X - Margin - width));

    private static float ClampY(float y, float height, Rect2 canvas) =>
        Math.Clamp(y, canvas.Position.Y + Margin, Math.Max(canvas.Position.Y + Margin, canvas.End.Y - Margin - height));

    private static float OverlapShare(Rect2 column, Rect2 item)
    {
        Rect2 inter = column.Intersection(item);
        float area = column.Size.X * column.Size.Y;
        return area <= 0f || inter.Size.X <= 0f || inter.Size.Y <= 0f ? 0f : inter.Size.X * inter.Size.Y / area;
    }

    /// <summary>
    /// The item a tip set describes. A screen-sized owner (the card and relic inspect screens own their tips) stands for the
    /// card or popup it shows, so a tip pushed over it moves above or below that, not over it.
    /// </summary>
    private static Rect2 ItemRect(Control owner, Rect2 canvas)
    {
        Rect2 rect = OwnerRect(owner);
        if (rect.Size.X * rect.Size.Y < canvas.Size.X * canvas.Size.Y * 0.5f) return rect;
        foreach (string name in new[] { "_card", "_popup" })
        {
            for (Type? t = owner.GetType(); t != null && t != typeof(Control); t = t.BaseType)
            {
                FieldInfo? field = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (field?.GetValue(owner) is not Control item || !GodotObject.IsInstanceValid(item) || !item.IsVisibleInTree()) continue;
                if (item.Size.X >= 2f && item.Size.Y >= 2f) return OwnerRect(item);
                // A card node has no size: it is drawn centred on its origin, 300 x 422 at its scale.
                Transform2D xform = item.GetGlobalTransformWithCanvas();
                Vector2 half = new Vector2(150f, 211f) * xform.Scale;
                return new Rect2(xform.Origin - half, half * 2f);
            }
        }
        return rect;
    }

    private static Rect2 OwnerRect(Control owner)
    {
        Control target = owner;
        if (owner.Size.X < 2f || owner.Size.Y < 2f)
            target = owner.GetNodeOrNull<Control>("%Hitbox") ?? owner.GetNodeOrNull<Control>("Hitbox") ?? owner;
        Transform2D xform = target.GetGlobalTransformWithCanvas();
        Vector2 size = target.Size;
        Vector2[] corners = [xform * Vector2.Zero, xform * new Vector2(size.X, 0f), xform * size, xform * new Vector2(0f, size.Y)];
        float minX = Math.Min(Math.Min(corners[0].X, corners[1].X), Math.Min(corners[2].X, corners[3].X));
        float maxX = Math.Max(Math.Max(corners[0].X, corners[1].X), Math.Max(corners[2].X, corners[3].X));
        float minY = Math.Min(Math.Min(corners[0].Y, corners[1].Y), Math.Min(corners[2].Y, corners[3].Y));
        float maxY = Math.Max(Math.Max(corners[0].Y, corners[1].Y), Math.Max(corners[2].Y, corners[3].Y));
        return new Rect2(minX, minY, maxX - minX, maxY - minY);
    }

    private void Prune()
    {
        var dead = new List<ulong>();
        foreach (ulong id in _columns.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _columns.Remove(id);
    }
}
