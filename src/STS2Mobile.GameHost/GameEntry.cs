using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// The launcher's way into the game. The launcher creates this by name once the game's files are present, after it has
/// registered this assembly's scripts and the game's. Nothing in the launcher's assembly refers to a game type.
/// </summary>
public sealed partial class GameEntry : Node, IGameEntry
{
    private const string GameScene = "res://scenes/game.tscn";
    private const string ModCatalog = "user://game/builtin-mods.json";
    private readonly PhoneGraphics _graphics = new();
    private readonly ModImportFlow _mods = new();
    private NGame? _game;

    /// <inheritdoc />
    public bool GameCreated { get; private set; }

    public override void _Ready()
    {
        AddChild(_graphics);
        AddChild(_mods);
    }

    /// <inheritdoc />
    public async Task StartAsync()
    {
        PrepareMods();
        using var scene = ResourceLoader.Load<PackedScene>(GameScene) ?? throw new InvalidDataException("Recovered game scene is missing.");
        var game = scene.Instantiate<NGame>();
        GameCreated = true;
        GetTree().Root.AddChild(game);
        await game.GameStartupComplete;
        if (game.MainMenu == null) throw new InvalidOperationException("The game did not reach its main menu.");
        _game = game;
        _graphics.GameStarted();
        StartAutomaticPlay();
        if (OS.GetEnvironment("STS2_PORTRAIT_PROOF").Length > 0)
            _ = RunPortraitProofAfterLauncherHidesAsync(game);
    }

    private async Task RunPortraitProofAfterLauncherHidesAsync(NGame game)
    {
        for (int frame = 0; frame < 3; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await PortraitProof.RunIfRequestedAsync(GetTree(), game);
    }

    /// <summary>
    /// <c>STS2_AUTOSLAY=&lt;seed&gt;</c> has the game's own automatic player start a new run, for measuring long play with nobody
    /// there. It replaces the saved run and quits at the end, so it is for test installs only. The game keeps it out of release
    /// builds behind a check that is always true, so it is reached by name here.
    /// </summary>
    private static void StartAutomaticPlay()
    {
        string seed = OS.GetEnvironment("STS2_AUTOSLAY");
        if (seed.Length == 0) return;
        var player = typeof(NGame).Assembly.GetType("MegaCrit.Sts2.Core.AutoSlay.AutoSlayer", throwOnError: true)!;
        player.GetMethod("Start")!.Invoke(Activator.CreateInstance(player), [seed, null]);
    }

    /// <inheritdoc />
    public void FlushSaves()
    {
        if (_game == null || !SaveManager.Instance.IsProfileInitialized) return;
        SaveManager.Instance.SaveSettings();
        SaveManager.Instance.SavePrefsFile();
        SaveManager.Instance.SaveProgressFile();
        SaveManager.Instance.SaveProfile();
    }

    /// <summary>
    /// Hands the game's mod loader the woven code mods listed beside the game, which the assembly hook finds in the same
    /// folder, and their manifests in the app's mods folder. The loader runs during the game's own startup, after this.
    /// </summary>
    private static void PrepareMods()
    {
        if (!ModLaunchSettings.Enabled)
        {
            GD.Print("Mods are off: no mod assemblies, manifests or packs are loaded.");
            return;
        }
        if (BuiltInMods.All.Count == 0 && Godot.FileAccess.FileExists(ModCatalog))
            BuiltInMods.Register(BuiltInMods.ParseCatalog(Godot.FileAccess.GetFileAsString(ModCatalog)), id => Assembly.Load(new AssemblyName(id)), message => GD.Print(message));
        PhoneRelayConnector.Register(ProjectSettings.GlobalizePath("user://" + ModPackageImport.FolderName));
        BuiltInMods.WriteManifests(ProjectSettings.GlobalizePath("user://mods"), ProjectSettings.GlobalizePath("user://game"));
        ModPackageImport.PublishResources(ProjectSettings.GlobalizePath("user://" + ModPackageImport.FolderName), ProjectSettings.GlobalizePath("user://mods"), BuiltInMods.All.Select(mod => mod.Id).ToArray());
    }
}
