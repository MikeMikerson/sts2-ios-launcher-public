using System;
using System.IO;
using System.Reflection;
using Godot;
using Godot.Bridge;
using STS2Mobile.Platform;

namespace STS2Mobile.FullGame;

/// <summary>
/// Connects the launcher to the game when the game's files are present. Everything here goes by name: this assembly is AOT
/// compiled and has to start with no game, so it holds no reference to one.
/// </summary>
internal static class GameLoader
{
    private const string GameAssembly = "sts2";
    private static string GameFile => "user://game/" + ModLaunchSettings.AssemblyFile;
    private const string GameScene = "res://scenes/game.tscn";
    private const string HostAssembly = "STS2Mobile.GameHost";
    private const string HostEntry = HostAssembly + ".GameEntry";

    /// <summary>The game's code is in the data folder, and its resources are mounted.</summary>
    public static bool GameIsPresent => Godot.FileAccess.FileExists(GameFile) && ResourceLoader.Exists(GameScene);

    /// <summary>Registers the game's scripts and the game-facing host's, then adds the host under the launcher.</summary>
    public static IGameEntry Load(Node parent)
    {
        // Before the first scene is instantiated, and before the first node of the host's own is created.
        var game = Assembly.Load(new AssemblyName(GameAssembly));
        GD.Print($"Loaded game assembly: {Path.GetFileName(game.Location)}; mods {(ModLaunchSettings.Enabled ? "on" : "off")}.");
        ScriptManagerBridge.LookupScriptsInAssembly(game);
        var host = Assembly.Load(new AssemblyName(HostAssembly));
        ScriptManagerBridge.LookupScriptsInAssembly(host);
        var entry = (Node)Activator.CreateInstance(host.GetType(HostEntry, throwOnError: true)!)!;
        parent.AddChild(entry);
        // The launcher's screen came first and has to stay in front of the game while it starts.
        parent.MoveChild(entry, 0);
        return (IGameEntry)entry;
    }

}
