namespace STS2Mobile.Platform.Tests;

public class PortraitCombatMathTests
{
    [Theory]
    [InlineData(0, false, 0, 0)]
    [InlineData(1, false, 0, 1)]
    [InlineData(3, false, 0, 3)]
    [InlineData(5, false, 0, 5)]
    [InlineData(6, true, 3, 3)]
    [InlineData(7, true, 4, 3)]
    [InlineData(8, true, 4, 4)]
    [InlineData(9, true, 5, 4)]
    [InlineData(10, true, 5, 5)]
    public void SplitHandRowsUsesSingleRowUpToFiveAndCeilFloorAboveFive(
        int count,
        bool expectedTwoRows,
        int expectedBack,
        int expectedFront)
    {
        HandRowSplit split = PortraitCombatMath.SplitHandRows(count);
        Assert.Equal(expectedTwoRows, split.TwoRows);
        Assert.Equal(expectedBack, split.BackRowCount);
        Assert.Equal(expectedFront, split.FrontRowCount);
        Assert.Equal(count, split.BackRowCount + split.FrontRowCount);
    }

    [Fact]
    public void DraggingSixthCardKeepsTwoRowModeUntilCardPlayFinishes()
    {
        // When a 6-card hand begins dragging 1 card, 5 holders remain in CardHolderContainer while InCardPlay is true.
        int whileDragging = PortraitCombatMath.EffectiveCardCount(activeHoldersInHand: 5, inUncommittedCardPlay: true);
        Assert.True(PortraitCombatMath.UsesTwoCardRows(whileDragging));

        // Once the card play commits, 5 holders remain with InCardPlay false, collapsing to 1 row.
        int afterPlayed = PortraitCombatMath.EffectiveCardCount(activeHoldersInHand: 5, inUncommittedCardPlay: false);
        Assert.False(PortraitCombatMath.UsesTwoCardRows(afterPlayed));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(10)]
    public void AllHandSlotsStayInsidePortraitCanvasAndClearCornerPiles(int count)
    {
        const float canvasHeight = 2341f;
        const float stageTopY = 436f;
        bool twoRows = PortraitCombatMath.UsesTwoCardRows(count);
        PortraitHudLayout hud = PortraitCombatMath.ComputeHudLayout(canvasHeight, stageTopY, twoRows);

        float drawPileTop = hud.DrawShowY + 40f - 40f * hud.PileScale;
        float energyBottom = hud.EnergyContainerY + 64f + 64f * hud.EnergyScale;

        for (int i = 0; i < count; i++)
        {
            PortraitCardSlot slot = PortraitCombatMath.ComputeHandCardSlot(count, i, focusedIndex: -1, playable: true, handDisabled: false);
            float halfH = PortraitCombatMath.CardDefaultHeight * slot.Scale * 0.5f;
            float top = canvasHeight + slot.LocalY - halfH;
            float bottom = canvasHeight + slot.LocalY + halfH;

            Assert.True(top > energyBottom, $"Card {i}/{count} top {top} overlapped HUD bottom {energyBottom}.");
            Assert.True(bottom < drawPileTop, $"Card {i}/{count} bottom {bottom} overlapped corner piles top {drawPileTop}.");
        }
    }

    // On a phone the leftmost of five cards lost its cost badge and left side past the screen edge.
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]
    public void EveryHandCardStaysInsideTheSideEdgesWithItsBadgeAndRotationWhateverIsFocused(int count)
    {
        for (int focused = -1; focused < count; focused++)
        {
            for (int i = 0; i < count; i++)
            {
                PortraitCardSlot slot = PortraitCombatMath.ComputeHandCardSlot(count, i, focused, playable: true, handDisabled: false);
                float extent = PortraitCombatMath.HandSlotHalfExtent(slot.Scale, slot.AngleDegrees);
                float left = 540f + slot.LocalX - extent;
                float right = 540f + slot.LocalX + extent;
                Assert.True(left >= PortraitCombatMath.HandEdgeMargin - 0.01f, $"Card {i}/{count} (focused {focused}) left edge {left}.");
                Assert.True(right <= 1080f - PortraitCombatMath.HandEdgeMargin + 0.01f, $"Card {i}/{count} (focused {focused}) right edge {right}.");
            }
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(10)]
    public void FocusingACardLeavesEveryOtherCardWhereItWas(int count)
    {
        // A touch focuses the card under the finger; the rest of the hand must not move (the hand bounced on a touch).
        for (int focused = 0; focused < count; focused++)
        {
            for (int i = 0; i < count; i++)
            {
                if (i == focused) continue;
                PortraitCardSlot rest = PortraitCombatMath.ComputeHandCardSlot(count, i, focusedIndex: -1, playable: true, handDisabled: false);
                PortraitCardSlot slot = PortraitCombatMath.ComputeHandCardSlot(count, i, focused, playable: true, handDisabled: false);
                Assert.Equal(rest, slot);
            }
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    public void HoldingACardLeavesItsSlotEmptySoTheOthersStayPut(int count)
    {
        // Every touch picks the card up; the rest of the hand keeps the places it had with the card in it.
        for (int held = 0; held < count; held++)
        {
            for (int i = 0; i < count - 1; i++)
            {
                int slot = PortraitCombatMath.SlotIndexBesideHeldCard(i, held);
                int original = i < held ? i : i + 1;
                Assert.Equal(original, slot);
                Assert.Equal(
                    PortraitCombatMath.ComputeHandCardSlot(count, original, focusedIndex: -1, playable: true, handDisabled: false),
                    PortraitCombatMath.ComputeHandCardSlot(count, slot, focusedIndex: -1, playable: true, handDisabled: false));
            }
        }
        Assert.Equal(3, PortraitCombatMath.SlotIndexBesideHeldCard(3, -1));
    }

    [Fact]
    public void FiveCardFanStillSpreadsAcrossMostOfTheWidth()
    {
        PortraitCardSlot first = PortraitCombatMath.ComputeHandCardSlot(5, 0, focusedIndex: -1, playable: true, handDisabled: false);
        PortraitCardSlot last = PortraitCombatMath.ComputeHandCardSlot(5, 4, focusedIndex: -1, playable: true, handDisabled: false);
        Assert.InRange(last.LocalX - first.LocalX, 600f, 700f);
        Assert.Equal(-first.LocalX, last.LocalX, 3);
    }

    [Fact]
    public void TenCardTwoRowFanExposesOverSeventyFivePercentOfBackRowCards()
    {
        PortraitCardSlot backCenter = PortraitCombatMath.ComputeHandCardSlot(10, 2, focusedIndex: -1, playable: true, handDisabled: false);
        PortraitCardSlot frontCenter = PortraitCombatMath.ComputeHandCardSlot(10, 7, focusedIndex: -1, playable: true, handDisabled: false);

        Assert.Equal(0, backCenter.RowIndex);
        Assert.Equal(1, frontCenter.RowIndex);

        float cardHeight = PortraitCombatMath.CardDefaultHeight * backCenter.Scale;
        float visibleBackHeight = frontCenter.LocalY - backCenter.LocalY;
        Assert.InRange(visibleBackHeight / cardHeight, 0.78f, 1.2f);
    }

    [Fact]
    public void StageDropsLowerWhenHandCollapsesFromTwoRowsToOneRow()
    {
        const float canvasHeight = 2341f;
        const float stageTopY = 436f;
        PortraitHudLayout hudTwo = PortraitCombatMath.ComputeHudLayout(canvasHeight, stageTopY, twoRows: true);
        PortraitHudLayout hudOne = PortraitCombatMath.ComputeHudLayout(canvasHeight, stageTopY, twoRows: false);

        Assert.True(hudOne.HudTopY > hudTwo.HudTopY + 180f);

        PortraitCreatureSlot[] enemiesTwo = PortraitCombatMath.ComputeEnemyStageSlots(2, stageTopY, hudTwo.HudTopY, twoRows: true);
        PortraitCreatureSlot[] enemiesOne = PortraitCombatMath.ComputeEnemyStageSlots(2, stageTopY, hudOne.HudTopY, twoRows: false);
        Assert.True(enemiesOne[0].CanvasFeetY > enemiesTwo[0].CanvasFeetY + 120f);
    }

    [Fact]
    public void FiveEnemyAndPetStageUsesTwoBackPlusThreeFrontStaggerWithoutCrossingColumns()
    {
        const float stageTopY = 436f;
        const float hudTopY = 1209f;

        PortraitCreatureSlot[] allies = PortraitCombatMath.ComputeAllyStageSlots(
            playerCount: 1, petCount: 1, hasOrbs: true, stageTopY, hudTopY, twoRows: true);
        Assert.Equal(2, allies.Length);
        Assert.All(allies, slot => Assert.InRange(slot.CanvasX, 120f, 395f));
        Assert.True(allies[1].CanvasFeetY > allies[0].CanvasFeetY + 150f);

        PortraitCreatureSlot[] enemies = PortraitCombatMath.ComputeEnemyStageSlots(5, stageTopY, hudTopY, twoRows: true);
        Assert.Equal(5, enemies.Length);
        Assert.Equal(2, enemies.Count(e => e.RowDepth == 0));
        Assert.Equal(3, enemies.Count(e => e.RowDepth == 1));
        Assert.All(enemies, slot => Assert.InRange(slot.CanvasX, 480f, 1010f));

        // Back row sits higher on screen (smaller Y) and horizontally between the 3 front-row slots.
        Assert.True(enemies[0].CanvasFeetY < enemies[2].CanvasFeetY - 200f);
        Assert.InRange(enemies[0].CanvasX, enemies[2].CanvasX + 60f, enemies[3].CanvasX - 60f);
        Assert.InRange(enemies[1].CanvasX, enemies[3].CanvasX + 60f, enemies[4].CanvasX - 60f);
    }

    [Fact]
    public void EnemiesKeepAFifthOfTheWidthFromTheLonePlayer()
    {
        // The Waterfall Giant fight with the earlier layout: the player's hit box ended at 353, the giant's started at 496.
        Assert.Equal(216f, PortraitCombatMath.PlayerEnemyGap);
        Assert.Equal(527f, PortraitCombatMath.EnemyStageLeft(playersHitRight: 311f, alliesHitRight: 311f, minimum: 360f));
        // A pet in front of the player keeps half a gap from the enemies.
        Assert.Equal(560f, PortraitCombatMath.EnemyStageLeft(playersHitRight: 300f, alliesHitRight: 452f, minimum: 360f));
        Assert.Equal(360f, PortraitCombatMath.EnemyStageLeft(playersHitRight: 0f, alliesHitRight: 0f, minimum: 360f));

        PortraitCreatureSlot[] lone = PortraitCombatMath.ComputeAllyStageSlots(1, 0, hasOrbs: false, 436f, 1400f, twoRows: false);
        Assert.Equal(PortraitCombatMath.LonePlayerX, lone[0].CanvasX);
        Assert.Equal(0.80f, lone[0].BaseScale);
    }

    [Fact]
    public void ASqueezedEnemyGroupStartsAtTheGapAndKeepsItsArrangement()
    {
        Assert.Equal(1f, PortraitCombatMath.EnemyGroupSqueeze(leftmostHitLeft: 540f, enemyLeft: 527f));
        float k = PortraitCombatMath.EnemyGroupSqueeze(leftmostHitLeft: 415f, enemyLeft: 527f);
        Assert.InRange(k, 0.82f, 0.84f);

        // The leftmost hit box started 140 units left of its slot's centre; drawn k times as large, it starts 140 * k left
        // of the squeezed centre, which is where the gap ends.
        var slot = new PortraitCreatureSlot(555f, 1200f, 0.76f, 1);
        PortraitCreatureSlot squeezed = PortraitCombatMath.SqueezeEnemySlot(slot, k);
        Assert.Equal(527f, squeezed.CanvasX - 140f * k, 0.5f);
        Assert.Equal(slot.CanvasFeetY, squeezed.CanvasFeetY);
        Assert.Equal(0.76f * k, squeezed.BaseScale, 0.0001f);
        Assert.Equal(slot, PortraitCombatMath.SqueezeEnemySlot(slot, 1f));
    }

    [Fact]
    public void UnplayableCardDimmingIsStrictlyNeutralGreyWithZeroColourCast()
    {
        var (r, g, b, a) = PortraitCombatMath.UnplayableBodyModulateRgba;
        Assert.Equal(0.54f, r);
        Assert.Equal(r, g);
        Assert.Equal(g, b);
        Assert.Equal(1f, a);
    }
}
