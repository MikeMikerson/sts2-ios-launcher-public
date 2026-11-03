namespace STS2Mobile.Platform;

/// <summary>
/// The Mods screen's "Add mod from Files" button. src/STS2Mobile.GamePatcher makes the game's button call
/// <see cref="Request"/> instead of opening the Steam Workshop; the host binds what happens then.
/// </summary>
public static class ModImportRequests
{
    private static Action? _handler;

    public static void Bind(Action handler) => _handler = handler;

    /// <summary>Called from the patched game. Without a bound host the press does nothing.</summary>
    public static void Request() => _handler?.Invoke();
}
