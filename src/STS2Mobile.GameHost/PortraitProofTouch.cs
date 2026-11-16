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
/// <c>touch-suite</c>: a fight played with touch events, not mouse events. Each gesture goes through
/// <c>Input.ParseInputEvent</c> as <c>InputEventScreenTouch</c> and <c>InputEventScreenDrag</c>, which the engine turns into
/// mouse events the way it does for a finger on the phone (no hover before the press, the pointer jumps to the touch). Steps:
/// the hand at the edges, a tap and a long press that open the card large, a short drag that puts the card back, a drag from
/// the visible strip of each card onto every enemy, the same from the back row of a two-row hand, and hover tips at the edges.
/// Each step logs <c>result=</c> with what the game did, then the usual audit text, and waits for the host's screenshot.
/// </summary>
internal static partial class PortraitProof
{
    private const string HandHolderType = "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder";

    private static async Task RunTouchSuiteAsync(SceneTree tree, NGame game)
    {
        ClearNextStepFile();
        await EnsurePortraitAsync(tree, game);
        object player = await StartProofRunAsync(tree, game, characterIndex: 0, fivePotions: false, thirtyRelics: false, pageTwo: false, addThreePotions: true, closeRelicHover: true);
        await WaitFramesAsync(tree, 90);
        Call(StaticProp(GameType(ScreensNs + "Map.NMapScreen"), "Instance")!, "Close", false);
        DismissFtues(tree, Sts2);
        await WaitFramesAsync(tree, 20);

        await EnterEncounterAsync(Sts2, tree, player, "NIBBITS_NORMAL", 42UL);
        await TrimHandToAsync(Sts2, player, ["STRIKE_IRONCLAD", "BASH", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD"]);
        await SetPlayerEnergyAsync(Sts2, player, 9m);
        await WaitFramesAsync(tree, 60);
        var results = new List<string>();

        await TouchStepAsync(tree, "touch-hand", "five cards", results);
        await LogHandGeometry(tree, "five");

        // A tap on the leftmost card's visible strip opens it large; a tap on the dimmed backdrop closes it.
        Control first = HandHolders(tree)[0];
        await TapAsync(tree, PointOnCard(first, 0.12f, 0.4f));
        await WaitFramesAsync(tree, 30);
        bool tapOpened = InspectOpen(game);
        await TouchStepAsync(tree, "touch-tap", $"inspect={(tapOpened ? "open" : "closed")} cardplay={InCardPlay(tree)} hand={HandHolders(tree).Count}", results);
        await TapAsync(tree, new Vector2(540f, 420f));
        await WaitFramesAsync(tree, 40);
        results.Add($"tap-close inspect={(InspectOpen(game) ? "open" : "closed")}");

        // A long press on the middle card: still down after 0.8 s, the card is shown large and put back in the hand.
        Control middle = HandHolders(tree)[2];
        Vector2 pressAt = PointOnCard(middle, 0.15f, 0.35f);
        SendTouch(tree, pressAt, pressed: true);
        await WaitMsecAsync(tree, 800);
        bool longOpened = InspectOpen(game);
        string whileDown = $"inspect={(longOpened ? "open" : "closed")} cardplay={InCardPlay(tree)}";
        SendTouch(tree, pressAt, pressed: false);
        await WaitFramesAsync(tree, 20);
        await TouchStepAsync(tree, "touch-long-press", $"{whileDown} after-release={(InspectOpen(game) ? "open" : "closed")} hand={HandHolders(tree).Count} {DescribeTipSets(game)}", results);
        await TapAsync(tree, new Vector2(540f, 420f));
        await WaitFramesAsync(tree, 40);

        // A short sideways drag that never reaches the play zone: the card goes back to the hand, nothing opens.
        Control second = HandHolders(tree)[1];
        Vector2 start = PointOnCard(second, 0.12f, 0.4f);
        await DragAsync(tree, start, start + new Vector2(90f, -20f), steps: 6);
        await WaitFramesAsync(tree, 30);
        await TouchStepAsync(tree, "touch-short-drag", $"cardplay={InCardPlay(tree)} inspect={(InspectOpen(game) ? "open" : "closed")} hand={HandHolders(tree).Count}", results);

        // A drag that is let go above the enemies but off every target: the card goes back too.
        Control third = HandHolders(tree)[0];
        start = PointOnCard(third, 0.12f, 0.4f);
        await DragAsync(tree, start, new Vector2(540f, 520f), steps: 10);
        await WaitFramesAsync(tree, 30);
        await TouchStepAsync(tree, "touch-drag-off-target", $"cardplay={InCardPlay(tree)} hand={HandHolders(tree).Count}", results);

        // A Strike dragged from its visible strip onto each living enemy plays on that enemy.
        int enemyCount = GetLivingEnemyNodes(tree, Sts2).Count;
        for (int e = 0; e < enemyCount; e++)
        {
            await TrimHandToAsync(Sts2, player, ["DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD"]);
            await SetPlayerEnergyAsync(Sts2, player, 9m);
            await WaitFramesAsync(tree, 45);
            string outcome = await DragStrikeOntoEnemyAsync(tree, e, cardIndex: e == 0 ? 1 : 3);
            await TouchStepAsync(tree, $"touch-drag-enemy-{e}", outcome, results);
        }

        // Eight cards in two rows: a back-row card dragged from its top strip.
        await TrimHandToAsync(Sts2, player, ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD",
            "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD"]);
        await SetPlayerEnergyAsync(Sts2, player, 9m);
        await WaitFramesAsync(tree, 60);
        await TouchStepAsync(tree, "touch-hand-two-rows", "eight cards", results);
        await LogHandGeometry(tree, "eight");
        string backRow = await DragStrikeOntoEnemyAsync(tree, 0, cardIndex: 0, fy: 0.12f);
        await TouchStepAsync(tree, "touch-drag-back-row", backRow, results);

        // Hover tips at the edges: the leftmost hand card held, then Bash in the middle of a card choice (its Vulnerable tip is wide; a Defend has no tips, and the left slot sits under the screen's peek button in portrait).
        Control left = HandHolders(tree)[0];
        Vector2 hold = PointOnCard(left, 0.12f, 0.3f);
        SendTouch(tree, hold, pressed: true);
        await WaitFramesAsync(tree, 12);
        await TouchStepAsync(tree, "touch-held-card", $"cardplay={InCardPlay(tree)}", results);
        SendTouch(tree, hold, pressed: false);
        await WaitFramesAsync(tree, 30);
        if (InspectOpen(game)) { await TapAsync(tree, new Vector2(540f, 420f)); await WaitFramesAsync(tree, 40); }

        IList deck = DeckCards(player);
        object overlays = StaticProp(GameType(ScreensNs + "Overlays.NOverlayStack"), "Instance")!;
        CallStatic(GameType(ScreensNs + "CardSelection.NChooseACardSelectionScreen"), "ShowScreen",
            TypedList("MegaCrit.Sts2.Core.Models.CardModel", deck.Cast<object>()
                .OrderBy(c => c.GetType().Name == "Bash" ? 0 : 1).Take(3).OrderBy(c => c.GetType().Name == "Bash" ? 1 : 0)), true);
        await WaitFramesAsync(tree, 60);
        Control? rewardCard = Descendants(tree.Root).OfType<Control>()
            .Where(c => c.GetType().Name.EndsWith("CardHolder", StringComparison.Ordinal) && c.GetType().Name != "NHandCardHolder" && c.IsVisibleInTree()
                && c.GetType().GetProperty("CardModel")?.GetValue(c)?.GetType().Name == "Bash")
            .FirstOrDefault();
        string how = "no reward card";
        Vector2 press = Vector2.Zero;
        bool pressed = false;
        if (rewardCard != null)
        {
            // Aim with the canvas transform: the overlay screens sit on their own canvas layer.
            Control hit = rewardCard.GetNodeOrNull<Control>("%Hitbox") ?? rewardCard.GetNodeOrNull<Control>("Hitbox") ?? rewardCard;
            Vector2 centre = hit.GetGlobalTransformWithCanvas() * (hit.Size * 0.5f);
            SendMouseMotion(tree.Root.GetFinalTransform() * centre, Vector2.Zero, leftMask: false);
            await WaitFramesAsync(tree, 40);
            Control? under = tree.Root.GuiGetHoveredControl();
            how = $"hovered {rewardCard.Name} at=({centre.X:F0},{centre.Y:F0}) focused={Field(rewardCard, "_isHovered")} block={TipsBlocked()} tips={TipSetCount(game)}"
                + $" hitbox={hit.GetType().Name} enabled={Field(hit, "_isEnabled")} filter={hit.MouseFilter} under={(under == null ? "none" : under.GetType().Name + ":" + under.GetPath().ToString().Split('/').TakeLast(3).Aggregate((a, b) => a + "/" + b) + " filter=" + under.MouseFilter)}";
            if (TipSetCount(game) == 0)
            {
                // A phone shows the tip while the finger is down on the card: press and hold, then slide off before letting go.
                press = centre;
                SendTouch(tree, press, pressed: true);
                pressed = true;
                await WaitFramesAsync(tree, 40);
                Control? underPress = tree.Root.GuiGetHoveredControl();
                how += $" pressed: focused={Field(rewardCard, "_isHovered")} under={(underPress == null ? "none" : underPress.GetType().Name + ":" + underPress.Name)}";
            }
        }
        await TouchStepAsync(tree, "touch-reward-tip", how + " " + DescribeTipSets(game), results);
        if (pressed)
        {
            for (int i = 1; i <= 8; i++)
            {
                SendTouchDrag(tree, press.Lerp(new Vector2(540f, 300f), i / 8f), press.Lerp(new Vector2(540f, 300f), (i - 1) / 8f));
                await WaitFramesAsync(tree, 2);
            }
            SendTouch(tree, new Vector2(540f, 300f), pressed: false);
            await WaitFramesAsync(tree, 10);
        }
        SendMouseMotion(tree.Root.GetFinalTransform() * new Vector2(540f, 300f), Vector2.Zero, leftMask: false);
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        GD.Print("Touch proof results: " + string.Join("; ", results));
    }

    /// <summary>Portrait Mode off from the main menu, for the proofs that start in landscape.</summary>
    private static async Task EnsureLandscapeAsync(SceneTree tree, NGame game)
    {
        if (!STS2Mobile.Platform.SafeAreaCanvas.IsPortrait) return;
        await OpenGraphicsSettingsAsync(tree, game);
        await StepPaginatorAsync(tree, "PhonePortraitMode", 0, expectPortrait: false);
        PopAll(Prop(game.MainMenu!, "SubmenuStack")!);
        await WaitFramesAsync(tree, 25);
    }

    private static async Task<string> DragStrikeOntoEnemyAsync(SceneTree tree, int enemyIndex, int cardIndex, float fy = 0.4f)
    {
        List<Control> enemies = GetLivingEnemyNodes(tree, Sts2);
        if (enemyIndex >= enemies.Count) return "no such enemy";
        Control enemy = enemies[enemyIndex];
        Control hitbox = (Control)enemy.GetType().GetProperty("Hitbox")!.GetValue(enemy)!;
        Vector2 target = hitbox.GetGlobalTransformWithCanvas() * (hitbox.Size * 0.5f);
        int hpBefore = CreatureHp(enemy);
        List<Control> holders = HandHolders(tree);
        int handBefore = holders.Count;
        Control holder = holders[Math.Clamp(cardIndex, 0, holders.Count - 1)];
        string card = CardId(holder);
        Vector2 start = PointOnCard(holder, 0.12f, fy);
        await DragAsync(tree, start, target, steps: 14, holdFrames: 8);
        await WaitFramesAsync(tree, 70);
        int hpAfter = CreatureHp(enemy);
        int handAfter = HandHolders(tree).Count;
        bool played = handAfter == handBefore - 1 && hpAfter < hpBefore;
        return $"card={card} from=({start.X:F0},{start.Y:F0}) to=({target.X:F0},{target.Y:F0}) played={played} hp={hpBefore}->{hpAfter} hand={handBefore}->{handAfter} cardplay={InCardPlay(tree)}";
    }

    /// <summary>Where each card's hitbox and drawn card are, and which control the GUI picks at each test point.</summary>
    private static async Task LogHandGeometry(SceneTree tree, string label)
    {
        List<Control> holders = HandHolders(tree);
        if (holders.Count > 0)
        {
            for (Node? n = holders[0].GetParent(); n is Control a; n = n.GetParent())
                GD.Print($"Touch ancestor {label} {a.GetType().Name}:{a.Name} rect={CanvasRect(a)} size={a.Size} clip={a.ClipContents} filter={a.MouseFilter} z={a.ZIndex} top={a.TopLevel}");
        }
        for (int i = 0; i < holders.Count; i++)
        {
            Control h = holders[i];
            Control? hit = h.GetNodeOrNull<Control>("%Hitbox") ?? h.GetNodeOrNull<Control>("Hitbox");
            Control? card = h.GetType().GetProperty("CardNode")?.GetValue(h) as Control;
            string hitText = hit == null ? "none" : $"pos={hit.Position} size={hit.Size} filter={hit.MouseFilter} enabled={hit.GetType().GetProperty("IsEnabled")?.GetValue(hit)} rect={CanvasRect(hit)}";
            string cardText = card == null ? "none" : $"pos={card.Position} size={card.Size} pivot={card.PivotOffset} rect={CanvasRect(card)}";
            Vector2 probe = PointOnCard(h, 0.12f, 0.4f);
            Control? over = null;
            GD.Print($"Touch geometry {label} {i} {CardId(h)} holder={h.Position} rot={h.RotationDegrees:F1} scale={h.Scale.X:F2} z={h.ZIndex} hitbox[{hitText}] card[{cardText}] probe=({probe.X:F0},{probe.Y:F0}) over={over?.GetParent()?.Name}/{over?.Name}");
        }
    }

    private static int TipSetCount(NGame game) =>
        game.GetType().GetProperty("HoverTipsContainer", AnyInstance)?.GetValue(game) is Node container ? container.GetChildCount() : -1;

    private static string TipsBlocked() =>
        GameType("MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet").GetField("shouldBlockHoverTips", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)?.ToString() ?? "?";

    private static string DescribeTipSets(NGame game)
    {
        if (game.GetType().GetProperty("HoverTipsContainer", AnyInstance)?.GetValue(game) is not Node container) return "tips=?";
        var parts = new List<string>();
        foreach (Node set in container.GetChildren())
        {
            if (set is not Control c) continue;
            var cols = c.GetChildren().OfType<Control>().Select(col => $"{col.Name}:{(col.Visible ? "" : "hidden ")}{CanvasRect(col)} x{col.Scale.X:F2} n={col.GetChildCount()}");
            parts.Add($"{c.GetType().Name}{(c.Visible ? "" : "(hidden)")}[{string.Join(" ", cols)}]");
        }
        return $"tips={parts.Count} {string.Join("; ", parts)}";
    }

    private static string CanvasRect(Control c)
    {
        Transform2D x = c.GetGlobalTransformWithCanvas();
        Vector2 a = x * Vector2.Zero, b = x * c.Size;
        return $"({a.X:F0},{a.Y:F0})-({b.X:F0},{b.Y:F0})";
    }

    private static async Task TouchStepAsync(SceneTree tree, string step, string result, List<string> results)
    {
        results.Add($"{step} {result}");
        Vector2I win = DisplayServer.WindowGetSize();
        GD.Print($"Portrait proof step: {step} window={win.X}x{win.Y} portrait={STS2Mobile.Platform.SafeAreaCanvas.IsPortrait} result=[{result}] {PortraitAudit.Describe(tree.Root)}");
        GD.Print($"Portrait small: {step} {PortraitAudit.DescribeSmall(tree.Root)}");
        await WaitForHostStepAckAsync(tree, step);
    }

    private static List<Control> HandHolders(SceneTree tree) =>
        Descendants(tree.Root).OfType<Control>()
            .Where(n => n.GetType().FullName == HandHolderType && n.GetParent()?.Name == "CardHolderContainer" && n.IsVisibleInTree())
            .ToList();

    /// <summary>A point on the card's hitbox, as a fraction of its width and height (0.12 is inside the strip a neighbour leaves visible).</summary>
    private static Vector2 PointOnCard(Control holder, float fx, float fy)
    {
        Control hitbox = holder.GetNodeOrNull<Control>("%Hitbox") ?? holder.GetNodeOrNull<Control>("Hitbox") ?? holder;
        return hitbox.GetGlobalTransformWithCanvas() * new Vector2(hitbox.Size.X * fx, hitbox.Size.Y * fy);
    }

    private static string CardId(Control holder)
    {
        object? model = holder.GetType().GetProperty("CardModel")?.GetValue(holder);
        object? id = model?.GetType().GetProperty("Id")?.GetValue(model);
        return id?.GetType().GetProperty("Entry")?.GetValue(id) as string ?? "?";
    }

    private static int CreatureHp(Control creature)
    {
        object? entity = creature.GetType().GetProperty("Entity")?.GetValue(creature);
        return Convert.ToInt32(entity?.GetType().GetProperty("CurrentHp")?.GetValue(entity) ?? -1);
    }

    // Before its first use in a launch the screen reads as visible though nothing is shown, so this is only meaningful after an
    // inspect was opened once (the landscape proof counts PhoneTouchCards.InspectsOpened instead).
    private static bool InspectOpen(NGame game) => Call(game, "GetInspectCardScreen") is CanvasItem screen && screen.Visible;

    private static bool InCardPlay(SceneTree tree)
    {
        Control? hand = Descendants(tree.Root).OfType<Control>().FirstOrDefault(c => c.GetType().Name == "NPlayerHand");
        return hand != null && (bool)(hand.GetType().GetProperty("InCardPlay")?.GetValue(hand) ?? false);
    }

    private static void SendTouch(SceneTree tree, Vector2 canvasPos, bool pressed)
    {
        Vector2 win = tree.Root.GetFinalTransform() * canvasPos;
        Input.ParseInputEvent(new InputEventScreenTouch { Index = 0, Pressed = pressed, Position = win });
    }

    private static void SendTouchDrag(SceneTree tree, Vector2 canvasPos, Vector2 canvasPrev)
    {
        Transform2D toWin = tree.Root.GetFinalTransform();
        Vector2 win = toWin * canvasPos;
        Input.ParseInputEvent(new InputEventScreenDrag { Index = 0, Position = win, Relative = win - toWin * canvasPrev });
    }

    private static async Task TapAsync(SceneTree tree, Vector2 canvasPos)
    {
        SendTouch(tree, canvasPos, pressed: true);
        await WaitFramesAsync(tree, 4);
        SendTouch(tree, canvasPos, pressed: false);
    }

    private static async Task DragAsync(SceneTree tree, Vector2 from, Vector2 to, int steps, int holdFrames = 2)
    {
        // Which hand card's hitbox gets the press (several cards can share an id, so log the index).
        var hooks = new List<(Control Hitbox, Callable Hook)>();
        List<Control> before = HandHolders(tree);
        Control? handNode = Descendants(tree.Root).OfType<Control>().FirstOrDefault(c => c.GetType().Name == "NPlayerHand");
        object? focused = handNode?.GetType().GetProperty("FocusedHolder", AnyInstance)?.GetValue(handNode);
        for (int i = 0; i < before.Count; i++)
        {
            int index = i;
            string id = CardId(before[i]);
            if ((before[i].GetNodeOrNull<Control>("%Hitbox") ?? before[i].GetNodeOrNull<Control>("Hitbox")) is not { } hb) continue;
            Callable hook = Callable.From<InputEvent>(ev => GD.Print($"Touch hitbox press: card {index} {id} at {(ev as InputEventMouse)?.Position} pressed={ev.IsPressed()}"));
            hb.Connect("MousePressed", hook);
            hooks.Add((hb, hook));
        }
        int focusedIndex = focused is Control fc ? before.IndexOf(fc) : -1;
        GD.Print($"Touch drag from ({from.X:F0},{from.Y:F0}): focused before={focusedIndex} pointer before={tree.Root.GetMousePosition()}");
        SendTouch(tree, from, pressed: true);
        await WaitFramesAsync(tree, 3);
        foreach ((Control hb, Callable hook) in hooks)
            if (GodotObject.IsInstanceValid(hb) && hb.IsConnected("MousePressed", hook)) hb.Disconnect("MousePressed", hook);
        Control? hovered = tree.Root.GuiGetHoveredControl();
        Control? hand = Descendants(tree.Root).OfType<Control>().FirstOrDefault(c => c.GetType().Name == "NPlayerHand");
        object? play = hand?.GetType().GetField("_currentCardPlay", AnyInstance)?.GetValue(hand);
        string held = play != null && GodotObject.IsInstanceValid(play as GodotObject) && play.GetType().GetProperty("Holder")?.GetValue(play) is Control ph ? CardId(ph) + "@" + ph.GetIndex() : "none";
        GD.Print($"Touch press at ({from.X:F0},{from.Y:F0}): hovered={hovered?.GetParent()?.Name}/{hovered?.Name} cardplay={InCardPlay(tree)} held={held}");
        Vector2 prev = from;
        for (int s = 1; s <= steps; s++)
        {
            Vector2 p = from.Lerp(to, (float)s / steps);
            SendTouchDrag(tree, p, prev);
            prev = p;
            await WaitFramesAsync(tree, 2);
        }
        await WaitFramesAsync(tree, holdFrames);
        SendTouch(tree, to, pressed: false);
    }

    private static async Task WaitMsecAsync(SceneTree tree, ulong msec)
    {
        ulong end = Time.GetTicksMsec() + msec;
        while (Time.GetTicksMsec() < end) await WaitFramesAsync(tree, 1);
    }
}
