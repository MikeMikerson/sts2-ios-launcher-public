namespace STS2Mobile.Platform;

/// <summary>
/// Decides the frame cap from the game's own Max FPS setting and the phone's choices. It only ever
/// lowers the game's setting, never raises it.
/// </summary>
public static class FrameRateGovernor
{
    public const int IdleFrameRate = 30;
    public const double IdleAfterSeconds = 2;
    public const int SeriousFrameRate = 30;
    public const int CriticalFrameRate = 20;

    /// <param name="gameLimit">The game's Max FPS setting; 0 or less means no limit, as for <c>Engine.MaxFps</c>.</param>
    /// <returns>The value for <c>Engine.MaxFps</c>; 0 means no limit.</returns>
    public static int Target(int gameLimit, PhoneGraphicsSettings settings, bool touching, double secondsSinceTouch, int thermalState)
    {
        int cap = int.MaxValue;
        if (settings.LowerIdleFrameRate && !touching && secondsSinceTouch >= IdleAfterSeconds) cap = IdleFrameRate;
        if (settings.CoolDownWhenHot && thermalState >= DevicePower.Critical) cap = Math.Min(cap, CriticalFrameRate);
        else if (settings.CoolDownWhenHot && thermalState >= DevicePower.Serious) cap = Math.Min(cap, SeriousFrameRate);
        if (gameLimit <= 0) return cap == int.MaxValue ? 0 : cap;
        return Math.Min(gameLimit, cap);
    }
}
