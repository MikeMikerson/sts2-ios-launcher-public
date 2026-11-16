using System;
using Godot;

namespace STS2Mobile.GameHost;

internal static partial class PortraitSubmenuLayout
{
    public const string RunHistoryTypeName = Ns + "RunHistoryScreen.NRunHistory";

    /// <summary>
    /// Run history drawn at 1.14 (the width fit drew it at 0.755), so its 24 unit text (the date,
    /// the seed, the relic and deck headers, the deck's entries) reads at 9.5 points and the top row's 32 unit text at up
    /// to 12.6.
    /// </summary>
    public const float RunHistoryDesignWidth = 950f;

    private const string RunHistoryContainer = "ScreenContents/Content/Container";
    private const float RunHistorySide = 40f;
    private const float RunHistoryArrow = 112f;

    /// <summary>
    /// Run history in one column the screen's width: the top row (the player, health, gold, potions, floor and time) drawn
    /// smaller when it is wider than the screen, the date, seed, mode and build under it on the right, the death quote, the
    /// map's history drawn smaller to the width (it is moved out of its centring container, whose minimum width would
    /// otherwise hold the column at 1280), then the relics and the deck, which flow in the width. The list scrolls above a
    /// strip that holds the previous and next arrows at the sides, just above the back button and Share.
    /// The game finds these nodes once in _Ready (fields), so the map's move is safe; it slides <c>ScreenContents</c> by its
    /// position on a new run and scrolls <c>Content</c> by its position, so only their other edges are set here.
    /// </summary>
    private static void LayoutRunHistory(Control screen, float w, float h, Action<Control> capture)
    {
        float arrowTop = h - CornerButtonsTop - RunHistoryArrow;
        float viewBottom = arrowTop - Gap;
        float cw = w - 2f * RunHistorySide;

        // The scroll area ends above the arrows and clips the list there, so nothing scrolls under them or the back button.
        if (screen.GetNodeOrNull<Control>("ScreenContents") is { } contents)
        {
            capture(contents);
            float bottom = viewBottom - h;
            if (MathF.Abs(contents.OffsetBottom - bottom) > 0.5f) contents.OffsetBottom = bottom;
            SetKept(contents, "clip_contents", true);
        }
        Place(screen.GetNodeOrNull<Control>("ScreenContents/Scrollbar"), w - 48f, 0f, 48f, viewBottom, capture);

        // The column: the desktop's 250 unit margins and 1420 unit minimum make way for the screen's width.
        if (screen.GetNodeOrNull<Control>("ScreenContents/Content") is { } content)
        {
            capture(content);
            if (MathF.Abs(content.OffsetLeft + w / 2f) > 0.5f) content.OffsetLeft = -w / 2f;
            if (MathF.Abs(content.OffsetRight - w / 2f) > 0.5f) content.OffsetRight = w / 2f;
            RunHistoryConstant(content, "margin_left", (int)RunHistorySide);
            RunHistoryConstant(content, "margin_right", (int)RunHistorySide);
        }
        RunHistoryMinSize(screen.GetNodeOrNull<Control>(RunHistoryContainer), new Vector2(cw, 0f), capture);

        LayoutRunHistoryTop(screen, cw, capture);

        // The badges keep their own width (the desktop held 400 for them), so the quote has the rest.
        RunHistoryMinSize(screen.GetNodeOrNull<Control>(RunHistoryContainer + "/MarginContainer/HBoxContainer/BadgeContainer"), new Vector2(0f, 64f), capture);

        LayoutRunHistoryMap(screen, cw, capture);

        foreach (string list in new[] { "RelicHistory", "DeckHistory" })
            if (screen.GetNodeOrNull<Control>(RunHistoryContainer + "/" + list + "/MarginContainer") is { } margin)
            {
                RunHistoryConstant(margin, "margin_left", 16, capture);
                RunHistoryConstant(margin, "margin_right", 8, capture);
            }

        Place(screen.GetNodeOrNull<Control>("LeftArrow"), 8f, arrowTop, RunHistoryArrow, RunHistoryArrow, capture);
        Place(screen.GetNodeOrNull<Control>("RightArrow"), w - 8f - RunHistoryArrow, arrowTop, RunHistoryArrow, RunHistoryArrow, capture);
    }

    /// <summary>
    /// The top section is a plain control whose two blocks the desktop puts side by side: the icon row stays at the top,
    /// drawn smaller only when it is wider than the column (more players, more potion slots), and the date block stands
    /// under it, right aligned as on the desktop. The section's height is set to hold both.
    /// </summary>
    private static void LayoutRunHistoryTop(Control screen, float cw, Action<Control> capture)
    {
        if (screen.GetNodeOrNull<Control>(RunHistoryContainer + "/TopSection") is not { } top) return;
        float rowHeight = 80f;
        if (top.GetNodeOrNull<Control>("LeftAlignedStuff") is { } row)
        {
            Vector2 rowMin = row.GetCombinedMinimumSize();
            float scale = rowMin.X > cw ? cw / rowMin.X : 1f;
            rowHeight = Math.Max(80f, rowMin.Y);
            Place(row, 0f, 0f, cw / scale, rowHeight, capture);
            var rowScale = new Vector2(scale, scale);
            if (row.Scale.DistanceSquaredTo(rowScale) > 0.0001f) row.Scale = rowScale;
            rowHeight *= scale;
        }
        float dateHeight = 0f;
        if (top.GetNodeOrNull<Control>("RightAlignedStuff") is { } dates)
        {
            dateHeight = dates.GetCombinedMinimumSize().Y;
            Place(dates, 0f, rowHeight + 8f, cw, dateHeight, capture);
        }
        RunHistoryMinSize(top, new Vector2(0f, rowHeight + 8f + dateHeight), capture);
    }

    /// <summary>
    /// The map's history (an act a row of 60 unit room icons after a 140 unit title, 1280 x 174 at least on the desktop)
    /// is moved into the plain spacer under it and drawn smaller to the column's width, centred; the spacer takes its drawn
    /// height. The emptied centring container is hidden, so it adds no gap.
    /// </summary>
    private static void LayoutRunHistoryMap(Control screen, float cw, Action<Control> capture)
    {
        Control? spacer = screen.GetNodeOrNull<Control>(RunHistoryContainer + "/Spacer");
        Control? center = screen.GetNodeOrNull<Control>(RunHistoryContainer + "/CenterContainer");
        Control? map = center?.GetNodeOrNull<Control>("MapPointHistoryContainer") ?? spacer?.GetNodeOrNull<Control>("MapPointHistoryContainer");
        if (spacer == null || center == null || map == null) return;
        MoveKept(map, spacer);
        SetKept(center, "visible", false);

        // The desktop's 1280 unit minimum width goes, so the scale follows the acts' real width.
        RunHistoryMinSize(map, new Vector2(0f, 174f), capture);
        Vector2 mapMin = map.GetCombinedMinimumSize();
        float scale = mapMin.X > cw ? cw / mapMin.X : 1f;
        Place(map, (cw - mapMin.X * scale) / 2f, 0f, mapMin.X, mapMin.Y, capture);
        var mapScale = new Vector2(scale, scale);
        if (map.Scale.DistanceSquaredTo(mapScale) > 0.0001f) map.Scale = mapScale;
        RunHistoryMinSize(spacer, new Vector2(0f, mapMin.Y * scale + 12f), capture);
    }

    private static void RunHistoryMinSize(Control? control, Vector2 size, Action<Control> capture)
    {
        if (control == null) return;
        capture(control);
        if (control.CustomMinimumSize.DistanceSquaredTo(size) > 0.25f) control.CustomMinimumSize = size;
    }

    /// <summary>Sets a spacing or margin theme constant (the snapshot holds these), changing only what differs.</summary>
    private static void RunHistoryConstant(Control control, string name, int value, Action<Control>? capture = null)
    {
        capture?.Invoke(control);
        if (!control.HasThemeConstantOverride(name) || control.GetThemeConstant(name) != value)
            control.AddThemeConstantOverride(name, value);
    }
}
