using System;
using Godot;

namespace STS2Mobile.GameHost;

internal static partial class PortraitSubmenuLayout
{
    public const string BestiaryTypeName = Ns + "Bestiary.NBestiary";

    /// <summary>
    /// The bestiary at 740, drawn at 1.46 (at the canvas's 1080 it was drawn at 1.0), so the list's
    /// 24 unit names read at 12.2 points, the stats' 28 unit text (drawn at 1.1 more) at 15.6 and the monster's name at 24.
    /// A unit is 0.507 points; 44 points are 87 units.
    /// </summary>
    public const float BestiaryDesignWidth = 740f;

    // The game slides the monster's name to 88 units from the top (64 tall, the epithet under it); the monster stands under.
    private const float BestiaryMonsterTop = 190f;
    // The monster's area takes what the lower part leaves, between these heights.
    private const float BestiaryMonsterMinHeight = 220f;
    private const float BestiaryMonsterMaxHeight = 420f;
    // A monster layout is made for the desktop's 1920 x 1080 screen: the default layout stands the creature around
    // (960, 588), the Kaiser Crab and the Decimillipede place theirs at fixed desktop points. The layout container keeps
    // that size and is drawn smaller, so every layout keeps its own arrangement: a monster 500 desktop units tall fits the
    // area's height, at most 0.75 (about 990 desktop units across the screen; the monster is drawn about 1.1 times as
    // large as the width fit drew it).
    private static readonly Vector2 BestiaryLayoutSize = new(1920f, 1080f);
    private static readonly Vector2 BestiaryMonsterCenter = new(960f, 588f);
    private const float BestiaryMonsterFit = 500f;
    private const float BestiaryMonsterScaleMax = 0.75f;

    // The character's line (stats mode with a character chosen): an 800 x 78 row, drawn at 0.91 (728 wide, its 24 unit
    // text at 11 points) in a band under the monster.
    private static readonly Vector2 BestiaryDialogueSize = new(800f, 78f);
    private const float BestiaryDialogueScale = 0.91f;
    private const float BestiaryDialogueRoom = 80f;
    // Where the game's tweens leave things: the line slides to x 560 (from 528), the stats and moves panels to x 242 (from
    // 210). These x positions are kept, and a pivot far to the side turns them into the places used here (see
    // PlaceBestiarySliding), so the slides still run, in the new place.
    private const float BestiaryDialogueRestX = 560f;
    private const float BestiaryPanelRestX = 242f;

    // The lower part has two columns: the stats or moves panel, the back button and the mode button at the left (340
    // wide), the list at the right. The panel is drawn 1.1 times as large, or smaller when it would not fit the column
    // above the back button; 450 units of height are kept for it (the stats are about 370 tall at 1.1, six moves 414).
    private const float BestiaryLeftColumn = 340f;
    private const float BestiaryInset = 8f;
    private const float BestiaryPanelScale = 1.1f;
    private const float BestiaryPanelRoom = 450f;
    // The character filters (64 on the desktop, a grid of three columns): 80 units, drawn at 88 (44.6 points).
    private const float BestiaryFilterSize = 80f;
    // The mode button (260 x 58 on the desktop, its picture keeping its shape) over the back button: 88 units tall, 44.6
    // points.
    private const float BestiaryModeHeight = 88f;

    // The list: entries 88 units tall (36 on the desktop; 44.6 points), the whole width of the column a touch target.
    private const float BestiaryEntryHeight = 88f;
    // The game puts its selection arrow (36 tall) at an entry's position plus (-42, 119) in the list's margin container,
    // made for a list 120 units down in it and entries 36 tall. A top margin of 119 + 18 - 44 = 93 centres the arrow on an
    // 88 unit entry. The scroll's top padding then lifts that margin above the clipped area but for 8 units.
    private static readonly Vector2 BestiaryArrowOffset = new(-42f, 119f);
    private const float BestiaryListTopMargin = 93f;
    // The arrow stands 42 to 6 units left of the list: the list starts 46 units into the column, inside the clip.
    private const float BestiaryArrowRoom = 46f;
    private const float BestiaryScrollbarWidth = 48f;

    /// <summary>
    /// The bestiary in portrait: the monster's name at the top (where the game slides it), the monster under it across the
    /// width, the character's line under the monster, then two columns down to the lower edge: the stats or moves panel,
    /// the back button and the mode button at the left, and the list of monsters at the right, scrolling in a clipped area
    /// from under the line to the lower edge. On the desktop the panel stands at the left middle, the list at the right
    /// (400 wide, the whole height) and the mode button at the lower left. The game slides the panels and the line by their
    /// x position on each selection and mode change, scrolls the list's content by its y position, rebuilds the entries,
    /// the filters and the moves each time, and swaps the monster's layout node on selection; all of it keeps working, as
    /// only the things around those writes are set, every frame, where they differ.
    /// </summary>
    private static void LayoutBestiary(Control screen, float w, float h, Action<Control> capture)
    {
        float buttonsTop = h - CornerButtonsTop;
        float monsterHeight = Math.Clamp(buttonsTop - Gap - BestiaryPanelRoom - BestiaryDialogueRoom - BestiaryMonsterTop,
            BestiaryMonsterMinHeight, BestiaryMonsterMaxHeight);
        float dialogueTop = BestiaryMonsterTop + monsterHeight;
        float lowerTop = dialogueTop + BestiaryDialogueRoom;

        if (screen.GetNodeOrNull<Control>("LayoutContainer") is { } layout)
        {
            float scale = Math.Min(BestiaryMonsterScaleMax, monsterHeight / BestiaryMonsterFit);
            Vector2 center = new(w / 2f, BestiaryMonsterTop + monsterHeight / 2f);
            Vector2 origin = center - BestiaryMonsterCenter * scale;
            Place(layout, origin.X, origin.Y, BestiaryLayoutSize.X, BestiaryLayoutSize.Y, capture);
            if (layout.PivotOffset != Vector2.Zero) layout.PivotOffset = Vector2.Zero;
            var layoutScale = new Vector2(scale, scale);
            if (layout.Scale.DistanceSquaredTo(layoutScale) > 0.000001f) layout.Scale = layoutScale;
        }

        if (screen.GetNodeOrNull<Control>("DialogueLine") is { } dialogue)
        {
            float scale = Math.Min(BestiaryDialogueScale, (w - 12f) / BestiaryDialogueSize.X);
            float top = dialogueTop + (BestiaryDialogueRoom - BestiaryDialogueSize.Y * scale) / 2f;
            PlaceBestiarySliding(dialogue, BestiaryDialogueRestX, (w - BestiaryDialogueSize.X * scale) / 2f, top,
                BestiaryDialogueSize.X, BestiaryDialogueSize.Y, scale, BestiaryTweenRunning(screen, "_dialogueTween"), capture);
        }

        // The filters first, so the stats panel's size below already holds them.
        if (screen.GetNodeOrNull<Control>("StatsContainer/VBoxContainer/PoolFilters") is { } filters)
            foreach (Node child in filters.GetChildren())
                if (child is Control filter && !filter.IsQueuedForDeletion())
                    BestiaryMinSize(filter, new Vector2(BestiaryFilterSize, BestiaryFilterSize), capture);

        // The mode button stands over the back button, whose place is the window's (its _showPos, on the canvas): on a
        // screen drawn larger it sits lower than the 354 units the corner buttons use.
        float backTop = BestiaryBackButtonTop(screen, buttonsTop);
        float modeTop = backTop - Gap - BestiaryModeHeight;
        bool sliding = BestiaryTweenRunning(screen, "_tween");
        float panelBottom = modeTop - Gap;
        LayoutBestiaryPanel(screen.GetNodeOrNull<Control>("StatsContainer"), lowerTop, panelBottom, sliding, capture);
        LayoutBestiaryPanel(screen.GetNodeOrNull<Control>("MoveContainer"), lowerTop, panelBottom, sliding, capture);

        if (screen.GetNodeOrNull<Control>("ModeButton") is { } mode)
        {
            // The button grows and shrinks about its pivot (its own tweens on hover and press), so the pivot is its middle.
            float width = BestiaryLeftColumn - 2f * BestiaryInset;
            Place(mode, BestiaryInset, modeTop, width, BestiaryModeHeight, capture);
            var pivot = new Vector2(width / 2f, BestiaryModeHeight / 2f);
            if (mode.PivotOffset.DistanceSquaredTo(pivot) > 0.25f) mode.PivotOffset = pivot;
        }

        LayoutBestiaryList(screen, BestiaryLeftColumn, lowerTop, w - BestiaryLeftColumn - 4f, h - Gap - lowerTop, capture);
    }

    /// <summary>
    /// The stats or moves panel at the top of the left column, its content's height tall, drawn 1.1 times as large or
    /// smaller when its widest line (a long move name) or its height (many moves) would leave the column or reach the back
    /// button.
    /// </summary>
    private static void LayoutBestiaryPanel(Control? panel, float top, float bottom, bool sliding, Action<Control> capture)
    {
        if (panel == null) return;
        float width = BestiaryLeftColumn - 2f * BestiaryInset;
        Vector2 min = panel.GetCombinedMinimumSize();
        float scale = BestiaryPanelScale;
        if (min.X > 0f) scale = Math.Min(scale, width / min.X);
        if (min.Y > 0f) scale = Math.Min(scale, Math.Max(100f, bottom - top) / min.Y);
        // The pivot that keeps the game's x needs a scale other than 1.
        if (MathF.Abs(1f - scale) < 0.02f) scale = 0.98f;
        PlaceBestiarySliding(panel, BestiaryPanelRestX, BestiaryInset, top, width / scale, min.Y, scale, sliding, capture);
    }

    /// <summary>
    /// Places a control the game slides by its x position (a tween to <paramref name="restX"/>), drawn at
    /// <paramref name="scale"/> with its left edge at <paramref name="drawnLeft"/>. A control draws its local point p at
    /// position + pivot + scale (p - pivot), so its left edge lands at x + pivot (1 - scale): the pivot
    /// (drawnLeft - restX) / (1 - scale) puts the game's resting x at the wanted place, and its slide (32 units) still runs
    /// into it. The x is left to the game while its tween runs; the rest is set every frame.
    /// </summary>
    private static void PlaceBestiarySliding(Control control, float restX, float drawnLeft, float top, float width, float height,
        float scale, bool sliding, Action<Control> capture)
    {
        capture(control);
        if (control.AnchorLeft != 0f || control.AnchorTop != 0f || control.AnchorRight != 0f || control.AnchorBottom != 0f)
        {
            control.AnchorLeft = 0f;
            control.AnchorTop = 0f;
            control.AnchorRight = 0f;
            control.AnchorBottom = 0f;
        }
        if (!sliding && MathF.Abs(control.OffsetLeft - restX) > 0.5f) control.OffsetLeft = restX;
        if (MathF.Abs(control.OffsetRight - (control.OffsetLeft + width)) > 0.5f) control.OffsetRight = control.OffsetLeft + width;
        if (MathF.Abs(control.OffsetTop - top) > 0.5f) control.OffsetTop = top;
        if (MathF.Abs(control.OffsetBottom - (top + height)) > 0.5f) control.OffsetBottom = top + height;
        var drawScale = new Vector2(scale, scale);
        if (control.Scale.DistanceSquaredTo(drawScale) > 0.000001f) control.Scale = drawScale;
        var pivot = new Vector2((drawnLeft - restX) / (1f - scale), 0f);
        if (control.PivotOffset.DistanceSquaredTo(pivot) > 0.25f) control.PivotOffset = pivot;
    }

    /// <summary>
    /// The list (<c>Sidebar</c>, a scroll container) in the right column, clipped to it. Its content keeps the desktop's
    /// arrangement in the container (the arrow's place depends on it): the top margin centres the arrow on the taller
    /// entries and the scroll's top padding (<c>_paddingTop</c>, which the scroll adds to every position it writes) lifts
    /// that margin out of sight. The list is the column's width less the arrow's room and the scrollbar. The entries are
    /// made again each time the screen opens, so their height is set as they come.
    /// </summary>
    private static void LayoutBestiaryList(Control screen, float x, float y, float width, float height, Action<Control> capture)
    {
        if (screen.GetNodeOrNull<Control>("Sidebar") is not { } sidebar) return;
        // The desktop holds the sidebar at 400 wide at least, more than the column.
        BestiaryMinSize(sidebar, Vector2.Zero, capture);
        Place(sidebar, x, y, width, height, capture);
        SetKept(sidebar, "clip_contents", true);
        SetKept(sidebar, "_paddingTop", BestiaryInset - BestiaryListTopMargin);
        Place(sidebar.GetNodeOrNull<Control>("Scrollbar"), width - BestiaryScrollbarWidth - 2f, BestiaryInset, BestiaryScrollbarWidth,
            Math.Max(100f, height - 2f * BestiaryInset), capture);

        float right = BestiaryScrollbarWidth + 4f;
        if (sidebar.GetNodeOrNull<Control>("Content") is { } content)
        {
            // The scroll writes the content's vertical position, so only its horizontal edges and its margins are set.
            capture(content);
            if (MathF.Abs(content.OffsetLeft - BestiaryArrowRoom) > 0.5f) content.OffsetLeft = BestiaryArrowRoom;
            if (MathF.Abs(content.OffsetRight + right) > 0.5f) content.OffsetRight = -right;
            BestiaryConstant(content, "margin_top", (int)BestiaryListTopMargin);
            BestiaryConstant(content, "margin_bottom", (int)Gap);
        }

        if (sidebar.GetNodeOrNull<Control>("Content/BestiaryList") is not { } list) return;
        BestiaryMinSize(list, new Vector2(Math.Max(280f, width - BestiaryArrowRoom - right), 0f), capture);
        foreach (Node child in list.GetChildren())
            if (child is Control entry && !entry.IsQueuedForDeletion() && child.GetType().Name == "NBestiaryEntry")
                BestiaryMinSize(entry, new Vector2(entry.CustomMinimumSize.X, BestiaryEntryHeight), capture);

        // The game points the arrow at the chosen entry once, after a frame, and again by a tween on each choice; the
        // content's container puts it back in its middle whenever it lays its children out again (the entries growing, a
        // margin changing). Between the game's tweens it is held where the game points it.
        if (sidebar.GetNodeOrNull<Control>("Content/SelectionArrow") is not { } arrow) return;
        if (Get<Control>(screen, "_selectedEntry") is not Control selected || !GodotObject.IsInstanceValid(selected) ||
            selected.GetParent() != list || Get<bool>(screen, "_initSelectionArrow") is true || BestiaryTweenRunning(screen, "_arrowTween"))
            return;
        Vector2 target = selected.Position + BestiaryArrowOffset;
        if (arrow.Position.DistanceSquaredTo(target) > 0.25f) arrow.Position = target;
    }

    /// <summary>
    /// The back button's top in the screen's units: the game shows it at <c>_showPos</c>, a place on the canvas taken from
    /// the window's height, whatever the screen's scale; <paramref name="fallback"/> when it cannot be read.
    /// </summary>
    private static float BestiaryBackButtonTop(Control screen, float fallback)
    {
        if (screen.GetNodeOrNull<Control>("BackButton") is not { } back || Get<Vector2>(back, "_showPos") is not Vector2 show) return fallback;
        float scale = screen.GetGlobalTransformWithCanvas().Scale.Y;
        if (scale <= 0f) return fallback;
        return (show.Y - screen.GetGlobalTransformWithCanvas().Origin.Y) / scale;
    }

    private static bool BestiaryTweenRunning(Control screen, string field) =>
        Get<Tween>(screen, field) is Tween tween && tween.IsValid() && tween.IsRunning();

    /// <summary>Sets a minimum size (the snapshot holds it), changing it only when it differs.</summary>
    private static void BestiaryMinSize(Control control, Vector2 size, Action<Control> capture)
    {
        capture(control);
        if (control.CustomMinimumSize.DistanceSquaredTo(size) > 0.25f) control.CustomMinimumSize = size;
    }

    /// <summary>Sets a margin theme constant (the snapshot holds these, captured first), changing it only when it differs.</summary>
    private static void BestiaryConstant(Control control, string name, int value)
    {
        if (!control.HasThemeConstantOverride(name) || control.GetThemeConstant(name) != value)
            control.AddThemeConstantOverride(name, value);
    }
}
