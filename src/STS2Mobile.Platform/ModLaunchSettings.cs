namespace STS2Mobile.Platform;

/// <summary>The choice made before the game assembly loads. Changes on the home screen take effect after reopening.</summary>
public static class ModLaunchSettings
{
    public const string FileName = "mods-enabled";
    public const string PlainAssembly = "sts2.unmodded.dll";
    public const string WovenAssembly = "sts2.dll";

    public static bool Enabled { get; private set; }
    public static string AssemblyFile => Enabled ? WovenAssembly : PlainAssembly;

    /// <summary>
    /// The game's own mods agreement (its <c>mods_enabled</c> setting) on the phone: the launcher's switch is that agreement, so the
    /// game loads mods exactly when the launcher chose the woven assembly. The game would otherwise ask in its Settings &gt; Mods popup,
    /// whose Load button quits the game. The setting in its save file is left as it is.
    /// </summary>
    public static bool PlayerAgreedToModLoading() => Enabled;

    /// <summary>Captures the choice once at launcher startup. A test launch may explicitly override it with 1 or 0.</summary>
    public static void Capture(string launcherFolder, string? overrideValue = null) =>
        Enabled = overrideValue switch { "1" => true, "0" => false, _ => Read(launcherFolder) };

    /// <summary>Missing, unreadable or invalid settings keep mods off.</summary>
    public static bool Read(string launcherFolder)
    {
        try { return File.ReadAllText(Path.Combine(launcherFolder, FileName)).Trim() == "1"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Saves the next launch's choice without changing which assembly this launch already loaded.</summary>
    public static void Write(string launcherFolder, bool enabled)
    {
        Directory.CreateDirectory(launcherFolder);
        string path = Path.Combine(launcherFolder, FileName);
        string temporary = path + ".partial";
        File.WriteAllText(temporary, enabled ? "1\n" : "0\n");
        File.Move(temporary, path, overwrite: true);
    }
}
