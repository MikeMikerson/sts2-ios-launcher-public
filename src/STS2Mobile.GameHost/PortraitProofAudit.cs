using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.GameHost;

/// <summary>
/// The portrait audit: <c>audit-menus</c> opens every main menu side screen and <c>audit-run</c> every in-run screen
/// that has no proof step elsewhere, one step each. Every step logs <see cref="PortraitAudit.Describe"/> (what reaches past the
/// canvas) and waits for the host's screenshot. A step that cannot open its screen logs why and the suite carries on.
/// </summary>
internal static partial class PortraitProof
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
    private const string ScreensNs = "MegaCrit.Sts2.Core.Nodes.Screens.";

    private static Assembly Sts2 => typeof(NGame).Assembly;

    private static async Task RunMenuAuditAsync(SceneTree tree, NGame game)
    {
        ClearNextStepFile();
        await EnsurePortraitAsync(tree, game);
        object menu = game.MainMenu!;
        object stack = Prop(menu, "SubmenuStack")!;

        await AuditStepAsync(tree, "main-menu", null);

        foreach (string tab in new[] { "General", "Graphics", "Sound", "Input" })
        {
            await AuditStepAsync(tree, "settings-" + tab.ToLowerInvariant(), async () =>
            {
                Call(menu, "OpenSettingsMenu");
                await WaitFramesAsync(tree, 20);
                Node? tabNode = Descendants(tree.Root).FirstOrDefault(n =>
                    n.Name == tab && n.GetParent()?.Name == "SettingsTabManager" && (n as CanvasItem)?.IsVisibleInTree() == true);
                tabNode?.Call("ForceClick");
            });
            PopAll(stack);
        }

        await AuditStepAsync(tree, "singleplayer", () => { Call(menu, "OpenSingleplayerSubmenu"); return Task.CompletedTask; });
        PopAll(stack);

        foreach (string screen in new[] { "CharacterSelect.NCharacterSelectScreen", "CustomRun.NCustomRunScreen", "DailyRun.NDailyRunScreen", "Blitz.NBlitzScreen", "Dread.NDreadScreen" })
        {
            string step = screen[(screen.IndexOf(".N", StringComparison.Ordinal) + 2)..].Replace("Screen", "").ToLowerInvariant();
            await AuditStepAsync(tree, step, () =>
            {
                object submenu = Call(stack, "GetSubmenuType", GameType(ScreensNs + screen))!;
                Call(submenu, "InitializeSingleplayer");
                Call(stack, "Push", submenu);
                return Task.CompletedTask;
            });
            if (step == "characterselect")
            {
                await AuditStepAsync(tree, "characterselect-picked", async () =>
                {
                    Node? button = Descendants(tree.Root).FirstOrDefault(n =>
                        n.GetType().Name == "NCharacterSelectButton" && (n as CanvasItem)?.IsVisibleInTree() == true);
                    button?.Call("ForceClick");
                    await WaitFramesAsync(tree, 40);
                });
            }
            PopAll(stack);
        }

        await AuditStepAsync(tree, "multiplayer", () => { Call(menu, "OpenMultiplayerSubmenu"); return Task.CompletedTask; });
        await AuditStepAsync(tree, "multiplayer-host", () => { PushType(stack, "MainMenu.NMultiplayerHostSubmenu"); return Task.CompletedTask; });
        PopAll(stack);
        await AuditStepAsync(tree, "join-friend", () =>
        {
            Call(menu, "OpenMultiplayerSubmenu");
            PushType(stack, "MainMenu.NJoinFriendScreen");
            return Task.CompletedTask;
        });
        PopAll(stack);

        await AuditStepAsync(tree, "compendium", () => { Call(menu, "OpenCompendiumSubmenu"); return Task.CompletedTask; });
        await AuditStepAsync(tree, "card-library", () =>
        {
            object library = Call(stack, "GetSubmenuType", GameType(ScreensNs + "CardLibrary.NCardLibrary"))!;
            CallWith(library, null, "Initialize", [null]);
            Call(stack, "Push", library);
            return Task.CompletedTask;
        });
        Call(stack, "Pop");
        foreach (string screen in new[] { "RelicCollection.NRelicCollection", "PotionLab.NPotionLab", "Bestiary.NBestiary", "StatsScreen.NStatsScreen", "RunHistoryScreen.NRunHistory" })
        {
            string step = screen[(screen.IndexOf(".N", StringComparison.Ordinal) + 2)..].ToLowerInvariant();
            await AuditStepAsync(tree, step, () => { PushType(stack, screen); return Task.CompletedTask; });
            Call(stack, "Pop");
        }
        PopAll(stack);

        await AuditStepAsync(tree, "timeline", () => { PushType(stack, "Timeline.NTimelineScreen"); return Task.CompletedTask; });
        await AuditStepAsync(tree, "epoch-inspect", async () =>
        {
            object timeline = StaticProp(GameType(ScreensNs + "Timeline.NTimelineScreen"), "Instance")!;
            Node? slot = Descendants((Node)timeline).FirstOrDefault(n => n.GetType().Name == "NEpochSlot" && (n as CanvasItem)?.IsVisibleInTree() == true);
            if (slot == null) throw new InvalidOperationException("no visible epoch slot");
            Call(timeline, "OpenInspectScreen", slot, false);
            await WaitFramesAsync(tree, 40);
        });
        if (Field(StaticProp(GameType(ScreensNs + "Timeline.NTimelineScreen"), "Instance"), "_inspectScreen") is { } inspect)
            TryCall(inspect, "Close");
        await WaitFramesAsync(tree, 30);
        await AuditStepAsync(tree, "unlock-cards", () =>
        {
            object timeline = StaticProp(GameType(ScreensNs + "Timeline.NTimelineScreen"), "Instance")!;
            Call(timeline, "QueueCardUnlock", TypedList("MegaCrit.Sts2.Core.Models.CardModel", ModelDbList("AllCards").Take(3)));
            Call(timeline, "OpenQueuedScreen");
            return Task.CompletedTask;
        });
        CloseUnlockScreens(tree);
        await WaitFramesAsync(tree, 30);
        await AuditStepAsync(tree, "unlock-relics", () =>
        {
            object timeline = StaticProp(GameType(ScreensNs + "Timeline.NTimelineScreen"), "Instance")!;
            Call(timeline, "QueueRelicUnlock", TypedList("MegaCrit.Sts2.Core.Models.RelicModel", ModelDbList("AllRelics").Take(3)));
            Call(timeline, "OpenQueuedScreen");
            return Task.CompletedTask;
        });
        CloseUnlockScreens(tree);
        await WaitFramesAsync(tree, 30);
        await AuditStepAsync(tree, "unlock-misc", () =>
        {
            object timeline = StaticProp(GameType(ScreensNs + "Timeline.NTimelineScreen"), "Instance")!;
            Call(timeline, "QueueMiscUnlock", "A new mode is now available. This line stands in for the game's unlock text, long enough to wrap.");
            Call(timeline, "OpenQueuedScreen");
            return Task.CompletedTask;
        });
        CloseUnlockScreens(tree);
        await WaitFramesAsync(tree, 30);
        // The character unlock as the Silent's first epoch queues it, measured once its lines have faded in
        // (1.5 s after it opens, over 1 s).
        await AuditStepAsync(tree, "unlock-character", async () =>
        {
            object timeline = StaticProp(GameType(ScreensNs + "Timeline.NTimelineScreen"), "Instance")!;
            Type epochType = GameType("MegaCrit.Sts2.Core.Timeline.EpochModel");
            object epoch = CallStatic(epochType, "Get", (string)CallStatic(epochType, "GetId", GameType("MegaCrit.Sts2.Core.Timeline.Epochs.Silent1Epoch"))!)!;
            timeline.GetType().GetMethod("QueueCharacterUnlock")!.MakeGenericMethod(GameType("MegaCrit.Sts2.Core.Models.Characters.Silent"))
                .Invoke(timeline, [epoch]);
            Call(timeline, "OpenQueuedScreen");
            await WaitFramesAsync(tree, 150);
        });
        CloseUnlockScreens(tree);
        await WaitFramesAsync(tree, 70);
        await AuditStepAsync(tree, "unlock-timeline", () => OpenTimelineExpansionAsync(tree));
        PopAll(stack);

        await AuditStepAsync(tree, "profile", () => { Call(menu, "OpenProfileScreen"); return Task.CompletedTask; });
        PopAll(stack);
        await AuditStepAsync(tree, "modding", () => { PushType(stack, "ModdingScreen.NModdingScreen"); return Task.CompletedTask; });
        PopAll(stack);

        await AuditStepAsync(tree, "credits", () =>
        {
            object? credits = CallStatic(GameType(ScreensNs + "Credits.NCreditsScreen"), "Create");
            if (credits == null) throw new InvalidOperationException("credits Create returned null");
            CallWith(ModalContainer(), null, "Add", [credits, false]);
            return Task.CompletedTask;
        });
        Call(ModalContainer(), "Clear");
        await WaitFramesAsync(tree, 20);

        await AuditStepAsync(tree, "patch-notes", () => { Call(Prop(menu, "PatchNotesScreen")!, "Open"); return Task.CompletedTask; });
        TryCall(Prop(menu, "PatchNotesScreen"), "Close");
        await WaitFramesAsync(tree, 20);

        await AuditStepAsync(tree, "feedback", () => { Call(Call(game, "GetOrCreateFeedbackScreen")!, "Open"); return Task.CompletedTask; });
        TryCall(Call(game, "GetOrCreateFeedbackScreen"), "Close");
        await WaitFramesAsync(tree, 20);

        await AuditStepAsync(tree, "abandon-popup-menu", () =>
        {
            object popup = CallStatic(GameType("MegaCrit.Sts2.Core.Nodes.CommonUi.NAbandonRunConfirmPopup"), "Create", menu)!;
            Call(ModalContainer(), "Add", popup);
            return Task.CompletedTask;
        });
        Call(ModalContainer(), "Clear");
        await WaitFramesAsync(tree, 20);
    }

    private static async Task RunInRunAuditAsync(SceneTree tree, NGame game)
    {
        ClearNextStepFile();
        await EnsurePortraitAsync(tree, game);
        object player = await StartProofRunAsync(tree, game, characterIndex: 0, fivePotions: false, thirtyRelics: false, pageTwo: false, addThreePotions: true, closeRelicHover: true);
        await WaitFramesAsync(tree, 90);
        Type mapType = GameType(ScreensNs + "Map.NMapScreen");
        Call(StaticProp(mapType, "Instance")!, "Close", false);
        DismissFtues(tree, Sts2);
        await WaitFramesAsync(tree, 20);

        await AuditStepAsync(tree, "ancient", async () =>
        {
            await RunConsoleCmdAsync(Sts2, "AncientConsoleCmd", player, ["NEOW"]);
            await WaitFramesAsync(tree, 150);
        });

        object runState = Prop(player, "RunState")!;
        object submenus = Prop(Prop(StaticProp(GameType("MegaCrit.Sts2.Core.Nodes.NRun"), "Instance")!, "GlobalUi")!, "SubmenuStack")!;
        Type capstoneType = GameType(ScreensNs + "CapstoneSubmenuType");

        await AuditStepAsync(tree, "pause-menu", () =>
        {
            object pause = Call(submenus, "ShowScreen", Enum.Parse(capstoneType, "PauseMenu"))!;
            Call(pause, "Initialize", runState);
            return Task.CompletedTask;
        });
        CloseCapstone(Sts2);
        await WaitFramesAsync(tree, 20);
        foreach (string kind in new[] { "Settings", "Compendium" })
        {
            await AuditStepAsync(tree, "run-" + kind.ToLowerInvariant(), () => { Call(submenus, "ShowScreen", Enum.Parse(capstoneType, kind)); return Task.CompletedTask; });
            CloseCapstone(Sts2);
            await WaitFramesAsync(tree, 20);
        }
        await AuditStepAsync(tree, "abandon-popup-run", () =>
        {
            object popup = CallWith(null, GameType("MegaCrit.Sts2.Core.Nodes.CommonUi.NAbandonRunConfirmPopup"), "Create", [null])!;
            Call(ModalContainer(), "Add", popup);
            return Task.CompletedTask;
        });
        Call(ModalContainer(), "Clear");
        await WaitFramesAsync(tree, 20);

        // Card selections open inside a room, as in play; at the run's start the map would cover them.
        await RunConsoleCmdAsync(Sts2, "RoomConsoleCmd", player, ["RestSite"]);
        await WaitFramesAsync(tree, 120);
        DismissFtues(tree, Sts2);
        await WaitFramesAsync(tree, 20);

        // A deck big enough that grids scroll and previews hold several cards.
        string[] deckFill = ["ANGER", "ARMAMENTS", "BLOODLETTING", "BURNING_PACT", "BODY_SLAM", "CORRUPTION", "BARRICADE", "BLUDGEON", "BASH", "CLOTHESLINE"];
        for (int i = 0; DeckCount(player) < 24; i++)
            await RunConsoleCmdAsync(Sts2, "CardConsoleCmd", player, [deckFill[i % deckFill.Length], "Deck"]);
        IList deck = DeckCards(player);
        Type prefsType = GameType("MegaCrit.Sts2.Core.CardSelection.CardSelectorPrefs", "CardSelectorPrefs");
        object Prefs(string prompt, int count) => Activator.CreateInstance(prefsType, StaticProp(prefsType, prompt), count)!;
        object Prefs2(string prompt, int min, int max) => Activator.CreateInstance(prefsType, StaticProp(prefsType, prompt), min, max)!;
        object overlays = StaticProp(GameType(ScreensNs + "Overlays.NOverlayStack"), "Instance")!;
        string cs = ScreensNs + "CardSelection.";

        object? remove = null;
        await AuditStepAsync(tree, "select-remove", () =>
        {
            remove = CallStatic(GameType(cs + "NDeckCardSelectScreen"), "Create", deck, Prefs("RemoveSelectionPrompt", 1));
            Call(overlays, "Push", remove!);
            return Task.CompletedTask;
        });
        await AuditStepAsync(tree, "select-remove-preview", async () => { Call(remove!, "OnCardClicked", deck[0]!); await WaitFramesAsync(tree, 40); });
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);
        object? removeThree = null;
        await AuditStepAsync(tree, "select-remove3-preview", async () =>
        {
            removeThree = CallStatic(GameType(cs + "NDeckCardSelectScreen"), "Create", deck, Prefs("RemoveSelectionPrompt", 3));
            Call(overlays, "Push", removeThree!);
            await WaitFramesAsync(tree, 30);
            for (int i = 0; i < 3; i++) Call(removeThree!, "OnCardClicked", deck[i]!);
            await WaitFramesAsync(tree, 40);
        });
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        object? upgrade = null;
        await AuditStepAsync(tree, "select-upgrade-preview", async () =>
        {
            upgrade = CallStatic(GameType(cs + "NDeckUpgradeSelectScreen"), "ShowScreen", deck, Prefs("UpgradeSelectionPrompt", 1), runState);
            await WaitFramesAsync(tree, 30);
            Call(upgrade!, "OnCardClicked", deck[1]!);
            await WaitFramesAsync(tree, 40);
        });
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);
        await AuditStepAsync(tree, "select-upgrade2-preview", async () =>
        {
            upgrade = CallStatic(GameType(cs + "NDeckUpgradeSelectScreen"), "ShowScreen", deck, Prefs("UpgradeSelectionPrompt", 2), runState);
            await WaitFramesAsync(tree, 30);
            Call(upgrade!, "OnCardClicked", deck[1]!);
            Call(upgrade!, "OnCardClicked", deck[2]!);
            await WaitFramesAsync(tree, 40);
        });
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        await AuditStepAsync(tree, "select-transform-preview", async () =>
        {
            Type transformation = GameType("MegaCrit.Sts2.Core.Entities.Cards.CardTransformation", "CardTransformation");
            Type cardModel = GameType("MegaCrit.Sts2.Core.Models.CardModel");
            Delegate toTransform = MakeFunc(cardModel, transformation, card => Activator.CreateInstance(transformation, card)!);
            object screen = CallStatic(GameType(cs + "NDeckTransformSelectScreen"), "ShowScreen", deck, toTransform, Prefs("TransformSelectionPrompt", 1))!;
            await WaitFramesAsync(tree, 30);
            Call(screen, "OnCardClicked", deck[3]!);
            await WaitFramesAsync(tree, 40);
        });
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        await AuditStepAsync(tree, "select-enchant-preview", async () =>
        {
            object enchantment = ModelDbList("DebugEnchantments").FirstOrDefault()
                ?? throw new InvalidOperationException("no enchantment models");
            object screen = CallStatic(GameType(cs + "NDeckEnchantSelectScreen"), "ShowScreen", deck, enchantment, 1, Prefs("EnchantSelectionPrompt", 1))!;
            await WaitFramesAsync(tree, 30);
            Call(screen, "OnCardClicked", deck[0]!);
            await WaitFramesAsync(tree, 40);
        });
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        await AuditStepAsync(tree, "select-simple", () =>
        {
            object screen = CallStatic(GameType(cs + "NSimpleCardSelectScreen"), "Create", TypedList("MegaCrit.Sts2.Core.Models.CardModel", deck.Cast<object>().Take(12)), Prefs2("ExhaustSelectionPrompt", 0, 2))!;
            Call(overlays, "Push", screen);
            return Task.CompletedTask;
        });
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        await AuditStepAsync(tree, "choose-a-card", () =>
        {
            CallStatic(GameType(cs + "NChooseACardSelectionScreen"), "ShowScreen", TypedList("MegaCrit.Sts2.Core.Models.CardModel", deck.Cast<object>().Skip(4).Take(3)), true);
            return Task.CompletedTask;
        });
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        await AuditStepAsync(tree, "choose-a-bundle", () =>
        {
            Type cardModel = GameType("MegaCrit.Sts2.Core.Models.CardModel");
            Type inner = typeof(IReadOnlyList<>).MakeGenericType(cardModel);
            IList bundles = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(inner))!;
            for (int b = 0; b < 3; b++) bundles.Add(TypedList(cardModel.FullName!, deck.Cast<object>().Skip(b * 3).Take(3)));
            CallStatic(GameType(cs + "NChooseABundleSelectionScreen"), "ShowScreen", bundles, true);
            return Task.CompletedTask;
        });
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        await AuditStepAsync(tree, "choose-a-relic", () =>
        {
            CallStatic(GameType(ScreensNs + "NChooseARelicSelection"), "ShowScreen",
                TypedList("MegaCrit.Sts2.Core.Models.RelicModel", ModelDbList("AllRelics").Skip(10).Take(3).Select(r => Call(r, "ToMutable")!)));
            return Task.CompletedTask;
        });
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        await AuditStepAsync(tree, "inspect-card", () =>
        {
            IList cards = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(GameType("MegaCrit.Sts2.Core.Models.CardModel")))!;
            foreach (object card in deck) cards.Add(card);
            Call(Call(game, "GetInspectCardScreen")!, "Open", cards, 2, false);
            return Task.CompletedTask;
        });
        TryCall(Call(game, "GetInspectCardScreen"), "Close");
        await WaitFramesAsync(tree, 20);

        await AuditStepAsync(tree, "inspect-relic", () =>
        {
            object relics = Prop(player, "Relics")!;
            object first = ((IEnumerable)relics).Cast<object>().First();
            Call(Call(game, "GetInspectRelicScreen")!, "Open", relics, first);
            return Task.CompletedTask;
        });
        TryCall(Call(game, "GetInspectRelicScreen"), "Close");
        await WaitFramesAsync(tree, 20);

        // Combat: the draw, discard and exhaust piles, and a pick from a combat pile.
        await EnterEncounterAsync(Sts2, tree, player, "NIBBITS_NORMAL", 42UL);
        await AuditStepAsync(tree, "combat", null);
        // Hover tips: the rightmost hand card, an enemy and the first relic, each under a pointer as a long press would be.
        foreach ((string step, string typeName, bool last) in new[] { ("hover-hand-card", "NHandCardHolder", true), ("hover-enemy", "NCreature", true), ("hover-relic", "NRelicInventoryHolder", false) })
        {
            await AuditStepAsync(tree, step, async () =>
            {
                var nodes = Descendants(tree.Root).OfType<Control>().Where(n => n.GetType().Name == typeName && n.IsVisibleInTree()).ToList();
                if (nodes.Count == 0) throw new InvalidOperationException("nothing to hover: " + typeName);
                Control target = last ? nodes.OrderBy(n => n.GetGlobalRect().GetCenter().X).Last() : nodes[0];
                Vector2 at = tree.Root.GetFinalTransform() * target.GetGlobalRect().GetCenter();
                SendMouseMotion(at, Vector2.Zero, leftMask: false);
                await WaitFramesAsync(tree, 40);
            });
        }
        SendMouseMotion(tree.Root.GetFinalTransform() * new Vector2(540f, 900f), Vector2.Zero, leftMask: false);
        await WaitFramesAsync(tree, 20);
        Type pileTypeEnum = GameType("MegaCrit.Sts2.Core.Entities.Cards.PileType");
        Type pileExt = GameType("MegaCrit.Sts2.Core.Entities.Cards.PileTypeExtensions", "PileTypeExtensions");
        object Pile(string kind) => CallWith(null, pileExt, "GetPile", [Enum.Parse(pileTypeEnum, kind), player])!;
        await ExhaustCardsForProofAsync(Sts2, player, 2);
        foreach (string kind in new[] { "Draw", "Discard", "Exhaust" })
        {
            await AuditStepAsync(tree, "pile-" + kind.ToLowerInvariant(), () =>
            {
                CallStatic(GameType(ScreensNs + "NCardPileScreen"), "ShowScreen", Pile(kind), Array.Empty<string>());
                return Task.CompletedTask;
            });
            CloseCapstone(Sts2);
            await WaitFramesAsync(tree, 20);
        }
        await AuditStepAsync(tree, "select-from-hand", () =>
        {
            Type ctxType = GameType("MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext", "ThrowingPlayerChoiceContext");
            object ctx = Activator.CreateInstance(ctxType)!;
            // Not awaited: it waits for the player's pick; winning the fight below ends it.
            _ = CallWith(null, GameType("MegaCrit.Sts2.Core.Commands.CardSelectCmd", "CardSelectCmd"), "FromHand",
                [ctx, player, Prefs2("DiscardSelectionPrompt", 1, 2), null, deck[0]]);
            return Task.CompletedTask;
        });
        await AuditStepAsync(tree, "select-from-hand-picked", async () =>
        {
            Node? holder = Descendants(tree.Root).FirstOrDefault(n => n.GetType().Name == "NHandCardHolder" && (n as CanvasItem)?.IsVisibleInTree() == true);
            Node? hand = Descendants(tree.Root).FirstOrDefault(n => n.GetType().Name == "NPlayerHand");
            if (holder == null || hand == null) throw new InvalidOperationException("no hand card");
            object? card = Prop(holder, "CardModel") ?? Prop(Prop(holder, "CardNode") ?? holder, "Model");
            MethodInfo? select = hand.GetType().GetMethods(AnyInstance).FirstOrDefault(m => m.Name.Contains("SelectCard", StringComparison.Ordinal) && m.GetParameters().Length == 1);
            if (select == null) throw new InvalidOperationException("no hand select method");
            select.Invoke(hand, [select.GetParameters()[0].ParameterType.IsInstanceOfType(holder) ? holder : card]);
            await WaitFramesAsync(tree, 30);
        });
        await AuditStepAsync(tree, "select-combat-pile", () =>
        {
            object screen = CallWith(null, GameType(cs + "NCombatPileCardSelectScreen"), "Create", [Pile("Draw"), Prefs("DiscardSelectionPrompt", 1), null])!;
            Call(overlays, "Push", screen);
            return Task.CompletedTask;
        });
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);
        await RunConsoleCmdAsync(Sts2, "WinConsoleCmd", player, []);
        await WaitFramesAsync(tree, 120);
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        await AuditStepAsync(tree, "crystal-sphere", () =>
        {
            Type rng = GameType("MegaCrit.Sts2.Core.Random.Rng");
            Type minigameType = GameType("MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame", "CrystalSphereMinigame");
            object minigame = Activator.CreateInstance(minigameType, player, Activator.CreateInstance(rng, 7UL), 3)!;
            CallStatic(GameType("MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen"), "ShowScreen", minigame);
            return Task.CompletedTask;
        });
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        await AuditStepAsync(tree, "event-fake-merchant", async () =>
        {
            await RunConsoleCmdAsync(Sts2, "EventConsoleCmd", player, ["FAKE_MERCHANT"]);
            await WaitFramesAsync(tree, 120);
            DismissFtues(tree, Sts2);
        });
        await AuditStepAsync(tree, "fake-merchant-inventory", async () =>
        {
            Node? merchant = Descendants(tree.Root).FirstOrDefault(n => n.GetType().Name == "NFakeMerchant");
            if (merchant == null) throw new InvalidOperationException("no fake merchant");
            CallWith(merchant, null, "OpenInventory", []);
            await WaitFramesAsync(tree, 60);
        });
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);
        await AuditStepAsync(tree, "event-crystal-sphere", async () =>
        {
            await RunConsoleCmdAsync(Sts2, "EventConsoleCmd", player, ["CRYSTAL_SPHERE"]);
            await WaitFramesAsync(tree, 120);
        });

        await AuditStepAsync(tree, "treasure-ftue", async () =>
        {
            await RunConsoleCmdAsync(Sts2, "RoomConsoleCmd", player, ["Treasure"]);
            await WaitFramesAsync(tree, 120);
            Node? room = Descendants(tree.Root).FirstOrDefault(n => n.GetType().Name == "NTreasureRoom");
            if (room != null) CallWith(room, null, "OnChestButtonReleased", [null]);
            await WaitFramesAsync(tree, 120);
        });
        await AuditStepAsync(tree, "treasure-open", async () =>
        {
            DismissFtues(tree, Sts2);
            await WaitFramesAsync(tree, 30);
        });

        await AuditStepAsync(tree, "rest-smith", async () =>
        {
            await RunConsoleCmdAsync(Sts2, "RoomConsoleCmd", player, ["RestSite"]);
            await WaitFramesAsync(tree, 120);
            DismissFtues(tree, Sts2);
            await WaitFramesAsync(tree, 20);
        });

        await AuditStepAsync(tree, "game-over-death", async () =>
        {
            await RunConsoleCmdAsync(Sts2, "DieConsoleCmd", player, []);
            await WaitFramesAsync(tree, 240);
        });
        await AuditStepAsync(tree, "game-over-death-summary", async () => await ClickGameOverContinueAsync(tree));
        await AuditStepAsync(tree, "game-over-death-buttons", async () => { await WaitFramesAsync(tree, 240); ShowGameOverButtons(tree); await WaitFramesAsync(tree, 60); });
    }

    /// <summary>
    /// Lays the reflowed screens out in portrait, turns Portrait Mode off live, and opens them again in landscape: each must be
    /// back to its desktop layout. Then the same for the pause menu in a run.
    /// </summary>
    private static async Task RunLandscapeRestoreAuditAsync(SceneTree tree, NGame game)
    {
        ClearNextStepFile();
        await EnsurePortraitAsync(tree, game);
        object menu = game.MainMenu!;
        object stack = Prop(menu, "SubmenuStack")!;
        async Task OpenAll(string prefix)
        {
            await AuditStepAsync(tree, prefix + "-singleplayer", () => { Call(menu, "OpenSingleplayerSubmenu"); return Task.CompletedTask; });
            await AuditStepAsync(tree, prefix + "-characterselect", () =>
            {
                object submenu = Call(stack, "GetSubmenuType", GameType(ScreensNs + "CharacterSelect.NCharacterSelectScreen"))!;
                Call(submenu, "InitializeSingleplayer");
                Call(stack, "Push", submenu);
                return Task.CompletedTask;
            });
            PopAll(stack);
            await AuditStepAsync(tree, prefix + "-compendium", () => { Call(menu, "OpenCompendiumSubmenu"); return Task.CompletedTask; });
            PopAll(stack);
            await AuditStepAsync(tree, prefix + "-multiplayer-host", () =>
            {
                Call(menu, "OpenMultiplayerSubmenu");
                PushType(stack, "MainMenu.NMultiplayerHostSubmenu");
                return Task.CompletedTask;
            });
            PopAll(stack);
        }
        await OpenAll("portrait");
        await OpenGraphicsSettingsAsync(tree, game);
        await StepPaginatorAsync(tree, "PhonePortraitMode", 0, expectPortrait: false);
        PopAll(stack);
        await WaitFramesAsync(tree, 30);
        await OpenAll("landscape");

        // In a run: pause menu laid out in portrait, then Portrait Mode off live, then the pause menu again.
        await EnsurePortraitAsync(tree, game);
        object player = await StartProofRunAsync(tree, game, characterIndex: 0, fivePotions: false, thirtyRelics: false, pageTwo: false, addThreePotions: true, closeRelicHover: true);
        await WaitFramesAsync(tree, 90);
        await RunConsoleCmdAsync(Sts2, "RoomConsoleCmd", player, ["RestSite"]);
        await WaitFramesAsync(tree, 120);
        DismissFtues(tree, Sts2);
        object runState = Prop(player, "RunState")!;
        object submenus = Prop(Prop(StaticProp(GameType("MegaCrit.Sts2.Core.Nodes.NRun"), "Instance")!, "GlobalUi")!, "SubmenuStack")!;
        Type capstoneType = GameType(ScreensNs + "CapstoneSubmenuType");
        await AuditStepAsync(tree, "portrait-pause", () =>
        {
            object pause = Call(submenus, "ShowScreen", Enum.Parse(capstoneType, "PauseMenu"))!;
            Call(pause, "Initialize", runState);
            return Task.CompletedTask;
        });
        CloseCapstone(Sts2);
        await WaitFramesAsync(tree, 20);
        await TogglePortraitOffMidRunAsync(tree, Sts2);
        await WaitFramesAsync(tree, 50);
        await AuditStepAsync(tree, "landscape-pause", () =>
        {
            object pause = Call(submenus, "ShowScreen", Enum.Parse(capstoneType, "PauseMenu"))!;
            Call(pause, "Initialize", runState);
            return Task.CompletedTask;
        });
        CloseCapstone(Sts2);
        await WaitFramesAsync(tree, 20);
        await AuditStepAsync(tree, "landscape-rest", null);
    }

    private static async Task RunVictoryAuditAsync(SceneTree tree, NGame game)
    {
        ClearNextStepFile();
        await EnsurePortraitAsync(tree, game);
        object player = await StartProofRunAsync(tree, game, characterIndex: 0, fivePotions: false, thirtyRelics: false, pageTwo: false, addThreePotions: true, closeRelicHover: true);
        await WaitFramesAsync(tree, 90);
        await AuditStepAsync(tree, "game-over-victory", async () =>
        {
            await RunConsoleCmdAsync(Sts2, "WinConsoleCmd", player, ["run"]);
            await WaitFramesAsync(tree, 240);
        });
        await AuditStepAsync(tree, "game-over-victory-later", async () => await WaitFramesAsync(tree, 300));
        await AuditStepAsync(tree, "game-over-victory-summary", async () => await ClickGameOverContinueAsync(tree));
        await AuditStepAsync(tree, "game-over-victory-buttons", async () => { await WaitFramesAsync(tree, 240); ShowGameOverButtons(tree); await WaitFramesAsync(tree, 60); });
    }

    /// <summary>
    /// Proof runs start with no character stats saved, so the game over summary stops at its badges (KeyNotFoundException for
    /// the character) before it shows its buttons. Show them as the summary's end would, to check where they sit.
    /// </summary>
    private static void ShowGameOverButtons(SceneTree tree)
    {
        Node? screen = Descendants(tree.Root).FirstOrDefault(n => n.GetType().Name == "NGameOverScreen");
        if (screen == null) throw new InvalidOperationException("no game over screen");
        // The game shows only the main menu button here; the view run button stays hidden and disabled in this game version.
        foreach (string field in new[] { "_mainMenuButton" })
        {
            if (Field(screen, field) is not CanvasItem button) continue;
            button.Visible = true;
            TryCall(button, "Enable");
        }
    }

    private static async Task ClickGameOverContinueAsync(SceneTree tree)
    {
        Node? button = Descendants(tree.Root).FirstOrDefault(n => n.GetType().Name == "NGameOverContinueButton" && (n as CanvasItem)?.IsVisibleInTree() == true);
        if (button == null) throw new InvalidOperationException("no continue button");
        button.Call("ForceClick");
        await WaitFramesAsync(tree, 120);
    }

    private static async Task EnsurePortraitAsync(SceneTree tree, NGame game)
    {
        if (STS2Mobile.Platform.SafeAreaCanvas.IsPortrait) return;
        await OpenGraphicsSettingsAsync(tree, game);
        await StepPaginatorAsync(tree, "PhonePortraitMode", 1, expectPortrait: true);
        PopAll(Prop(game.MainMenu!, "SubmenuStack")!);
        await WaitFramesAsync(tree, 25);
    }

    private static async Task AuditStepAsync(SceneTree tree, string step, Func<Task>? open)
    {
        string error = "";
        if (open != null)
        {
            try
            {
                await open();
                await WaitFramesAsync(tree, 45);
            }
            catch (Exception e)
            {
                Exception inner = e is TargetInvocationException { InnerException: { } ie } ? ie : e;
                error = $" error={inner.GetType().Name}: {inner.Message.Replace('\n', ' ')}";
            }
        }
        Vector2I win = DisplayServer.WindowGetSize();
        string ftues = string.Join(",", Descendants(tree.Root).Where(n => n.GetType().Namespace == "MegaCrit.Sts2.Core.Nodes.Ftue" && (n as CanvasItem)?.IsVisibleInTree() == true).Select(n => n.GetType().Name));
        GD.Print($"Portrait proof step: {step} window={win.X}x{win.Y} portrait={STS2Mobile.Platform.SafeAreaCanvas.IsPortrait}{error}{(ftues.Length > 0 ? " ftue=" + ftues : "")} {PortraitAudit.Describe(tree.Root)}");
        GD.Print($"Portrait small: {step} {PortraitAudit.DescribeSmall(tree.Root)}");
        await WaitForHostStepAckAsync(tree, step);
    }

    /// <summary>
    /// The timeline unlock: what an epoch's <c>QueueTimelineExpansion</c> queues, with up to two epochs
    /// the timeline does not show yet (from new eras first, so it grows), as slots not obtained. Only the timeline's nodes
    /// change; the save does not (the epoch's own call, not used here, is what writes it). The screen shows nothing of its
    /// own: the timeline grows, and the screen closes when that ends, which is when the step measures.
    /// </summary>
    private static async Task OpenTimelineExpansionAsync(SceneTree tree)
    {
        object timeline = StaticProp(GameType(ScreensNs + "Timeline.NTimelineScreen"), "Instance")!;
        var shown = new HashSet<string>(Descendants((Node)timeline).Where(n => n.GetType().Name == "NEpochSlot")
            .Select(n => Field(n, "model")).OfType<object>().Select(model => (string)Prop(model, "Id")!));
        var eras = (IDictionary)Field(timeline, "_uniqueEpochEras")!;
        Type epochType = GameType("MegaCrit.Sts2.Core.Timeline.EpochModel");
        List<object> unseen = ((IEnumerable)StaticProp(epochType, "AllEpochIds")!).Cast<string>().Where(id => !shown.Contains(id))
            .Select(id => CallStatic(epochType, "Get", id)!).ToList();
        List<object> picks = unseen.Where(epoch => !eras.Contains(Prop(epoch, "Era")!)).GroupBy(epoch => Prop(epoch, "Era"))
            .Select(era => era.First()).Take(2).ToList();
        if (picks.Count == 0) picks = unseen.Take(2).ToList();
        if (picks.Count == 0) throw new InvalidOperationException("every epoch is on the timeline already");
        Type state = GameType(ScreensNs + "Timeline.EpochSlotState", "EpochSlotState");
        const string slotData = "MegaCrit.Sts2.Core.Timeline.EpochSlotData";
        IList slots = TypedList(slotData, picks.Select(epoch => Activator.CreateInstance(GameType(slotData), epoch, Enum.Parse(state, "NotObtained"))!));
        GD.Print($"Portrait audit: timeline expansion with {string.Join(",", picks.Select(epoch => Prop(epoch, "Id")))}");
        Call(timeline, "QueueTimelineExpansion", slots);
        Call(timeline, "OpenQueuedScreen");
        for (int i = 0; i < 600 && Descendants(tree.Root).Any(n => n.GetType().Name == "NUnlockTimelineScreen"); i += 10)
            await WaitFramesAsync(tree, 10);
        await WaitFramesAsync(tree, 30);
    }

    private static void CloseUnlockScreens(SceneTree tree)
    {
        foreach (Node screen in Descendants(tree.Root).Where(n => n.GetType().Namespace == ScreensNs + "Timeline.UnlockScreens").ToList())
        {
            if (screen.GetNodeOrNull("ConfirmButton") is { } confirm) confirm.EmitSignal("Released", confirm);
            else TryCall(screen, "Close");
        }
    }

    // ---- reflection helpers ---------------------------------------------------------------------------------------------

    private static Type GameType(string fullName, string? shortName = null) =>
        Sts2.GetType(fullName) ?? (shortName != null ? Sts2.GetTypes().FirstOrDefault(t => t.Name == shortName) : null)
        ?? throw new InvalidOperationException("missing type " + fullName);

    private static object ModalContainer() => StaticProp(GameType("MegaCrit.Sts2.Core.Nodes.CommonUi.NModalContainer"), "Instance")!;

    private static void PushType(object stack, string screen) => Call(stack, "PushSubmenuType", GameType(ScreensNs + screen));

    private static void PopAll(object stack)
    {
        for (int i = 0; i < 8 && Prop(stack, "SubmenusOpen") is true; i++) Call(stack, "Pop");
        TryCall(ModalContainer(), "Clear");
    }

    private static IEnumerable<object> ModelDbList(string property) =>
        ((IEnumerable)StaticProp(GameType("MegaCrit.Sts2.Core.Models.ModelDb"), property)!).Cast<object>();

    private static IList DeckCards(object player)
    {
        IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(GameType("MegaCrit.Sts2.Core.Models.CardModel")))!;
        foreach (object card in (IEnumerable)Prop(Prop(player, "Deck")!, "Cards")!) list.Add(card);
        return list;
    }

    private static IList TypedList(string elementType, IEnumerable<object> items)
    {
        IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(GameType(elementType)))!;
        foreach (object item in items) list.Add(item);
        return list;
    }

    private static Delegate MakeFunc(Type argType, Type returnType, Func<object, object> body)
    {
        var parameter = System.Linq.Expressions.Expression.Parameter(argType, "x");
        var call = System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(body),
            System.Linq.Expressions.Expression.Convert(parameter, typeof(object)));
        return System.Linq.Expressions.Expression.Lambda(typeof(Func<,>).MakeGenericType(argType, returnType),
            System.Linq.Expressions.Expression.Convert(call, returnType), parameter).Compile();
    }

    private static object? Prop(object target, string name) =>
        target.GetType().GetProperty(name, AnyInstance)?.GetValue(target) ?? Field(target, name);

    private static object? Field(object? target, string name) =>
        target?.GetType().GetField(name, AnyInstance)?.GetValue(target);

    private static object? StaticProp(Type type, string name) =>
        type.GetProperty(name, AnyStatic)?.GetValue(null) ?? type.GetField(name, AnyStatic)?.GetValue(null);

    private static object? Call(object target, string name, params object?[] args) => CallWith(target, null, name, args);

    private static object? CallStatic(Type type, string name, params object?[] args) => CallWith(null, type, name, args);

    private static void TryCall(object? target, string name)
    {
        if (target == null) return;
        try { Call(target, name); }
        catch (Exception e) { GD.Print($"Portrait audit: {target.GetType().Name}.{name} failed: {(e.InnerException ?? e).Message}"); }
    }

    /// <summary>Calls the method whose parameters accept these arguments (nulls match any reference type), filling defaults.</summary>
    private static object? CallWith(object? target, Type? type, string name, object?[] args)
    {
        type ??= target!.GetType();
        BindingFlags flags = target == null ? AnyStatic : AnyInstance;
        for (Type? t = type; t != null; t = t.BaseType)
        {
            foreach (MethodInfo method in t.GetMethods(flags | BindingFlags.DeclaredOnly).Where(m => m.Name == name && !m.IsGenericMethodDefinition))
            {
                ParameterInfo[] ps = method.GetParameters();
                if (args.Length > ps.Length || ps.Skip(args.Length).Any(p => !p.HasDefaultValue)) continue;
                bool fits = true;
                for (int i = 0; i < args.Length && fits; i++)
                {
                    Type pt = ps[i].ParameterType;
                    fits = args[i] == null ? !pt.IsValueType || Nullable.GetUnderlyingType(pt) != null : pt.IsInstanceOfType(args[i]);
                }
                if (!fits) continue;
                object?[] full = args.Concat(ps.Skip(args.Length).Select(p => p.DefaultValue)).ToArray();
                return method.Invoke(target, full);
            }
        }
        throw new MissingMethodException(type.Name, name + "/" + args.Length);
    }
}
