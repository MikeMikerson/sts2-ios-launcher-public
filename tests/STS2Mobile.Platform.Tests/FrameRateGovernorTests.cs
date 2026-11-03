namespace STS2Mobile.Platform.Tests;

public class FrameRateGovernorTests
{
    private static readonly PhoneGraphicsSettings Off = PhoneGraphicsSettings.Default.WithPreset(BatteryPreset.Off);
    private static readonly PhoneGraphicsSettings Idle = Off with { LowerIdleFrameRate = true };
    private static readonly PhoneGraphicsSettings Cool = Off with { CoolDownWhenHot = true };

    [Fact]
    public void WithoutChoicesTheGameSettingStands() =>
        Assert.Equal(60, FrameRateGovernor.Target(60, Off, false, 100, DevicePower.Critical));

    [Fact]
    public void UnlimitedGameSettingStaysUnlimited() =>
        Assert.Equal(0, FrameRateGovernor.Target(0, Off, false, 100, DevicePower.Nominal));

    [Fact]
    public void TouchingOrARecentTouchKeepsTheFullRate()
    {
        Assert.Equal(60, FrameRateGovernor.Target(60, Idle, true, 100, DevicePower.Nominal));
        Assert.Equal(60, FrameRateGovernor.Target(60, Idle, false, 1.9, DevicePower.Nominal));
    }

    [Fact]
    public void IdleLowersTo30()
    {
        Assert.Equal(30, FrameRateGovernor.Target(60, Idle, false, 2, DevicePower.Nominal));
        Assert.Equal(30, FrameRateGovernor.Target(0, Idle, false, 5, DevicePower.Nominal));
    }

    [Fact]
    public void CapNeverRaisesTheGameSetting() =>
        Assert.Equal(24, FrameRateGovernor.Target(24, Idle, false, 5, DevicePower.Nominal));

    [Theory]
    [InlineData(DevicePower.Fair, 60)]
    [InlineData(DevicePower.Serious, 30)]
    [InlineData(DevicePower.Critical, 20)]
    [InlineData(DevicePower.UnknownThermalState, 60)]
    public void HeatLowersTheCapByThermalState(int thermalState, int expected) =>
        Assert.Equal(expected, FrameRateGovernor.Target(60, Cool, true, 0, thermalState));
}
