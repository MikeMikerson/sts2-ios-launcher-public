namespace STS2Mobile.Platform;

/// <summary>
/// The game's canvas on the phone, kept clear of the notch or the Dynamic Island and the rounded corners. The game's patched
/// window handlers (<c>NGlobalUi.OnWindowChange</c> and <c>NMainMenu.OnWindowChange</c>) call <see cref="Layout"/> in place of
/// their own automatic aspect ratio, which stretched the canvas over the whole 19.5:9 screen, so the island and the corners
/// covered whatever the game anchors to its left and right edges.
/// <para>
/// In landscape iOS reports the same inset on both sides of a phone with a notch or an island, whichever side it is on, so a
/// canvas as wide as the screen less both insets, letterboxed in the middle (Godot's Keep), lands exactly inside the safe area.
/// The bars are black, under the island. The game then lays out as it does on a desktop monitor of that shape. The home
/// indicator's inset at the bottom is left out: it only overlays the bottom edge, it cuts nothing off.
/// </para>
/// </summary>
public static unsafe class SafeAreaCanvas
{
    // Godot's Window.ContentScaleAspectEnum, whose underlying type is long.
    public const long Keep = 1, KeepWidth = 2, KeepHeight = 3, Expand = 4;

    // The game's own canvas sizes for its automatic aspect ratio (NGlobalUi.OnWindowChange).
    private const int BaseWidth = 1680, BaseHeight = 1080, WidestWidth = 2580, TallestHeight = 1260;
    private const float WidestRatio = 2.3888888f, NarrowestRatio = 1.3333334f;

    private static readonly delegate* unmanaged<double> SideInsetFunction = (delegate* unmanaged<double>)NativeExports.Find("sts2_screen_side_inset");

    /// <summary>
    /// The larger of the left and right safe-area insets as a fraction of the screen's width, from <c>native/orientation_guard.mm</c>.
    /// 0 off the phone, in portrait, and on a screen with no notch or island.
    /// </summary>
    public static double SideInset() => SideInsetFunction != null ? SideInsetFunction() : 0;

    /// <summary>The canvas for a window of this many pixels: the aspect mode to set, and the canvas size through the out values.</summary>
    public static long Layout(int windowWidth, int windowHeight, out int canvasWidth, out int canvasHeight) =>
        Choose(windowWidth, windowHeight, SideInset(), out canvasWidth, out canvasHeight);

    /// <summary>
    /// <see cref="Layout"/> for a given side inset. With none it is the game's own rule, unchanged. With one, the canvas has the shape
    /// of the screen less both insets, at the game's height of 1080 and no wider than its widest layout (21:9), and is letterboxed.
    /// </summary>
    public static long Choose(int windowWidth, int windowHeight, double sideInset, out int canvasWidth, out int canvasHeight)
    {
        if (windowWidth <= 0 || windowHeight <= 0 || !(sideInset > 0) || sideInset >= 0.5)
        {
            float shape = windowHeight > 0 ? (float)windowWidth / windowHeight : 0;
            if (shape > WidestRatio)
            {
                (canvasWidth, canvasHeight) = (WidestWidth, BaseHeight);
                return KeepWidth;
            }
            if (shape < NarrowestRatio)
            {
                (canvasWidth, canvasHeight) = (BaseWidth, TallestHeight);
                return KeepHeight;
            }
            (canvasWidth, canvasHeight) = (BaseWidth, BaseHeight);
            return Expand;
        }
        double ratio = windowWidth * (1 - 2 * sideInset) / windowHeight;
        if (ratio >= (double)BaseWidth / BaseHeight)
        {
            // Rounded down, so the letterboxed canvas is never wider than the safe area.
            (canvasWidth, canvasHeight) = (Math.Min((int)Math.Floor(BaseHeight * ratio), WidestWidth), BaseHeight);
        }
        else
        {
            (canvasWidth, canvasHeight) = (BaseWidth, (int)Math.Ceiling(BaseWidth / ratio));
        }
        return Keep;
    }
}
