namespace STS2Mobile.Platform;

/// <summary>
/// When a touch on a hand card is a tap, a long press or a drag. Distances are in canvas units: the portrait canvas is
/// 1080 wide, so on a 375 pt wide phone one point is about 2.9 units and the slop is about 11 pt, the usual touch slop.
/// </summary>
public static class TouchCardMath
{
    public const float TapSlop = 32f;
    public const double TapSeconds = 0.45;
    public const double LongPressSeconds = 0.5;

    public static bool IsTap(float movedCanvasUnits, double heldSeconds) =>
        movedCanvasUnits < TapSlop && heldSeconds >= 0 && heldSeconds < TapSeconds;

    public static bool IsLongPress(float movedCanvasUnits, double heldSeconds) =>
        movedCanvasUnits < TapSlop && heldSeconds >= LongPressSeconds;
}
