using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.GameHost;

/// <summary>
/// <c>touch-choices</c> (no single tap takes a card or relic, the shop needs a confirm), with
/// real touch events: in a fight, choose a card (where the peek button sits against the first card; card 1 tapped, card 3 tapped,
/// Choose), a combat pile grid that takes one card (a tap, a tap on another, the game's confirm), and a relic choice; then the shop
/// (a tap selects, a tap elsewhere lets go, a second tap buys), the treasure chest's relic (a tap, then Choose), and the Spiraling
/// Whirlpool event's last page (where text was cut off). Each step logs whether anything was taken. Portrait only: landscape is
/// the game's own (<c>stock-landscape</c>). It starts a run, so it is for the simulator only.
/// </summary>
internal static partial class PortraitProof
{
    private static async Task RunTouchChoicesAsync(SceneTree tree, NGame game)
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
        string cs = ScreensNs + "CardSelection.";
        object overlays = StaticProp(GameType(ScreensNs + "Overlays.NOverlayStack"), "Instance")!;
        IList deck = DeckCards(player);

        // Choose a card, in a fight (the peek button shows): a tap selects, a tap on another moves it, Choose takes it.
        object chooseScreen = CallStatic(GameType(cs + "NChooseACardSelectionScreen"), "ShowScreen",
            TypedList("MegaCrit.Sts2.Core.Models.CardModel", deck.Cast<object>().Skip(4).Take(3)), true)!;
        Task chooseTask = (Task)Call(chooseScreen, "CardsSelected")!;
        await WaitFramesAsync(tree, 70);
        var screen = (Control)chooseScreen;
        List<Control> cards = RowItems(screen, "CardRow");
        await TouchStepAsync(tree, "choice-card-open", $"{DescribeChoice(screen, cards, chooseTask)} {DescribePeek(screen, cards)}", results);
        if (cards.Count > 2)
        {
            await TapAsync(tree, CentreOf(cards[0]));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-card-first", DescribeChoice(screen, cards, chooseTask), results);
            await TapAsync(tree, CentreOf(cards[2]));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-card-third", DescribeChoice(screen, cards, chooseTask), results);
            string expectedCard = CardName(cards[2]);
            if (ChooseOf(screen) is { } choose) await TapAsync(tree, CentreOf(choose));
            await WaitFramesAsync(tree, 60);
            await TouchStepAsync(tree, "choice-card-chosen", $"expected={expectedCard} {DescribeTask(chooseTask)} open={GodotObject.IsInstanceValid(screen) && screen.IsInsideTree()}", results);
        }
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        // A combat pile grid that takes one card: the game would close on the first tap; now it waits for its confirm button.
        Type prefsType = GameType("MegaCrit.Sts2.Core.CardSelection.CardSelectorPrefs", "CardSelectorPrefs");
        object prefs = Activator.CreateInstance(prefsType, StaticProp(prefsType, "DiscardSelectionPrompt"), 1)!;
        Type pileTypeEnum = GameType("MegaCrit.Sts2.Core.Entities.Cards.PileType");
        Type pileExt = GameType("MegaCrit.Sts2.Core.Entities.Cards.PileTypeExtensions", "PileTypeExtensions");
        object pile = CallWith(null, pileExt, "GetPile", [Enum.Parse(pileTypeEnum, "Draw"), player])!;
        var grid = (Control)CallWith(null, GameType(cs + "NCombatPileCardSelectScreen"), "Create", [pile, prefs, null])!;
        Call(overlays, "Push", grid);
        Task gridTask = (Task)Call(grid, "CardsSelected")!;
        await WaitFramesAsync(tree, 60);
        List<Control> gridCards = GridHolders(grid);
        await TouchStepAsync(tree, "choice-grid-open", $"{DescribeGrid(grid, gridTask)} {DescribePeek(grid, gridCards)}", results);
        // The second tap goes to a card of another kind, so the log shows the selection move.
        Control? otherCard = gridCards.Skip(1).FirstOrDefault(c => gridCards.Count > 0 && CardName(c) != CardName(gridCards[0]));
        if (gridCards.Count > 1 && otherCard != null)
        {
            string expected = CardName(otherCard);
            await TapAsync(tree, CentreOf(gridCards[0]));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-grid-first", $"tapped={CardName(gridCards[0])} {DescribeGrid(grid, gridTask)}", results);
            await TapAsync(tree, CentreOf(otherCard));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-grid-second", $"tapped={expected} {DescribeGrid(grid, gridTask)}", results);
            if (ConfirmOf(grid) is { } confirm) await TapAsync(tree, CentreOf(confirm));
            await WaitFramesAsync(tree, 60);
            await TouchStepAsync(tree, "choice-grid-confirmed", $"expected={expected} {DescribeTask(gridTask)}", results);
        }
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        // A relic choice: a tap selects, a tap on another moves it, Choose takes it.
        var relicScreen = (Control)CallStatic(GameType(ScreensNs + "NChooseARelicSelection"), "ShowScreen",
            TypedList("MegaCrit.Sts2.Core.Models.RelicModel", ModelDbList("AllRelics").Skip(10).Take(3).Select(r => Call(r, "ToMutable")!)))!;
        Task relicTask = (Task)Call(relicScreen, "RelicsSelected")!;
        await WaitFramesAsync(tree, 70);
        List<Control> relics = RowItems(relicScreen, "RelicRow");
        await TouchStepAsync(tree, "choice-relic-open", DescribeChoice(relicScreen, relics, relicTask), results);
        if (relics.Count > 2)
        {
            await TapAsync(tree, CentreOf(relics[0]));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-relic-first", DescribeChoice(relicScreen, relics, relicTask), results);
            await TapAsync(tree, CentreOf(relics[2]));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-relic-third", DescribeChoice(relicScreen, relics, relicTask), results);
            string expected = RelicName(relics[2]);
            if (ChooseOf(relicScreen) is { } choose) await TapAsync(tree, CentreOf(choose));
            await WaitFramesAsync(tree, 60);
            await TouchStepAsync(tree, "choice-relic-chosen", $"expected={expected} {DescribeTask(relicTask)}", results);
        }
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);
        await RunConsoleCmdAsync(Sts2, "WinConsoleCmd", player, []);
        await WaitFramesAsync(tree, 120);
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        // The shop: a tap selects, a tap elsewhere lets go, a second tap on the same item buys it.
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
        Control? cardSlot = slots.FirstOrDefault(s => s.GetType().Name == "NMerchantCard");
        Control? relicSlot = slots.FirstOrDefault(s => s.GetType().Name == "NMerchantRelic");
        Control? potionSlot = slots.FirstOrDefault(s => s.GetType().Name == "NMerchantPotion");
        await TouchStepAsync(tree, "choice-shop-open", DescribeShop(player, slots), results);
        if (cardSlot != null && relicSlot != null)
        {
            await TapAsync(tree, CentreOf(cardSlot));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-shop-card-tap", DescribeShop(player, slots), results);
            await TapAsync(tree, CentreOf(relicSlot));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-shop-relic-tap", DescribeShop(player, slots), results);
            await TapAsync(tree, CentreOf(relicSlot));
            await WaitFramesAsync(tree, 60);
            await TouchStepAsync(tree, "choice-shop-relic-bought", DescribeShop(player, slots), results);
        }
        if (potionSlot != null)
        {
            await TapAsync(tree, CentreOf(potionSlot));
            await WaitFramesAsync(tree, 30);
            await TapAsync(tree, new Vector2(tree.Root.GetVisibleRect().Size.X / 2f, tree.Root.GetVisibleRect().Size.Y - 60f));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-shop-potion-let-go", DescribeShop(player, slots), results);
            await TapAsync(tree, CentreOf(potionSlot));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-shop-potion-tap", DescribeShop(player, slots), results);
        }
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        // The treasure chest: a tap on the relic selects it, Choose takes it.
        await RunConsoleCmdAsync(Sts2, "RoomConsoleCmd", player, ["Treasure"]);
        await WaitFramesAsync(tree, 120);
        Node? room = Descendants(tree.Root).FirstOrDefault(n => n.GetType().Name == "NTreasureRoom");
        if (room != null) CallWith(room, null, "OnChestButtonReleased", [null]);
        await WaitFramesAsync(tree, 120);
        DismissFtues(tree, Sts2);
        await WaitFramesAsync(tree, 40);
        Control? collection = Descendants(tree.Root).OfType<Control>().FirstOrDefault(c => c.GetType().Name == "NTreasureRoomRelicCollection");
        List<Control> chestRelics = collection == null ? [] : ((IEnumerable)Prop(collection, "HoldersInUse")!).OfType<Control>().ToList();
        int relicsBefore = RelicCount(player);
        await TouchStepAsync(tree, "choice-treasure-open", DescribeTreasure(collection, chestRelics, player, relicsBefore), results);
        if (chestRelics.Count > 0 && collection != null)
        {
            await TapAsync(tree, CentreOf(chestRelics[0]));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-treasure-tap", DescribeTreasure(collection, chestRelics, player, relicsBefore), results);
            if (ChooseOf(collection) is { } choose) await TapAsync(tree, CentreOf(choose));
            await WaitFramesAsync(tree, 90);
            await TouchStepAsync(tree, "choice-treasure-chosen", DescribeTreasure(collection, chestRelics, player, relicsBefore), results);
        }

        // Spiraling Whirlpool: Observe, enchant a Strike, and the page whose text was cut off.
        await RunConsoleCmdAsync(Sts2, "EventConsoleCmd", player, ["SPIRALING_WHIRLPOOL"]);
        await WaitFramesAsync(tree, 150);
        await TouchStepAsync(tree, "event-whirlpool-initial", DescribeEventText(tree), results);
        if (EventOption(tree, "Observe") is { } observe)
        {
            await TapAsync(tree, CentreOf(observe));
            await WaitFramesAsync(tree, 90);
            Control? enchant = Descendants(tree.Root).OfType<Control>().FirstOrDefault(c => c.GetType().Name == "NDeckEnchantSelectScreen" && c.IsVisibleInTree());
            if (enchant != null && GridHolders(enchant).FirstOrDefault() is { } first)
            {
                CallWith(enchant, null, "OnCardClicked", [Prop(first, "CardModel")]);
                await WaitFramesAsync(tree, 40);
                Control? confirm = Descendants(enchant).OfType<Control>().FirstOrDefault(c => c.GetType().Name == "NConfirmButton" && c.IsVisibleInTree() &&
                    c.GetType().GetProperty("IsEnabled")?.GetValue(c) is true);
                confirm?.Call("ForceClick");
                await WaitFramesAsync(tree, 150);
            }
            await TouchStepAsync(tree, "event-whirlpool-observed", $"enchant-screen={(enchant != null)} {DescribeEventText(tree)}", results);
        }

        GD.Print("Touch choices results: " + string.Join("; ", results));
    }

    private static List<Control> RowItems(Control screen, string row) =>
        screen.GetNodeOrNull<Control>(row)?.GetChildren().OfType<Control>().Where(c => !c.IsQueuedForDeletion()).OrderBy(c => c.Position.X).ToList() ?? [];

    private static List<Control> GridHolders(Control screen) =>
        Descendants(screen).OfType<Control>().Where(c => c.GetType().Name == "NGridCardHolder" && c.IsVisibleInTree())
            .OrderBy(c => c.GetGlobalRect().Position.Y).ThenBy(c => c.GetGlobalRect().Position.X).ToList();

    private static Control? ChooseOf(Control screen) =>
        Descendants(screen).OfType<Control>().FirstOrDefault(c => c.Name == PhoneCardRewardChoice.ChooseButtonName);

    private static Control? ConfirmOf(Control screen) =>
        Descendants(screen).OfType<Control>().FirstOrDefault(c => c.GetType().Name == "NConfirmButton" && c.IsVisibleInTree());

    private static string RelicName(Control holder) =>
        Prop(Prop(holder, "Relic") ?? holder, "Model")?.GetType().Name ?? holder.Name;

    private static string CardName(Control holder) => holder.GetType().GetProperty("CardModel")?.GetValue(holder)?.GetType().Name ?? holder.Name;

    private static string DescribeTask(Task task)
    {
        if (!task.IsCompleted) return "taken=none";
        if (!task.IsCompletedSuccessfully) return $"taken=cancelled";
        object? result = task.GetType().GetProperty("Result")?.GetValue(task);
        string names = result is IEnumerable items ? string.Join(",", items.Cast<object>().Select(i => i.GetType().Name)) : "?";
        return $"taken=[{names}]";
    }

    private static string DescribeChoice(Control screen, List<Control> items, Task task)
    {
        int outlined = items.FindIndex(i => i.GetNodeOrNull<Node>("PhoneSelectedOutline") is { } o && !o.IsQueuedForDeletion());
        Control? choose = ChooseOf(screen);
        string chooseText = choose == null ? "none" : $"visible={choose.IsVisibleInTree()} enabled={choose.GetType().GetProperty("IsEnabled")?.GetValue(choose)} rect={CanvasRect(choose)}";
        string skip = screen.GetNodeOrNull<Control>("SkipButton") is { } s ? $"{(s.Visible ? "" : "hidden ")}{CanvasRect(s)}" : "none";
        return $"items={items.Count} outlined={outlined} selected={PhoneChoiceConfirm.SelectedName(screen) ?? "none"} {DescribeTask(task)} choose[{chooseText}] skip={skip}";
    }

    private static string DescribePeek(Control screen, List<Control> items)
    {
        if (Descendants(screen).OfType<Control>().FirstOrDefault(c => c.GetType().Name == "NPeekButton") is not { } peek) return "peek=none";
        Rect2 peekRect = CanvasRectOf(peek);
        int covered = items.Count(i => CanvasRectOf(i.GetNodeOrNull<Control>("%Hitbox") ?? i).Intersects(peekRect));
        return $"peek={(peek.IsVisibleInTree() ? "shown" : "hidden")} {CanvasRect(peek)} covers-cards={covered}";
    }

    private static string DescribeGrid(Control grid, Task task)
    {
        int selected = grid.GetType().GetField("_selectedCards", AnyInstance)?.GetValue(grid) is IEnumerable s ? s.Cast<object>().Count() : -1;
        string selectedNames = grid.GetType().GetField("_selectedCards", AnyInstance)?.GetValue(grid) is IEnumerable n
            ? string.Join(",", n.Cast<object>().Select(c => c.GetType().Name)) : "";
        Control? confirm = ConfirmOf(grid);
        string confirmText = confirm == null ? "none" : $"enabled={confirm.GetType().GetProperty("IsEnabled")?.GetValue(confirm)} {CanvasRect(confirm)}";
        return $"selected={selected}[{selectedNames}] {DescribeTask(task)} confirm[{confirmText}]";
    }

    private static List<Control> ShopSlots(SceneTree tree) =>
        Descendants(tree.Root).OfType<Control>().Where(c => IsMerchantSlot(c.GetType()) && c.IsVisibleInTree()).ToList();

    private static bool IsMerchantSlot(Type type)
    {
        for (Type? t = type; t != null; t = t.BaseType)
            if (t.Name == "NMerchantSlot") return true;
        return false;
    }

    private static int RelicCount(object player) => Prop(player, "Relics") is ICollection c ? c.Count : -1;

    private static string DescribeShop(object player, List<Control> slots)
    {
        Control? selected = PhoneChoiceConfirm.SelectedSlot();
        string kinds = string.Join(",", slots.GroupBy(s => s.GetType().Name).Select(g => $"{g.Key}x{g.Count()}"));
        return $"slots=[{kinds}] selected={selected?.GetType().Name ?? "none"} gold={Prop(player, "Gold")} deck={DeckCards(player).Count} relics={RelicCount(player)}";
    }

    private static string DescribeTreasure(Control? collection, List<Control> holders, object player, int relicsBefore)
    {
        if (collection == null) return "collection=none";
        int outlined = holders.FindIndex(i => i.GetNodeOrNull<Node>("PhoneSelectedOutline") is { } o && !o.IsQueuedForDeletion());
        Control? choose = ChooseOf(collection);
        string chooseText = choose == null ? "none" : $"visible={choose.IsVisibleInTree()} enabled={choose.GetType().GetProperty("IsEnabled")?.GetValue(choose)} rect={CanvasRect(choose)}";
        return $"relics-on-offer={holders.Count} outlined={outlined} relics={relicsBefore}->{RelicCount(player)} choose[{chooseText}]";
    }

    private static Control? EventOption(SceneTree tree, string text) =>
        Descendants(tree.Root).OfType<Control>().FirstOrDefault(c => c.GetType().Name.Contains("EventOptionButton", StringComparison.Ordinal) &&
            c.IsVisibleInTree() && Descendants(c).Any(d => (d as RichTextLabel)?.Text.Contains(text, StringComparison.Ordinal) == true ||
                (d as Label)?.Text.Contains(text, StringComparison.Ordinal) == true));

    /// <summary>The event's art, title, description and options on the canvas, and whether the description's text fits its box.</summary>
    private static string DescribeEventText(SceneTree tree)
    {
        Control? layout = Descendants(tree.Root).OfType<Control>().FirstOrDefault(c => c.GetType().Name == "NEventLayout" && c.IsVisibleInTree());
        if (layout == null) return "event=none";
        string Rect(string path) => layout.GetNodeOrNull<Control>(path) is { } c ? CanvasRect(c) : "none";
        string description = "none";
        if ((layout.GetNodeOrNull<RichTextLabel>("%EventDescription") ?? layout.GetNodeOrNull<RichTextLabel>("VBoxContainer/EventDescription")) is { } d)
            description = $"{CanvasRect(d)} content-height={d.GetContentHeight()} size-y={d.Size.Y:F0} font={d.GetThemeFontSize("normal_font_size")} chars={d.GetTotalCharacterCount()} visible-ratio={d.VisibleRatio:F2}";
        return $"canvas={tree.Root.GetVisibleRect().Size} art={Rect("%Portrait")} text={Rect("VBoxContainer")} title={Rect("VBoxContainer/Title")} description[{description}] options={Rect("%OptionsContainer")}";
    }
}
