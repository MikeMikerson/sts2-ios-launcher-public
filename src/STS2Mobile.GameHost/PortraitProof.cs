using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.GameHost;

/// <summary>
/// <c>STS2_PORTRAIT_PROOF=&lt;state&gt;</c> opens a requested portrait/landscape UI or combat state directly for unattended simulator
/// verification. Never touches saved runs (<c>shouldSave: false</c>) and runs nothing unless set in the environment.
/// </summary>
internal static partial class PortraitProof
{
    private const string NextStepSignalPath = "user://proof-next.txt";

    public static async Task RunIfRequestedAsync(SceneTree tree, NGame game)
    {
        string mode = OS.GetEnvironment("STS2_PORTRAIT_PROOF");
        if (mode.Length == 0) return;
        try
        {
            await DismissEarlyAccessIfPresentAsync(tree);
            switch (mode)
            {
                case "settings-off":
                    await OpenGraphicsSettingsAsync(tree, game);
                    break;
                case "settings-live-on":
                    await OpenGraphicsSettingsAsync(tree, game);
                    await StepPaginatorAsync(tree, "PhonePortraitMode", 1, expectPortrait: true);
                    break;
                case "settings-default-on":
                    await OpenGraphicsSettingsAsync(tree, game);
                    await StepPaginatorAsync(tree, "PhonePortraitMode", 1, expectPortrait: true);
                    await StepPaginatorAsync(tree, "PhoneSetPortraitasDefault", 1, expectPortrait: null);
                    break;
                case "relaunch-portrait":
                    await WaitFramesAsync(tree, 45);
                    break;
                case "settings-live-off":
                    await OpenGraphicsSettingsAsync(tree, game);
                    await StepPaginatorAsync(tree, "PhonePortraitMode", 0, expectPortrait: false);
                    break;
                case "run-potion-5":
                    await StartProofRunAsync(tree, game, characterIndex: 0, fivePotions: true, thirtyRelics: false, pageTwo: false);
                    break;
                case "run-relics-30":
                    await StartProofRunAsync(tree, game, characterIndex: 0, fivePotions: false, thirtyRelics: true, pageTwo: false);
                    break;
                case "run-relics-30-page2":
                    await StartProofRunAsync(tree, game, characterIndex: 0, fivePotions: false, thirtyRelics: true, pageTwo: true);
                    break;
                case "combat-suite":
                    await RunCombatProofSuiteAsync(tree, game);
                    break;
                case "between-suite":
                    await RunBetweenFightProofSuiteAsync(tree, game);
                    break;
                case "audit-menus":
                    await RunMenuAuditAsync(tree, game);
                    break;
                case "audit-run":
                    await RunInRunAuditAsync(tree, game);
                    break;
                case "audit-landscape":
                    await RunLandscapeRestoreAuditAsync(tree, game);
                    break;
                case "audit-victory":
                    await RunVictoryAuditAsync(tree, game);
                    break;
                case "touch-suite":
                    await RunTouchSuiteAsync(tree, game);
                    break;
                case "touch-rewards":
                    await RunTouchRewardsAsync(tree, game);
                    break;
                case "touch-choices":
                    await RunTouchChoicesAsync(tree, game);
                    break;
                case "stock-landscape":
                    await RunStockLandscapeAsync(tree, game);
                    break;
                case "switch-mid-choice":
                    await RunSwitchMidChoiceAsync(tree, game);
                    break;
                case "map-acts":
                    await RunMapActsAsync(tree, game);
                    break;
                case "phone-notes":
                    await RunPhoneNotesAsync(tree, game);
                    break;
                default:
                    GD.Print($"Portrait proof unknown mode: {mode}");
                    return;
            }
            Vector2I win = DisplayServer.WindowGetSize();
            GD.Print($"Portrait proof ready: {mode} window={win.X}x{win.Y} canvas={tree.Root.ContentScaleSize.X}x{tree.Root.ContentScaleSize.Y}");
        }
        catch (Exception error)
        {
            GD.Print($"Portrait proof failed ({mode}): {error}");
        }
    }

    private static async Task RunCombatProofSuiteAsync(SceneTree tree, NGame game)
    {
        ClearNextStepFile();

        // Ensure portrait mode is active before starting the combat run if not launched with portrait default.
        if (!STS2Mobile.Platform.SafeAreaCanvas.IsPortrait)
        {
            await OpenGraphicsSettingsAsync(tree, game);
            await StepPaginatorAsync(tree, "PhonePortraitMode", 1, expectPortrait: true);
            Node? backBtn = Descendants(tree.Root).FirstOrDefault(n =>
                n.Name == "BackButton" && (n as CanvasItem)?.IsVisibleInTree() == true);
            backBtn?.Call("ForceClick");
            await WaitFramesAsync(tree, 25);
        }

        Assembly sts2 = typeof(NGame).Assembly;

        // 1. Ironclad run with 30 relics and 3 potions in Overgrowth 2-enemy combat.
        object ironcladPlayer = await StartProofRunAsync(
            tree, game, characterIndex: 0, fivePotions: false, thirtyRelics: true, pageTwo: false, addThreePotions: true, closeRelicHover: true);

        await EnterEncounterAsync(sts2, tree, ironcladPlayer, "NIBBITS_NORMAL", 42UL);

        // Exhaust 4 cards through CardCmd.Exhaust so NExhaustPileButton animates into the mid-bar next to Energy.
        await ExhaustCardsForProofAsync(sts2, ironcladPlayer, 4);

        // Clear initial 5-card hand and build the exact 10-card 5+5 Theater Fan with 2/3 Energy affordability contrast.
        await ClearHandAsync(sts2, ironcladPlayer);
        string[] tenCardIds =
        [
            "ANGER", "ARMAMENTS", "BLOODLETTING", "BURNING_PACT", "BODY_SLAM",
            "DEFEND_IRONCLAD", "BASH", "CORRUPTION", "BARRICADE", "BLUDGEON",
        ];
        foreach (string cardId in tenCardIds)
            await RunConsoleCmdAsync(sts2, "CardConsoleCmd", ironcladPlayer, [cardId, "Hand"]);

        await SetPlayerEnergyAsync(sts2, ironcladPlayer, 2m);
        await RunConsoleCmdAsync(sts2, "BlockConsoleCmd", ironcladPlayer, ["8"]);
        await RunConsoleCmdAsync(sts2, "ApplyPowerConsoleCmd", ironcladPlayer, ["STRENGTH_POWER", "2", "0"]);
        await WaitFramesAsync(tree, 55);

        LogCombatSnapshot(tree, "combat-10-cards");
        await WaitForHostStepAckAsync(tree, "combat-10-cards");

        // 2. Collapse from 6 cards (2 rows: 3+3) down to 5 cards (1 row Classic Fan) by playing a card!
        await TrimHandToAsync(sts2, ironcladPlayer, [
            "ARMAMENTS", "BODY_SLAM", "STRIKE_IRONCLAD", "BASH", "BARRICADE", "BLUDGEON"
        ]);
        await SetPlayerEnergyAsync(sts2, ironcladPlayer, 2m);
        await WaitFramesAsync(tree, 45);
        LogCombatSnapshot(tree, "combat-6-cards-before-collapse");

        // Drag-play STRIKE_IRONCLAD onto Enemy 0 so hand shrinks 6 -> 5 (collapsing 2 rows -> 1 row) and Energy drops 2/3 -> 1/3.
        await DragCardOntoEnemyAsync(tree, sts2, cardIndexInHand: 2, releaseToPlay: true);
        await WaitFramesAsync(tree, 60);
        LogCombatSnapshot(tree, "combat-5-cards");
        await WaitForHostStepAckAsync(tree, "combat-5-cards");

        // 3. Touch drag single-target BASH onto Enemy 0 and hold to capture live NTargetingArrow + selection reticle, then release!
        await SetPlayerEnergyAsync(sts2, ironcladPlayer, 2m);
        await WaitFramesAsync(tree, 25);
        int bashIdx = FindHandCardIndexById(tree, sts2, "BASH");
        if (bashIdx < 0) bashIdx = 0;

        int hpBefore = GetEnemyHp(sts2, enemyIndex: 0);
        Vector2 releaseWinPos = await DragCardOntoEnemyAsync(tree, sts2, bashIdx, releaseToPlay: false);
        await WaitFramesAsync(tree, 25);
        LogCombatSnapshot(tree, "combat-targeting-arrow");
        await WaitForHostStepAckAsync(tree, "combat-targeting-arrow");

        SendMouseButton(releaseWinPos, pressed: false);
        await WaitFramesAsync(tree, 60);
        int hpAfter = GetEnemyHp(sts2, enemyIndex: 0);
        GD.Print($"Portrait proof targeting play result: enemy0HpBefore={hpBefore} enemy0HpAfter={hpAfter}");

        // 4. Crowded 5-enemy + Osty + 5 Defect orbs + 6 powers stress test on Necrobinder.
        await (Task)typeof(NGame).GetMethod("ReturnToMainMenu")!.Invoke(game, null)!;
        await WaitFramesAsync(tree, 25);
        await DismissEarlyAccessIfPresentAsync(tree);

        int necroIndex = FindCharacterIndex(sts2, "Necrobinder");
        object necroPlayer = await StartProofRunAsync(
            tree, game, characterIndex: necroIndex, fivePotions: false, thirtyRelics: false, pageTwo: false, addThreePotions: true, closeRelicHover: true);

        await EnterEncounterAsync(sts2, tree, necroPlayer, "TWO_TAILED_RATS_NORMAL", 42UL);
        await SpawnExtraRatsUpToFiveAsync(sts2);
        await SummonOstyAndChannelOrbsAsync(sts2, necroPlayer);
        await RunConsoleCmdAsync(sts2, "BlockConsoleCmd", necroPlayer, ["14"]);
        foreach (string powerId in new[] { "STRENGTH_POWER", "DEXTERITY_POWER", "THORNS_POWER", "ARTIFACT_POWER", "BLUR_POWER", "BARRICADE_POWER" })
            await RunConsoleCmdAsync(sts2, "ApplyPowerConsoleCmd", necroPlayer, [powerId, "3", "0"]);

        // Ensure 7 cards in hand (4+3 two-row Theater Fan).
        while (CountHandHolders(tree) < 7)
            await RunConsoleCmdAsync(sts2, "CardConsoleCmd", necroPlayer, ["DEFEND_NECROBINDER", "Hand"]);
        await SetPlayerEnergyAsync(sts2, necroPlayer, 3m);
        await WaitFramesAsync(tree, 60);

        LogCombatSnapshot(tree, "combat-5-enemies");
        await WaitForHostStepAckAsync(tree, "combat-5-enemies");

        // 5. Live toggle Portrait Mode -> Off mid-fight via TopBar Pause -> Settings -> Graphics -> Portrait Mode Off -> Resume.
        await TogglePortraitOffMidRunAsync(tree, sts2);
        await WaitFramesAsync(tree, 50);
        LogCombatSnapshot(tree, "combat-live-landscape");
        await WaitForHostStepAckAsync(tree, "combat-live-landscape");
    }

    /// <summary>
    /// Walks one portrait run through every between-fight screen (start-of-run ancient, map with boss, fight, spoils,
    /// 3- and 4-card rewards, master deck, shop and its inventory, event, rest site, treasure) and then turns Portrait Mode off
    /// live. Each step logs "Portrait proof step: name" and waits for the host's acknowledgement file so it can take a screenshot.
    /// </summary>
    private static async Task RunBetweenFightProofSuiteAsync(SceneTree tree, NGame game)
    {
        ClearNextStepFile();
        GD.Print($"Portrait proof between-suite: launched portrait={STS2Mobile.Platform.SafeAreaCanvas.IsPortrait} ack={ProjectSettings.GlobalizePath(NextStepSignalPath)}");
        if (!STS2Mobile.Platform.SafeAreaCanvas.IsPortrait)
        {
            await OpenGraphicsSettingsAsync(tree, game);
            await StepPaginatorAsync(tree, "PhonePortraitMode", 1, expectPortrait: true);
            Node? backBtn = Descendants(tree.Root).FirstOrDefault(n =>
                n.Name == "BackButton" && (n as CanvasItem)?.IsVisibleInTree() == true);
            backBtn?.Call("ForceClick");
            await WaitFramesAsync(tree, 25);
        }

        Assembly sts2 = typeof(NGame).Assembly;
        await BetweenStepAsync(tree, "main-menu");

        object player = await StartProofRunAsync(
            tree, game, characterIndex: 0, fivePotions: false, thirtyRelics: false, pageTwo: false, addThreePotions: true, closeRelicHover: true);
        await WaitFramesAsync(tree, 90);
        Type mapType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen")!;
        object? startMap = mapType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        mapType.GetMethod("Close")?.Invoke(startMap, [false]);
        DismissFtues(tree, sts2);
        await WaitFramesAsync(tree, 20);
        await RunConsoleCmdAsync(sts2, "AncientConsoleCmd", player, ["NEOW"]);
        await WaitFramesAsync(tree, 150);
        await BetweenStepAsync(tree, "ancient");

        // Map at the current row, then scrolled to the top so the boss medallion shows.
        object? map = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen")!
            .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        map?.GetType().GetMethod("Open")!.Invoke(map, [false]);
        await WaitFramesAsync(tree, 120);
        await BetweenStepAsync(tree, "map");
        DismissFtues(tree, sts2);
        await WaitFramesAsync(tree, 20);
        if (map is Control mapControl && mapControl.GetNodeOrNull<Control>("TheMap") is { } theMap)
        {
            // A touch drag downwards scrolls the map towards the boss; the portrait range then holds it.
            float before = theMap.Position.Y;
            Transform2D toWin = tree.Root.GetFinalTransform();
            Vector2 from = toWin * new Vector2(540f, 1100f);
            Vector2 to = toWin * new Vector2(540f, 1800f);
            SendMouseMotion(from, Vector2.Zero, leftMask: false);
            SendMouseButton(from, pressed: true);
            await WaitFramesAsync(tree, 2);
            Vector2 previous = from;
            for (int i = 1; i <= 20; i++)
            {
                Vector2 point = from.Lerp(to, i / 20f);
                SendMouseMotion(point, point - previous, leftMask: true);
                previous = point;
                await WaitFramesAsync(tree, 1);
            }
            SendMouseButton(to, pressed: false);
            await WaitFramesAsync(tree, 60);
            GD.Print($"Portrait proof map drag: mapYBefore={before:F0} mapYAfter={theMap.Position.Y:F0} limits={PortraitBetweenLayout.MapScrollLimits(mapControl, tree.Root)}");
            await BetweenStepAsync(tree, "map-dragged");
        }
        if (map != null)
        {
            FieldInfo? target = map.GetType().GetField("_targetDragPos", BindingFlags.Instance | BindingFlags.NonPublic);
            float top = PortraitBetweenLayout.MapScrollLimits((Control)map, tree.Root).Max;
            target?.SetValue(map, new Vector2(0f, top));
            await WaitFramesAsync(tree, 60);
            await BetweenStepAsync(tree, "map-boss");
            map.GetType().GetMethod("Close")?.Invoke(map, [true]);
            await WaitFramesAsync(tree, 30);
        }

        // A fight won at once opens the spoils screen with its card reward.
        await EnterEncounterAsync(sts2, tree, player, "NIBBITS_NORMAL", 42UL);
        await RunConsoleCmdAsync(sts2, "WinConsoleCmd", player, []);
        await WaitFramesAsync(tree, 150);
        await BetweenStepAsync(tree, "rewards");

        Node? cardRewardButton = Descendants(tree.Root).FirstOrDefault(n =>
            n.GetType().FullName == "MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton" &&
            n.GetType().GetProperty("Reward")?.GetValue(n)?.GetType().Name == "CardReward");
        cardRewardButton?.Call("ForceClick");
        await WaitFramesAsync(tree, 60);
        await BetweenStepAsync(tree, "card-reward-3");

        Type overlayType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack", throwOnError: true)!;
        object overlays = overlayType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        ShowFourCardReward(sts2, player);
        await WaitFramesAsync(tree, 60);
        await BetweenStepAsync(tree, "card-reward-4");
        overlayType.GetMethod("Clear")!.Invoke(overlays, null);
        await WaitFramesAsync(tree, 30);

        // A 38-card master deck in the 3-column grid.
        string[] deckFill = ["ANGER", "ARMAMENTS", "BLOODLETTING", "BURNING_PACT", "BODY_SLAM", "CORRUPTION", "BARRICADE", "BLUDGEON", "BASH", "CLOTHESLINE"];
        int deckCount = DeckCount(player);
        for (int i = 0; deckCount < 38; i++, deckCount++)
            await RunConsoleCmdAsync(sts2, "CardConsoleCmd", player, [deckFill[i % deckFill.Length], "Deck"]);
        overlayType.GetMethod("Clear")!.Invoke(overlays, null);
        // The deck opens as a player opens it, by a tap on the top bar's deck button; ShowScreen only if that fails.
        string opened = "none";
        if (Descendants(tree.Root).OfType<Control>().FirstOrDefault(n => n.GetType().Name == "NTopBar") is { } topBar &&
            topBar.GetNodeOrNull<Control>("%Deck") is { } deckButton && deckButton.IsVisibleInTree())
        {
            await TapAsync(tree, deckButton.GetGlobalTransformWithCanvas() * (deckButton.Size * 0.5f));
            await WaitFramesAsync(tree, 60);
            if (FindScreen(tree, "NDeckViewScreen") != null) opened = "top-bar-tap";
        }
        if (opened == "none")
        {
            sts2.GetType("MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen")!.GetMethod("ShowScreen")!.Invoke(null, [player]);
            await WaitFramesAsync(tree, 60);
            opened = "show-screen";
        }
        GD.Print($"Portrait proof deck: opened by {opened}; first cards {DeckGridFirstCards(tree)}");
        await BetweenStepAsync(tree, "deck");
        // A tap on the Card Type sort button: the grid sorts again (its first cards change) and the button keeps a touch.
        if (Descendants(tree.Root).OfType<Control>().FirstOrDefault(n => n.Name == "CardTypeSorter" && n.IsVisibleInTree()) is { } sorter)
        {
            await TapAsync(tree, sorter.GetGlobalTransformWithCanvas() * (sorter.Size * 0.5f));
            await WaitFramesAsync(tree, 40);
            GD.Print($"Portrait proof deck: after the Card Type tap, first cards {DeckGridFirstCards(tree)}");
            await BetweenStepAsync(tree, "deck-sorted");
        }
        CloseCapstone(sts2);
        overlayType.GetMethod("Clear")!.Invoke(overlays, null);
        await WaitFramesAsync(tree, 30);

        await RunConsoleCmdAsync(sts2, "RoomConsoleCmd", player, ["Shop"]);
        await WaitFramesAsync(tree, 120);
        DismissFtues(tree, sts2);
        await WaitFramesAsync(tree, 20);
        await BetweenStepAsync(tree, "shop");
        object? merchant = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom")!
            .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        merchant?.GetType().GetMethod("OpenInventory")?.Invoke(merchant, null);
        await WaitFramesAsync(tree, 60);
        await BetweenStepAsync(tree, "shop-inventory");
        overlayType.GetMethod("Clear")!.Invoke(overlays, null);
        await WaitFramesAsync(tree, 30);

        await RunConsoleCmdAsync(sts2, "EventConsoleCmd", player, ["SUNKEN_STATUE"]);
        await WaitFramesAsync(tree, 120);
        await BetweenStepAsync(tree, "event");

        await RunConsoleCmdAsync(sts2, "RoomConsoleCmd", player, ["RestSite"]);
        await WaitFramesAsync(tree, 120);
        await BetweenStepAsync(tree, "rest");
        DismissFtues(tree, sts2);
        await WaitFramesAsync(tree, 20);
        await BetweenStepAsync(tree, "rest-clear");

        await RunConsoleCmdAsync(sts2, "RoomConsoleCmd", player, ["Treasure"]);
        await WaitFramesAsync(tree, 120);
        DismissFtues(tree, sts2);
        await BetweenStepAsync(tree, "treasure");

        // Back to the shop, then Portrait Mode off live from the pause menu.
        await RunConsoleCmdAsync(sts2, "RoomConsoleCmd", player, ["Shop"]);
        await WaitFramesAsync(tree, 120);
        await TogglePortraitOffMidRunAsync(tree, sts2);
        await WaitFramesAsync(tree, 50);
        await BetweenStepAsync(tree, "landscape-shop");
        sts2.GetType("MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen")!.GetMethod("ShowScreen")!.Invoke(null, [player]);
        await WaitFramesAsync(tree, 60);
        await BetweenStepAsync(tree, "landscape-deck");
    }

    /// <summary>The first six cards of the deck view's grid, in the order they stand (top row first, then left to right).</summary>
    private static string DeckGridFirstCards(SceneTree tree)
    {
        if (FindScreen(tree, "NDeckViewScreen") is not { } screen) return "(no deck view)";
        IEnumerable<Control> holders = Descendants(screen).OfType<Control>()
            .Where(c => c.GetType().Name == "NGridCardHolder" && c.IsVisibleInTree() && !c.IsQueuedForDeletion())
            .OrderBy(c => MathF.Round(c.GlobalPosition.Y)).ThenBy(c => c.GlobalPosition.X).Take(6);
        return string.Join(",", holders.Select(h => h.GetType().GetProperty("CardModel")?.GetValue(h) is { } model
            ? model.GetType().GetProperty("Id")?.GetValue(model)?.ToString() ?? model.GetType().Name : "?"));
    }

    private static async Task BetweenStepAsync(SceneTree tree, string step)
    {
        Vector2I win = DisplayServer.WindowGetSize();
        Vector2I canvas = tree.Root.ContentScaleSize;
        GD.Print($"Portrait proof step: {step} window={win.X}x{win.Y} canvas={canvas.X}x{canvas.Y} portrait={STS2Mobile.Platform.SafeAreaCanvas.IsPortrait} {PortraitBetweenLayout.DescribeForProof(tree.Root)}");
        GD.Print($"Portrait small: {step} {PortraitAudit.DescribeSmall(tree.Root)}");
        await WaitForHostStepAckAsync(tree, step);
    }

    private static void DismissFtues(SceneTree tree, Assembly sts2)
    {
        foreach (Node ftue in Descendants(tree.Root).Where(n => n.GetType().Namespace == "MegaCrit.Sts2.Core.Nodes.Ftue").ToList())
        {
            ftue.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy)
                .FirstOrDefault(m => m.Name == "CloseFtue" && m.GetParameters().Length == 0)
                ?.Invoke(ftue, null);
        }
        Type? modalType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.CommonUi.NModalContainer");
        object? modalInst = modalType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        modalType?.GetMethod("Clear")?.Invoke(modalInst, null);
    }

    private static int DeckCount(object player)
    {
        object? deck = player.GetType().GetProperty("Deck")?.GetValue(player);
        object? cards = deck?.GetType().GetProperty("Cards")?.GetValue(deck);
        return cards is ICollection collection ? collection.Count : 0;
    }

    private static void CloseCapstone(Assembly sts2)
    {
        Type? capstoneType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Screens.Capstones.NCapstoneContainer");
        object? capstoneInst = capstoneType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        capstoneType?.GetMethod("Close")?.Invoke(capstoneInst, null);
    }

    private static void ShowFourCardReward(Assembly sts2, object player)
    {
        Type optionsType = sts2.GetType("MegaCrit.Sts2.Core.Runs.CardCreationOptions", throwOnError: true)!;
        Type roomType = sts2.GetType("MegaCrit.Sts2.Core.Rooms.RoomType", throwOnError: true)!;
        object options = optionsType.GetMethod("ForRoom", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [player, Enum.Parse(roomType, "Monster")])!;
        object cards = sts2.GetType("MegaCrit.Sts2.Core.Factories.CardFactory", throwOnError: true)!
            .GetMethod("CreateForReward", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [player, 4, options])!;
        Type resultType = sts2.GetType("MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult", throwOnError: true)!;
        IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(resultType))!;
        foreach (object card in (IEnumerable)cards) list.Add(card);
        Type altType = sts2.GetType("MegaCrit.Sts2.Core.Entities.Cards.CardRewardAlternative")
            ?? sts2.GetTypes().First(t => t.Name == "CardRewardAlternative");
        IList alts = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(altType))!;
        sts2.GetType("MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen", throwOnError: true)!
            .GetMethod("ShowScreen", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [list, alts]);
    }

    private static async Task EnterEncounterAsync(Assembly sts2, SceneTree tree, object player, string encounterId, ulong seed)
    {
        await RunConsoleCmdAsync(sts2, "FightConsoleCmd", player, [encounterId, seed.ToString()]);

        Type combatMgrType = sts2.GetType("MegaCrit.Sts2.Core.Combat.CombatManager", throwOnError: true)!;
        PropertyInfo instProp = combatMgrType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!;
        PropertyInfo inProgProp = combatMgrType.GetProperty("IsInProgress")!;
        PropertyInfo startingProp = combatMgrType.GetProperty("IsStarting")!;
        Type? endTurnType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NEndTurnButton");
        FieldInfo? endTurnStateField = endTurnType?.GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        for (int frame = 0; frame < 360; frame++)
        {
            await WaitFramesAsync(tree, 1);
            object? mgr = instProp.GetValue(null);
            bool inProg = mgr != null && (bool)(inProgProp.GetValue(mgr) ?? false);
            bool starting = mgr != null && (bool)(startingProp.GetValue(mgr) ?? false);
            Node? endTurnBtn = Descendants(tree.Root).FirstOrDefault(n => n.Name == "EndTurnButton");
            bool endTurnReady = endTurnBtn != null && endTurnStateField != null && Convert.ToInt32(endTurnStateField.GetValue(endTurnBtn) ?? 2) == 0;
            bool ftueUp = Descendants(tree.Root).Any(n => n.GetType().FullName == "MegaCrit.Sts2.Core.Nodes.Ftue.NCombatRulesFtue");
            if (inProg && !starting && CountHandHolders(tree) >= 5 && (endTurnReady || ftueUp))
                break;
        }

        await WaitFramesAsync(tree, 35);
        DismissCombatFtueIfPresent(tree, sts2);
        await WaitFramesAsync(tree, 30);
    }

    private static void DismissCombatFtueIfPresent(SceneTree tree, Assembly sts2)
    {
        Node? ftue = Descendants(tree.Root).FirstOrDefault(n =>
            n.GetType().FullName == "MegaCrit.Sts2.Core.Nodes.Ftue.NCombatRulesFtue");
        if (ftue != null)
        {
            MethodInfo? closeFtue = ftue.GetType().GetMethod(
                "CloseFtue",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            closeFtue?.Invoke(ftue, null);
        }

        Type? modalType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.CommonUi.NModalContainer");
        object? modalInst = modalType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        modalType?.GetMethod("Clear")?.Invoke(modalInst, null);
    }

    private static async Task RunConsoleCmdAsync(Assembly sts2, string cmdClassName, object player, string[] args)
    {
        Type? cmdType = sts2.GetType("MegaCrit.Sts2.Core.DevConsole.ConsoleCommands." + cmdClassName);
        if (cmdType == null) return;
        object cmd = Activator.CreateInstance(cmdType)!;
        object result = cmdType.GetMethod("Process")!.Invoke(cmd, [player, args])!;
        FieldInfo? taskField = result.GetType().GetField("task");
        if (taskField?.GetValue(result) is Task task)
            await task;
    }

    private static async Task SetPlayerEnergyAsync(Assembly sts2, object player, decimal amount)
    {
        Type playerCmdType = sts2.GetType("MegaCrit.Sts2.Core.Commands.PlayerCmd", throwOnError: true)!;
        MethodInfo setEnergy = playerCmdType.GetMethod("SetEnergy", BindingFlags.Public | BindingFlags.Static)!;
        if (setEnergy.Invoke(null, [amount, player]) is Task task)
            await task;
    }

    private static async Task ExhaustCardsForProofAsync(Assembly sts2, object player, int count)
    {
        Type choiceCtxType = sts2.GetType("MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext", throwOnError: true)!;
        object ctx = Activator.CreateInstance(choiceCtxType)!;
        Type cardCmdType = sts2.GetType("MegaCrit.Sts2.Core.Commands.CardCmd", throwOnError: true)!;
        MethodInfo exhaustMethod = cardCmdType.GetMethod("Exhaust", BindingFlags.Public | BindingFlags.Static)!;

        Type pileTypeEnum = sts2.GetType("MegaCrit.Sts2.Core.Entities.Cards.PileType", throwOnError: true)!;
        Type pileExtType = sts2.GetType("MegaCrit.Sts2.Core.Entities.Cards.PileTypeExtensions") ??
                           sts2.GetTypes().First(t => t.GetMethod("GetPile", BindingFlags.Public | BindingFlags.Static) != null);
        object drawEnum = Enum.ToObject(pileTypeEnum, 1); // PileType.Draw
        object drawPile = pileExtType.GetMethod("GetPile", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [drawEnum, player])!;

        for (int i = 0; i < count; i++)
        {
            await RunConsoleCmdAsync(sts2, "CardConsoleCmd", player, ["STRIKE_IRONCLAD", "Draw"]);
            var drawCards = ((IEnumerable)drawPile.GetType().GetProperty("Cards")!.GetValue(drawPile)!).Cast<object>().ToList();
            if (drawCards.Count == 0) continue;
            object card = drawCards[^1];
            if (exhaustMethod.Invoke(null, [ctx, card, false, false]) is Task t)
                await t;
        }
    }

    private static async Task ClearHandAsync(Assembly sts2, object player)
    {
        Type pileTypeEnum = sts2.GetType("MegaCrit.Sts2.Core.Entities.Cards.PileType", throwOnError: true)!;
        Type pileExtType = sts2.GetType("MegaCrit.Sts2.Core.Entities.Cards.PileTypeExtensions") ??
                           sts2.GetTypes().First(t => t.GetMethod("GetPile", BindingFlags.Public | BindingFlags.Static) != null);
        object handEnum = Enum.ToObject(pileTypeEnum, 2); // PileType.Hand
        object handPile = pileExtType.GetMethod("GetPile", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [handEnum, player])!;
        var cards = ((IEnumerable)handPile.GetType().GetProperty("Cards")!.GetValue(handPile)!).Cast<object>().ToList();

        Type cardPileCmd = sts2.GetType("MegaCrit.Sts2.Core.Commands.CardPileCmd", throwOnError: true)!;
        MethodInfo removeSingle = cardPileCmd.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "RemoveFromCombat" && m.GetParameters()[0].ParameterType.Name == "CardModel");
        foreach (object card in cards)
        {
            if (removeSingle.Invoke(null, [card, false]) is Task t)
                await t;
        }
    }

    private static async Task TrimHandToAsync(Assembly sts2, object player, string[] cardIds)
    {
        await ClearHandAsync(sts2, player);
        foreach (string id in cardIds)
            await RunConsoleCmdAsync(sts2, "CardConsoleCmd", player, [id, "Hand"]);
    }

    private static async Task<Vector2> DragCardOntoEnemyAsync(SceneTree tree, Assembly sts2, int cardIndexInHand, bool releaseToPlay)
    {
        var holders = Descendants(tree.Root)
            .Where(n => n.GetType().FullName == "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder" &&
                        n.GetParent()?.Name == "CardHolderContainer" &&
                        (n as CanvasItem)?.IsVisibleInTree() == true)
            .Cast<Control>()
            .ToList();
        Control holder = holders[Math.Clamp(cardIndexInHand, 0, holders.Count - 1)];

        var enemyNodes = GetLivingEnemyNodes(tree, sts2);
        Control enemy = enemyNodes[0];
        Control enemyHitbox = (Control)enemy.GetType().GetProperty("Hitbox")!.GetValue(enemy)!;

        Transform2D toWin = tree.Root.GetFinalTransform();
        Vector2 startCanvas = holder.GlobalPosition + new Vector2(0f, -65f);
        Vector2 midCanvas = new(540f, 1180f);
        Vector2 endCanvas = enemyHitbox.GlobalPosition + enemyHitbox.Size * enemy.Scale * 0.5f;

        Vector2 startWin = toWin * startCanvas;
        Vector2 midWin = toWin * midCanvas;
        Vector2 endWin = toWin * endCanvas;

        SendMouseMotion(startWin, Vector2.Zero, leftMask: false);
        await WaitFramesAsync(tree, 3);
        SendMouseButton(startWin, pressed: true);
        await WaitFramesAsync(tree, 4);

        const int steps = 10;
        Vector2 prev = startWin;
        for (int s = 1; s <= steps; s++)
        {
            float t = (float)s / steps;
            Vector2 p = t < 0.5f ? startWin.Lerp(midWin, t * 2f) : midWin.Lerp(endWin, (t - 0.5f) * 2f);
            SendMouseMotion(p, p - prev, leftMask: true);
            prev = p;
            await WaitFramesAsync(tree, 2);
        }

        await WaitFramesAsync(tree, 6);
        if (releaseToPlay)
        {
            SendMouseButton(endWin, pressed: false);
        }
        return endWin;
    }

    private static void SendMouseButton(Vector2 winPos, bool pressed)
    {
        var ev = new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = pressed,
            ButtonMask = pressed ? MouseButtonMask.Left : 0,
            Position = winPos,
            GlobalPosition = winPos,
        };
        Input.ParseInputEvent(ev);
    }

    private static void SendMouseMotion(Vector2 winPos, Vector2 relative, bool leftMask)
    {
        var ev = new InputEventMouseMotion
        {
            Position = winPos,
            GlobalPosition = winPos,
            Relative = relative,
            ButtonMask = leftMask ? MouseButtonMask.Left : 0,
        };
        Input.ParseInputEvent(ev);
    }

    private static async Task SpawnExtraRatsUpToFiveAsync(Assembly sts2)
    {
        Type combatMgrType = sts2.GetType("MegaCrit.Sts2.Core.Combat.CombatManager", throwOnError: true)!;
        object mgr = combatMgrType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        object combatState = combatMgrType.GetMethod("DebugOnlyGetState")!.Invoke(mgr, null)!;

        Type ratType = sts2.GetType("MegaCrit.Sts2.Core.Models.Monsters.TwoTailedRat", throwOnError: true)!;
        Type creatureCmd = sts2.GetType("MegaCrit.Sts2.Core.Commands.CreatureCmd", throwOnError: true)!;
        MethodInfo addGeneric = creatureCmd.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "Add" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2)
            .MakeGenericMethod(ratType);

        foreach (string slot in new[] { "first", "second" })
        {
            var enemies = (ICollection)combatState.GetType().GetProperty("Enemies")!.GetValue(combatState)!;
            if (enemies.Count >= 5) break;
            if (addGeneric.Invoke(null, [combatState, slot]) is Task t)
                await t;
        }
    }

    private static async Task SummonOstyAndChannelOrbsAsync(Assembly sts2, object player)
    {
        Type choiceCtxType = sts2.GetType("MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext", throwOnError: true)!;
        object ctx = Activator.CreateInstance(choiceCtxType)!;

        Type ostyCmd = sts2.GetType("MegaCrit.Sts2.Core.Commands.OstyCmd", throwOnError: true)!;
        MethodInfo summon = ostyCmd.GetMethod("Summon", BindingFlags.Public | BindingFlags.Static)!;
        if (summon.Invoke(null, [ctx, player, 25m, null]) is Task summonTask)
            await summonTask;

        Type orbCmd = sts2.GetType("MegaCrit.Sts2.Core.Commands.OrbCmd", throwOnError: true)!;
        if (orbCmd.GetMethod("AddSlots", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, [player, 5]) is Task slotsTask)
            await slotsTask;

        MethodInfo channelGeneric = orbCmd.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "Channel" && m.IsGenericMethodDefinition);
        foreach (string orbClass in new[] { "LightningOrb", "FrostOrb", "DarkOrb", "PlasmaOrb", "GlassOrb" })
        {
            Type? orbType = sts2.GetType("MegaCrit.Sts2.Core.Models.Orbs." + orbClass);
            if (orbType == null) continue;
            if (channelGeneric.MakeGenericMethod(orbType).Invoke(null, [ctx, player]) is Task chTask)
                await chTask;
        }
    }

    private static Task TogglePortraitOffMidRunAsync(SceneTree tree, Assembly sts2) => TogglePortraitMidRunAsync(tree, sts2, portrait: false);

    /// <summary>Portrait Mode switched in a run as a player does: pause, Settings, Graphics, the row, and back to the run.</summary>
    private static async Task TogglePortraitMidRunAsync(SceneTree tree, Assembly sts2, bool portrait)
    {
        Node? pauseBtn = Descendants(tree.Root).FirstOrDefault(n =>
            n.Name == "PauseButton" && (n as CanvasItem)?.IsVisibleInTree() == true);
        pauseBtn?.Call("ForceClick");
        await WaitFramesAsync(tree, 25);

        Node? settingsBtn = Descendants(tree.Root).FirstOrDefault(n =>
            n.Name == "Settings" && n.GetType().FullName == "MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu.NPauseMenuButton" &&
            (n as CanvasItem)?.IsVisibleInTree() == true);
        settingsBtn?.Call("ForceClick");
        await WaitFramesAsync(tree, 25);

        Node? graphicsTab = Descendants(tree.Root).FirstOrDefault(n =>
            n.Name == "Graphics" && n.GetParent()?.Name == "SettingsTabManager" && (n as CanvasItem)?.IsVisibleInTree() == true);
        graphicsTab?.Call("ForceClick");
        await WaitFramesAsync(tree, 25);

        await StepPaginatorAsync(tree, "PhonePortraitMode", portrait ? 1 : 0, expectPortrait: portrait);

        Type? capstoneType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Screens.Capstones.NCapstoneContainer");
        object? capstoneInst = capstoneType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        capstoneType?.GetMethod("Close")?.Invoke(capstoneInst, null);
    }

    private static int FindCharacterIndex(Assembly sts2, string className)
    {
        Type modelDbType = sts2.GetType("MegaCrit.Sts2.Core.Models.ModelDb", throwOnError: true)!;
        var characters = ((IEnumerable)modelDbType.GetProperty("AllCharacters")!.GetValue(null)!).Cast<object>().ToList();
        for (int i = 0; i < characters.Count; i++)
        {
            if (characters[i].GetType().Name.Equals(className, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 0;
    }

    private static int FindHandCardIndexById(SceneTree tree, Assembly sts2, string entryId)
    {
        var holders = Descendants(tree.Root)
            .Where(n => n.GetType().FullName == "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder" &&
                        (n as CanvasItem)?.IsVisibleInTree() == true)
            .ToList();
        for (int i = 0; i < holders.Count; i++)
        {
            object? cardNode = holders[i].GetType().GetProperty("CardNode")?.GetValue(holders[i]);
            object? model = cardNode?.GetType().GetProperty("Model")?.GetValue(cardNode);
            object? modelId = model?.GetType().GetProperty("Id")?.GetValue(model);
            string? entry = modelId?.GetType().GetProperty("Entry")?.GetValue(modelId) as string;
            if (string.Equals(entry, entryId, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static List<Control> GetLivingEnemyNodes(SceneTree tree, Assembly sts2)
    {
        var list = new List<Control>();
        foreach (Node n in Descendants(tree.Root))
        {
            if (n is not Control c || c.GetType().FullName != "MegaCrit.Sts2.Core.Nodes.Combat.NCreature")
                continue;
            object? entity = c.GetType().GetProperty("Entity")?.GetValue(c);
            if (entity == null) continue;
            int side = Convert.ToInt32(entity.GetType().GetProperty("Side")?.GetValue(entity) ?? 0);
            bool dead = (bool)(entity.GetType().GetProperty("IsDead")?.GetValue(entity) ?? true);
            if (side == 2 && !dead)
                list.Add(c);
        }
        return list;
    }

    private static int GetEnemyHp(Assembly sts2, int enemyIndex)
    {
        Type combatMgrType = sts2.GetType("MegaCrit.Sts2.Core.Combat.CombatManager", throwOnError: true)!;
        object mgr = combatMgrType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        object state = combatMgrType.GetMethod("DebugOnlyGetState")!.Invoke(mgr, null)!;
        var enemies = ((IEnumerable)state.GetType().GetProperty("Enemies")!.GetValue(state)!).Cast<object>().ToList();
        if (enemyIndex < 0 || enemyIndex >= enemies.Count) return -1;
        return Convert.ToInt32(enemies[enemyIndex].GetType().GetProperty("CurrentHp")!.GetValue(enemies[enemyIndex]));
    }

    private static int CountHandHolders(SceneTree tree) =>
        Descendants(tree.Root).Count(n =>
            n.GetType().FullName == "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder" &&
            n.GetParent()?.Name == "CardHolderContainer" &&
            (n as CanvasItem)?.IsVisibleInTree() == true);

    private static void LogCombatSnapshot(SceneTree tree, string step)
    {
        Vector2I win = DisplayServer.WindowGetSize();
        Vector2I canvas = tree.Root.ContentScaleSize;
        int handCount = CountHandHolders(tree);
        Control? energy = Descendants(tree.Root).OfType<Control>().FirstOrDefault(c => c.Name == "EnergyCounterContainer");
        Control? endTurn = Descendants(tree.Root).OfType<Control>().FirstOrDefault(c => c.Name == "EndTurnButton");
        var enemies = Descendants(tree.Root).OfType<Control>()
            .Where(c => c.GetType().FullName == "MegaCrit.Sts2.Core.Nodes.Combat.NCreature")
            .Select(c => $"{c.Name}@({c.GlobalPosition.X:F0},{c.GlobalPosition.Y:F0})")
            .ToArray();
        GD.Print(
            $"Portrait proof step: {step} window={win.X}x{win.Y} canvas={canvas.X}x{canvas.Y} " +
            $"hand={handCount} energyY={energy?.GlobalPosition.Y:F0} endTurn=({endTurn?.GlobalPosition.X:F0},{endTurn?.GlobalPosition.Y:F0}) " +
            $"creatures=[{string.Join(", ", enemies)}]");
    }

    private static void ClearNextStepFile()
    {
        if (Godot.FileAccess.FileExists(NextStepSignalPath))
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(NextStepSignalPath));
    }

    private static async Task WaitForHostStepAckAsync(SceneTree tree, string step)
    {
        for (int i = 0; i < 1800; i++)
        {
            if (Godot.FileAccess.FileExists(NextStepSignalPath))
            {
                ClearNextStepFile();
                await WaitFramesAsync(tree, 5);
                return;
            }
            await WaitFramesAsync(tree, 2);
        }
        GD.Print($"Portrait proof step ack timeout: {step}");
    }

    private static async Task DismissEarlyAccessIfPresentAsync(SceneTree tree)
    {
        await WaitFramesAsync(tree, 15);
        Node? disclaimer = Descendants(tree.Root).FirstOrDefault(n =>
            n.GetType().FullName == "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NEarlyAccessDisclaimer");
        if (disclaimer == null) return;

        Assembly sts2 = typeof(NGame).Assembly;
        Type? hotkeyMgrType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.CommonUi.NHotkeyManager");
        object? hotkeyMgr = hotkeyMgrType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        hotkeyMgrType?.GetMethod("RemoveBlockingScreen")?.Invoke(hotkeyMgr, [disclaimer]);

        Type? modalType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.CommonUi.NModalContainer");
        object? modalMgr = modalType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        modalType?.GetMethod("Clear")?.Invoke(modalMgr, null);

        disclaimer.QueueFree();
        await WaitFramesAsync(tree, 10);
    }

    private static async Task OpenGraphicsSettingsAsync(SceneTree tree, NGame game)
    {
        await WaitFramesAsync(tree, 15);
        object menu = game.MainMenu!;
        menu.GetType().GetMethod("OpenSettingsMenu", Type.EmptyTypes)!.Invoke(menu, null);
        await WaitFramesAsync(tree, 30);

        Node? graphicsTab = Descendants(tree.Root).FirstOrDefault(n =>
            n.Name == "Graphics" && n.GetParent()?.Name == "SettingsTabManager" && (n as CanvasItem)?.IsVisibleInTree() == true);
        graphicsTab?.Call("ForceClick");
        await WaitFramesAsync(tree, 30);
    }

    private static async Task StepPaginatorAsync(SceneTree tree, string rowName, int targetIndex, bool? expectPortrait)
    {
        Node? row = Descendants(tree.Root).FirstOrDefault(n => n.Name == rowName);
        PhoneOptionPaginator? paginator = row?.GetChildren().OfType<PhoneOptionPaginator>().FirstOrDefault();
        if (paginator == null) throw new InvalidOperationException($"Missing paginator in {rowName}.");
        paginator.SelectForProof(targetIndex);

        if (expectPortrait is { } portrait)
        {
            for (int frame = 0; frame < 90; frame++)
            {
                await WaitFramesAsync(tree, 1);
                Vector2I size = DisplayServer.WindowGetSize();
                if (portrait ? size.Y >= size.X : size.X >= size.Y) break;
            }
        }
        await WaitFramesAsync(tree, 30);
    }

    private static async Task<object> StartProofRunAsync(
        SceneTree tree,
        NGame game,
        int characterIndex,
        bool fivePotions,
        bool thirtyRelics,
        bool pageTwo,
        bool addThreePotions = false,
        bool closeRelicHover = false)
    {
        Assembly sts2 = typeof(NGame).Assembly;
        Type modelDbType = sts2.GetType("MegaCrit.Sts2.Core.Models.ModelDb", throwOnError: true)!;
        Type playerType = sts2.GetType("MegaCrit.Sts2.Core.Entities.Players.Player", throwOnError: true)!;
        Type actModelType = sts2.GetType("MegaCrit.Sts2.Core.Models.ActModel", throwOnError: true)!;
        Type modifierModelType = sts2.GetType("MegaCrit.Sts2.Core.Models.ModifierModel", throwOnError: true)!;
        Type gameModeType = sts2.GetType("MegaCrit.Sts2.Core.Runs.GameMode", throwOnError: true)!;

        object unlockState = typeof(SaveManager).GetMethod("GenerateUnlockStateFromProgress")!.Invoke(SaveManager.Instance, null)!;
        var characters = ((IEnumerable)modelDbType.GetProperty("AllCharacters")!.GetValue(null)!).Cast<object>().ToList();
        object character = characters[Math.Clamp(characterIndex, 0, characters.Count - 1)];

        MethodInfo createPlayer = playerType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "CreateForNewRun" && m.GetParameters().Length == 3);
        object player = createPlayer.Invoke(null, [character, unlockState, 1UL])!;
        playerType.GetProperty("Gold")!.SetValue(player, 1420);

        if (fivePotions || addThreePotions)
        {
            int currentMax = (int)(playerType.GetProperty("MaxPotionCount")?.GetValue(player) ?? 3);
            int targetPreAscensionMax = fivePotions ? 5 : 4; // Ascension 10 subtracts 1 potion slot during InitializeNewRun.
            if (currentMax < targetPreAscensionMax)
                playerType.GetMethod("AddToMaxPotionCount")!.Invoke(player, [targetPreAscensionMax - currentMax]);

            var potions = ((IEnumerable)modelDbType.GetProperty("AllPotions")!.GetValue(null)!).Cast<object>().Take(3).ToList();
            MethodInfo addPotion = playerType.GetMethod("AddPotionInternal")!;
            foreach (object canonical in potions)
            {
                object mutable = canonical.GetType().GetMethod("ToMutable")!.Invoke(canonical, null)!;
                addPotion.Invoke(player, [mutable, -1, true]);
            }
        }

        object acts = actModelType.GetMethod("GetDefaultList", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
        Array emptyModifiers = Array.CreateInstance(modifierModelType, 0);
        object standardMode = Enum.ToObject(gameModeType, 1);

        MethodInfo startRun = typeof(NGame).GetMethod("StartNewSingleplayerRun")!;
        var runTask = (Task)startRun.Invoke(game, [player, false, acts, emptyModifiers, "PORTRAIT21B", standardMode, false, 10, null])!;
        await runTask;
        await WaitFramesAsync(tree, 60);

        if (fivePotions)
        {
            int afterMax = (int)(playerType.GetProperty("MaxPotionCount")?.GetValue(player) ?? 4);
            if (afterMax < 5)
            {
                playerType.GetMethod("AddToMaxPotionCount")!.Invoke(player, [5 - afterMax]);
                await WaitFramesAsync(tree, 15);
            }
            Node? holder = Descendants(tree.Root).FirstOrDefault(n =>
                n.GetType().FullName == "MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder" &&
                (bool)(n.GetType().GetProperty("HasPotion")?.GetValue(n) ?? false));
            holder?.GetType().GetMethod("OpenPotionPopup", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(holder, null);
            await WaitFramesAsync(tree, 25);
        }

        if (thirtyRelics)
        {
            var currentRelics = (ICollection)playerType.GetProperty("Relics")!.GetValue(player)!;
            var allRelics = ((IEnumerable)modelDbType.GetProperty("AllRelics")!.GetValue(null)!).Cast<object>();
            MethodInfo addRelic = playerType.GetMethod("AddRelicInternal")!;
            foreach (object canonical in allRelics)
            {
                if (currentRelics.Count >= 30) break;
                Type rt = canonical.GetType();
                bool pickup = (bool)(rt.GetProperty("HasUponPickupEffect")?.GetValue(canonical) ?? false);
                bool pets = (bool)(rt.GetProperty("SpawnsPets")?.GetValue(canonical) ?? false) ||
                            (bool)(rt.GetProperty("AddsPet")?.GetValue(canonical) ?? false);
                const BindingFlags DeclInst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                bool altersCombat = rt.GetMethods(DeclInst).Any(m =>
                    typeof(Task).IsAssignableFrom(m.ReturnType) ||
                    m.Name.Contains("HandDraw", StringComparison.Ordinal) ||
                    m.Name.Contains("Energy", StringComparison.Ordinal));
                if (pickup || pets || altersCombat || rt.Name.StartsWith("Fake", StringComparison.Ordinal)) continue;
                object mutable = rt.GetMethod("ToMutable")!.Invoke(canonical, null)!;
                addRelic.Invoke(player, [mutable, -1, false]);
            }
            await WaitFramesAsync(tree, 35);

            foreach (Node node in Descendants(tree.Root))
            {
                if (node.GetType().FullName != "MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventoryHolder") continue;
                foreach (CanvasItem item in Descendants(node).OfType<CanvasItem>())
                {
                    if (item.Name == "Icon" && item.Modulate.A < 0.99f)
                        item.Modulate = Colors.White;
                }
            }

            if (pageTwo)
            {
                if (Descendants(tree.Root).FirstOrDefault(n => n.Name == "PortraitRelicPager") is Button pager)
                    pager.EmitSignal(BaseButton.SignalName.Pressed);
                await WaitFramesAsync(tree, 20);
            }

            if (!closeRelicHover)
            {
                Node? visibleHolder = Descendants(tree.Root).FirstOrDefault(n =>
                    n.GetType().FullName == "MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventoryHolder" &&
                    (n as CanvasItem)?.IsVisibleInTree() == true);
                visibleHolder?.GetType().GetMethod("OnFocus", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(visibleHolder, null);
                await WaitFramesAsync(tree, 25);
            }
        }

        return player;
    }

    private static async Task WaitFramesAsync(SceneTree tree, int count)
    {
        for (int i = 0; i < count; i++)
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (Node below in Descendants(child)) yield return below;
        }
    }
}
