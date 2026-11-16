using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// The portrait fallback for desktop screens that have no reflow of their own: the screen is laid out on a virtual canvas
/// as wide as its content needs (by default the game's narrowest supported shape, 4:3 at 1680 x 1260) and as tall as the portrait
/// band, then scaled down to the portrait width. Nothing reaches past the screen edges, touches still land where they are drawn,
/// and content anchored to the top and bottom spreads out over the tall screen. Every edit is captured first so turning Portrait
/// Mode off restores the desktop layout live.
/// </summary>
internal sealed class PortraitScreenFit
{
    private const string Ns = "MegaCrit.Sts2.Core.Nodes.Screens.";

    private const float BottomMargin = 40f;

    /// <summary>
    /// Screens fitted this way, with the width their content needs, measured with the fallback off
    /// (<c>STS2_PORTRAIT_NOFIT=1</c>): 1080 means the screen fits at full size and only moves into the band under the notch.
    /// </summary>
    public static readonly Dictionary<string, float> FittedScreens = new(StringComparer.Ordinal)
    {
        [Ns + "MainMenu.NMultiplayerSubmenu"] = 1080f,
        [Ns + "MainMenu.NCompendiumSubmenu"] = 1540f,
        [Ns + "MainMenu.NJoinFriendScreen"] = 1080f,
        [Ns + "CharacterSelect.NMultiplayerLoadGameScreen"] = 1310f,
        // Custom runs (the load screen): 1380 rather than 1310 so the ascension arrows (shown once an ascension is unlocked,
        // centred in the left half) stay inside; seen on the phone with a real profile. The custom run and the
        // daily run are laid out in one column, larger (PortraitSubmenuLayout).
        [PortraitSubmenuLayout.CustomRunTypeName] = PortraitSubmenuLayout.DesignWidths[PortraitSubmenuLayout.CustomRunTypeName],
        [Ns + "CustomRun.NCustomRunLoadScreen"] = 1380f,
        [PortraitSubmenuLayout.DailyRunTypeName] = PortraitSubmenuLayout.DesignWidths[PortraitSubmenuLayout.DailyRunTypeName],
        [Ns + "DailyRun.NDailyRunLoadScreen"] = 1310f,
        [PortraitSubmenuLayout.CardLibraryTypeName] = PortraitSubmenuLayout.CardLibraryDesignWidth,
        [Ns + "RelicCollection.NRelicCollection"] = 1080f,
        [Ns + "PotionLab.NPotionLab"] = 1080f,
        // Run history, stats and the profiles (1430, 1240 and 1380 to the width) are laid out larger.
        [PortraitSubmenuLayout.RunHistoryTypeName] = PortraitSubmenuLayout.RunHistoryDesignWidth,
        [PortraitSubmenuLayout.StatsTypeName] = PortraitSubmenuLayout.StatsDesignWidth,
        // The timeline keeps its size; its reminder line is kept inside the width.
        [PortraitSubmenuLayout.TimelineTypeName] = 1080f,
        [PortraitSubmenuLayout.ProfileTypeName] = PortraitSubmenuLayout.ProfileDesignWidth,
        [Ns + "ModdingScreen.NModdingScreen"] = 1080f,
        [Ns + "Dread.NDreadScreen"] = 1220f,
        // In a run, under the top bar. The pause menu is one narrow column of buttons (420 wide, each 80 tall), drawn larger
        // for touch: at 672 its entries are 44.6 points tall (800 gave 37.5).
        [Ns + "PauseMenu.NPauseMenu"] = 672f,
        [Ns + "CardSelection.NChooseABundleSelectionScreen"] = 1160f,
    };

    // Every screen PortraitSubmenuLayout lays out is fitted at its width there (patch notes, the bestiary and the unlock
    // screens are listed only there).
    static PortraitScreenFit()
    {
        foreach ((string typeName, float designWidth) in PortraitSubmenuLayout.DesignWidths) FittedScreens[typeName] = designWidth;
    }

    /// <summary>Screens shown in a run, below the top bar.</summary>
    public static readonly HashSet<string> InRunScreens = new(StringComparer.Ordinal)
    {
        Ns + "PauseMenu.NPauseMenu", Ns + "CardSelection.NChooseABundleSelectionScreen",
    };

    /// <summary>Screens drawn larger than their desktop size: the pause menu and the submenus laid out in one column.</summary>
    private static readonly HashSet<string> Enlarged = new(PortraitSubmenuLayout.DesignWidths.Keys, StringComparer.Ordinal)
    {
        Ns + "PauseMenu.NPauseMenu",
    };

    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private readonly Dictionary<ulong, float> _fitted = new();
    private readonly HashSet<ulong> _logged = new();

    // STS2_PORTRAIT_NOFIT=1 turns the fallback off, so a proof run can measure what each screen needs at full size.
    private static readonly bool Disabled = OS.GetEnvironment("STS2_PORTRAIT_NOFIT") == "1";

    public static bool Handles(string typeName) => !Disabled && FittedScreens.ContainsKey(typeName);

    public void ApplyNode(Control screen, string typeName, float bandTop, Vector2 canvas)
    {
        float designWidth = FittedScreens[typeName];
        if (!_snapshots.ContainsKey(screen.GetInstanceId()))
            _snapshots[screen.GetInstanceId()] = new PortraitControlSnapshot(screen, captureVisible: false);
        _fitted[screen.GetInstanceId()] = bandTop;
        Fit(screen, designWidth, Enlarged.Contains(typeName), bandTop, canvas);
        PortraitSubmenuLayout.Apply(screen, typeName, Capture);
        if (_logged.Add(screen.GetInstanceId()))
            GD.Print($"Portrait screen fit: {screen.GetType().Name} design={designWidth:F0} scale={screen.Scale.X:F3} size={screen.Size.X:F0}x{screen.Size.Y:F0} top={bandTop:F0}");
    }

    /// <summary>Holds the fit each frame: some screens reset their own rectangle when they open or the window changes.</summary>
    public void OnProcessFrame(Window root, float capHeight)
    {
        Vector2 canvas = root.GetVisibleRect().Size;
        foreach ((ulong id, float bandTop) in _fitted)
        {
            if (GodotObject.InstanceFromId(id) is not Control screen || !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree()) continue;
            string? typeName = screen.GetType().FullName;
            if (typeName == null || !FittedScreens.TryGetValue(typeName, out float designWidth)) continue;
            Fit(screen, designWidth, Enlarged.Contains(typeName), bandTop, canvas);
            PortraitSubmenuLayout.Apply(screen, typeName, Capture);
        }
    }

    private void Capture(Control control)
    {
        if (!_snapshots.ContainsKey(control.GetInstanceId()))
            _snapshots[control.GetInstanceId()] = new PortraitControlSnapshot(control, captureVisible: false);
    }

    private static void Fit(Control screen, float designWidth, bool enlarge, float bandTop, Vector2 canvas)
    {
        VirtualCanvas fit = PortraitFitMath.Fit(canvas.X, bandTop, canvas.Y - BottomMargin, designWidth, enlarge);
        // The parent's own position on the canvas, so the band is measured on the canvas even under an offset parent.
        Vector2 parentOrigin = screen.GetParent() is Control parent ? parent.GetGlobalTransformWithCanvas().Origin : Vector2.Zero;
        var position = new Vector2(fit.Left, fit.Top) - parentOrigin;
        var size = new Vector2(fit.Width, fit.Height);
        var scale = new Vector2(fit.Scale, fit.Scale);
        if (screen.AnchorLeft != 0f || screen.AnchorTop != 0f || screen.AnchorRight != 0f || screen.AnchorBottom != 0f)
        {
            screen.AnchorLeft = 0f;
            screen.AnchorTop = 0f;
            screen.AnchorRight = 0f;
            screen.AnchorBottom = 0f;
        }
        if (screen.PivotOffset != Vector2.Zero) screen.PivotOffset = Vector2.Zero;
        if (screen.Scale != scale) screen.Scale = scale;
        if (screen.Position.DistanceSquaredTo(position) > 0.25f) screen.Position = position;
        if (screen.Size.DistanceSquaredTo(size) > 0.25f) screen.Size = size;
    }

    public void RestoreLandscape()
    {
        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
        PortraitSubmenuLayout.RestoreLandscape();
        _fitted.Clear();
    }

    public void PruneDead()
    {
        foreach (ulong id in _snapshots.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _snapshots.Remove(id);
        foreach (ulong id in _fitted.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _fitted.Remove(id);
        PortraitSubmenuLayout.PruneDead();
    }
}
