using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace STS2Mobile.GameHost;

internal static partial class PortraitSubmenuLayout
{
    private const string UnlockNs = Ns + "Timeline.UnlockScreens.";
    private const string UnlockCardsTypeName = UnlockNs + "NUnlockCardsScreen";
    private const string UnlockRelicsTypeName = UnlockNs + "NUnlockRelicsScreen";
    private const string UnlockPotionsTypeName = UnlockNs + "NUnlockPotionsScreen";
    private const string UnlockMiscTypeName = UnlockNs + "NUnlockMiscScreen";
    private const string UnlockCharacterTypeName = UnlockNs + "NUnlockCharacterScreen";

    /// <summary>
    /// The unlock screens at 720, drawn at 1.5 (unfitted they were drawn at 1, laid out on the
    /// 1080 canvas from the desktop's anchors), on a virtual canvas about 1445 tall on the 11 Pro. The explanation's 28 unit
    /// text reads at about 14.6 points and the misc screen's 32 unit text at about 16.7.
    /// </summary>
    private const float UnlockDesignWidth = 720f;

    // Two unlock screens are left out. The timeline unlock screen is an empty control over the timeline: what
    // it shows is the timeline itself growing new eras (fitted at 1080 with the timeline), and it closes when that ends.
    // The epoch unlock screen has no scene and nothing in v0.113.0 creates it; v0.113.1 no longer has it.
    private static IEnumerable<(string TypeName, float DesignWidth)> UnlockScreenWidths()
    {
        yield return (UnlockCardsTypeName, UnlockDesignWidth);
        yield return (UnlockRelicsTypeName, UnlockDesignWidth);
        yield return (UnlockPotionsTypeName, UnlockDesignWidth);
        yield return (UnlockMiscTypeName, UnlockDesignWidth);
        yield return (UnlockCharacterTypeName, UnlockDesignWidth);
    }

    public const string TimelineTypeName = Ns + "Timeline.NTimelineScreen";

    // The timeline's reminder under the eras ("4 Epochs are waiting to be revealed..."), which the timeline unlock leaves
    // showing: a 1716 unit box on one line of 24 unit text (8.3 points at the timeline's 1080), centred on a 40 unit point
    // 84 units above the lower edge. Here the box is the width less the margins and the text up to 34 units (11.8 points);
    // the label still shrinks it to fit one line.
    private const int TimelineReminderFontSize = 34;
    private const float TimelineReminderHeight = 52f;

    /// <summary>
    /// The timeline (fitted at 1080, its own layout kept) with its reminder line kept inside the width. The reminder hangs
    /// from a 40 unit control at the lower middle, so the label's box is given in that control's units.
    /// </summary>
    private static void LayoutTimeline(Control screen, float w, Action<Control> capture)
    {
        if (screen.GetNodeOrNull<Control>("EpochReminderText") is not { } reminder ||
            reminder.GetNodeOrNull<Control>("ReminderLabel") is not { } label) return;
        Vector2 middle = reminder.Size / 2f;
        float width = w - 2f * UnlockSide;
        // The label sizes its text when its box changes, so the limit goes up first.
        SetKept(label, "MaxFontSize", TimelineReminderFontSize);
        Place(label, middle.X - width / 2f, middle.Y - TimelineReminderHeight / 2f, width, TimelineReminderHeight, capture);
    }

    private static bool IsUnlockScreen(string typeName) => typeName.StartsWith(UnlockNs, StringComparison.Ordinal) && DesignWidths.ContainsKey(typeName);

    // The side margin, and the banner (common_banner.tscn, 654 x 162) with the space under it, which leaves room for a
    // card's energy orb and glow standing over the card's top edge.
    private const float UnlockSide = 24f;
    private static readonly Vector2 UnlockBannerSize = new(654f, 162f);
    private const float UnlockBannerGap = 32f;
    private const float UnlockItemGap = 24f;

    // The explanation under the items: 28 unit text on one line in 672 units, room for two.
    private const float UnlockTextGap = 24f;
    private const float UnlockTextHeight = 90f;

    // The confirm button: 260 x 58 on a desktop (20 points at 1, 30 at 1.5), here 380 x 85, so at 1.5 it is 44 points
    // tall, its picture (drawn at its own aspect, 4.48) filling the width. Its label goes from 28 to 38 units (about 20
    // points). Its lower edge stands 72 units (108 on the canvas, under the band's own 40) above the band's lower edge,
    // clear of the home indicator.
    private static readonly Vector2 UnlockConfirmSize = new(380f, 85f);
    private const int UnlockConfirmFontSize = 38;
    private const float UnlockConfirmBottom = 72f;

    // The misc screen's text box: the scene's height (486), the width less the margins.
    private const float UnlockMiscHeight = 486f;

    // The character screen (unlock_character_screen.tscn): the line above the character (1794 x 173 on a desktop, 28 unit
    // text shrinking to 24 to fit), the character standing on a point (its feet), and the line under it (1280 x 193, its
    // text at the box's foot). The characters' bounds are 210 to 250 units wide and 244 to 335 tall at 1, their art about
    // a fifth larger; here they are drawn at 1.5 (the desktop's 1), so the tallest stands about 500 units in a 560 unit
    // space and the widest fits the 720. The rare glow stands 153 units above the feet and 5 left on a desktop.
    private const float UnlockCharacterScale = 1.5f;
    private const float UnlockCharacterSpace = 560f;
    private const float UnlockCharacterTopHeight = 180f;
    private const float UnlockCharacterBottomHeight = 120f;
    private static readonly Vector2 UnlockCharacterGlowFromFeet = new(-5f, -153f);

    /// <summary>
    /// The items an unlock screen shows in its row, as the game makes them in <c>Open</c>: each one tweened from the row's
    /// origin to <see cref="GamePlace"/> (350 units apart, in one row) while it fades in from <see cref="StartModulate"/>.
    /// <see cref="Size"/> is what an item covers at the row's scale of 1 (a card holder at its small scale 0.8 of 300 x 422;
    /// a relic holder of 68 at its scale of 3; a potion holder of 60 at 3), and <see cref="CentreFromOrigin"/> runs from
    /// the holder's position to its middle (a card holder's origin is the card's middle; a relic or potion holder grows
    /// from its pivot, the middle of its own rectangle).
    /// </summary>
    private sealed record UnlockItems(string RowPath, string TweenField, Vector2 Size, Vector2 CentreFromOrigin, int Columns,
        float MaxScale, Color StartModulate, Func<int, int, Vector2> GamePlace);

    // Cards: two to a row (three at 1.5 would need 1080 units of the 720), and drawn up to 1.25 times larger again while
    // the height allows (their description, 5.8 points unfitted, reads at about 10.9). Relics and potions: three to a row
    // (660 and 588 units), at their own size.
    private static readonly UnlockItems UnlockCards = new("UI/CardRow", "_cardTween", new Vector2(240f, 338f), Vector2.Zero, 2, 1.25f,
        Colors.Black, (i, n) => new Vector2(350f * i - 175f * (n - 1), 0f));
    private static readonly UnlockItems UnlockRelics = new("UI/RelicRow", "_relicTween", new Vector2(204f, 204f), new Vector2(34f, 34f), 3, 1f,
        new Color(0f, 0f, 0f, 0f), (i, n) => new Vector2(350f * i - 175f * (n - 1), 0f));
    // The potions start one place left of the screen's own position (on a desktop, the origin).
    private static readonly UnlockItems UnlockPotions = new("UI/PotionRow", "_potionTween", new Vector2(180f, 180f), new Vector2(30f, 30f), 3, 1f,
        new Color(0f, 0f, 0f, 0f), (i, _) => new Vector2(350f * (i - 1), 0f));

    /// <summary>
    /// An unlock screen (a child of the timeline, opened over it): the confirm button large at the bottom, and above it,
    /// centred in the height left, the banner, the unlocked items (cards two to a row, relics and potions three) and the
    /// explanation under them; the misc screen has only its text, wrapped to the width. The particles burst from the middle
    /// of that space instead of the desktop's (960, 540).
    /// </summary>
    private static void LayoutUnlock(Control screen, string typeName, float w, float h, Action<Control> capture)
    {
        ForgetClosedUnlockHolders();
        float confirmTop = h - UnlockConfirmBottom - UnlockConfirmSize.Y;
        float contentBottom = confirmTop - Gap;
        PlaceUnlockConfirm(screen, w, confirmTop, capture);
        if (typeName == UnlockCharacterTypeName)
        {
            LayoutUnlockCharacter(screen, w, contentBottom, capture);
            return;
        }
        SetKept(screen.GetNodeOrNull<Node2D>("GPUParticles2D"), "position", new Vector2(w / 2f, contentBottom / 2f));

        if (typeName == UnlockMiscTypeName)
        {
            float height = Math.Min(UnlockMiscHeight, contentBottom - 2f * UnlockSide);
            Place(screen.GetNodeOrNull<Control>("MarginContainer"), UnlockSide, Math.Max(UnlockSide, (contentBottom - height) / 2f),
                w - 2f * UnlockSide, height, capture);
            return;
        }
        UnlockItems? items = typeName switch
        {
            UnlockCardsTypeName => UnlockCards,
            UnlockRelicsTypeName => UnlockRelics,
            UnlockPotionsTypeName => UnlockPotions,
            _ => null,
        };
        if (items != null) LayoutUnlockItems(screen, items, w, contentBottom, capture);
    }

    /// <summary>
    /// The banner, the items and the explanation, one under another and centred together above the confirm button. The row
    /// is a point at the middle of the width and the top of the items (the desktop's is the screen's middle), scaled when the
    /// items are drawn larger or must shrink to fit a short screen; each item stands at its place in rows under it.
    /// The game tweens the items out to one row 350 apart (wider than the screen at this size) when the screen opens, and
    /// the banner and button slide in: each of those slides is replaced once, as it starts, by the same slide to the place
    /// here (<see cref="UnlockSliding"/>), so nothing jumps when it ends. The items' desktop places are kept for landscape.
    /// </summary>
    private static void LayoutUnlockItems(Control screen, UnlockItems items, float w, float contentBottom, Action<Control> capture)
    {
        Control? row = screen.GetNodeOrNull<Control>(items.RowPath);
        List<Control> holders = row == null ? new List<Control>()
            : row.GetChildren().OfType<Control>().Where(holder => !holder.IsQueuedForDeletion()).ToList();
        int count = holders.Count;
        int columns = Math.Clamp(count, 1, items.Columns);
        int rows = Math.Max(1, (count + items.Columns - 1) / items.Columns);
        float blockWidth = columns * items.Size.X + (columns - 1) * UnlockItemGap;
        float blockHeight = rows * items.Size.Y + (rows - 1) * UnlockItemGap;
        float fixedHeight = UnlockBannerSize.Y + UnlockBannerGap + UnlockTextGap + UnlockTextHeight;
        float scale = Math.Min(items.MaxScale, Math.Min((w - 2f * UnlockSide) / blockWidth, (contentBottom - 2f * UnlockSide - fixedHeight) / blockHeight));
        scale = Math.Max(0.5f, scale);

        float top = Math.Max(UnlockSide, (contentBottom - fixedHeight - blockHeight * scale) / 2f);
        PlaceUnlockBanner(screen, (w - UnlockBannerSize.X) / 2f, top, capture);
        float itemsTop = top + UnlockBannerSize.Y + UnlockBannerGap;
        float textTop = itemsTop + blockHeight * scale + UnlockTextGap;
        Place(screen.GetNodeOrNull<Control>("ExplanationText"), UnlockSide, textTop, w - 2f * UnlockSide, UnlockTextHeight, capture);

        if (row == null) return;
        Place(row, w / 2f, itemsTop, 0f, 0f, capture);
        var rowScale = new Vector2(scale, scale);
        if (row.Scale.DistanceSquaredTo(rowScale) > 0.0001f) row.Scale = rowScale;

        var places = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            int line = i / items.Columns;
            int inLine = Math.Min(items.Columns, count - line * items.Columns);
            float x = (i % items.Columns - (inLine - 1) / 2f) * (items.Size.X + UnlockItemGap);
            float y = line * (items.Size.Y + UnlockItemGap) + items.Size.Y / 2f;
            places[i] = new Vector2(x, y) - items.CentreFromOrigin;
        }

        bool sliding = UnlockSliding(screen, items.TweenField, () =>
        {
            Tween tween = screen.CreateTween().SetParallel(true);
            Vector2 from = new Vector2(0f, items.Size.Y / 2f) - items.CentreFromOrigin;
            for (int i = 0; i < count; i++)
            {
                KeepUnlockHolderPlace(holders[i], row, items.GamePlace(i, count));
                tween.TweenProperty(holders[i], "position", places[i], 0.5).From(from)
                    .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
                tween.TweenProperty(holders[i], "modulate", Colors.White, 1.0).From(items.StartModulate)
                    .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
            }
            return tween;
        });
        if (sliding) return;
        for (int i = 0; i < count; i++)
        {
            KeepUnlockHolderPlace(holders[i], row, null);
            SetKept(holders[i], "position", places[i]);
        }
    }

    /// <summary>
    /// The character screen: the line above, the character drawn at 1.5 on its feet, and the line under, one under another
    /// and centred together above the confirm button, the text wrapped inside the width. The rare glow and the particles
    /// stand at the character's middle. Nothing here slides: the game only fades these in.
    /// </summary>
    private static void LayoutUnlockCharacter(Control screen, float w, float contentBottom, Action<Control> capture)
    {
        float blockHeight = UnlockCharacterTopHeight + UnlockItemGap + UnlockCharacterSpace + UnlockItemGap + UnlockCharacterBottomHeight;
        float top = Math.Max(UnlockSide, (contentBottom - blockHeight) / 2f);
        Place(screen.GetNodeOrNull<Control>("TopLabel"), UnlockSide, top, w - 2f * UnlockSide, UnlockCharacterTopHeight, capture);
        float feet = top + UnlockCharacterTopHeight + UnlockItemGap + UnlockCharacterSpace;
        Place(screen.GetNodeOrNull<Control>("BottomLabel"), UnlockSide, feet + UnlockItemGap, w - 2f * UnlockSide, UnlockCharacterBottomHeight, capture);

        var scale = new Vector2(UnlockCharacterScale, UnlockCharacterScale);
        if (screen.GetNodeOrNull<Control>("SpineAnchor") is { } anchor)
        {
            Place(anchor, w / 2f, feet, 0f, 0f, capture);
            SetKept(anchor, "scale", scale);
        }
        Vector2 middle = new Vector2(w / 2f, feet) + UnlockCharacterGlowFromFeet * UnlockCharacterScale;
        if (screen.GetNodeOrNull<Node2D>("RareGlow") is { } glow)
        {
            SetKept(glow, "position", middle);
            SetKept(glow, "scale", scale);
        }
        SetKept(screen.GetNodeOrNull<Node2D>("GPUParticles2D"), "position", middle);
    }

    /// <summary>
    /// The banner at the top of the group, 654 x 162 as in its scene. It places itself from the window (on <c>_Ready</c> and
    /// each resize) and slides up 50 units as it fades in; that slide ends at the place here instead.
    /// </summary>
    private static void PlaceUnlockBanner(Control screen, float x, float y, Action<Control> capture)
    {
        if (screen.GetNodeOrNull<Control>("UI/Banner") is not { } banner) return;
        var place = new Vector2(x, y);
        bool sliding = UnlockSliding(banner, "_tween", () =>
        {
            Place(banner, x, y, UnlockBannerSize.X, UnlockBannerSize.Y, capture);
            banner.Position = place + new Vector2(0f, 50f);
            Tween tween = banner.CreateTween().SetParallel(true);
            tween.TweenProperty(banner, "modulate:a", 1f, 0.4).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
            tween.TweenProperty(banner, "position", place, 0.4).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
            return tween;
        });
        if (!sliding) Place(banner, x, y, UnlockBannerSize.X, UnlockBannerSize.Y, capture);
    }

    /// <summary>
    /// The confirm button, 380 x 85 and centred, its top at <paramref name="top"/>. It rises 180 units into place when it
    /// is made (its <c>_Ready</c>), and grows from its pivot when focused or pressed, so the pivot moves to its new middle.
    /// </summary>
    private static void PlaceUnlockConfirm(Control screen, float w, float top, Action<Control> capture)
    {
        if (screen.GetNodeOrNull<Control>("ConfirmButton") is not { } confirm) return;
        float x = (w - UnlockConfirmSize.X) / 2f;
        capture(confirm);
        Vector2 pivot = UnlockConfirmSize / 2f;
        if (confirm.PivotOffset.DistanceSquaredTo(pivot) > 0.25f) confirm.PivotOffset = pivot;
        SetKept(confirm.GetNodeOrNull("Label"), "theme_override_font_sizes/font_size", UnlockConfirmFontSize);
        bool sliding = UnlockSliding(confirm, "_tween", () =>
        {
            Place(confirm, x, top, UnlockConfirmSize.X, UnlockConfirmSize.Y, capture);
            confirm.Position = new Vector2(x, top + 180f);
            Tween tween = confirm.CreateTween();
            tween.TweenProperty(confirm, "position:y", top, 0.3).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back).SetDelay(0.1);
            return tween;
        });
        if (!sliding) Place(confirm, x, top, UnlockConfirmSize.X, UnlockConfirmSize.Y, capture);
    }

    private static readonly HashSet<(ulong Owner, string Field)> ReplacedUnlockTweens = new();

    /// <summary>
    /// True while the tween the game holds in <paramref name="field"/> of <paramref name="owner"/> runs, so the caller
    /// leaves what it moves alone. The first such tween of each owner is killed and <paramref name="start"/> makes the same
    /// animation to the portrait place, kept in that field; a later one (a fade out) only runs out.
    /// </summary>
    private static bool UnlockSliding(Node owner, string field, Func<Tween> start)
    {
        if (Get<Tween>(owner, field) is not Tween tween || !tween.IsValid() || !tween.IsRunning()) return false;
        if (ReplacedUnlockTweens.Count > 64) ReplacedUnlockTweens.RemoveWhere(key => !GodotObject.IsInstanceIdValid(key.Owner));
        if (!ReplacedUnlockTweens.Add((owner.GetInstanceId(), field))) return true;
        tween.Kill();
        Field(owner, field)?.SetValue(owner, start());
        return true;
    }

    // Item holders whose desktop place is kept, with the row they stand in. A card holder is pooled, not freed, when its
    // screen closes, so its kept place is dropped (by the next unlock screen's layout) once it has left the row; landscape
    // then cannot move it on a grid that took it from the pool since.
    private static readonly Dictionary<ulong, ulong> UnlockHolderRows = new();

    /// <summary>
    /// Notes a holder's kept place: <paramref name="desktop"/> (where the game's tween, replaced here, would have left it),
    /// or with null its position now, read by <see cref="SetKept"/> on its first call.
    /// </summary>
    private static void KeepUnlockHolderPlace(Control holder, Control row, Vector2? desktop)
    {
        ulong id = holder.GetInstanceId();
        UnlockHolderRows[id] = row.GetInstanceId();
        if (desktop is Vector2 place) OriginalProperties.TryAdd((id, "position"), place);
    }

    private static void ForgetClosedUnlockHolders()
    {
        if (UnlockHolderRows.Count == 0) return;
        foreach ((ulong holderId, ulong rowId) in UnlockHolderRows.ToList())
        {
            if (GodotObject.InstanceFromId(holderId) is Node holder && GodotObject.IsInstanceValid(holder) &&
                holder.GetParent() is { } parent && parent.GetInstanceId() == rowId) continue;
            OriginalProperties.Remove((holderId, "position"));
            UnlockHolderRows.Remove(holderId);
        }
    }
}
