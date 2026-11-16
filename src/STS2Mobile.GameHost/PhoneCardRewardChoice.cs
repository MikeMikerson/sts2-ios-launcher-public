using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.GameHost;

/// <summary>
/// Card rewards picked by a tap and then a Choose button, on screens that open in portrait. On the game's
/// card reward screen one click takes a card and closes the screen, so on a phone a touch meant to read a card took it. Here a
/// tap selects a card (outlined), a tap on another card moves the selection, and a Choose button beside Skip, dimmed until a card
/// is selected, takes it. Skip and the other alternatives work as before.
/// <para>How: the screen answers the reward through its <c>_completionSource</c>, which a card's press completes with the card's
/// index. While the screen waits, that field holds a stand-in: a press completes the stand-in, which this pass reads as a
/// selection and replaces; Choose (or an alternative such as Skip) completes the game's own source. The game's types are reached
/// by name (the host compiles against a stub that names only what it must).</para>
/// </summary>
internal sealed class PhoneCardRewardChoice
{
    public const string ChooseButtonName = "PhoneChooseButton";
    private const string OutlineName = "PhoneSelectedOutline";
    private const string ScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen";
    private const string ButtonTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardAlternativeButton";
    private const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticAll = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    // The card's own outline colour (NCardHighlight.playableColor), as a border only; nothing is laid over the card.
    private static readonly Color OutlineColor = new(0f, 0.957f, 0.988f, 0.98f);
    private static readonly Color Dimmed = new(0.5f, 0.5f, 0.5f, 0.7f);

    private readonly Dictionary<ulong, ScreenState> _screens = new();
    private readonly HashSet<ulong> _wiredHitboxes = new();
    private readonly FieldInfo? _source;
    private readonly FieldInfo? _options;
    private readonly MethodInfo? _createButton;
    private readonly MethodInfo? _enable;
    private readonly MethodInfo? _disable;
    private readonly PropertyInfo? _isEnabled;
    private readonly FieldInfo? _isHovered;
    private bool _failed;

    private sealed class ScreenState
    {
        public TaskCompletionSource<int?>? Real;
        public TaskCompletionSource<int?>? StandIn;
        public int Selected = -1;
        public ulong SelectedHolder;
        public Control? Choose;
    }

    /// <summary>The selected card's index on a screen, or -1 (for the proof runs).</summary>
    public static int SelectedIndex(Control screen) => Instance?._screens.TryGetValue(screen.GetInstanceId(), out ScreenState? s) == true ? s.Selected : -1;

    /// <summary>Card reward screens and card hitboxes now under tap-then-Choose (for the proof runs): 0 in landscape.</summary>
    public static int TrackedForProof() => Instance == null ? 0 : Instance._screens.Count + Instance._wiredHitboxes.Count;

    internal static PhoneCardRewardChoice? Instance { get; private set; }

    /// <summary>The option a card holder on the screen shows, or -1 (for the proof runs).</summary>
    public static int OptionIndexOf(Control screen, Control holder) => Instance?.IndexOfHolder(screen, holder.GetInstanceId()) ?? -1;

    public PhoneCardRewardChoice()
    {
        Instance = this;
        Assembly sts2 = typeof(NGame).Assembly;
        Type? screenType = sts2.GetType(ScreenTypeName);
        Type? buttonType = sts2.GetType(ButtonTypeName);
        Type? clickable = sts2.GetType("MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl");
        _source = screenType?.GetField("_completionSource", InstAll);
        _options = screenType?.GetField("_options", InstAll);
        _createButton = buttonType?.GetMethod("Create", StaticAll, [typeof(string), typeof(string)]);
        _enable = clickable?.GetMethod("Enable", InstAll, Type.EmptyTypes);
        _disable = clickable?.GetMethod("Disable", InstAll, Type.EmptyTypes);
        _isEnabled = clickable?.GetProperty("IsEnabled", InstAll);
        _isHovered = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder")?.GetField("_isHovered", InstAll);
        if (_source?.FieldType != typeof(TaskCompletionSource<int?>) || _options == null || _createButton == null ||
            _enable == null || _disable == null || _isEnabled == null)
        {
            _failed = true;
            GD.PrintErr("Phone card reward choice: the game's card reward screen changed; one tap takes a card as in the game.");
        }
    }

    public void OnNodeAdded(Node node)
    {
        if (_failed || node.GetType().FullName != ScreenTypeName) return;
        ulong id = node.GetInstanceId();
        if (_screens.ContainsKey(id)) return;
        _screens[id] = new ScreenState();
        node.TreeExiting += () => OnScreenExiting(id);
    }

    public void OnProcessFrame()
    {
        if (_failed || _screens.Count == 0) return;
        try
        {
            List<ulong>? dead = null;
            foreach ((ulong id, ScreenState state) in _screens)
            {
                if (GodotObject.InstanceFromId(id) is not Control screen || !GodotObject.IsInstanceValid(screen))
                {
                    (dead ??= new List<ulong>()).Add(id);
                    continue;
                }
                if (screen.IsInsideTree()) Step(screen, state);
            }
            if (dead != null) foreach (ulong id in dead) _screens.Remove(id);
        }
        catch (Exception error)
        {
            _failed = true;
            GD.PrintErr("Phone card reward choice: turned off after an error: ", error);
        }
    }

    private void Step(Control screen, ScreenState state)
    {
        // The game asks again for each pick (OptionSelected makes a new source): put a stand-in in front of each one.
        if (_source!.GetValue(screen) is TaskCompletionSource<int?> current && current != state.StandIn && !current.Task.IsCompleted)
        {
            state.Real = current;
            state.StandIn = new TaskCompletionSource<int?>();
            _source.SetValue(screen, state.StandIn);
        }

        EnsureChooseButton(screen, state);
        WireCardPresses(screen);

        if (state.StandIn is { Task.IsCompleted: true } done && state.Real is { } real)
        {
            int? index = done.Task.IsCompletedSuccessfully ? done.Task.Result : null;
            int cards = (_options!.GetValue(screen) as ICollection)?.Count ?? 0;
            if (index is int card && card >= 0 && card < cards)
            {
                if (card != state.Selected) Select(screen, state, card);
                state.StandIn = new TaskCompletionSource<int?>();
                _source.SetValue(screen, state.StandIn);
            }
            else if (done.Task.IsCompletedSuccessfully)
            {
                // Skip, Reroll or another alternative: the game's own answer, at once.
                Answer(screen, state, real, index);
            }
        }

        // New cards (a reroll) or a card gone: the selection goes with them.
        if (state.Selected >= 0 && !(GodotObject.InstanceFromId(state.SelectedHolder) is Control holder &&
                GodotObject.IsInstanceValid(holder) && !holder.IsQueuedForDeletion() && holder.IsInsideTree()))
            ClearSelection(state);

        RefreshChoose(state);
    }

    /// <summary>
    /// A press on a card selects it. Selecting takes nothing, so it need not wait for the release, and under touch the release
    /// did not reach the card on the simulator (the game's own take-on-release never fired for a still tap). The
    /// press also hovers that card, and only that card, for the game's enlarged look.
    /// </summary>
    private void WireCardPresses(Control screen)
    {
        if (screen.GetNodeOrNull<Control>("UI/CardRow") is not { } row) return;
        ulong screenId = screen.GetInstanceId();
        foreach (Node child in row.GetChildren())
        {
            if (child is not Control holder || holder.GetNodeOrNull<Control>("%Hitbox") is not { } hitbox) continue;
            ulong hitboxId = hitbox.GetInstanceId();
            if (!_wiredHitboxes.Add(hitboxId)) continue;
            ulong holderId = holder.GetInstanceId();
            hitbox.Connect("MousePressed", Callable.From((InputEvent e) => OnCardPressed(screenId, row, holderId, e)));
            hitbox.TreeExiting += () => _wiredHitboxes.Remove(hitboxId);
        }
    }

    private void OnCardPressed(ulong screenId, Control row, ulong holderId, InputEvent e)
    {
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left } || !GodotObject.IsInstanceValid(row)) return;
        if (_isHovered != null)
        {
            foreach (Node child in row.GetChildren())
            {
                if (child is not Control holder || holder.GetNodeOrNull<Control>("%Hitbox") is not { } hitbox) continue;
                bool hovered = _isHovered.GetValue(holder) is true;
                bool pressed = holder.GetInstanceId() == holderId;
                if (pressed && !hovered) hitbox.EmitSignal("Focused", hitbox);
                else if (!pressed && hovered) hitbox.EmitSignal("Unfocused", hitbox);
            }
        }
        if (!_screens.TryGetValue(screenId, out ScreenState? state) || state.Real is not { Task.IsCompleted: false }) return;
        if (GodotObject.InstanceFromId(screenId) is not Control screen || !GodotObject.IsInstanceValid(screen)) return;
        int index = IndexOfHolder(screen, holderId);
        if (index >= 0 && index != state.Selected) Select(screen, state, index);
    }

    /// <summary>
    /// The option a card holder shows, as the game counts it (its <c>_options</c>, matched by card). Not the holder's place in
    /// the row: the game moves a hovered card to the end of the row so it draws on top.
    /// </summary>
    private int IndexOfHolder(Control screen, ulong holderId)
    {
        if (GodotObject.InstanceFromId(holderId) is not Control holder || !GodotObject.IsInstanceValid(holder)) return -1;
        object? card = holder.GetType().GetProperty("CardModel", InstAll)?.GetValue(holder);
        if (card == null || _options!.GetValue(screen) is not IList options) return -1;
        for (int i = 0; i < options.Count; i++)
            if (ReferenceEquals(options[i]?.GetType().GetProperty("Card", InstAll)?.GetValue(options[i]), card)) return i;
        return -1;
    }

    private void Select(Control screen, ScreenState state, int index)
    {
        Control? holder = HolderAt(screen, index);
        ClearSelection(state);
        if (holder == null) return;
        state.Selected = index;
        state.SelectedHolder = holder.GetInstanceId();
        holder.AddChild(MakeOutline());
        GD.Print($"Phone card reward choice: card {index + 1} selected ({holder.GetType().GetProperty("CardModel", InstAll)?.GetValue(holder)?.GetType().Name}).");
    }

    private void Answer(Control screen, ScreenState state, TaskCompletionSource<int?> real, int? index)
    {
        ClearSelection(state);
        state.StandIn = null;
        state.Real = null;
        _source!.SetValue(screen, real);
        real.TrySetResult(index);
    }

    private void OnChoosePressed(ulong screenId)
    {
        if (!_screens.TryGetValue(screenId, out ScreenState? state) || state.Selected < 0 || state.Real is not { } real) return;
        if (GodotObject.InstanceFromId(screenId) is not Control screen || !GodotObject.IsInstanceValid(screen)) return;
        int index = state.Selected;
        GD.Print($"Phone card reward choice: card {index + 1} taken with Choose.");
        Answer(screen, state, real, index);
    }

    private void OnScreenExiting(ulong id)
    {
        // Closed without an answer (the game cancels its own source in _ExitTree; here that is the stand-in): cancel the real one.
        if (_screens.TryGetValue(id, out ScreenState? state) && state.Real is { } real && !real.Task.IsCompleted)
            real.TrySetException(new TaskCanceledException());
        _screens.Remove(id);
    }

    private Control? HolderAt(Control screen, int index)
    {
        if (screen.GetNodeOrNull<Control>("UI/CardRow") is not { } row) return null;
        foreach (Node child in row.GetChildren())
            if (child is Control holder && !holder.IsQueuedForDeletion() && IndexOfHolder(screen, holder.GetInstanceId()) == index)
                return holder;
        return null;
    }

    private static void ClearSelection(ScreenState state)
    {
        if (GodotObject.InstanceFromId(state.SelectedHolder) is Control holder && GodotObject.IsInstanceValid(holder) &&
            holder.GetNodeOrNull<Node>(OutlineName) is { } outline)
            outline.QueueFree();
        state.Selected = -1;
        state.SelectedHolder = 0;
    }

    /// <summary>A border around the card (300 x 422, drawn centred on its holder), the card's own outline colour.</summary>
    internal static Panel MakeOutline()
    {
        var style = new StyleBoxFlat
        {
            DrawCenter = false,
            BorderColor = OutlineColor,
            BorderWidthLeft = 8, BorderWidthTop = 8, BorderWidthRight = 8, BorderWidthBottom = 8,
            CornerRadiusTopLeft = 22, CornerRadiusTopRight = 22, CornerRadiusBottomLeft = 22, CornerRadiusBottomRight = 22,
        };
        var panel = new Panel
        {
            Name = OutlineName,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(-164f, -225f),
            Size = new Vector2(328f, 450f),
        };
        panel.AddThemeStyleboxOverride("panel", style);
        return panel;
    }

    private void EnsureChooseButton(Control screen, ScreenState state)
    {
        if (state.Choose != null && GodotObject.IsInstanceValid(state.Choose)) return;
        if (screen.GetNodeOrNull<Control>("UI/RewardAlternatives") is not { } alternatives) return;
        // The game's own Skip button, with "Choose" on it.
        if (_createButton!.Invoke(null, [ChooseText(), "ui_select"]) is not Control choose) return;
        choose.Name = ChooseButtonName;
        alternatives.AddChild(choose);
        alternatives.MoveChild(choose, 0);
        ulong screenId = screen.GetInstanceId();
        choose.Connect("Released", Callable.From((Control _) => OnChoosePressed(screenId)));
        state.Choose = choose;
        _disable!.Invoke(choose, null);
        choose.Modulate = Dimmed;
        GD.Print($"Phone card reward choice: Choose button added to {alternatives.GetType().Name} ({alternatives.GetChildCount()} buttons).");
    }

    private void RefreshChoose(ScreenState state)
    {
        if (state.Choose is not { } choose || !GodotObject.IsInstanceValid(choose)) return;
        bool on = state.Selected >= 0 && state.Real is { Task.IsCompleted: false };
        bool enabled = _isEnabled!.GetValue(choose) is true;
        if (on && !enabled)
        {
            _enable!.Invoke(choose, null);
            choose.Modulate = Colors.White;
        }
        else if (!on && enabled)
        {
            _disable!.Invoke(choose, null);
            choose.Modulate = Dimmed;
        }
    }

    /// <summary>"Choose" in English; in other languages the game's own word for selecting (Settings, Input).</summary>
    internal static string ChooseText()
    {
        try
        {
            Type? locManager = typeof(NGame).Assembly.GetType("MegaCrit.Sts2.Core.Localization.LocManager");
            object? manager = locManager?.GetProperty("Instance", StaticAll)?.GetValue(null);
            if (manager != null && locManager!.GetProperty("Language", InstAll)?.GetValue(manager) is string language && language != "eng")
            {
                var loc = new LocString("settings_ui", "INPUT_SETTINGS.INPUT_TITLE.select");
                if (typeof(LocString).GetMethod("GetFormattedText", InstAll, Type.EmptyTypes)?.Invoke(loc, null) is string text &&
                    !string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }
        catch (Exception)
        {
            // Fall back to English below.
        }
        return "Choose";
    }
}
