using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.FullGame;

/// <summary>
/// The launcher is portrait and the game defaults to landscape, unless Portrait Mode and Set Portrait as Default are both
/// enabled in <see cref="PhoneGraphicsSettings"/>.
/// </summary>
public static class LauncherOrientation
{
    private static readonly Vector2I GameCanvas = new(1920, 1080);
    private const long TurnLimitMilliseconds = 2000;

    public static void UsePortrait(Window window)
    {
        SafeAreaCanvas.SetPortrait(false);
        ScreenOrientation.SetDeferEdges(false);
        ScreenOrientation.Set(landscape: false);
        window.ContentScaleAspect = Window.ContentScaleAspectEnum.Expand;
        window.ContentScaleSize = LauncherTheme.PortraitCanvas;
    }

    /// <summary>Enters the game's chosen orientation (landscape unless Portrait Mode + Set Portrait as Default are enabled).</summary>
    public static Task UseGameAsync(Window window, bool portrait) =>
        portrait ? UseGamePortraitAsync(window) : UseLandscapeAsync(window);

    /// <summary>Turns to landscape and waits until iOS has turned the window, so the game starts at the size it plays at.</summary>
    public static async Task UseLandscapeAsync(Window window)
    {
        SafeAreaCanvas.SetPortrait(false);
        ScreenOrientation.SetDeferEdges(true);
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
        if (ScreenOrientation.GestureState() is { } gestures) GD.Print($"Edge gestures: {gestures}.");
    }

    /// <summary>Keeps or turns the window to portrait for the game when Set Portrait as Default is enabled.</summary>
    public static async Task UseGamePortraitAsync(Window window)
    {
        SafeAreaCanvas.SetPortrait(true);
        ScreenOrientation.SetDeferEdges(true);
        ScreenOrientation.Set(landscape: false);
        Vector2I size = DisplayServer.WindowGetSize();
        long aspect = SafeAreaCanvas.Layout(size.X, size.Y, out int w, out int h);
        window.ContentScaleAspect = (Window.ContentScaleAspectEnum)aspect;
        window.ContentScaleSize = new Vector2I(w, h);
        var clock = Stopwatch.StartNew();
        while (size.Y < size.X && clock.ElapsedMilliseconds < TurnLimitMilliseconds)
        {
            await window.ToSignal(window.GetTree(), SceneTree.SignalName.ProcessFrame);
            size = DisplayServer.WindowGetSize();
        }
        aspect = SafeAreaCanvas.Layout(size.X, size.Y, out w, out h);
        window.ContentScaleAspect = (Window.ContentScaleAspectEnum)aspect;
        window.ContentScaleSize = new Vector2I(w, h);
        GD.Print($"Orientation: portrait for the game, window {size.X}x{size.Y} after {clock.ElapsedMilliseconds} ms.");
        if (ScreenOrientation.GestureState() is { } gestures) GD.Print($"Edge gestures: {gestures}.");
    }
}
