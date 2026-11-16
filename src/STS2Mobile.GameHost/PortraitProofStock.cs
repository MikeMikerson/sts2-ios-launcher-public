using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.GameHost;

/// <summary>
/// Landscape plays as the regular game does, apart from touch working. With real touch events:
/// <list type="bullet">
/// <item><c>stock-landscape</c>: Portrait Mode never on. A tap on a hand card opens no inspect screen; choose a card, a relic
/// choice, a card reward and a shop item are each taken by one tap, with no Choose button; and the phone's touch helpers track
/// nothing.</item>
/// <item><c>switch-mid-choice</c>: a card reward opened in portrait keeps its Choose button when Portrait Mode is turned off
/// over it, and Choose still takes the card; the next card reward, opened in landscape, is one tap; and one opened in landscape
/// gets its Choose button when Portrait Mode is turned on over it.</item>
/// </list>
/// Both start a run, so they are for the simulator only.
/// </summary>
internal static partial class PortraitProof
{
    private static async Task RunStockLandscapeAsync(SceneTree tree, NGame game)
    {
        ClearNextStepFile();
        await EnsureLandscapeAsync(tree, game);
        object player = await StartProofRunAsync(tree, game, characterIndex: 0, fivePotions: false, thirtyRelics: false, pageTwo: false, addThreePotions: false, closeRelicHover: true);
        await WaitFramesAsync(tree, 90);
        Call(StaticProp(GameType(ScreensNs + "Map.NMapScreen"), "Instance")!, "Close", false);
        DismissFtues(tree, Sts2);
        await WaitFramesAsync(tree, 20);
        await EnterEncounterAsync(Sts2, tree, player, "NIBBITS_NORMAL", 42UL);
        await WaitFramesAsync(tree, 60);
        var results = new List<string>();
        string cs = ScreensNs + "CardSelection.";
        object overlays = StaticProp(GameType(ScreensNs + "Overlays.NOverlayStack"), "Instance")!;
        IList deck = DeckCards(player);

        // The hand: a tap picks the card up as a click does on a desktop; no inspect screen opens.
        await TrimHandToAsync(Sts2, player, ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "BASH", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD"]);
        await WaitFramesAsync(tree, 60);
        List<Control> hand = HandHolders(tree);
        if (hand.Count > 2)
        {
            await TapAsync(tree, PointOnCard(hand[2], 0.5f, 0.25f));
            await WaitFramesAsync(tree, 40);
        }
        await TouchStepAsync(tree, "stock-hand-tap", $"cards={hand.Count} phone-inspects={PhoneTouchCards.InspectsOpened} card-held={InCardPlay(tree)}", results);
        if (Descendants(tree.Root).OfType<Control>().FirstOrDefault(c => c.GetType().Name == "NPlayerHand") is { } handNode)
            handNode.GetType().GetMethod("CancelAllCardPlay", AnyInstance, null, Type.EmptyTypes, null)?.Invoke(handNode, null);
        await WaitFramesAsync(tree, 30);

        // Choose a card: one tap takes it, and there is no Choose button.
        object chooseScreen = CallStatic(GameType(cs + "NChooseACardSelectionScreen"), "ShowScreen",
            TypedList("MegaCrit.Sts2.Core.Models.CardModel", deck.Cast<object>().Skip(4).Take(3)), true)!;
        Task chooseTask = (Task)Call(chooseScreen, "CardsSelected")!;
        await WaitFramesAsync(tree, 70);
        var screen = (Control)chooseScreen;
        List<Control> cards = RowItems(screen, "CardRow");
        await TouchStepAsync(tree, "stock-choice-card-open", DescribeChoice(screen, cards, chooseTask), results);
        if (cards.Count > 0)
        {
            string expected = CardName(cards[0]);
            await TapAsync(tree, CentreOf(cards[0]));
            await WaitFramesAsync(tree, 60);
            await TouchStepAsync(tree, "stock-choice-card-tap", $"expected={expected} {DescribeTask(chooseTask)}", results);
        }
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        // A relic choice: one tap takes it.
        var relicScreen = (Control)CallStatic(GameType(ScreensNs + "NChooseARelicSelection"), "ShowScreen",
            TypedList("MegaCrit.Sts2.Core.Models.RelicModel", ModelDbList("AllRelics").Skip(10).Take(3).Select(r => Call(r, "ToMutable")!)))!;
        Task relicTask = (Task)Call(relicScreen, "RelicsSelected")!;
        await WaitFramesAsync(tree, 70);
        List<Control> relics = RowItems(relicScreen, "RelicRow");
        await TouchStepAsync(tree, "stock-choice-relic-open", DescribeChoice(relicScreen, relics, relicTask), results);
        if (relics.Count > 0)
        {
            string expected = RelicName(relics[0]);
            await TapAsync(tree, CentreOf(relics[0]));
            await WaitFramesAsync(tree, 60);
            await TouchStepAsync(tree, "stock-choice-relic-tap", $"expected={expected} {DescribeTask(relicTask)}", results);
        }
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        // The fight's card reward: the list takes a tap at once, and one tap on a card takes it.
        await RunConsoleCmdAsync(Sts2, "WinConsoleCmd", player, []);
        Control? rewards = await WaitForRewardsAsync(tree);
        await WaitFramesAsync(tree, 90);
        DismissFtues(tree, Sts2);
        await WaitFramesAsync(tree, 40);
        await TouchStepAsync(tree, "stock-rewards-list", $"guarded={PhoneRewardTapGuard.TrackedForProof()} {DescribeRewardScreens(tree)}", results);
        Control? cardScreen = await OpenCardRewardAsync(tree, rewards);
        await TouchStepAsync(tree, "stock-card-reward-open", DescribeCardScreen(cardScreen, player), results);
        if (cardScreen != null && RewardCards(cardScreen) is { Count: > 0 } rewardCards)
        {
            int deckBefore = DeckCards(player).Count;
            await TapAsync(tree, CentreOf(rewardCards[0]));
            await WaitFramesAsync(tree, 70);
            await TouchStepAsync(tree, "stock-card-reward-tap", $"deck={deckBefore}->{DeckCards(player).Count} {DescribeRewardScreens(tree)}", results);
        }
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        // The shop: one tap buys.
        player.GetType().GetProperty("Gold")!.SetValue(player, 999);
        await RunConsoleCmdAsync(Sts2, "RoomConsoleCmd", player, ["Shop"]);
        await WaitFramesAsync(tree, 120);
        DismissFtues(tree, Sts2);
        object? merchant = StaticProp(GameType("MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom"), "Instance");
        if (merchant != null) Call(merchant, "OpenInventory");
        await WaitFramesAsync(tree, 70);
        DismissFtues(tree, Sts2);
        await WaitFramesAsync(tree, 20);
        List<Control> slots = ShopSlots(tree);
        await TouchStepAsync(tree, "stock-shop-open", DescribeShop(player, slots), results);
        if (slots.FirstOrDefault(s => s.GetType().Name == "NMerchantRelic") is { } relicSlot)
        {
            await TapAsync(tree, CentreOf(relicSlot));
            await WaitFramesAsync(tree, 60);
            await TouchStepAsync(tree, "stock-shop-relic-tap", DescribeShop(player, slots), results);
        }

        await TouchStepAsync(tree, "stock-helpers", DescribeTouchHelpers(), results);
        GD.Print("Stock landscape results: " + string.Join("; ", results));
    }

    private static async Task RunSwitchMidChoiceAsync(SceneTree tree, NGame game)
    {
        ClearNextStepFile();
        await EnsurePortraitAsync(tree, game);
        object player = await StartProofRunAsync(tree, game, characterIndex: 0, fivePotions: false, thirtyRelics: false, pageTwo: false, addThreePotions: false, closeRelicHover: true);
        await WaitFramesAsync(tree, 90);
        Call(StaticProp(GameType(ScreensNs + "Map.NMapScreen"), "Instance")!, "Close", false);
        DismissFtues(tree, Sts2);
        await WaitFramesAsync(tree, 20);
        await EnterEncounterAsync(Sts2, tree, player, "NIBBITS_NORMAL", 42UL);
        await WaitFramesAsync(tree, 60);
        var results = new List<string>();

        // Three card rewards: the fight's own and two more.
        object? room = Prop(Prop(player, "RunState")!, "CurrentRoom");
        if (room != null && room.GetType().Name == "CombatRoom")
        {
            object options = CallStatic(GameType("MegaCrit.Sts2.Core.Runs.CardCreationOptions"), "ForRoom", player, Prop(room, "RoomType"))!;
            Type cardReward = GameType("MegaCrit.Sts2.Core.Rewards.CardReward");
            Call(room, "AddExtraReward", player, NewWithDefaults(cardReward, options, 3, player));
            Call(room, "AddExtraReward", player, NewWithDefaults(cardReward, options, 3, player));
        }
        await RunConsoleCmdAsync(Sts2, "WinConsoleCmd", player, []);
        Control? rewards = await WaitForRewardsAsync(tree);
        await WaitFramesAsync(tree, 90);
        DismissFtues(tree, Sts2);
        await WaitFramesAsync(tree, 40);

        // Opened in portrait: a tap selects. Portrait Mode off over it: the screen still waits for Choose, and Choose takes.
        Control? cardScreen = await OpenCardRewardAsync(tree, rewards);
        if (cardScreen != null && RewardCards(cardScreen) is { Count: > 0 } first)
        {
            await TapAsync(tree, CentreOf(first[0]));
            await WaitFramesAsync(tree, 30);
        }
        await TouchStepAsync(tree, "switch-portrait-selected", DescribeCardScreen(cardScreen, player), results);
        await TogglePortraitMidRunAsync(tree, Sts2, portrait: false);
        await WaitFramesAsync(tree, 60);
        cardScreen = FindScreen(tree, "NCardRewardSelectionScreen");
        await TouchStepAsync(tree, "switch-landscape-still-choose", DescribeCardScreen(cardScreen, player), results);
        int deckBefore = DeckCards(player).Count;
        if (ChooseButton(cardScreen) is { } choose)
        {
            await TapAsync(tree, CentreOf(choose));
            await WaitFramesAsync(tree, 70);
        }
        await TouchStepAsync(tree, "switch-landscape-chosen", $"deck={deckBefore}->{DeckCards(player).Count} {DescribeRewardScreens(tree)}", results);

        // Opened in landscape: no Choose button, and one tap takes the card.
        cardScreen = await OpenCardRewardAsync(tree, FindScreen(tree, "NRewardsScreen"));
        await TouchStepAsync(tree, "switch-landscape-next-open", DescribeCardScreen(cardScreen, player), results);
        deckBefore = DeckCards(player).Count;
        if (cardScreen != null && RewardCards(cardScreen) is { Count: > 0 } second)
        {
            await TapAsync(tree, CentreOf(second[0]));
            await WaitFramesAsync(tree, 70);
        }
        await TouchStepAsync(tree, "switch-landscape-next-tap", $"deck={deckBefore}->{DeckCards(player).Count} {DescribeRewardScreens(tree)}", results);

        // Opened in landscape, Portrait Mode on over it: the open screen gets Choose, a tap selects and Choose takes.
        cardScreen = await OpenCardRewardAsync(tree, FindScreen(tree, "NRewardsScreen"));
        await TogglePortraitMidRunAsync(tree, Sts2, portrait: true);
        await WaitFramesAsync(tree, 60);
        cardScreen = FindScreen(tree, "NCardRewardSelectionScreen");
        await TouchStepAsync(tree, "switch-portrait-over-open", DescribeCardScreen(cardScreen, player), results);
        deckBefore = DeckCards(player).Count;
        if (cardScreen != null && RewardCards(cardScreen) is { Count: > 0 } third)
        {
            await TapAsync(tree, CentreOf(third[0]));
            await WaitFramesAsync(tree, 30);
        }
        await TouchStepAsync(tree, "switch-portrait-over-selected", DescribeCardScreen(FindScreen(tree, "NCardRewardSelectionScreen"), player), results);
        if (ChooseButton(FindScreen(tree, "NCardRewardSelectionScreen")) is { } chooseOver)
        {
            await TapAsync(tree, CentreOf(chooseOver));
            await WaitFramesAsync(tree, 70);
        }
        await TouchStepAsync(tree, "switch-portrait-over-chosen", $"deck={deckBefore}->{DeckCards(player).Count} {DescribeRewardScreens(tree)} {DescribeTouchHelpers()}", results);

        GD.Print("Switch mid choice results: " + string.Join("; ", results));
    }

    /// <summary>
    /// <c>map-acts</c> (on a phone, the two boss icons at the top of act 3 could not be seen): in
    /// portrait, the map of act 1 and of act 3 (two bosses at ascension 10) scrolled as far up as it goes, with where each boss
    /// point sits against the band under the top bar and relics, and the top bar's own boss icons. It starts a run.
    /// </summary>
    private static async Task RunMapActsAsync(SceneTree tree, NGame game)
    {
        ClearNextStepFile();
        await EnsurePortraitAsync(tree, game);
        // Five potion slots: the fullest the top bar's row gets, so the boss icons are measured with the least room.
        object player = await StartProofRunAsync(tree, game, characterIndex: 0, fivePotions: true, thirtyRelics: false, pageTwo: false, addThreePotions: false, closeRelicHover: true);
        await WaitFramesAsync(tree, 120);
        DismissFtues(tree, Sts2);
        var results = new List<string>();
        var map = (Control)StaticProp(GameType(ScreensNs + "Map.NMapScreen"), "Instance")!;
        await ScrollMapToTopAsync(tree, map);
        await TouchStepAsync(tree, "map-act1-top", DescribeMapTop(tree, map), results);
        await RunConsoleCmdAsync(Sts2, "ActConsoleCmd", player, ["3"]);
        await WaitFramesAsync(tree, 300);
        DismissFtues(tree, Sts2);
        if (!map.IsVisibleInTree()) Call(map, "Open", false);
        await WaitFramesAsync(tree, 120);
        await TouchStepAsync(tree, "map-act3-opened", DescribeMapTop(tree, map), results);
        await ScrollMapToTopAsync(tree, map);
        await TouchStepAsync(tree, "map-act3-top", DescribeMapTop(tree, map), results);
        // A room entered in act 3: the game sets the top bar's boss icon again on every room.
        await RunConsoleCmdAsync(Sts2, "RoomConsoleCmd", player, ["Shop"]);
        await WaitFramesAsync(tree, 240);
        await TouchStepAsync(tree, "map-act3-room", DescribeMapTop(tree, map), results);
        await TogglePortraitMidRunAsync(tree, Sts2, portrait: false);
        if (!map.IsVisibleInTree()) Call(map, "Open", false);
        await WaitFramesAsync(tree, 120);
        await TouchStepAsync(tree, "map-act3-landscape", DescribeMapTop(tree, map), results);
        GD.Print("Map acts results: " + string.Join("; ", results));
    }

    /// <summary>Asks for more scroll than any limit allows and lets the game's and the portrait layout's limits settle it.</summary>
    private static async Task ScrollMapToTopAsync(SceneTree tree, Control map)
    {
        map.GetType().GetField("_targetDragPos", AnyInstance)?.SetValue(map, new Vector2(0f, 5000f));
        await WaitFramesAsync(tree, 150);
    }

    private static string DescribeMapTop(SceneTree tree, Control map)
    {
        (float bandTop, float bandBottom) = PortraitBetweenLayout.VisibleBand(tree.Root);
        string Boss(string field) => map.GetType().GetField(field, AnyInstance)?.GetValue(map) is Control node && GodotObject.IsInstanceValid(node)
            ? $"{(node.IsVisibleInTree() ? "" : "hidden ")}{CanvasRect(node)} scale={node.Scale.X:F2} size={node.Size.X:F0}x{node.Size.Y:F0}"
            : "none";
        Vector2 mapPos = map.GetNodeOrNull<Control>("TheMap")?.Position ?? new Vector2(float.NaN, float.NaN);
        // The outer edges of the room points on the canvas: equal gaps to both screen edges when the map is centred.
        float left = float.MaxValue, right = float.MinValue;
        foreach (Control point in map.GetNodeOrNull<Control>("TheMap/Points")?.GetChildren().OfType<Control>() ?? [])
        {
            if (point.GetType().Name != "NNormalMapPoint") continue;
            Rect2 rect = point.GetGlobalRect();
            left = Math.Min(left, rect.Position.X);
            right = Math.Max(right, rect.End.X);
        }
        float canvasWidth = tree.Root.GetVisibleRect().Size.X;
        string points = left <= right ? $"left-gap={left:F0} right-gap={canvasWidth - right:F0}" : "none";
        Control? topBar = Descendants(tree.Root).OfType<Control>().FirstOrDefault(c => c.GetType().Name == "NTopBar" && c.IsVisibleInTree());
        string bar = "none";
        if (topBar != null)
        {
            Control? icon = topBar.GetNodeOrNull<Control>("%BossIcon");
            Control? first = icon?.GetNodeOrNull<Control>("Icon");
            Control? second = first?.GetChildren().OfType<Control>().FirstOrDefault(c => c.Name.ToString() != "Outline");
            Control? mapButton = topBar.GetNodeOrNull<Control>("%Map");
            Control? leftBox = topBar.GetNodeOrNull<Control>("LeftAlignedStuff");
            string Shown(Control? c) => c == null ? "none" : $"{(c.IsVisibleInTree() ? "" : "hidden ")}{CanvasRect(c)}";
            bool clear = second == null || !second.IsVisibleInTree() || mapButton == null ||
                second.GetGlobalRect().End.X <= mapButton.GetGlobalRect().Position.X;
            bar = $"boss-icon={Shown(icon)} first={Shown(first)} second={Shown(second)} map-button={Shown(mapButton)} second-clear-of-map-button={clear} left-row={Shown(leftBox)}";
        }
        return $"map-visible={map.IsVisibleInTree()} mapX={mapPos.X:F1} mapY={mapPos.Y:F0} points[{points}] limits={PortraitBetweenLayout.MapScrollLimits(map, tree.Root)} band={bandTop:F0}..{bandBottom:F0} boss[{Boss("_bossPointNode")}] second[{Boss("_secondBossPointNode")}] top-bar[{bar}]";
    }

    private static async Task<Control?> WaitForRewardsAsync(SceneTree tree)
    {
        Control? rewards = null;
        for (int frame = 0; frame < 900 && rewards == null; frame++)
        {
            await WaitFramesAsync(tree, 1);
            rewards = FindScreen(tree, "NRewardsScreen");
        }
        return rewards;
    }

    /// <summary>Taps the first card reward row of the list and returns the card screen it opens.</summary>
    private static async Task<Control?> OpenCardRewardAsync(SceneTree tree, Control? rewards)
    {
        if (rewards != null && RewardButtons(rewards).FirstOrDefault(b => RewardKind(b) == "CardReward") is { } row)
        {
            await TapAsync(tree, CentreOf(row));
            await WaitFramesAsync(tree, 70);
        }
        return FindScreen(tree, "NCardRewardSelectionScreen");
    }

    /// <summary>What the phone's touch helpers track now: all zero in a launch that stayed in landscape.</summary>
    private static string DescribeTouchHelpers() =>
        $"phone-inspects={PhoneTouchCards.InspectsOpened} card-reward={PhoneCardRewardChoice.TrackedForProof()} tap-guard={PhoneRewardTapGuard.TrackedForProof()} choice-confirm={PhoneChoiceConfirm.TrackedForProof()}";
}
