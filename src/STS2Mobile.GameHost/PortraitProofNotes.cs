using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.GameHost;

/// <summary>
/// <c>phone-notes</c>: notes from playing on a phone, each shown in portrait. A run as The Fool
/// with thirty relics (two rows in the relic strip): Orobas and the Touch of Orobas tip; the hand's motion at a fight's start and
/// after a card is played, measured with the earlier rule and with the new one; End Turn under a held card; the
/// Waterfall Giant with Wah Cho Ki aimed, the Kaiser Crab, two other bosses and two elites; a scroll that starts on a card of the
/// upgrade screen; and the shop under the relic strip. Every step logs <c>result=</c> and waits for the host's screenshot.
/// </summary>
internal static partial class PortraitProof
{
    private static readonly string[] NotesFights =
        ["WATERFALL_GIANT_BOSS", "KAISER_CRAB_BOSS", "THE_KIN_BOSS", "QUEEN_BOSS", "PHROG_PARASITE_ELITE", "KNIGHTS_ELITE"];

    private static async Task RunPhoneNotesAsync(SceneTree tree, NGame game)
    {
        ClearNextStepFile();
        await EnsurePortraitAsync(tree, game);
        var characters = ModelDbList("AllCharacters").ToList();
        int fool = Math.Max(0, characters.FindIndex(c => c.GetType().Name == "Fool"));
        object player = await StartProofRunAsync(tree, game, characterIndex: fool, fivePotions: false, thirtyRelics: true, pageTwo: false, closeRelicHover: true);
        await WaitFramesAsync(tree, 90);
        Call(StaticProp(GameType(ScreensNs + "Map.NMapScreen"), "Instance")!, "Close", false);
        DismissFtues(tree, Sts2);
        await WaitFramesAsync(tree, 20);
        var results = new List<string>();
        GD.Print($"Notes proof: character={characters[fool].GetType().Name} relics={RelicCount(player)}");

        await NotesOrobasAsync(tree, game, player, results);

        // The hand at a fight's start and after a card is played: first with the earlier rule, then with the new one.
        foreach (bool oldRule in new[] { true, false })
        {
            HandStillness.ProofOldRule = oldRule;
            string rule = oldRule ? "old" : "new";
            Task<string> start = SampleHandAsync(tree, 150);
            await EnterEncounterAsync(Sts2, tree, player, "NIBBITS_NORMAL", 42UL);
            string atStart = await start;
            DismissFtues(tree, Sts2);
            await TrimHandToAsync(Sts2, player, ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD"]);
            await SetPlayerEnergyAsync(Sts2, player, 9m);
            await WaitFramesAsync(tree, 60);
            Task<string> afterPlay = SampleHandAsync(tree, 110);
            string played = await DragStrikeOntoEnemyAsync(tree, 0, cardIndex: 0);
            string afterPlayMotion = await afterPlay;
            await TouchStepAsync(tree, $"notes-hand-{rule}", $"rule={rule} start[{atStart}] play[{afterPlayMotion}] {played}", results);
            await RunConsoleCmdAsync(Sts2, "WinConsoleCmd", player, []);
            await WaitFramesAsync(tree, 90);
            Call(StaticProp(GameType(ScreensNs + "Overlays.NOverlayStack"), "Instance")!, "Clear");
            await WaitFramesAsync(tree, 20);
        }
        HandStillness.ProofOldRule = false;

        foreach (string fight in NotesFights)
        {
            await EnterEncounterAsync(Sts2, tree, player, fight, 42UL);
            await WaitFramesAsync(tree, 120);
            DismissFtues(tree, Sts2);
            await WaitFramesAsync(tree, 30);
            string name = fight.ToLowerInvariant().Replace('_', '-');
            await TouchStepAsync(tree, "notes-" + name, DescribeCreatures(tree), results);
            if (fight == "WATERFALL_GIANT_BOSS")
            {
                await NotesEndTurnAsync(tree, player, results);
                await NotesAimAsync(tree, player, results);
            }
            await RunConsoleCmdAsync(Sts2, "WinConsoleCmd", player, []);
            await WaitFramesAsync(tree, 90);
            Call(StaticProp(GameType(ScreensNs + "Overlays.NOverlayStack"), "Instance")!, "Clear");
            await WaitFramesAsync(tree, 20);
        }

        // The last fight's spoils screen comes up a moment after the win: closed before the grid screens.
        await WaitFramesAsync(tree, 120);
        Call(StaticProp(GameType(ScreensNs + "Overlays.NOverlayStack"), "Instance")!, "Clear");
        await WaitFramesAsync(tree, 30);
        await NotesScrollAsync(tree, player, results);
        await NotesShopAsync(tree, player, results);
        GD.Print("Notes proof results: " + string.Join("; ", results));
    }

    /// <summary>Orobas, then the Touch of Orobas tip over its option: from the event when it offers one, else made the game's way.</summary>
    private static async Task NotesOrobasAsync(SceneTree tree, NGame game, object player, List<string> results)
    {
        await RunConsoleCmdAsync(Sts2, "AncientConsoleCmd", player, ["OROBAS"]);
        await WaitFramesAsync(tree, 150);
        DismissFtues(tree, Sts2);
        // The dialogue first: a tap moves it on until the options show.
        for (int i = 0; i < 6 && OptionButtons(tree).Count == 0; i++)
        {
            await TapAsync(tree, new Vector2(540f, 900f));
            await WaitFramesAsync(tree, 60);
        }
        List<Control> options = OptionButtons(tree);
        string names = string.Join(",", options.Select(OptionRelicName));
        await TouchStepAsync(tree, "notes-orobas", $"options={options.Count}[{names}]", results);
        if (options.Count == 0) return;

        Control? touch = options.FirstOrDefault(o => OptionRelicName(o) == "TouchOfOrobas");
        Control anchor = touch ?? options[0];
        string how = "hover";
        if (touch != null)
        {
            SendMouseMotion(tree.Root.GetFinalTransform() * CentreOf(touch), Vector2.Zero, leftMask: false);
        }
        else
        {
            // Not offered this time: the tip the option would show, for this player, over the first option.
            object relic = Call(ModelDbList("AllRelics").First(r => r.GetType().Name == "TouchOfOrobas"), "ToMutable")!;
            bool setUp = Call(relic, "SetupForPlayer", player) is true;
            object tips = Prop(relic, "HoverTips")!;
            Type tipSet = GameType("MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet");
            Type alignment = GameType("MegaCrit.Sts2.Core.HoverTips.HoverTipAlignment", "HoverTipAlignment");
            CallStatic(tipSet, "CreateAndShow", anchor, tips, Enum.Parse(alignment, "Left"));
            how = $"made setup={setUp}";
        }
        await WaitFramesAsync(tree, 40);
        GD.Print("Notes proof tips: " + DescribeTipChildren(game));
        await TouchStepAsync(tree, "notes-orobas-tip", $"{how} {TipsInside(game)} {DescribeTipSets(game)}", results);
        SendMouseMotion(tree.Root.GetFinalTransform() * new Vector2(540f, 300f), Vector2.Zero, leftMask: false);
        CallStatic(GameType("MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet"), "Remove", anchor);
        await WaitFramesAsync(tree, 20);
    }

    /// <summary>A Strike held over End Turn: the button must not light. Then End Turn under the pointer with no card held: it does.</summary>
    private static async Task NotesEndTurnAsync(SceneTree tree, object player, List<string> results)
    {
        await TrimHandToAsync(Sts2, player, ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD"]);
        await SetPlayerEnergyAsync(Sts2, player, 9m);
        await WaitFramesAsync(tree, 60);
        Control? endTurn = Descendants(tree.Root).OfType<Control>().FirstOrDefault(c => c.GetType().Name == "NEndTurnButton" && c.IsVisibleInTree());
        List<Control> holders = HandHolders(tree);
        if (endTurn == null || holders.Count == 0)
        {
            results.Add("notes-end-turn no button or hand");
            return;
        }
        Vector2 over = CentreOf(endTurn);
        Vector2 start = PointOnCard(holders[0], 0.3f, 0.4f);
        SendTouch(tree, start, pressed: true);
        await WaitFramesAsync(tree, 3);
        Vector2 prev = start;
        for (int s = 1; s <= 12; s++)
        {
            Vector2 p = start.Lerp(over, s / 12f);
            SendTouchDrag(tree, p, prev);
            prev = p;
            await WaitFramesAsync(tree, 2);
        }
        await WaitFramesAsync(tree, 20);
        string held = $"cardplay={InCardPlay(tree)} {EndTurnState(endTurn)}";
        await TouchStepAsync(tree, "notes-end-turn-held", held, results);
        // Back down to the hand and let go there: the card goes back.
        for (int s = 1; s <= 8; s++)
        {
            Vector2 p = over.Lerp(start, s / 8f);
            SendTouchDrag(tree, p, prev);
            prev = p;
            await WaitFramesAsync(tree, 2);
        }
        SendTouch(tree, start, pressed: false);
        await WaitFramesAsync(tree, 30);
        if (InspectOpenAny(tree)) { await TapAsync(tree, new Vector2(540f, 420f)); await WaitFramesAsync(tree, 30); }
        SendMouseMotion(tree.Root.GetFinalTransform() * over, Vector2.Zero, leftMask: false);
        await WaitFramesAsync(tree, 20);
        string free = $"cardplay={InCardPlay(tree)} hand={HandHolders(tree).Count} {EndTurnState(endTurn)}";
        await TouchStepAsync(tree, "notes-end-turn-free", free, results);
        SendMouseMotion(tree.Root.GetFinalTransform() * new Vector2(540f, 600f), Vector2.Zero, leftMask: false);
        await WaitFramesAsync(tree, 20);
    }

    /// <summary>Wah Cho Ki held over the Waterfall Giant: its aiming arrow and the giant's powers.</summary>
    private static async Task NotesAimAsync(SceneTree tree, object player, List<string> results)
    {
        await TrimHandToAsync(Sts2, player, ["WAH_CHO_KI", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD"]);
        await SetPlayerEnergyAsync(Sts2, player, 9m);
        await WaitFramesAsync(tree, 60);
        List<Control> enemies = GetLivingEnemyNodes(tree, Sts2);
        List<Control> holders = HandHolders(tree);
        if (enemies.Count == 0 || holders.Count == 0) return;
        Control hitbox = (Control)enemies[0].GetType().GetProperty("Hitbox")!.GetValue(enemies[0])!;
        Vector2 target = hitbox.GetGlobalTransformWithCanvas() * (hitbox.Size * 0.5f);
        Vector2 start = PointOnCard(holders[0], 0.3f, 0.4f);
        SendTouch(tree, start, pressed: true);
        await WaitFramesAsync(tree, 3);
        Vector2 prev = start;
        for (int s = 1; s <= 14; s++)
        {
            Vector2 p = start.Lerp(target, s / 14f);
            SendTouchDrag(tree, p, prev);
            prev = p;
            await WaitFramesAsync(tree, 2);
        }
        await WaitFramesAsync(tree, 30);
        await TouchStepAsync(tree, "notes-waterfall-aim", $"card={CardId(holders[0])} cardplay={InCardPlay(tree)} {DescribeCreatures(tree)}", results);
        for (int s = 1; s <= 10; s++)
        {
            Vector2 p = target.Lerp(start, s / 10f);
            SendTouchDrag(tree, p, prev);
            prev = p;
            await WaitFramesAsync(tree, 2);
        }
        SendTouch(tree, start, pressed: false);
        await WaitFramesAsync(tree, 30);
        if (InspectOpenAny(tree)) { await TapAsync(tree, new Vector2(540f, 420f)); await WaitFramesAsync(tree, 30); }
    }

    /// <summary>The upgrade screen with a deck of 24: a drag that starts on a card scrolls, picks nothing; a tap still picks.</summary>
    private static async Task NotesScrollAsync(SceneTree tree, object player, List<string> results)
    {
        string[] fill = ["ANGER", "ARMAMENTS", "BLOODLETTING", "BURNING_PACT", "BODY_SLAM", "CORRUPTION", "BARRICADE", "BLUDGEON", "BASH", "CLOTHESLINE"];
        for (int i = 0; DeckCards(player).Count < 24; i++)
            await RunConsoleCmdAsync(Sts2, "CardConsoleCmd", player, [fill[i % fill.Length], "Deck"]);
        Type prefsType = GameType("MegaCrit.Sts2.Core.CardSelection.CardSelectorPrefs", "CardSelectorPrefs");
        object prefs = Activator.CreateInstance(prefsType, StaticProp(prefsType, "UpgradeSelectionPrompt"), 1)!;
        object screen = CallStatic(GameType(ScreensNs + "CardSelection.NDeckUpgradeSelectScreen"), "ShowScreen", DeckCards(player), prefs, Prop(player, "RunState"))!;
        await WaitFramesAsync(tree, 60);
        DismissFtues(tree, Sts2);
        await WaitFramesAsync(tree, 20);
        Control? grid = Descendants((Node)screen).OfType<Control>().FirstOrDefault(c => IsOrDerives(c.GetType(), "NCardGrid") && c.IsVisibleInTree());
        List<Control> cards = GridHolders((Control)screen);
        if (grid == null || cards.Count == 0)
        {
            await TouchStepAsync(tree, "notes-scroll", $"no grid: grid={(grid == null ? "none" : grid.GetPath())} cards={cards.Count}", results);
            return;
        }
        Control scroll = grid.GetNodeOrNull<Control>("%ScrollContainer") ?? grid;
        float before = scroll.Position.Y;
        Control onCard = cards.Where(c => c.IsVisibleInTree()).OrderBy(c => Math.Abs(CentreOf(c).Y - 1300f)).First();
        Vector2 from = CentreOf(onCard);
        Vector2 to = from + new Vector2(10f, -700f);
        int scrolls = PhoneTouchScroll.ScrollsStarted;
        SendTouch(tree, from, pressed: true);
        await WaitFramesAsync(tree, 3);
        Vector2 prev = from;
        for (int s = 1; s <= 14; s++)
        {
            Vector2 p = from.Lerp(to, s / 14f);
            SendTouchDrag(tree, p, prev);
            prev = p;
            await WaitFramesAsync(tree, 2);
        }
        SendTouch(tree, to, pressed: false);
        await WaitFramesAsync(tree, 60);
        string after = $"scrollY={before:F0}->{scroll.Position.Y:F0} scrolls={PhoneTouchScroll.ScrollsStarted - scrolls} released={PhoneTouchScroll.PressesReleased} from=({from.X:F0},{from.Y:F0}) {UpgradeSelection(screen)}";
        await TouchStepAsync(tree, "notes-scroll-from-card", after, results);

        // A tap still picks a card.
        Control tapCard = GridHolders((Control)screen).Where(c => c.IsVisibleInTree()).OrderBy(c => Math.Abs(CentreOf(c).Y - 1200f)).First();
        await TapAsync(tree, CentreOf(tapCard));
        await WaitFramesAsync(tree, 40);
        await TouchStepAsync(tree, "notes-scroll-tap", UpgradeSelection(screen), results);
        Call(StaticProp(GameType(ScreensNs + "Overlays.NOverlayStack"), "Instance")!, "Clear");
        await WaitFramesAsync(tree, 30);
    }

    /// <summary>The shop with two rows of relics: the first row of cards starts under the relic strip.</summary>
    private static async Task NotesShopAsync(SceneTree tree, object player, List<string> results)
    {
        player.GetType().GetProperty("Gold")!.SetValue(player, 999);
        await RunConsoleCmdAsync(Sts2, "RoomConsoleCmd", player, ["Shop"]);
        await WaitFramesAsync(tree, 120);
        DismissFtues(tree, Sts2);
        object? merchant = StaticProp(GameType("MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom"), "Instance");
        if (merchant != null) Call(merchant, "OpenInventory");
        await WaitFramesAsync(tree, 90);
        DismissFtues(tree, Sts2);
        await WaitFramesAsync(tree, 20);
        Control? strip = Descendants(tree.Root).OfType<Control>().FirstOrDefault(c => c.GetType().Name == "NRelicInventory" && c.IsVisibleInTree());
        float stripBottom = strip == null ? -1f : StripBottom(strip);
        List<Control> cards = ShopSlots(tree).Where(s => s.GetType().Name == "NMerchantCard").ToList();
        float cardTop = cards.Count == 0 ? -1f : cards.Min(c => CanvasTop(c));
        await TouchStepAsync(tree, "notes-shop", $"relics={RelicCount(player)} stripBottom={stripBottom:F0} cardTop={cardTop:F0} clear={cardTop >= stripBottom}", results);
    }

    private static bool IsOrDerives(Type type, string name)
    {
        for (Type? t = type; t != null; t = t.BaseType)
            if (t.Name == name) return true;
        return false;
    }

    /// <summary>Every tip of every tip set: its rect on the canvas, and its column's flow settings.</summary>
    private static string DescribeTipChildren(NGame game)
    {
        if (game.GetType().GetProperty("HoverTipsContainer", AnyInstance)?.GetValue(game) is not Node container) return "tipkids=?";
        var parts = new List<string>();
        foreach (Control set in container.GetChildren().OfType<Control>())
        {
            foreach (Control col in set.GetChildren().OfType<Control>())
            {
                string flow = col is FlowContainer f ? $" reverse={f.ReverseFill} align={f.Alignment} vsep={f.GetThemeConstant("v_separation")} hsep={f.GetThemeConstant("h_separation")}" : "";
                parts.Add($"{col.Name} size={col.Size} pos={col.Position}{flow}");
                foreach (Control tip in col.GetChildren().OfType<Control>())
                    parts.Add($"  tip {CanvasRect(tip)} pos={tip.Position} size={tip.Size} min={tip.GetCombinedMinimumSize()} vis={tip.Visible}");
            }
        }
        return string.Join(" |", parts);
    }

    private static float StripBottom(Control strip)
    {
        float bottom = CanvasBottom(strip);
        foreach (Control relic in strip.GetChildren().OfType<Control>().Where(c => c.IsVisibleInTree()))
            bottom = Math.Max(bottom, CanvasBottom(relic));
        return bottom;
    }

    private static float CanvasTop(Control c)
    {
        Control target = c.GetNodeOrNull<Control>("%Hitbox") ?? c.GetNodeOrNull<Control>("Hitbox") ?? c;
        Transform2D x = target.GetGlobalTransformWithCanvas();
        return Math.Min((x * Vector2.Zero).Y, (x * target.Size).Y);
    }

    private static float CanvasBottom(Control c)
    {
        Transform2D x = c.GetGlobalTransformWithCanvas();
        return Math.Max((x * Vector2.Zero).Y, (x * c.Size).Y);
    }

    private static string UpgradeSelection(object screen)
    {
        object? selected = Field(screen, "_selectedCards") ?? Field(screen, "_selection");
        int count = selected is ICollection c ? c.Count : -1;
        bool preview = Descendants((Node)screen).OfType<Control>().Any(n => n.Name.ToString().Contains("Preview", StringComparison.Ordinal) && n.IsVisibleInTree());
        return $"selected={count} preview={preview}";
    }

    private static List<Control> OptionButtons(SceneTree tree) =>
        Descendants(tree.Root).OfType<Control>().Where(c => c.GetType().Name == "NEventOptionButton" && c.IsVisibleInTree()).ToList();

    private static string OptionRelicName(Control option)
    {
        object? model = Prop(option, "Option");
        object? relic = model == null ? null : Prop(model, "Relic");
        return relic?.GetType().Name ?? (model == null ? "?" : Prop(model, "TextKey")?.ToString() ?? "text");
    }

    private static string EndTurnState(Control endTurn) =>
        $"endturn[hovered={Field(endTurn, "_isHovered")} focused={Prop(endTurn, "IsFocused")} filter={endTurn.MouseFilter} label={(Field(endTurn, "_label") as CanvasItem)?.Modulate.ToHtml() ?? "?"}]";

    private static bool InspectOpenAny(SceneTree tree) =>
        Descendants(tree.Root).OfType<CanvasItem>().Any(n => n.GetType().Name == "NInspectCardScreen" && n.Visible);

    /// <summary>Whether every tip column is inside the canvas, and the widest overrun.</summary>
    private static string TipsInside(NGame game)
    {
        if (game.GetType().GetProperty("HoverTipsContainer", AnyInstance)?.GetValue(game) is not Node container) return "inside=?";
        float width = ((Control)container).GetViewportRect().Size.X;
        float worst = 0f;
        int columns = 0;
        foreach (Control set in container.GetChildren().OfType<Control>())
        {
            foreach (Control col in set.GetChildren().OfType<Control>().Where(c => c.Visible))
            {
                foreach (Control tip in col.GetChildren().OfType<Control>().Where(c => c.Visible))
                {
                    columns++;
                    Transform2D x = tip.GetGlobalTransformWithCanvas();
                    float left = (x * Vector2.Zero).X, right = (x * tip.Size).X;
                    worst = Math.Max(worst, Math.Max(-left, right - width));
                }
            }
        }
        return $"tips={columns} inside={worst <= 0.5f} overrun={Math.Max(0f, worst):F0}";
    }

    /// <summary>Each creature: side, scale, its hit box on the canvas, and the stage's width it takes.</summary>
    private static string DescribeCreatures(SceneTree tree)
    {
        var parts = new List<string>();
        foreach (Control c in Descendants(tree.Root).OfType<Control>().Where(n => n.GetType().Name == "NCreature" && n.IsVisibleInTree()))
        {
            object? entity = Prop(c, "Entity");
            bool isPlayer = entity != null && Prop(entity, "IsPlayer") is true;
            string name = entity == null ? c.Name.ToString() : (Prop(entity, "Monster")?.GetType().Name ?? (isPlayer ? "player" : c.Name.ToString()));
            Control? hit = Prop(c, "Hitbox") as Control;
            parts.Add($"{name}:x{c.Scale.X:F2} hit={(hit == null ? "?" : CanvasRect(hit))}");
        }
        return $"creatures={parts.Count} {string.Join(" ", parts)}";
    }

    /// <summary>
    /// The hand's motion over some frames: how often a card moved away from its own target, and the largest such step (units).
    /// The game's desktop layout pulls the cards toward its targets, about 490 units lower, before the portrait pass sets them back.
    /// </summary>
    private static async Task<string> SampleHandAsync(SceneTree tree, int frames)
    {
        int reversals = 0, samples = 0;
        float maxAway = 0f;
        var last = new Dictionary<ulong, Vector2>();
        for (int f = 0; f < frames; f++)
        {
            await WaitFramesAsync(tree, 1);
            foreach (Control h in HandHolders(tree))
            {
                ulong id = h.GetInstanceId();
                Vector2 now = h.Position;
                if (last.TryGetValue(id, out Vector2 prev) && Field(h, "_targetPosition") is Vector2 target)
                {
                    Vector2 toTarget = target - prev;
                    if (toTarget.LengthSquared() > 4f)
                    {
                        samples++;
                        float along = (now - prev).Dot(toTarget.Normalized());
                        if (along < -1f)
                        {
                            reversals++;
                            maxAway = Math.Max(maxAway, -along);
                        }
                    }
                }
                last[id] = now;
            }
        }
        return $"moving={samples} away={reversals} maxAway={maxAway:F0}";
    }
}
