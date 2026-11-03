using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.FullGame;

/// <summary>
/// The launcher is portrait and the game landscape. Each side has its own canvas: the launcher's is 1080 across, the game's the
/// 1920x1080 of its settings, both stretched with "expand" so any phone shape fits.
/// </summary>
public static class LauncherOrientation
{
    private static readonly Vector2I GameCanvas = new(1920, 1080);
    private const long TurnLimitMilliseconds = 2000;

    public static void UsePortrait(Window window)
    {
        ScreenOrientation.Set(landscape: false);
        window.ContentScaleSize = LauncherTheme.PortraitCanvas;
    }

    /// <summary>Turns to landscape and waits until iOS has turned the window, so the game starts at the size it plays at.</summary>
    public static async Task UseLandscapeAsync(Window window)
    {
        ScreenOrientation.Set(landscape: true);
        window.ContentScaleSize = GameCanvas;
        var clock = Stopwatch.StartNew();
        Vector2I size = DisplayServer.WindowGetSize();
        while (size.X < size.Y && clock.ElapsedMilliseconds < TurnLimitMilliseconds)
        {
            await window.ToSignal(window.GetTree(), SceneTree.SignalName.ProcessFrame);
            size = DisplayServer.WindowGetSize();
        }
        GD.Print($"Orientation: landscape for the game, window {size.X}x{size.Y} after {clock.ElapsedMilliseconds} ms.");
    }
}
