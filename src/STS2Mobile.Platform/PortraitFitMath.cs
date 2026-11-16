using System;

namespace STS2Mobile.Platform;

/// <summary>A desktop screen laid out on a virtual canvas and scaled down onto the portrait band it gets.</summary>
public readonly record struct VirtualCanvas(float Scale, float Width, float Height, float Left, float Top);

/// <summary>
/// Pure math for fitting desktop screens into the portrait canvas: how far a rectangle reaches past the canvas, and the
/// virtual canvas a desktop screen is laid out on so its widest content fits the portrait width.
/// </summary>
public static class PortraitFitMath
{
    /// <summary>
    /// How far the rectangle (left, top, right, bottom) reaches past the canvas on its worst side, in canvas units; 0 when it is
    /// inside.
    /// </summary>
    public static float Overflow(float left, float top, float right, float bottom, float canvasLeft, float canvasTop, float canvasRight, float canvasBottom)
    {
        float worst = 0f;
        worst = Math.Max(worst, canvasLeft - left);
        worst = Math.Max(worst, canvasTop - top);
        worst = Math.Max(worst, right - canvasRight);
        worst = Math.Max(worst, bottom - canvasBottom);
        return worst;
    }

    /// <summary>
    /// The virtual canvas for a desktop screen whose content needs <paramref name="designWidth"/> units across, shown in the band
    /// from <paramref name="bandTop"/> to <paramref name="bandBottom"/> of a canvas <paramref name="canvasWidth"/> wide. The scale
    /// stays at or below 1 by default (a narrow screen keeps its desktop size) and the virtual height fills the band, so content anchored to
    /// the top and bottom spreads out over the tall screen and content anchored to the middle stays centred. With
    /// <paramref name="allowEnlarge"/> a design narrower than the canvas is drawn larger instead (a small menu on a big screen).
    /// </summary>
    public static VirtualCanvas Fit(float canvasWidth, float bandTop, float bandBottom, float designWidth, bool allowEnlarge = false)
    {
        if (canvasWidth <= 0f || bandBottom <= bandTop || designWidth <= 0f)
            return new VirtualCanvas(1f, Math.Max(canvasWidth, 0f), Math.Max(bandBottom - bandTop, 0f), 0f, bandTop);
        float scale = allowEnlarge ? canvasWidth / designWidth : Math.Min(1f, canvasWidth / designWidth);
        return new VirtualCanvas(scale, canvasWidth / scale, (bandBottom - bandTop) / scale, 0f, bandTop);
    }
}
