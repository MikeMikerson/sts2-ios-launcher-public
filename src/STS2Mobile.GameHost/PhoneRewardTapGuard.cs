using System;
using System.Collections.Generic;
using Godot;

namespace STS2Mobile.GameHost;

/// <summary>
/// Stops a tap from taking a reward the player never saw. On a phone the card pick screen opened with no rewards list in
/// sight: the phone's log showed the list did open, and two taps on one spot within a second took the gold and then the card reward
/// that slid up under the finger. So a reward row takes no touch while the rewards window is still fading in, or until a short
/// moment after the rows last moved (a reward taken, the list back from the card screen), and a finger already down when the
/// rows settle cannot take one either (the game takes a reward on a release that follows a press on the same row). Lists
/// that open in portrait only; reached by type name, as the host compiles against a stub that names only what it must.
/// </summary>
internal sealed class PhoneRewardTapGuard
{
    private const string ScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen";
    private const ulong SettleMs = 350;

    private readonly Dictionary<ulong, ScreenState> _screens = new();
    private bool _failed;

    private sealed class ScreenState
    {
        public string Layout = "";
        public ulong SettledAt;
        public readonly Dictionary<ulong, Control.MouseFilterEnum> Blocked = new();
    }

    /// <summary>Whether reward rows on this screen take touches now (for the proof runs).</summary>
    public static bool IsOpen(Control screen) =>
        Instance?._screens.TryGetValue(screen.GetInstanceId(), out ScreenState? s) == true && s.Blocked.Count == 0;

    /// <summary>Reward lists now guarded (for the proof runs): 0 in landscape.</summary>
    public static int TrackedForProof() => Instance?._screens.Count ?? 0;

    internal static PhoneRewardTapGuard? Instance { get; private set; }

    public PhoneRewardTapGuard() => Instance = this;

    public void OnNodeAdded(Node node)
    {
        if (_failed || node.GetType().FullName != ScreenTypeName) return;
        ulong id = node.GetInstanceId();
        if (_screens.ContainsKey(id)) return;
        _screens[id] = new ScreenState { SettledAt = Time.GetTicksMsec() + SettleMs };
        node.TreeExiting += () => _screens.Remove(id);
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
            GD.PrintErr("Phone reward tap guard: turned off after an error: ", error);
        }
    }

    private static void Step(Control screen, ScreenState state)
    {
        if (screen.GetNodeOrNull<Control>("%RewardsContainer") is not { } container) return;
        var rows = new List<Control>();
        CollectRows(container, rows, 0);

        // Where the rows are, coarsely: a row taken, added or slid is a change.
        var layout = new System.Text.StringBuilder();
        foreach (Control row in rows)
            layout.Append(row.GetInstanceId()).Append(':').Append(MathF.Round(row.GlobalPosition.Y / 8f)).Append(';');
        string now = layout.ToString();
        ulong ticks = Time.GetTicksMsec();
        if (now != state.Layout)
        {
            state.Layout = now;
            state.SettledAt = ticks + SettleMs;
        }

        bool fading = screen.GetNodeOrNull<CanvasItem>("Rewards") is { } window && window.Modulate.A < 0.99f;
        bool guard = fading || ticks < state.SettledAt || !screen.IsVisibleInTree();

        if (guard)
        {
            foreach (Control row in rows)
            {
                ulong rowId = row.GetInstanceId();
                if (state.Blocked.ContainsKey(rowId)) continue;
                state.Blocked[rowId] = row.MouseFilter;
                row.MouseFilter = Control.MouseFilterEnum.Ignore;
            }
        }
        else if (state.Blocked.Count > 0)
        {
            foreach ((ulong rowId, Control.MouseFilterEnum filter) in state.Blocked)
                if (GodotObject.InstanceFromId(rowId) is Control row && GodotObject.IsInstanceValid(row))
                    row.MouseFilter = filter;
            state.Blocked.Clear();
        }
    }

    /// <summary>The reward buttons in the list, also those inside a linked set (a choice of one of several rewards).</summary>
    private static void CollectRows(Node node, List<Control> rows, int depth)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is not Control control || control.IsQueuedForDeletion()) continue;
            if (control.GetType().Name == "NRewardButton") rows.Add(control);
            else if (depth < 4) CollectRows(control, rows, depth + 1);
        }
    }
}
