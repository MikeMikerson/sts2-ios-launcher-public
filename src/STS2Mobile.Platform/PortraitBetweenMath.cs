using System;
using System.Collections.Generic;

namespace STS2Mobile.Platform;

public readonly record struct MapScrollRange(float Min, float Max);

public readonly record struct GridSlot(float CenterX, float CenterY);

public readonly record struct CardGridColumns(int Columns, float CardScale, float Gap);

/// <summary>
/// Pure layout math for portrait between-fight screens: the Spire Map's scroll range, the 2 x 2 card reward grid,
/// the 3-column deck grid and the fit scale used to keep desktop panels inside the 1080-wide portrait canvas.
/// </summary>
public static class PortraitBetweenMath
{
    /// <summary>Desktop design height of the map scene's canvas; its points are laid out around the canvas centre.</summary>
    public const float DesktopCanvasHeight = 1080f;

    /// <summary>
    /// How far the map container may scroll in portrait so the boss medallion can be brought just under the top bar and the
    /// starting point just above the bottom edge. <paramref name="bossTop"/> and <paramref name="startBottom"/> are in the map
    /// container's own coordinates, so a point shows at <c>scroll + local</c>. When the whole map fits in the band, both ends
    /// collapse to the scroll that centres it.
    /// </summary>
    public static MapScrollRange MapScrollRange(float bossTop, float startBottom, float visibleTop, float visibleBottom)
    {
        float max = visibleTop - bossTop;
        float min = visibleBottom - startBottom;
        if (min > max)
        {
            float mid = (min + max) / 2f;
            return new MapScrollRange(mid, mid);
        }
        return new MapScrollRange(min, max);
    }

    /// <summary>The scroll the game would aim for, clamped into the portrait range.</summary>
    public static float ClampMapScroll(float scroll, MapScrollRange range) => Math.Clamp(scroll, range.Min, range.Max);

    /// <summary>The most the map is ever moved sideways to centre it; the background is widened by this much on both sides.</summary>
    public const float MaxMapCentreShift = 120f;

    /// <summary>
    /// How far right the map must move so its room points sit centred in the canvas. The game lays the columns out left of
    /// centre, leaving room for the legend panel on the right; in portrait the legend is a button, and the left column would
    /// touch or pass the screen edge. <paramref name="pointsLeft"/> and <paramref name="pointsRight"/> are the outer edges
    /// of the room points in the map container's own coordinates.
    /// </summary>
    public static float MapCentreShift(float pointsLeft, float pointsRight, float canvasWidth) =>
        Math.Clamp((canvasWidth - (pointsLeft + pointsRight)) / 2f, -MaxMapCentreShift, MaxMapCentreShift);

    /// <summary>
    /// How far the desktop map background (anchored to the top) must move down so it stays behind points that are laid out
    /// around the taller portrait canvas centre.
    /// </summary>
    public static float MapBackgroundShift(float canvasHeight) => (canvasHeight - DesktopCanvasHeight) / 2f;

    /// <summary>
    /// Centres of card reward slots in portrait: one row for one or two cards, 2 + 1 for three (the third centred below),
    /// 2 x 2 for four, and rows of two beyond that. Slots are given relative to the centre of the whole block.
    /// </summary>
    public static IReadOnlyList<GridSlot> RewardSlots(int count, float cardWidth, float cardHeight, float gapX, float gapY)
    {
        var slots = new List<GridSlot>(Math.Max(count, 0));
        if (count <= 0) return slots;
        int rows = count <= 2 ? 1 : (count + 1) / 2;
        float stepX = cardWidth + gapX;
        float stepY = cardHeight + gapY;
        float firstY = -(rows - 1) * stepY / 2f;
        for (int i = 0; i < count; i++)
        {
            int row = count <= 2 ? 0 : i / 2;
            int inRow = count <= 2 ? count : Math.Min(2, count - row * 2);
            int col = count <= 2 ? i : i % 2;
            float rowStartX = -(inRow - 1) * stepX / 2f;
            slots.Add(new GridSlot(rowStartX + col * stepX, firstY + row * stepY));
        }
        return slots;
    }

    /// <summary>
    /// Columns for a card grid in a given width: as many cards of <paramref name="cardWidth"/> (at
    /// <paramref name="maxScale"/>) as fit with <paramref name="minGap"/> between them, at least <paramref name="minColumns"/>,
    /// shrinking the cards when even that many do not fit.
    /// </summary>
    public static CardGridColumns GridColumns(float availableWidth, float cardWidth, float minGap, int minColumns, float maxScale)
    {
        if (availableWidth <= 0f || cardWidth <= 0f) return new CardGridColumns(Math.Max(minColumns, 1), maxScale, minGap);
        int columns = Math.Max(minColumns, (int)MathF.Floor((availableWidth + minGap) / (cardWidth * maxScale + minGap)));
        columns = Math.Max(columns, 1);
        float scale = Math.Min(maxScale, (availableWidth - (columns - 1) * minGap) / (columns * cardWidth));
        float gap = columns > 1 ? (availableWidth - columns * cardWidth * scale) / (columns - 1) : 0f;
        return new CardGridColumns(columns, scale, gap);
    }

    /// <summary>
    /// Pivot height for a panel the game slides to a fixed <paramref name="openY"/>, so that scaled by <paramref name="scale"/> its
    /// content starting at <paramref name="contentTop"/> (panel-local) shows at <paramref name="visualTop"/> on screen. A control
    /// draws local y at <c>position.y + pivot + (y - pivot) * scale</c>.
    /// </summary>
    public static float PivotForVisualTop(float openY, float contentTop, float visualTop, float scale)
    {
        if (Math.Abs(1f - scale) < 0.0001f) return 0f;
        return (visualTop - openY - contentTop * scale) / (1f - scale);
    }

    /// <summary>Scale that fits a desktop panel of <paramref name="contentWidth"/> inside <paramref name="availableWidth"/>, never above 1.</summary>
    public static float FitScale(float contentWidth, float availableWidth, float maxScale = 1f)
    {
        if (contentWidth <= 0f || availableWidth <= 0f) return maxScale;
        return Math.Min(maxScale, availableWidth / contentWidth);
    }
}
