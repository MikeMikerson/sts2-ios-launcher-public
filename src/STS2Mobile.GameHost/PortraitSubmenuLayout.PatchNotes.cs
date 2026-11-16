using System;
using Godot;

namespace STS2Mobile.GameHost;

internal static partial class PortraitSubmenuLayout
{
    public const string PatchNotesTypeName = Ns + "MainMenu.NPatchNotesScreen";

    /// <summary>
    /// Patch notes at 740, drawn at 1.46 (at the canvas's 1080 they were drawn at 1.0), so the notes'
    /// 22 unit text reads at 11.1 points and the date's 32 at 16.
    /// </summary>
    public const float PatchNotesDesignWidth = 740f;

    private const float PatchNotesSide = 24f;
    private const float PatchNotesScrollbarWidth = 48f;
    // The arrows (128 on the desktop) and the close toggle (64) at 96 and 88 units: 49 and 45 points.
    private const float PatchNotesArrow = 96f;
    private const float PatchNotesToggleSize = 88f;

    /// <summary>
    /// Patch notes: the notes fill the width under the close toggle (top right) and scroll in an area ending above the
    /// back button, clipped there; the previous (older) and next (newer) arrows stand side by side at the lower right, level
    /// with the back button, which has that row's left. On the desktop the notes are a 960 unit column in the middle with
    /// the arrows at its sides and the scrollbar 100 units past its right edge; at the canvas's width the text ran under
    /// the scrollbar and the previous arrow stood past the left edge. The game makes a new <c>ScreenContents</c> (a scroll
    /// container) for each note shown, so it is laid out again as it comes; the scroll writes its content's vertical
    /// position, so only the content's horizontal edges are set.
    /// </summary>
    private static void LayoutPatchNotes(Control screen, float w, float h, Action<Control> capture)
    {
        float rowTop = h - CornerButtonsTop;
        float viewBottom = rowTop - Gap;

        if (screen.GetNodeOrNull<Control>("PatchNotesToggle") is { } toggle)
        {
            Place(toggle, w - PatchNotesToggleSize - 12f, 12f, PatchNotesToggleSize, PatchNotesToggleSize, capture);
            SetKept(toggle, "pivot_offset", new Vector2(PatchNotesToggleSize / 2f, PatchNotesToggleSize / 2f));
        }

        float arrowTop = rowTop + 8f;
        PlacePatchArrow(screen.GetNodeOrNull<Control>("NextButton"), w - 12f - PatchNotesArrow, arrowTop, capture);
        PlacePatchArrow(screen.GetNodeOrNull<Control>("PrevButton"), w - 12f - 2f * PatchNotesArrow - Gap, arrowTop, capture);

        // The note's scroll container: the game adds it as the screen's first child (the next one gets a generated name
        // while the last waits to be freed), so it is found by its text.
        foreach (Node child in screen.GetChildren())
        {
            if (child is not Control contents || contents.IsQueuedForDeletion() || contents.GetNodeOrNull("Content/PatchText") == null) continue;
            float top = PatchNotesToggleSize + 12f;
            Place(contents, 0f, top, w, viewBottom - top, capture);
            SetKept(contents, "clip_contents", true);

            float textLeft = PatchNotesSide;
            float textRight = w - PatchNotesSide - PatchNotesScrollbarWidth - 8f;
            if (contents.GetNodeOrNull<Control>("Content") is { } content)
            {
                capture(content);
                float left = textLeft - w / 2f;
                float right = textRight - w / 2f;
                if (MathF.Abs(content.OffsetLeft - left) > 0.5f) content.OffsetLeft = left;
                if (MathF.Abs(content.OffsetRight - right) > 0.5f) content.OffsetRight = right;
                // The date hangs above the text from its middle, 960 wide on the desktop.
                if (content.GetNodeOrNull<Control>("PatchText/DateLabel") is { } date)
                {
                    capture(date);
                    float half = (textRight - textLeft) / 2f;
                    if (MathF.Abs(date.OffsetLeft + half) > 0.5f) date.OffsetLeft = -half;
                    if (MathF.Abs(date.OffsetRight - half) > 0.5f) date.OffsetRight = half;
                }
            }
            Place(contents.GetNodeOrNull<Control>("Scrollbar"), w - 8f - PatchNotesScrollbarWidth, 40f,
                PatchNotesScrollbarWidth, Math.Max(200f, viewBottom - top - 80f), capture);
        }
    }

    private static void PlacePatchArrow(Control? arrow, float x, float y, Action<Control> capture)
    {
        if (arrow == null) return;
        capture(arrow);
        var minimum = new Vector2(PatchNotesArrow, PatchNotesArrow);
        if (arrow.CustomMinimumSize.DistanceSquaredTo(minimum) > 0.25f) arrow.CustomMinimumSize = minimum;
        SetKept(arrow, "pivot_offset", new Vector2(PatchNotesArrow / 2f, PatchNotesArrow / 2f));
        Place(arrow, x, y, PatchNotesArrow, PatchNotesArrow, capture);
    }
}
