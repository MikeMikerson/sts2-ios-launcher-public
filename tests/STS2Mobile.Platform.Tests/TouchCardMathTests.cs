using STS2Mobile.Platform;
using Xunit;

namespace STS2Mobile.Platform.Tests;

public class TouchCardMathTests
{
    [Theory]
    [InlineData(0f, 0.08, true)]
    [InlineData(20f, 0.3, true)]
    [InlineData(40f, 0.1, false)]
    [InlineData(5f, 0.6, false)]
    public void TapIsShortAndStill(float moved, double seconds, bool expected) =>
        Assert.Equal(expected, TouchCardMath.IsTap(moved, seconds));

    [Theory]
    [InlineData(0f, 0.6, true)]
    [InlineData(10f, 0.5, true)]
    [InlineData(10f, 0.3, false)]
    [InlineData(60f, 1.0, false)]
    public void LongPressIsStillAndHeld(float moved, double seconds, bool expected) =>
        Assert.Equal(expected, TouchCardMath.IsLongPress(moved, seconds));

    [Fact]
    public void NoTouchIsBothATapAndALongPress()
    {
        for (double t = 0; t < 1.2; t += 0.05)
            Assert.False(TouchCardMath.IsTap(0f, t) && TouchCardMath.IsLongPress(0f, t));
    }
}
