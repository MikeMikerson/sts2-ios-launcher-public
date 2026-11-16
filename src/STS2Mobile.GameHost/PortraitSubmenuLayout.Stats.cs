using System;
using Godot;

namespace STS2Mobile.GameHost;

internal static partial class PortraitSubmenuLayout
{
    public const string StatsTypeName = Ns + "StatsScreen.NStatsScreen";

    /// <summary>
    /// Stats at 830, drawn at 1.30 (the width fit drew them at 0.87), so an entry's 28 unit text
    /// reads at about 12.6 points and the 36 unit headers at about 16 points.
    /// </summary>
    public const float StatsDesignWidth = 830f;

    private const string StatsContentPath = "StatsGrid/ScrollableContent/Mask/Content";
    private const string StatsColumnPath = StatsContentPath + "/StatsScrollableContainer";
    private const float StatsTabsTop = 24f;
    private const float StatsTabsHeight = 90f;
    private const float StatsSide = 32f;
    private const float StatsScrollbarWidth = 48f;

    // The mask's gradient (the scene's): clear down to 0.125 of its height, opaque from 0.15, fading out again from 0.975.
    private const float StatsMaskClear = 0.125f;
    private const float StatsMaskOpaque = 0.15f;

    /// <summary>
    /// Stats: the tabs at the top, and under them one column filling the width, the overall stats (a grid of one column)
    /// above the character stats (one column, each character's panel as wide as the column). The scroll list's mask runs
    /// from under the tabs to just above the back and Share buttons, so no entry rests under them (with the
    /// mask to the lower edge, the back button covered the first character panel while the list stood at its top).
    /// The list's scroll moves its content's vertical position each frame (<c>NScrollableContainer</c>),
    /// so the content is never placed: only its margins and its horizontal offsets, which the scroll keeps from the
    /// desktop's wider content, are set. The character panels are made again each time the screen opens.
    /// </summary>
    private static void LayoutStats(Control screen, float w, float h, Action<Control> capture)
    {
        Place(screen.GetNodeOrNull<Control>("Tabs"), 0f, StatsTabsTop, w, StatsTabsHeight, capture);
        float tabsBottom = StatsTabsTop + StatsTabsHeight;

        // The mask's clear band lies over the tabs, its opaque part starts just under them and it ends above the corner
        // buttons (its last 2.5 per cent fades the list out there). The scroll's limit is the mask's height.
        float listBottom = h - CornerButtonsTop - Gap;
        float maskHeight = (listBottom - tabsBottom) / (1f - StatsMaskClear);
        float maskTop = listBottom - maskHeight;
        float visibleTop = maskTop + StatsMaskOpaque * maskHeight;
        Place(screen.GetNodeOrNull<Control>("StatsGrid/ScrollableContent/Mask"), 0f, maskTop, w, maskHeight, capture);

        Place(screen.GetNodeOrNull<Control>("StatsGrid/ScrollableContent/Scrollbar"), w - 16f - StatsScrollbarWidth, visibleTop,
            StatsScrollbarWidth, Math.Max(200f, listBottom - visibleTop), capture);

        if (screen.GetNodeOrNull<Control>(StatsContentPath) is { } content)
        {
            capture(content);
            if (content.OffsetLeft != 0f) content.OffsetLeft = 0f;
            if (content.OffsetRight != 0f) content.OffsetRight = 0f;
            SetStatsConstant(content, "margin_left", (int)StatsSide);
            SetStatsConstant(content, "margin_right", (int)(StatsSide + StatsScrollbarWidth));
            SetStatsConstant(content, "margin_top", (int)MathF.Ceiling(visibleTop - maskTop + 8f));
            SetStatsConstant(content, "margin_bottom", (int)(2f * Gap));
        }

        if (screen.GetNodeOrNull<Control>(StatsColumnPath + "/OverallStats") is { } overall)
        {
            capture(overall);
            if (overall.CustomMinimumSize != Vector2.Zero) overall.CustomMinimumSize = Vector2.Zero;
        }
        SetKept(screen.GetNodeOrNull<GridContainer>(StatsColumnPath + "/OverallStats/OverallStatsDetails/GridContainer"), "columns", 1);

        if (screen.GetNodeOrNull<GridContainer>(StatsColumnPath + "/CharacterStats/CharacterStatsContainer") is not { } characters) return;
        SetKept(characters, "columns", 1);
        for (int i = 0; i < characters.GetChildCount(); i++)
        {
            if (characters.GetChild(i) is not Control panel || panel.IsQueuedForDeletion()) continue;
            if (panel.SizeFlagsHorizontal == Control.SizeFlags.ExpandFill) continue;
            capture(panel);
            panel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        }
    }

    /// <summary>Sets a spacing theme constant (one the snapshots hold, so capture first), changing it only when it differs.</summary>
    private static void SetStatsConstant(Control control, string name, int value)
    {
        if (!control.HasThemeConstantOverride(name) || control.GetThemeConstant(name) != value)
            control.AddThemeConstantOverride(name, value);
    }
}
