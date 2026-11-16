namespace STS2Mobile.Platform.Tests;

public class SafeAreaCanvasTests
{
    [Theory]
    // No inset: the game's own automatic aspect ratio, unchanged.
    [InlineData(1920, 1080, SafeAreaCanvas.Expand, 1680, 1080)]
    [InlineData(2532, 1170, SafeAreaCanvas.Expand, 1680, 1080)]
    [InlineData(3840, 1440, SafeAreaCanvas.KeepWidth, 2580, 1080)]
    [InlineData(1080, 1080, SafeAreaCanvas.KeepHeight, 1680, 1260)]
    public void WithNoInsetTheGameKeepsItsOwnRule(int width, int height, long aspect, int canvasWidth, int canvasHeight)
    {
        Assert.Equal(aspect, SafeAreaCanvas.Choose(width, height, 0, out int w, out int h));
        Assert.Equal((canvasWidth, canvasHeight), (w, h));
    }

    [Theory]
    // Landscape points and insets as iOS reports them: iPhone 11 Pro (notch), iPhone 15 Pro and 16 Pro Max (Dynamic Island).
    [InlineData(812, 375, 44, 3)]
    [InlineData(852, 393, 59, 3)]
    [InlineData(956, 440, 62, 3)]
    public void WithAnInsetTheCanvasFitsTheSafeArea(int pointsWide, int pointsHigh, int inset, int scale)
    {
        int width = pointsWide * scale, height = pointsHigh * scale;
        Assert.Equal(SafeAreaCanvas.Keep, SafeAreaCanvas.Choose(width, height, (double)inset / pointsWide, out int w, out int h));
        Assert.Equal(1080, h);
        // Letterboxed at the window's full height, the canvas is as wide as the screen less both insets, to the pixel, never wider.
        double drawnWidth = (double)w / h * height;
        double safeWidth = width - 2.0 * inset * scale;
        Assert.InRange(drawnWidth, safeWidth - 3, safeWidth);
        // Still at least the game's 16:9 desktop layout, and no wider than its widest.
        Assert.InRange(w, 1920, 2580);
    }

    [Fact]
    public void AVeryWideSafeAreaStopsAtTheGamesWidestLayout()
    {
        Assert.Equal(SafeAreaCanvas.Keep, SafeAreaCanvas.Choose(4000, 1000, 0.05, out int w, out int h));
        Assert.Equal((2580, 1080), (w, h));
    }

    [Fact]
    public void ANarrowSafeAreaGrowsTheCanvasDown()
    {
        Assert.Equal(SafeAreaCanvas.Keep, SafeAreaCanvas.Choose(1600, 1200, 0.05, out int w, out int h));
        Assert.Equal(1680, w);
        Assert.InRange((double)w / h, 0, 1600 * 0.9 / 1200);
    }

    [Fact]
    public void OffThePhoneThereIsNoInset()
    {
        Assert.Equal(0, SafeAreaCanvas.SideInset());
        Assert.Equal(0, SafeAreaCanvas.TopInset());
        Assert.False(SafeAreaCanvas.IsPortrait);
        Assert.Equal(SafeAreaCanvas.Expand, SafeAreaCanvas.Layout(2532, 1170, out int w, out int h));
        Assert.Equal((1680, 1080), (w, h));
    }

    [Theory]
    // Portrait iPhone 18 Pro (393 x 852 pt at 3x and 2x) and mid-rotation landscape window before iOS finishes turning.
    [InlineData(1179, 2556, 1080, 2341)]
    [InlineData(2556, 1179, 1080, 2341)]
    [InlineData(786, 1704, 1080, 2341)]
    [InlineData(1320, 2868, 1080, 2347)]
    public void PortraitCanvasMatchesPhoneShapeAcrossResolutionsAndRotation(int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(SafeAreaCanvas.Expand, SafeAreaCanvas.ChoosePortrait(width, height, out int w, out int h));
        Assert.Equal((expectedWidth, expectedHeight), (w, h));
    }

    [Fact]
    public void SwitchingPortraitModeUpdatesAndRestoresLayout()
    {
        try
        {
            SafeAreaCanvas.SetPortrait(true);
            Assert.True(SafeAreaCanvas.IsPortrait);
            Assert.Equal(SafeAreaCanvas.Expand, SafeAreaCanvas.Layout(1179, 2556, out int pw, out int ph));
            Assert.Equal((1080, 2341), (pw, ph));

            SafeAreaCanvas.SetPortrait(false);
            Assert.False(SafeAreaCanvas.IsPortrait);
            Assert.Equal(SafeAreaCanvas.Expand, SafeAreaCanvas.Layout(2556, 1179, out int lw, out int lh));
            Assert.Equal((1680, 1080), (lw, lh));
        }
        finally
        {
            SafeAreaCanvas.SetPortrait(false);
        }
    }
}
