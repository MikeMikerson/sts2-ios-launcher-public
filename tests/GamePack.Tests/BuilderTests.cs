using System.Text;
using STS2Mobile.GamePack;

namespace STS2Mobile.GamePack.Tests;

/// <summary>What goes in the game pack and in what order, from made-up packs.</summary>
public sealed class BuilderTests : IDisposable
{
    private const string Pin = "pack-pin";
    private const string Particles = "[sub_resource type=\"ParticleProcessMaterial\" id=\"M\"]\nparticle_flag_disable_z = true\n\n[node name=\"V\" type=\"Node2D\"]\n";
    private const string SlowEmitter = "[node name=\"clouds\" type=\"GPUParticles2D\" parent=\".\"]\npreprocess = 400.0\n";
    private readonly string _work = Directory.CreateTempSubdirectory("game-pack-builder-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    private string WritePack(string name, params (string Name, byte[] Data)[] files)
    {
        string path = Path.Combine(_work, name);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        var writer = new PackWriter(stream);
        foreach (var (entry, data) in files) writer.Add(entry, data);
        writer.Finish();
        return path;
    }

    private string GamePack(params (string Name, byte[] Data)[] more) => WritePack("game.pck",
    [
        (".godot/uid_cache.bin", Text("uids")),
        (".godot/global_script_class_cache.cfg", Text("classes")),
        (".godot/extension_list.cfg", Text("extensions")),
        ("project.binary", Text("settings")),
        ("addons/sentry/sentry.gdextension", Text("reporting")),
        ("addons/sentry/scene.tscn", Text(Particles.Replace("particle_flag_disable_z", "from_reporting"))),
        ("addons/fmod/fmod.gdextension", Text("desktop")),
        ("steam/steam_appid.txt", Text("0")),
        ("RiderTestRunner/runner.tscn", Text(Particles)),
        ("SlayTheSpire2.app/data.bin", Text("mac")),
        ("images/a.png.import", Text("old remap")),
        (".godot/imported/a.bptc.ctex", Text("desktop texture")),
        ("scenes/clouds.tscn", Text(SlowEmitter)),
        ("shaders/glow.gdshader", Text("shader")),
        .. more,
    ]);

    private string TexturePack(string pin = Pin) => WritePack("textures.pck",
        (GamePackBuilder.TextureManifest, Text($"{{\"profile\":\"{GamePackBuilder.TextureProfile}\",\"assetPackSha256\":\"{pin}\",\"textures\":[{{\"source\":\".godot/imported/a.bptc.ctex\",\"import\":\"images/a.png.import\",\"output\":\".godot/imported/a.astc.ctex\"}}]}}")),
        ("images/a.png.import", Text("new remap")),
        (".godot/imported/a.astc.ctex", Text("phone texture")));

    private Dictionary<string, string> Build(string game, string textures, out List<string> order)
    {
        string output = Path.Combine(_work, "out.pck");
        int entries = GamePackBuilder.Build(game, textures, Pin, output);
        using var reader = new PackReader(output);
        Assert.Equal(entries, reader.Entries.Count);
        order = reader.Entries.Select(entry => entry.Name).ToList();
        return reader.Entries.ToDictionary(entry => entry.Name, entry => Encoding.UTF8.GetString(reader.Read(entry)));
    }

    [Fact]
    public void SettingsFirstThenTheGamesOrderThenTheAddedFiles()
    {
        Build(GamePack(), TexturePack(), out var order);
        Assert.Equal(
        [
            "project.binary",
            ".godot/uid_cache.bin",
            ".godot/global_script_class_cache.cfg",
            "images/a.png.import",
            "scenes/clouds.tscn",
            "shaders/glow.gdshader",
            ".godot/imported/a.astc.ctex",
            "warmup/inventory.json",
            "warmup/particles_000.tscn",
        ], order);
    }

    [Fact]
    public void TheReportingSdkAndDesktopFilesAreGoneAndTheTexturesReplaced()
    {
        var pack = Build(GamePack(), TexturePack(), out _);
        Assert.DoesNotContain(pack.Keys, name => name.StartsWith("addons/") || name.StartsWith("steam/") || name.Contains("bptc"));
        Assert.Equal("new remap", pack["images/a.png.import"]);
        Assert.Equal("phone texture", pack[".godot/imported/a.astc.ctex"]);
    }

    [Fact]
    public void ScenesAreAdjustedAndTheWarmupSeesTheGamesScenesWithoutTheReportingSdk()
    {
        var pack = Build(GamePack(), TexturePack(), out _);
        Assert.Contains("fixed_fps = 1\n", pack["scenes/clouds.tscn"]);
        // A scene that does not ship still counts, as it does in the Python tool. The reporting SDK's does not.
        Assert.Contains("particle_flag_disable_z", pack["warmup/particles_000.tscn"]);
        Assert.DoesNotContain("from_reporting", pack["warmup/particles_000.tscn"]);
        Assert.Contains("res://shaders/glow.gdshader", pack["warmup/inventory.json"]);
    }

    [Fact]
    public void RefusesTexturesMadeForAnotherGame() =>
        Assert.Throws<PackException>(() => Build(GamePack(), TexturePack("another pack"), out _));

    [Fact]
    public void RefusesAGameFileAtAnAddedFilesName() =>
        Assert.Throws<PackException>(() => Build(GamePack(("warmup/inventory.json", Text("{}"))), TexturePack(), out _));

    [Fact]
    public void RefusesAGameWithoutItsCaches()
    {
        string game = WritePack("game.pck", ("project.binary", Text("settings")), ("scenes/a.tscn", Text(Particles)));
        Assert.Throws<PackException>(() => Build(game, TexturePack(), out _));
    }
}
