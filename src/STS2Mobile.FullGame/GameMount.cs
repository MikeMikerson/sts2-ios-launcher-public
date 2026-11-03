using System;
using System.IO;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.FullGame;

/// <summary>
/// Mounts the game pack from the app's data folder. It is the first autoload, and its
/// constructor runs before the engine loads the next one, so the game's autoloads and scenes resolve as if the files were
/// part of the main pack. In a build whose main pack holds the game, the folder does not exist and this does nothing.
/// </summary>
public sealed partial class GameMount : Node
{
    private const string Pack = "user://game/FullGame.pck";
    private const string Settings = "user://game/game-settings.godot";
    private const string MainSettings = "res://project.binary";
    private const string BusLayout = "res://default_bus_layout.tres";
    private static byte[] _hostSettings = [];

    /// <summary>The game's settings were written just now. The engine reads them only as it starts.</summary>
    public static bool NeedsRelaunch { get; private set; }

    public GameMount()
    {
        // The game pack has its own project.binary, which replaces this one in res:// once mounted.
        _hostSettings = Godot.FileAccess.GetFileAsBytes(MainSettings);
        if (Godot.FileAccess.FileExists(Pack)) Mount();
    }

    /// <summary>
    /// After preparation made a new pack: writes its settings now, so the player relaunches only once. The pack replaces any
    /// older one mounted when the app started, whose offsets no longer mean anything.
    /// </summary>
    /// <returns>False if the pack could not be opened.</returns>
    public static bool MountPrepared() => Mount();

    private static bool Mount()
    {
        try
        {
            if (!ProjectSettings.LoadResourcePack(Pack)) throw new InvalidDataException("The game pack could not be opened.");
            // The engine loads the default layout before any C# runs, so it found none in the main pack.
            if (ResourceLoader.Exists(BusLayout)) AudioServer.SetBusLayout(ResourceLoader.Load<AudioBusLayout>(BusLayout));
            if (!Godot.FileAccess.FileExists(Settings))
            {
                WriteSettings(_hostSettings, Godot.FileAccess.GetFileAsBytes(MainSettings));
                NeedsRelaunch = true;
            }
            return true;
        }
        catch (Exception error)
        {
            GD.PushError("Mounting the game failed: " + error);
            return false;
        }
    }

    private static void WriteSettings(byte[] host, byte[] game)
    {
        var text = GameSettingsText.Write(game, host, value => GD.VarToStr(GD.BytesToVarWithObjects(value)));
        var path = ProjectSettings.GlobalizePath(Settings);
        var partial = path + ".partial";
        File.WriteAllText(partial, text);
        File.Move(partial, path, overwrite: true);
    }
}
