using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Portrait reflows for the main menu side screens that are used on every run: the singleplayer and multiplayer host mode cards wrap into
/// rows of two, and character select stacks the character art over the info panel at full size. Every edit is captured first so
/// turning Portrait Mode off restores the desktop layout live.
/// </summary>
internal sealed class PortraitMenuLayout
{
    private const string Ns = "MegaCrit.Sts2.Core.Nodes.Screens.";
    public const string SingleplayerTypeName = Ns + "MainMenu.NSingleplayerSubmenu";
    public const string MultiplayerHostTypeName = Ns + "MainMenu.NMultiplayerHostSubmenu";
    public const string CharacterSelectTypeName = Ns + "CharacterSelect.NCharacterSelectScreen";
    // Blitz picks a character on the same layout as character select (art, info panel, character buttons).
    public const string BlitzTypeName = Ns + "Blitz.NBlitzScreen";

    public static readonly HashSet<string> WatchedTypeNames = new(StringComparer.Ordinal)
    {
        SingleplayerTypeName, MultiplayerHostTypeName, CharacterSelectTypeName, BlitzTypeName,
    };

    private const string ModeFlowName = "PortraitModeFlow";
    private const float ModeCardWidth = 362f;
    private const float ModeCardHeight = 705f;
    private const float ModeGap = 24f;
    private const float BackButtonBand = 200f;

    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private readonly HashSet<ulong> _singleplayer = new();
    private readonly HashSet<ulong> _characterSelect = new();

    public void ApplyNode(Control screen, string typeName, float capHeight, Vector2 canvas)
    {
        switch (typeName)
        {
            case SingleplayerTypeName:
            case MultiplayerHostTypeName:
                _singleplayer.Add(screen.GetInstanceId());
                LayoutSingleplayer(screen, capHeight, canvas);
                break;
            case CharacterSelectTypeName:
            case BlitzTypeName:
                if (_characterSelect.Add(screen.GetInstanceId()))
                    GD.Print($"Portrait menu: {screen.GetType().Name} children {string.Join(",", screen.GetChildren().Select(c => c.Name.ToString()))}");
                _characterSelect.Add(screen.GetInstanceId());
                LayoutCharacterSelect(screen, capHeight, canvas);
                break;
        }
    }

    public void OnProcessFrame(Window root, float capHeight)
    {
        Vector2 canvas = root.GetVisibleRect().Size;
        foreach (ulong id in _singleplayer)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen) && screen.IsVisibleInTree())
                FitModeFlow(screen, capHeight, canvas);
        foreach (ulong id in _characterSelect)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen) && screen.IsVisibleInTree())
                KeepInfoPanel(screen);
    }

    // ---- singleplayer modes -----------------------------------------------------------------------------------------------

    private void LayoutSingleplayer(Control screen, float capHeight, Vector2 canvas)
    {
        if (screen.GetNodeOrNull<BoxContainer>("ButtonContainer") is not { } row) return;
        var flow = screen.GetNodeOrNull<HFlowContainer>(ModeFlowName);
        if (flow == null)
        {
            flow = new HFlowContainer { Name = ModeFlowName, MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = FlowContainer.AlignmentMode.Center };
            flow.AddThemeConstantOverride("h_separation", (int)ModeGap);
            flow.AddThemeConstantOverride("v_separation", (int)ModeGap);
            screen.AddChild(flow);
            screen.MoveChild(flow, row.GetIndex() + 1);
        }
        foreach (Node child in row.GetChildren().ToList())
        {
            child.SetMeta("portrait_row_index", child.GetIndex());
            child.Reparent(flow, keepGlobalTransform: false);
        }
        flow.Visible = true;
        FitModeFlow(screen, capHeight, canvas);
    }

    private static void FitModeFlow(Control screen, float capHeight, Vector2 canvas)
    {
        if (screen.GetNodeOrNull<HFlowContainer>(ModeFlowName) is not { Visible: true } flow) return;
        int visible = flow.GetChildren().OfType<Control>().Count(c => c.Visible);
        int rows = Math.Max(1, (visible + 1) / 2);
        float width = 2f * ModeCardWidth + ModeGap;
        float height = rows * ModeCardHeight + (rows - 1) * ModeGap;
        float band = canvas.Y - capHeight - BackButtonBand - 40f;
        float scale = Math.Min(1f, band / height);
        flow.AnchorLeft = flow.AnchorRight = 0.5f;
        flow.AnchorTop = flow.AnchorBottom = 0f;
        flow.OffsetLeft = -width / 2f;
        flow.OffsetRight = width / 2f;
        float top = capHeight + 20f + (band - height * scale) / 2f;
        flow.OffsetTop = top;
        flow.OffsetBottom = top + height;
        flow.PivotOffset = new Vector2(width / 2f, 0f);
        flow.Scale = new Vector2(scale, scale);
    }

    private static void RestoreSingleplayer(Control screen)
    {
        if (screen.GetNodeOrNull<BoxContainer>("ButtonContainer") is not { } row) return;
        if (screen.GetNodeOrNull<HFlowContainer>(ModeFlowName) is not { } flow) return;
        foreach (Node child in flow.GetChildren().OrderBy(c => (int)c.GetMeta("portrait_row_index", 0)).ToList())
        {
            child.Reparent(row, keepGlobalTransform: false);
            row.MoveChild(child, Math.Min((int)child.GetMeta("portrait_row_index", 0), row.GetChildCount() - 1));
        }
        flow.Visible = false;
    }

    // ---- character select -------------------------------------------------------------------------------------------------

    private void LayoutCharacterSelect(Control screen, float capHeight, Vector2 canvas)
    {
        // The art is 2560 x 1200 around the screen centre with the figure right of centre; it moves up over the top half and
        // left so the figure stands in the middle of the narrow screen.
        float artCentreY = capHeight + 640f;
        float shiftY = artCentreY - canvas.Y / 2f;
        foreach (string name in new[] { "StaticBg", "AnimatedBg" })
        {
            if (screen.GetNodeOrNull<Control>(name) is not { } art) continue;
            PortraitControlSnapshot snap = Capture(art);
            art.OffsetLeft = snap.InitialOffsetLeft + ArtShiftX;
            art.OffsetRight = snap.InitialOffsetRight + ArtShiftX;
            art.OffsetTop = snap.InitialOffsetTop + shiftY;
            art.OffsetBottom = snap.InitialOffsetBottom + shiftY;
        }

        // The info panel (526 x 434) sits under the art, above the ascension panel and the character buttons.
        if (screen.GetNodeOrNull<Control>("InfoPanel") is { } info)
        {
            Capture(info);
            PlaceInfoPanel(screen, info);
        }

        // Blitz's title, its "under construction" tag and its info button sit along the top edge, under the notch.
        foreach (string name in new[] { "ActLabel", "ActDropdown", "RemotePlayerContainer", "BlitzPlaceholder", "UnderConstruction", "BlitzInfoButton" })
        {
            if (screen.GetNodeOrNull<Control>(name) is not { } top) continue;
            PortraitControlSnapshot snap = Capture(top);
            top.OffsetTop = snap.InitialOffsetTop + capHeight;
            top.OffsetBottom = snap.InitialOffsetBottom + capHeight;
        }
    }

    private const float ArtShiftX = -240f;

    private static void PlaceInfoPanel(Control screen, Control info)
    {
        // Picking a character slides the panel in; while a slide exists the screen snaps the panel back to its last resting
        // spot first, so a slide under way when the layout runs would carry it back to the desktop spot. Stop and forget it: with
        // no slide the next pick starts from where the panel is.
        StopInfoSlide(screen);
        info.AnchorLeft = info.AnchorRight = 0.5f;
        info.AnchorTop = info.AnchorBottom = 1f;
        info.OffsetLeft = -263f;
        info.OffsetRight = 263f;
        info.OffsetBottom = -InfoBottomFromEdge;
        info.OffsetTop = -InfoBottomFromEdge - 434f;
    }

    private static void StopInfoSlide(Control screen)
    {
        if (GetField(screen, "_infoPanelTween") is Tween tween && tween.IsValid()) tween.Kill();
        SetField(screen, "_infoPanelTween", null);
    }

    /// <summary>Puts the info panel back on its portrait spot when the game has returned it to the desktop one.</summary>
    private void KeepInfoPanel(Control screen)
    {
        if (screen.GetNodeOrNull<Control>("InfoPanel") is not { } info || !_snapshots.ContainsKey(info.GetInstanceId())) return;
        if (GetField(screen, "_infoPanelTween") is Tween tween && tween.IsValid() && tween.IsRunning()) return;
        if (Math.Abs(info.OffsetLeft + 263f) < 0.5f && Math.Abs(info.OffsetBottom + InfoBottomFromEdge) < 0.5f) return;
        PlaceInfoPanel(screen, info);
    }

    private static object? GetField(object target, string name) =>
        target.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(target);

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(target, value);

    private const float InfoBottomFromEdge = 380f;

    // ---- shared -----------------------------------------------------------------------------------------------------------

    public void RestoreLandscape()
    {
        foreach (ulong id in _singleplayer)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen))
                RestoreSingleplayer(screen);
        foreach (ulong id in _characterSelect)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen))
                StopInfoSlide(screen);
        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
    }

    public void PruneDead()
    {
        foreach (ulong id in _snapshots.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _snapshots.Remove(id);
        _singleplayer.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _characterSelect.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
    }

    private PortraitControlSnapshot Capture(Control control)
    {
        if (!_snapshots.TryGetValue(control.GetInstanceId(), out PortraitControlSnapshot? snap))
            _snapshots[control.GetInstanceId()] = snap = new PortraitControlSnapshot(control, captureVisible: false);
        return snap;
    }
}
