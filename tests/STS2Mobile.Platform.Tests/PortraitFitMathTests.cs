namespace STS2Mobile.Platform.Tests;

public class PortraitFitMathTests
{
    [Fact]
    public void OverflowIsZeroInsideTheCanvas()
    {
        Assert.Equal(0f, PortraitFitMath.Overflow(10f, 10f, 1070f, 2300f, 0f, 0f, 1080f, 2348f));
    }

    [Fact]
    public void OverflowReportsTheWorstSide()
    {
        Assert.Equal(420f, PortraitFitMath.Overflow(-420f, 100f, 1500f, 400f, 0f, 0f, 1080f, 2348f));
        Assert.Equal(52f, PortraitFitMath.Overflow(100f, 2000f, 300f, 2400f, 0f, 0f, 1080f, 2348f));
    }

    [Fact]
    public void FitScalesADesktopScreenToThePortraitWidth()
    {
        VirtualCanvas fit = PortraitFitMath.Fit(1080f, 160f, 2348f, 1920f);
        Assert.Equal(0.5625f, fit.Scale, 4);
        Assert.Equal(1920f, fit.Width, 2);
        Assert.Equal((2348f - 160f) / 0.5625f, fit.Height, 2);
        Assert.Equal(160f, fit.Top);
    }

    [Fact]
    public void FitNeverEnlargesANarrowScreen()
    {
        VirtualCanvas fit = PortraitFitMath.Fit(1080f, 0f, 2348f, 900f);
        Assert.Equal(1f, fit.Scale);
        Assert.Equal(1080f, fit.Width);
        Assert.Equal(2348f, fit.Height);
    }

    [Fact]
    public void FitEnlargesANarrowMenuWhenAsked()
    {
        VirtualCanvas fit = PortraitFitMath.Fit(1080f, 250f, 2300f, 800f, allowEnlarge: true);
        Assert.Equal(1.35f, fit.Scale, 3);
        Assert.Equal(800f, fit.Width, 2);
        Assert.Equal(2050f / 1.35f, fit.Height, 2);
    }

    [Fact]
    public void FitFallsBackToTheBandForBadInput()
    {
        VirtualCanvas fit = PortraitFitMath.Fit(1080f, 500f, 400f, 1920f);
        Assert.Equal(1f, fit.Scale);
        Assert.Equal(0f, fit.Height);
    }
}
