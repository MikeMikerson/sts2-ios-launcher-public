using System;

namespace STS2Mobile.Platform;

public readonly record struct HandRowSplit(bool TwoRows, int BackRowCount, int FrontRowCount);

public readonly record struct PortraitCardSlot(
    float LocalX,
    float LocalY,
    float Scale,
    float AngleDegrees,
    int RowIndex);

public readonly record struct PortraitHudLayout(
    float HudTopY,
    float EnergyContainerX,
    float EnergyContainerY,
    float EnergyScale,
    float StarX,
    float StarY,
    float StarScale,
    float ExhaustShowX,
    float ExhaustShowY,
    float ExhaustHideX,
    float ExhaustScale,
    float EndTurnShowX,
    float EndTurnShowY,
    float EndTurnHideX,
    float EndTurnScale,
    float DrawShowX,
    float DrawShowY,
    float DrawHideX,
    float DrawHideY,
    float DiscardShowX,
    float DiscardShowY,
    float DiscardHideX,
    float DiscardHideY,
    float PileScale);

public readonly record struct PortraitCreatureSlot(
    float CanvasX,
    float CanvasFeetY,
    float BaseScale,
    int RowDepth);

/// <summary>
/// Pure portrait-mode combat geometry and playability contrast constants on the 1080-wide portrait canvas.
/// Kept free of engine types so layout invariants are directly unit-tested on the host.
/// </summary>
public static class PortraitCombatMath
{
    public const float CardDefaultWidth = 300f;
    public const float CardDefaultHeight = 422f;
    public const float SingleRowCardScale = 1.0f;
    public const float TwoRowCardScale = 0.824f;
    public const float UnplayableBodyBrightness = 0.54f;

    /// <summary>How far the energy cost badge reaches past a card's left edge, in card units (measured).</summary>
    public const float CostBadgeOverhang = 28f;

    /// <summary>The gap kept between every hand card and the canvas's side edges.</summary>
    public const float HandEdgeMargin = 12f;

    private const float CanvasHalfWidth = SafeAreaCanvas.PortraitWidth * 0.5f;

    // #00F4FC playable highlight contour and #FF5252 unaffordable energy cost text.
    public static readonly (float R, float G, float B, float A) PlayableHighlightRgba = (0f, 244f / 255f, 252f / 255f, 0.98f);
    public static readonly (float R, float G, float B, float A) UnplayableBodyModulateRgba =
        (UnplayableBodyBrightness, UnplayableBodyBrightness, UnplayableBodyBrightness, 1f);
    public static readonly (float R, float G, float B, float A) UnaffordableCostRgba = (1f, 82f / 255f, 82f / 255f, 1f);

    public static int EffectiveCardCount(int activeHoldersInHand, bool inUncommittedCardPlay) =>
        Math.Max(0, activeHoldersInHand) + (inUncommittedCardPlay ? 1 : 0);

    public static bool UsesTwoCardRows(int effectiveCardCount) => effectiveCardCount > 5;

    /// <summary>
    /// The slot of the card at <paramref name="indexInHand"/> while the card that was at <paramref name="heldIndex"/> is held
    /// (-1: none): the held card's slot stays empty, so the cards after it keep their places.
    /// </summary>
    public static int SlotIndexBesideHeldCard(int indexInHand, int heldIndex) =>
        heldIndex >= 0 && indexInHand >= heldIndex ? indexInHand + 1 : indexInHand;

    public static HandRowSplit SplitHandRows(int cardCount, bool? forceTwoRows = null)
    {
        int clamped = Math.Max(0, cardCount);
        bool twoRows = forceTwoRows ?? (clamped > 5);
        if (!twoRows || clamped <= 1)
            return new HandRowSplit(false, 0, clamped);
        int back = (clamped + 1) / 2;
        int front = clamped - back;
        return new HandRowSplit(true, back, front);
    }

    /// <summary>
    /// Half the width a hand card takes on the canvas, rotation and cost badge included: the card is centred on its holder,
    /// so a card at <c>540 + x</c> fits when <c>|x| + HandSlotHalfExtent</c> stays inside the canvas.
    /// </summary>
    public static float HandSlotHalfExtent(float scale, float angleDegrees)
    {
        float radians = MathF.Abs(angleDegrees) * MathF.PI / 180f;
        float rotated = CardDefaultWidth * 0.5f * MathF.Cos(radians) + CardDefaultHeight * 0.5f * MathF.Sin(radians);
        return scale * (rotated + CostBadgeOverhang);
    }

    /// <summary>The farthest a card's centre may sit from the canvas centre and still keep the edge margin on both sides.</summary>
    public static float MaxHandCenterOffset(float scale, float angleDegrees) =>
        Math.Max(0f, CanvasHalfWidth - HandEdgeMargin - HandSlotHalfExtent(scale, angleDegrees));

    public static PortraitCardSlot ComputeHandCardSlot(
        int count,
        int index,
        int focusedIndex,
        bool playable,
        bool handDisabled,
        bool? forceTwoRows = null)
    {
        if (count <= 0)
            return new PortraitCardSlot(0f, -506f, SingleRowCardScale, 0f, 0);

        int safeIndex = Math.Clamp(index, 0, count - 1);
        HandRowSplit split = SplitHandRows(count, forceTwoRows);

        if (!split.TwoRows)
        {
            float t = count <= 1 ? 0f : (safeIndex - (count - 1) * 0.5f) / ((count - 1) * 0.5f);
            float maxHalfSpan = Math.Min(Math.Min(368f, (count - 1) * 92f), MaxHandCenterOffset(SingleRowCardScale, 7.5f));
            float x = t * maxHalfSpan;
            float y = (playable ? -540f : -506f) + t * t * 48f;
            float scale = SingleRowCardScale;
            float angle = t * 7.5f;

            if (focusedIndex >= 0 && focusedIndex < count)
            {
                // Only the focused card moves: a touch focuses the card it lands on, and pushing the others aside made the whole
                // hand bounce under a finger on a phone.
                if (focusedIndex == safeIndex)
                {
                    angle = 0f;
                    scale = 1.08f;
                    y = -566f - (handDisabled ? 100f : 0f);
                }
            }

            float limit = MaxHandCenterOffset(scale, angle);
            return new PortraitCardSlot(Math.Clamp(x, -limit, limit), y, scale, angle, 0);
        }

        int rowIndex = safeIndex < split.BackRowCount ? 0 : 1;
        int col = rowIndex == 0 ? safeIndex : safeIndex - split.BackRowCount;
        int rowCount = rowIndex == 0 ? split.BackRowCount : split.FrontRowCount;

        float rowT = rowCount <= 1 ? 0f : (col - (rowCount - 1) * 0.5f) / ((rowCount - 1) * 0.5f);
        float rowHalfSpan = Math.Min(Math.Min(396f, (rowCount - 1) * 99f), MaxHandCenterOffset(TwoRowCardScale, 4.2f));
        float rowX = rowT * rowHalfSpan;
        float baseY = rowIndex == 0 ? -787f : -408f;
        float rowY = baseY + rowT * rowT * 27.5f;
        float rowScale = TwoRowCardScale;
        float rowAngle = rowT * 4.2f;

        if (focusedIndex == safeIndex)
        {
            rowAngle = 0f;
            rowScale = 1.04f;
            rowY = baseY - 46f - (handDisabled ? 100f : 0f);
        }

        float rowLimit = MaxHandCenterOffset(rowScale, rowAngle);
        return new PortraitCardSlot(Math.Clamp(rowX, -rowLimit, rowLimit), rowY, rowScale, rowAngle, rowIndex);
    }

    public static PortraitHudLayout ComputeHudLayout(
        float canvasHeight,
        float stageTopY,
        bool twoRows,
        bool hasStarCounter = false)
    {
        float ch = Math.Max(canvasHeight, SafeAreaCanvas.MinPortraitHeight);
        float minHudTop = stageTopY + 560f;
        float hudTopY = Math.Max(minHudTop, ch - (twoRows ? 1132f : 929f));

        float exhaustLeft = hasStarCounter ? 292f : 198f;
        float pileY = ch - 157f;

        return new PortraitHudLayout(
            HudTopY: hudTopY,
            EnergyContainerX: 36f,
            EnergyContainerY: hudTopY + 8f,
            EnergyScale: 1.12f,
            StarX: 152f,
            StarY: hudTopY + 12f,
            StarScale: 0.88f,
            ExhaustShowX: exhaustLeft,
            ExhaustShowY: hudTopY + 28f,
            ExhaustHideX: -180f,
            ExhaustScale: 1.32f,
            EndTurnShowX: 805.6f,
            EndTurnShowY: hudTopY + 27f,
            EndTurnHideX: 1130f,
            EndTurnScale: 1.24f,
            DrawShowX: 41f,
            DrawShowY: pileY,
            DrawHideX: -160f,
            DrawHideY: ch + 40f,
            DiscardShowX: 959f,
            DiscardShowY: pileY,
            DiscardHideX: 1160f,
            DiscardHideY: ch + 40f,
            PileScale: 1.32f);
    }

    public static PortraitCreatureSlot[] ComputeAllyStageSlots(
        int playerCount,
        int petCount,
        bool hasOrbs,
        float stageTopY,
        float hudTopY,
        bool twoRows)
    {
        int pCount = Math.Max(0, playerCount);
        int pets = Math.Max(0, petCount);
        var slots = new PortraitCreatureSlot[pCount + pets];
        if (slots.Length == 0)
            return slots;

        float frontFeetY = hudTopY - (twoRows ? 165f : 220f);
        float upperFeetY = Math.Max(stageTopY + 320f, frontFeetY - 210f);

        if (pCount == 1 && pets == 0)
        {
            // A little smaller and nearer the left edge, so the enemies can stand a fifth of the width
            // away (EnemyStageLeft) and still be large. The Fool's bow ends about 115 units in, left of the player.
            float feetY = frontFeetY - (hasOrbs ? 8f : 30f);
            slots[0] = new PortraitCreatureSlot(LonePlayerX, feetY, twoRows ? 0.75f : 0.80f, 1);
            return slots;
        }

        if (pCount == 1 && pets >= 1)
        {
            // Stack hero upper-left and pets (such as Osty) below-right inside the left ally column (X <= 395).
            slots[0] = new PortraitCreatureSlot(162f, upperFeetY, 0.78f, 0);
            for (int i = 0; i < pets; i++)
            {
                float petX = i == 0 ? 334f : Math.Max(130f, 334f - i * 165f);
                float petY = frontFeetY + 6f + (i > 1 ? -110f : 0f);
                slots[1 + i] = new PortraitCreatureSlot(petX, petY, i == 0 ? 0.76f : 0.70f, 1);
            }
            return slots;
        }

        // Co-op multi-player + optional pets in 2-column left stagger.
        int total = slots.Length;
        int backCount = total / 2;
        int frontCount = total - backCount;
        for (int i = 0; i < total; i++)
        {
            bool back = i < backCount;
            int col = back ? i : i - backCount;
            int rowLen = back ? backCount : frontCount;
            float t = rowLen <= 1 ? 0.5f : (float)col / (rowLen - 1);
            float minX = back ? 145f : 165f;
            float maxX = back ? 335f : 355f;
            float x = rowLen <= 1 ? (minX + maxX) * 0.5f : Lerp(minX, maxX, t);
            float y = back ? upperFeetY : frontFeetY;
            slots[i] = new PortraitCreatureSlot(x, y, 0.72f, back ? 0 : 1);
        }

        return slots;
    }

    public static PortraitCreatureSlot[] ComputeEnemyStageSlots(
        int enemyCount,
        float stageTopY,
        float hudTopY,
        bool twoRows)
    {
        int count = Math.Max(0, enemyCount);
        var slots = new PortraitCreatureSlot[count];
        if (count == 0)
            return slots;

        float frontFeetY = hudTopY - (twoRows ? 165f : 220f);
        float backFeetY = Math.Max(stageTopY + 290f, frontFeetY - 250f);

        if (count == 1)
        {
            slots[0] = new PortraitCreatureSlot(755f, frontFeetY - 18f, twoRows ? 0.88f : 0.94f, 1);
            return slots;
        }

        if (count == 2)
        {
            slots[0] = new PortraitCreatureSlot(625f, frontFeetY - 8f, twoRows ? 0.82f : 0.88f, 1);
            slots[1] = new PortraitCreatureSlot(908f, frontFeetY - 118f, twoRows ? 0.78f : 0.84f, 0);
            return slots;
        }

        if (count == 3)
        {
            slots[0] = new PortraitCreatureSlot(755f, backFeetY + 25f, 0.73f, 0);
            slots[1] = new PortraitCreatureSlot(555f, frontFeetY, 0.76f, 1);
            slots[2] = new PortraitCreatureSlot(945f, frontFeetY - 16f, 0.76f, 1);
            return slots;
        }

        // 4+ enemies: 2-depth stagger (2 back + 2 front at 4 enemies; 2 back + 3 front at 5 enemies).
        int backCount = count / 2;
        int frontCount = count - backCount;

        for (int i = 0; i < backCount; i++)
        {
            float x;
            if (count == 4)
                x = i == 0 ? 655f : 940f;
            else if (count == 5)
                x = i == 0 ? 635f : 855f;
            else
            {
                float t = backCount <= 1 ? 0.5f : (float)i / (backCount - 1);
                x = Lerp(560f, 950f, t);
            }
            slots[i] = new PortraitCreatureSlot(x, backFeetY, count >= 5 ? 0.68f : 0.72f, 0);
        }

        for (int j = 0; j < frontCount; j++)
        {
            float x;
            if (count == 4)
                x = j == 0 ? 540f : 825f;
            else if (count == 5)
                x = 525f + j * 220f;
            else
            {
                float t = frontCount <= 1 ? 0.5f : (float)j / (frontCount - 1);
                x = Lerp(515f, 975f, t);
            }
            slots[backCount + j] = new PortraitCreatureSlot(x, frontFeetY, count >= 5 ? 0.73f : 0.76f, 1);
        }

        return slots;
    }

    /// <summary>Where a lone player stands (its feet), in canvas units from the left edge.</summary>
    public const float LonePlayerX = 180f;

    /// <summary>The right end of the enemies' stage: their hit boxes end here.</summary>
    public const float EnemyStageRight = 1072f;

    /// <summary>
    /// In portrait the enemy stood much closer to the player than on a desktop. This is the gap kept between
    /// a lone player's hit box and the nearest enemy's, a fifth of the width. With pets or other players on the left,
    /// whose front stands nearer the enemies, half of it from the nearest of them.
    /// </summary>
    public const float PlayerEnemyGap = SafeAreaCanvas.PortraitWidth * 0.2f;

    /// <summary>
    /// Where the enemies' hit boxes may start: a full gap right of the player's hit box, and half a gap right of every
    /// other ally's (pets and other players), never left of <paramref name="minimum"/>. Whole units, so the layout holds
    /// still from frame to frame.
    /// </summary>
    public static float EnemyStageLeft(float playersHitRight, float alliesHitRight, float minimum) =>
        MathF.Round(Math.Max(minimum, Math.Max(playersHitRight + PlayerEnemyGap, alliesHitRight + PlayerEnemyGap * 0.5f)));

    /// <summary>
    /// How much a group of enemies is drawn smaller and nearer the right edge (<see cref="SqueezeEnemySlot"/>) so the
    /// leftmost hit box starts at <paramref name="enemyLeft"/>: 1 when it already does.
    /// </summary>
    public static float EnemyGroupSqueeze(float leftmostHitLeft, float enemyLeft)
    {
        if (leftmostHitLeft >= enemyLeft || EnemyStageRight - leftmostHitLeft < 1f)
            return 1f;
        return Math.Clamp((EnemyStageRight - enemyLeft) / (EnemyStageRight - leftmostHitLeft), 0.5f, 1f);
    }

    /// <summary>
    /// An enemy's slot drawn <paramref name="k"/> times as large and <paramref name="k"/> times as far from the stage's
    /// right end: with its size limits scaled by the same factor, every hit box edge moves to that end by
    /// <paramref name="k"/>, so the group keeps its arrangement.
    /// </summary>
    public static PortraitCreatureSlot SqueezeEnemySlot(PortraitCreatureSlot slot, float k) =>
        k >= 1f ? slot : slot with { CanvasX = EnemyStageRight - (EnemyStageRight - slot.CanvasX) * k, BaseScale = slot.BaseScale * k };

    public static float FitCreatureScale(
        float baseScale,
        float boundsWidth,
        float boundsHeight,
        float maxAllowedWidth,
        float maxAllowedHeight)
    {
        float scale = baseScale;
        if (boundsWidth > 1f && maxAllowedWidth > 1f)
            scale = Math.Min(scale, maxAllowedWidth / boundsWidth);
        if (boundsHeight > 1f && maxAllowedHeight > 1f)
            scale = Math.Min(scale, maxAllowedHeight / boundsHeight);
        return Math.Clamp(scale, 0.42f, 1.15f);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
