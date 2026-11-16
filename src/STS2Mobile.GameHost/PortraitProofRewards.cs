using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.GameHost;

/// <summary>
/// <c>touch-rewards</c>, with real touch events: a long press on Ascender's Bane and
/// whether every tip is inside the screen; how far the other hand cards move while one card is pressed and after it is let go; a
/// fight ended by Strikes dragged onto the enemies, with a potion, a relic and a second card reward added, and the screen that
/// follows; a card reward picked by a tap and then Choose (a tap first, then a tap on another card, then Choose), and a second
/// card reward skipped. Portrait only: landscape is the game's own (<c>stock-landscape</c>).
/// </summary>
internal static partial class PortraitProof
{
    private static async Task RunTouchRewardsAsync(SceneTree tree, NGame game)
    {
        ClearNextStepFile();
        await EnsurePortraitAsync(tree, game);
        object player = await StartProofRunAsync(tree, game, characterIndex: 0, fivePotions: false, thirtyRelics: false, pageTwo: false, addThreePotions: false, closeRelicHover: true);
        await WaitFramesAsync(tree, 90);
        Call(StaticProp(GameType(ScreensNs + "Map.NMapScreen"), "Instance")!, "Close", false);
        DismissFtues(tree, Sts2);
        await WaitFramesAsync(tree, 20);
        await EnterEncounterAsync(Sts2, tree, player, "NIBBITS_NORMAL", 42UL);
        var results = new List<string>();
        Vector2 backdrop = new(540f, 420f);

        // A long press on Ascender's Bane (Eternal, Unplayable, Ethereal): the card large, and every tip inside the screen.
        await TrimHandToAsync(Sts2, player, ["STRIKE_IRONCLAD", "ASCENDERS_BANE", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "BASH"]);
        await SetPlayerEnergyAsync(Sts2, player, 9m);
        await WaitFramesAsync(tree, 60);
        List<Control> hand = HandHolders(tree);
        Control bane = hand.FirstOrDefault(h => CardId(h) == "ASCENDERS_BANE") ?? hand[Math.Min(1, hand.Count - 1)];
        Vector2 pressAt = PointOnCard(bane, 0.15f, 0.35f);
        SendTouch(tree, pressAt, pressed: true);
        await WaitMsecAsync(tree, 800);
        SendTouch(tree, pressAt, pressed: false);
        await WaitFramesAsync(tree, 40);
        await TouchStepAsync(tree, "touch-long-tips", $"card={CardId(bane)} inspect={(InspectOpen(game) ? "open" : "closed")} {TipsInsideCanvas(tree, game)} {DescribeTipSets(game)}", results);
        await TapAsync(tree, backdrop);
        await WaitFramesAsync(tree, 40);

        // The other cards while one is pressed and after it is let go (a tap: it opens the card large, which is closed after).
        await TrimHandToAsync(Sts2, player, ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "BASH", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD"]);
        await WaitFramesAsync(tree, 60);
        string still = await MeasureHandStillnessAsync(tree, game, touchedIndex: 2);
        await TouchStepAsync(tree, "touch-hand-still", still, results);
        if (InspectOpen(game)) { await TapAsync(tree, backdrop); await WaitFramesAsync(tree, 40); }
        await TrimHandToAsync(Sts2, player, ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "BASH", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD"]);
        await WaitFramesAsync(tree, 60);
        string stillTwoRows = await MeasureHandStillnessAsync(tree, game, touchedIndex: 5);
        await TouchStepAsync(tree, "touch-hand-still-two-rows", stillTwoRows, results);
        if (InspectOpen(game)) { await TapAsync(tree, backdrop); await WaitFramesAsync(tree, 40); }

        // A fight won by touch: a potion, a relic and a second card reward on top of the gold and card the fight gives.
        string added = "none";
        object? room = Prop(Prop(player, "RunState")!, "CurrentRoom");
        if (room != null && room.GetType().Name == "CombatRoom")
        {
            const string rewardNs = "MegaCrit.Sts2.Core.Rewards.";
            object options = CallStatic(GameType("MegaCrit.Sts2.Core.Runs.CardCreationOptions"), "ForRoom", player, Prop(room, "RoomType"))!;
            Call(room, "AddExtraReward", player, NewWithDefaults(GameType(rewardNs + "PotionReward"), player));
            Call(room, "AddExtraReward", player, NewWithDefaults(GameType(rewardNs + "RelicReward"), player));
            Call(room, "AddExtraReward", player, NewWithDefaults(GameType(rewardNs + "CardReward"), options, 3, player));
            added = "potion relic card";
        }
        await RunConsoleCmdAsync(Sts2, "DamageConsoleCmd", player, ["44"]);
        await WaitFramesAsync(tree, 60);
        for (int attempt = 0; attempt < 4 && GetLivingEnemyNodes(tree, Sts2).Count > 0; attempt++)
        {
            await TrimHandToAsync(Sts2, player, ["DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD"]);
            await SetPlayerEnergyAsync(Sts2, player, 9m);
            await WaitFramesAsync(tree, 45);
            results.Add("kill " + await DragStrikeOntoEnemyAsync(tree, 0, cardIndex: 1));
        }
        Control? rewards = null;
        for (int frame = 0; frame < 900 && rewards == null && FindScreen(tree, "NCardRewardSelectionScreen") == null; frame++)
        {
            await WaitFramesAsync(tree, 1);
            rewards = FindScreen(tree, "NRewardsScreen");
        }
        // On a phone, two quick taps on one spot as the list opened took the gold and then the card reward that slid up under
        // the finger. The same two taps here, on the first row while the list fades in, must take nothing.
        string early = "no list";
        if (rewards != null && RewardButtons(rewards).FirstOrDefault() is { } firstRow)
        {
            int rowsBefore = RewardButtons(rewards).Count;
            await WaitFramesAsync(tree, 4);
            Vector2 spot = CentreOf(firstRow);
            await TapAsync(tree, spot);
            await WaitFramesAsync(tree, 8);
            await TapAsync(tree, spot);
            await WaitFramesAsync(tree, 4);
            early = $"early-taps rows={rowsBefore}->{RewardButtons(rewards).Count} cardscreen={(FindScreen(tree, "NCardRewardSelectionScreen") != null)}";
        }
        await WaitFramesAsync(tree, 90);
        rewards ??= FindScreen(tree, "NRewardsScreen");
        await TouchStepAsync(tree, "touch-rewards", $"added={added} enemies={GetLivingEnemyNodes(tree, Sts2).Count} {early} open={(rewards != null && PhoneRewardTapGuard.IsOpen(rewards))} {DescribeRewardScreens(tree)}", results);
        // The first relic reward shows the game's relic tutorial over the list; close it as a player would.
        DismissFtues(tree, Sts2);
        await WaitFramesAsync(tree, 40);
        await TouchStepAsync(tree, "touch-rewards-list", DescribeRewardScreens(tree), results);

        // The first card reward: a tap selects, a tap on another card moves the selection, Choose takes it.
        int deckBefore = DeckCards(player).Count;
        if (rewards != null && RewardButtons(rewards).FirstOrDefault(b => RewardKind(b) == "CardReward") is { } cardButton)
        {
            await TapAsync(tree, CentreOf(cardButton));
            await WaitFramesAsync(tree, 70);
        }
        Control? cardScreen = FindScreen(tree, "NCardRewardSelectionScreen");
        await TouchStepAsync(tree, "touch-card-reward-open", DescribeCardScreen(cardScreen, player), results);
        if (cardScreen != null)
        {
            List<Control> cards = RewardCards(cardScreen);
            if (cards.Count > 0)
            {
                await TapAsync(tree, CentreOf(cards[0]));
                await WaitFramesAsync(tree, 30);
                await TouchStepAsync(tree, "touch-card-select-first", DescribeCardScreen(FindScreen(tree, "NCardRewardSelectionScreen"), player), results);
            }
            if (cards.Count > 2 && GodotObject.IsInstanceValid(cards[2]))
            {
                await TapAsync(tree, CentreOf(cards[2]));
                await WaitFramesAsync(tree, 30);
                await TouchStepAsync(tree, "touch-card-select-third", DescribeCardScreen(FindScreen(tree, "NCardRewardSelectionScreen"), player), results);
            }
            if (ChooseButton(FindScreen(tree, "NCardRewardSelectionScreen")) is { } choose)
            {
                await TapAsync(tree, CentreOf(choose));
                await WaitFramesAsync(tree, 70);
            }
            await TouchStepAsync(tree, "touch-card-chosen", $"deck={deckBefore}->{DeckCards(player).Count} {DescribeRewardScreens(tree)}", results);
        }

        // The second card reward, skipped.
        rewards = FindScreen(tree, "NRewardsScreen");
        if (rewards != null && RewardButtons(rewards).FirstOrDefault(b => RewardKind(b) == "CardReward") is { } second)
        {
            await TapAsync(tree, CentreOf(second));
            await WaitFramesAsync(tree, 70);
            Control? secondScreen = FindScreen(tree, "NCardRewardSelectionScreen");
            await TouchStepAsync(tree, "touch-card-reward-second", DescribeCardScreen(secondScreen, player), results);
            Control? skip = secondScreen == null ? null : Descendants(secondScreen).OfType<Control>()
                .FirstOrDefault(c => c.GetType().Name == "NCardRewardAlternativeButton" && c.Name != PhoneCardRewardChoice.ChooseButtonName);
            int deckBeforeSkip = DeckCards(player).Count;
            if (skip != null)
            {
                await TapAsync(tree, CentreOf(skip));
                await WaitFramesAsync(tree, 70);
            }
            await TouchStepAsync(tree, "touch-card-skipped", $"skip={(skip != null)} deck={deckBeforeSkip}->{DeckCards(player).Count} {DescribeRewardScreens(tree)}", results);
        }

        GD.Print("Touch rewards results: " + string.Join("; ", results));
    }

    /// <summary>The largest distance any other hand card moves while one card is held under a finger, and after it is let go.</summary>
    private static async Task<string> MeasureHandStillnessAsync(SceneTree tree, NGame game, int touchedIndex)
    {
        List<Control> hand = HandHolders(tree);
        if (hand.Count == 0) return "no hand";
        Control touched = hand[Math.Clamp(touchedIndex, 0, hand.Count - 1)];
        Dictionary<Control, Vector2> home = hand.Where(h => h != touched).ToDictionary(h => h, h => h.GetGlobalTransformWithCanvas().Origin);
        Vector2 at = PointOnCard(touched, 0.5f, 0.25f);
        float held = 0f, after = 0f;
        SendTouch(tree, at, pressed: true);
        // Held by the clock, longer than a tap and shorter than a long press, so neither opens the card large. A count of
        // frames sat on the long press boundary and crossed it when the simulator ran slow.
        ulong heldUntil = Time.GetTicksMsec() + (ulong)((STS2Mobile.Platform.TouchCardMath.TapSeconds + STS2Mobile.Platform.TouchCardMath.LongPressSeconds) * 500.0);
        while (Time.GetTicksMsec() < heldUntil)
        {
            await WaitFramesAsync(tree, 1);
            held = Math.Max(held, LargestMove(home));
        }
        bool wasHeld = InCardPlay(tree);
        SendTouch(tree, at, pressed: false);
        for (int i = 0; i < 45; i++)
        {
            await WaitFramesAsync(tree, 1);
            after = Math.Max(after, LargestMove(home));
        }
        return $"cards={hand.Count} touched={touchedIndex} card-held={wasHeld} moved-while-held={held:F1} moved-after-release={after:F1} inspect={(InspectOpen(game) ? "open" : "closed")}";
    }

    private static float LargestMove(Dictionary<Control, Vector2> home)
    {
        float largest = 0f;
        foreach ((Control holder, Vector2 start) in home)
            if (GodotObject.IsInstanceValid(holder) && holder.IsInsideTree())
                largest = Math.Max(largest, holder.GetGlobalTransformWithCanvas().Origin.DistanceTo(start));
        return largest;
    }

    /// <summary>How many tips (each tip of every shown tip set) reach past the canvas.</summary>
    private static string TipsInsideCanvas(SceneTree tree, NGame game)
    {
        if (game.GetType().GetProperty("HoverTipsContainer", AnyInstance)?.GetValue(game) is not Node container) return "tipsout=?";
        Rect2 canvas = tree.Root.GetVisibleRect().Grow(1f);
        int total = 0, outside = 0;
        foreach (Control set in container.GetChildren().OfType<Control>().Where(c => c.Visible))
        foreach (Control column in set.GetChildren().OfType<Control>().Where(c => c.Visible))
        foreach (Control tip in column.GetChildren().OfType<Control>().Where(c => c.Visible))
        {
            total++;
            if (!canvas.Encloses(CanvasRectOf(tip))) outside++;
        }
        return $"tipsout={outside}/{total}";
    }

    private static Rect2 CanvasRectOf(Control c)
    {
        Transform2D x = c.GetGlobalTransformWithCanvas();
        Vector2 a = x * Vector2.Zero, b = x * c.Size;
        return new Rect2(new Vector2(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), (b - a).Abs());
    }

    private static Control? FindScreen(SceneTree tree, string typeName) =>
        Descendants(tree.Root).OfType<Control>().FirstOrDefault(c => c.GetType().Name == typeName && c.IsInsideTree() && !c.IsQueuedForDeletion() && c.IsVisibleInTree());

    private static List<Control> RewardButtons(Control rewardsScreen) =>
        rewardsScreen.GetNodeOrNull<Control>("%RewardsContainer")?.GetChildren().OfType<Control>().Where(c => !c.IsQueuedForDeletion()).ToList() ?? [];

    private static string RewardKind(Control button) =>
        button.GetType().GetProperty("Reward")?.GetValue(button)?.GetType().Name ?? button.GetType().Name;

    private static string DescribeRewardScreens(SceneTree tree)
    {
        Rect2 canvas = tree.Root.GetVisibleRect().Grow(1f);
        Control? rewards = FindScreen(tree, "NRewardsScreen");
        string list = rewards == null ? "none" : string.Join(",", RewardButtons(rewards).Select(b =>
            $"{RewardKind(b)}{(canvas.Encloses(CanvasRectOf(b)) ? "" : "(out)")}"));
        string window = rewards?.GetNodeOrNull<Control>("Rewards") is { } w ? $" window={CanvasRect(w)} alpha={w.Modulate.A:F2}" : "";
        if (rewards?.GetNodeOrNull<Control>("%RewardsContainer") is { } listNode && rewards.GetNodeOrNull<Control>("%RewardContainerMask") is { } mask)
            window += $" list={CanvasRect(listNode)} mask={CanvasRect(mask)} hidden-rows={RewardButtons(rewards).Count(b => !CanvasRectOf(mask).Grow(2f).Encloses(CanvasRectOf(b)))}";
        return $"rewards-screen={(rewards != null)} rewards=[{list}]{window} card-screen={(FindScreen(tree, "NCardRewardSelectionScreen") != null)}";
    }

    // In the order of the game's options (the row's own order changes: a hovered card moves to the end to draw on top).
    private static List<Control> RewardCards(Control cardScreen) =>
        cardScreen.GetNodeOrNull<Control>("UI/CardRow")?.GetChildren().OfType<Control>().Where(c => !c.IsQueuedForDeletion())
            .OrderBy(c => PhoneCardRewardChoice.OptionIndexOf(cardScreen, c)).ToList() ?? [];

    private static Control? ChooseButton(Control? cardScreen) =>
        cardScreen == null ? null : Descendants(cardScreen).OfType<Control>().FirstOrDefault(c => c.Name == PhoneCardRewardChoice.ChooseButtonName);

    private static string DescribeCardScreen(Control? cardScreen, object player)
    {
        if (cardScreen == null) return $"card-screen=closed deck={DeckCards(player).Count}";
        Control? choose = ChooseButton(cardScreen);
        string chooseText = choose == null ? "none" : $"enabled={choose.GetType().GetProperty("IsEnabled")?.GetValue(choose)} rect={CanvasRect(choose)}";
        string alternatives = cardScreen.GetNodeOrNull<Control>("UI/RewardAlternatives") is { } alts
            ? $"{alts.GetType().Name}[{string.Join(",", alts.GetChildren().OfType<Control>().Select(c => $"{c.Name}:{CanvasRect(c)}"))}]"
            : "none";
        List<Control> cards = RewardCards(cardScreen);
        int outlined = cards.FindIndex(c => c.GetNodeOrNull<Node>("PhoneSelectedOutline") is { } o && !o.IsQueuedForDeletion());
        string names = string.Join(",", cards.Select(c => c.GetType().GetProperty("CardModel")?.GetValue(c)?.GetType().Name ?? "?"));
        // Each card's place in its row, in the game's option order: equal before and after a tap when no card moved.
        string slots = string.Join(";", cards.Select(c => $"{c.Position.X:F0},{c.Position.Y:F0}"));
        return $"card-screen=open cards={cards.Count}[{names}] slots=[{slots}] selected={PhoneCardRewardChoice.SelectedIndex(cardScreen)} outlined={outlined} choose[{chooseText}] alternatives={alternatives} deck={DeckCards(player).Count}";
    }

    /// <summary>A game object made with the constructor these arguments fit, its optional parameters left at their defaults.</summary>
    private static object NewWithDefaults(Type type, params object[] args)
    {
        foreach (ConstructorInfo ctor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public))
        {
            ParameterInfo[] ps = ctor.GetParameters();
            if (ps.Length < args.Length || ps.Skip(args.Length).Any(p => !p.HasDefaultValue)) continue;
            bool fits = true;
            for (int i = 0; i < args.Length && fits; i++) fits = ps[i].ParameterType.IsInstanceOfType(args[i]);
            if (fits) return ctor.Invoke(args.Concat(ps.Skip(args.Length).Select(p => p.DefaultValue)).ToArray());
        }
        throw new MissingMethodException(type.Name, ".ctor/" + args.Length);
    }

    /// <summary>The centre of a control on the canvas (a card holder's hitbox when it has one), for a tap.</summary>
    private static Vector2 CentreOf(Control control)
    {
        Control target = control.GetNodeOrNull<Control>("%Hitbox") ?? control.GetNodeOrNull<Control>("Hitbox") ?? control;
        return target.GetGlobalTransformWithCanvas() * (target.Size * 0.5f);
    }
}
