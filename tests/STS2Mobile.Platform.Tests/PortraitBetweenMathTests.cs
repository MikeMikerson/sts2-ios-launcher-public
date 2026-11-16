namespace STS2Mobile.Platform.Tests;

public class PortraitBetweenMathTests
{
    [Fact]
    public void MapScrollRangeBringsBossUnderTopBarAndStartAboveBottomEdge()
    {
        MapScrollRange range = PortraitBetweenMath.MapScrollRange(bossTop: -806f, startBottom: 2300f, visibleTop: 450f, visibleBottom: 2200f);
        Assert.True(range.Min < range.Max);
        Assert.Equal(450f, range.Max + -806f, 3);
        Assert.Equal(2200f, range.Min + 2300f, 3);
    }

    [Fact]
    public void MapScrollRangeCollapsesToCentreWhenWholeMapFits()
    {
        MapScrollRange range = PortraitBetweenMath.MapScrollRange(bossTop: 0f, startBottom: 1000f, visibleTop: 400f, visibleBottom: 2200f);
        Assert.Equal(range.Min, range.Max);
        Assert.Equal(800f, range.Max, 3);
    }

    [Fact]
    public void MapScrollClampKeepsGameTargetsInsidePortraitRange()
    {
        var range = new MapScrollRange(-250f, 560f);
        Assert.Equal(-250f, PortraitBetweenMath.ClampMapScroll(-600f, range));
        Assert.Equal(560f, PortraitBetweenMath.ClampMapScroll(1800f, range));
        Assert.Equal(100f, PortraitBetweenMath.ClampMapScroll(100f, range));
    }

    [Theory]
    [InlineData(-5f, 990f, 1080f, 47.5f)]
    [InlineData(40f, 1040f, 1080f, 0f)]
    [InlineData(-400f, 200f, 1080f, 120f)]
    [InlineData(900f, 1500f, 1080f, -120f)]
    public void MapCentreShiftCentresTheRoomPointsAndStaysBounded(float left, float right, float canvas, float expected)
    {
        Assert.Equal(expected, PortraitBetweenMath.MapCentreShift(left, right, canvas), 3);
    }

    [Theory]
    [InlineData(1080f, 0f)]
    [InlineData(2337f, 628.5f)]
    public void MapBackgroundShiftFollowsCanvasCentre(float canvas, float expected)
    {
        Assert.Equal(expected, PortraitBetweenMath.MapBackgroundShift(canvas), 3);
    }

    [Fact]
    public void FourRewardCardsFormTwoByTwoGridAroundCentre()
    {
        var slots = PortraitBetweenMath.RewardSlots(4, cardWidth: 300f, cardHeight: 420f, gapX: 40f, gapY: 30f);
        Assert.Equal(4, slots.Count);
        Assert.Equal(new GridSlot(-170f, -225f), slots[0]);
        Assert.Equal(new GridSlot(170f, -225f), slots[1]);
        Assert.Equal(new GridSlot(-170f, 225f), slots[2]);
        Assert.Equal(new GridSlot(170f, 225f), slots[3]);
    }

    [Fact]
    public void ThreeRewardCardsArePairAboveCentredThird()
    {
        var slots = PortraitBetweenMath.RewardSlots(3, cardWidth: 300f, cardHeight: 420f, gapX: 40f, gapY: 30f);
        Assert.Equal(new GridSlot(-170f, -225f), slots[0]);
        Assert.Equal(new GridSlot(170f, -225f), slots[1]);
        Assert.Equal(new GridSlot(0f, 225f), slots[2]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OneOrTwoRewardCardsStayOnOneRow(int count)
    {
        var slots = PortraitBetweenMath.RewardSlots(count, cardWidth: 300f, cardHeight: 420f, gapX: 40f, gapY: 30f);
        Assert.All(slots, s => Assert.Equal(0f, s.CenterY));
        Assert.Equal(0f, slots[0].CenterX + slots[^1].CenterX, 3);
    }

    [Fact]
    public void FiveRewardCardsUseRowsOfTwo()
    {
        var slots = PortraitBetweenMath.RewardSlots(5, cardWidth: 300f, cardHeight: 420f, gapX: 40f, gapY: 30f);
        Assert.Equal(3, slots.Select(s => s.CenterY).Distinct().Count());
        Assert.Equal(0f, slots[4].CenterX);
    }

    [Fact]
    public void NoRewardCardsGiveNoSlots()
    {
        Assert.Empty(PortraitBetweenMath.RewardSlots(0, 300f, 420f, 40f, 30f));
    }

    [Fact]
    public void PortraitDeckGridFitsThreeColumnsInPortraitWidth()
    {
        CardGridColumns grid = PortraitBetweenMath.GridColumns(availableWidth: 1000f, cardWidth: 300f, minGap: 24f, minColumns: 3, maxScale: 1f);
        Assert.Equal(3, grid.Columns);
        Assert.True(grid.CardScale <= 1f);
        Assert.Equal(1000f, grid.Columns * 300f * grid.CardScale + (grid.Columns - 1) * grid.Gap, 2);
    }

    [Fact]
    public void GridShrinksCardsWhenMinimumColumnsDoNotFit()
    {
        CardGridColumns grid = PortraitBetweenMath.GridColumns(availableWidth: 600f, cardWidth: 300f, minGap: 20f, minColumns: 3, maxScale: 1f);
        Assert.Equal(3, grid.Columns);
        Assert.Equal((600f - 40f) / 900f, grid.CardScale, 4);
    }

    [Fact]
    public void LandscapeWidthGivesMoreColumns()
    {
        CardGridColumns grid = PortraitBetweenMath.GridColumns(availableWidth: 1800f, cardWidth: 300f, minGap: 24f, minColumns: 3, maxScale: 1f);
        Assert.Equal(5, grid.Columns);
        Assert.Equal(1f, grid.CardScale);
    }

    [Fact]
    public void ShopPanelPivotPutsContentUnderTopBarWhenOpenAndOffScreenWhenClosed()
    {
        const float scale = 0.815f;
        float pivot = PortraitBetweenMath.PivotForVisualTop(openY: 80f, contentTop: 150f, visualTop: 280f, scale);
        float VisualY(float positionY, float localY) => positionY + pivot + (localY - pivot) * scale;
        Assert.Equal(280f, VisualY(80f, 150f), 3);
        Assert.True(VisualY(-1000f, 978f) < 0f);
    }

    [Fact]
    public void PivotIsZeroAtFullScale()
    {
        Assert.Equal(0f, PortraitBetweenMath.PivotForVisualTop(80f, 150f, 280f, 1f));
    }

    [Theory]
    [InlineData(1920f, 1040f, 1040f / 1920f)]
    [InlineData(800f, 1040f, 1f)]
    [InlineData(0f, 1040f, 1f)]
    public void FitScaleNeverEnlarges(float content, float available, float expected)
    {
        Assert.Equal(expected, PortraitBetweenMath.FitScale(content, available), 4);
    }
}
