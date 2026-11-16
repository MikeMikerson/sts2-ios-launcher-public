namespace STS2Mobile.GameHost;

/// <summary>
/// The fixed bands at the top of a run screen in portrait, in canvas units (1080 across; 2.88 units are one point on an
/// iPhone 11 Pro, so 127 units are the 44 points a touch target should have). From the top: the safe cap over the notch
/// (measured, passed around as capHeight), the top bar's row, the potion row (potions at 44 points do not fit
/// in the top bar's row), then the relic strip. Every portrait layout takes its top from here.
/// </summary>
internal static class PortraitBands
{
    /// <summary>One point of an iPhone 11 Pro in canvas units.</summary>
    public const float UnitsPerPoint = 1080f / 375f;

    /// <summary>The smallest side a touch target should have: 44 points.</summary>
    public const float MinTarget = 127f;

    /// <summary>The top bar's own row: character, health, gold, floor, boss, then map, deck and settings.</summary>
    public const float TopBarRowHeight = 88f;

    /// <summary>The potion row under the top bar's row.</summary>
    public const float PotionRowHeight = 140f;

    /// <summary>Everything the top bar covers below the safe cap. Screens that hide the relic strip start here.</summary>
    public const float TopBarHeight = TopBarRowHeight + PotionRowHeight;

    /// <summary>The gap above the relic strip plus its two rows of 96 unit relics (186 with 78 unit relics).</summary>
    public const float RelicBandHeight = 222f;

    /// <summary>Below the safe cap, where a screen that keeps the relic strip in view starts (the fight's stage).</summary>
    public const float RunContentTop = TopBarHeight + RelicBandHeight;
}
