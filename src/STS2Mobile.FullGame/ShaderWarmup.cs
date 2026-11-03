using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace STS2Mobile.FullGame;

/// <summary>
/// Compiles the game's particle and canvas shaders once, behind the launcher, so that their first
/// use during play does not freeze the game. On a phone an uncached particle shader can cost about
/// a second, and a first combat can need several of them in one frame.
///
/// The work list comes from <c>res://warmup/inventory.json</c> (tools/prepare_shader_warmup.py).
/// Everything is drawn into a small off-screen viewport. The engine keeps what it compiles in
/// <c>user://shader_cache</c>, so this runs again only when the inventory changes or that cache is
/// gone. It is an optimization: a failure here is reported and the game starts anyway.
/// </summary>
public sealed partial class ShaderWarmup : Node
{
    private const string Directory = "res://warmup/";
    private const string Marker = "user://shader-warmup-done.txt";
    private const string EngineCache = "user://shader_cache";
    private const int CanvasBatch = 6;
    // Process frames, not draw callbacks: the engine skips drawing whenever the window cannot be
    // shown, and a wait for a draw would then never end.
    private const int SettleFrames = 3;
    private static readonly Vector2[] Square = [new(0, 20), new(16, 20), new(16, 36), new(0, 36)];

    /// <summary>Whether the mounted game's inventory has not been warmed up on this iPhone.</summary>
    public static bool IsNeeded
    {
        get
        {
            string text = FileAccess.FileExists(Directory + "inventory.json") ? FileAccess.GetFileAsString(Directory + "inventory.json") : "";
            if (text.Length == 0) return false;
            using var inventory = Json.ParseString(text).AsGodotDictionary();
            return !(FileAccess.FileExists(Marker) && FileAccess.GetFileAsString(Marker) == Stamp(inventory) && DirAccess.DirExistsAbsolute(EngineCache));
        }
    }

    /// <summary>
    /// What the marker holds, so a different game build warms up again: the build, from the manifest it came from (a tested version's profile
    /// can keep an older stamp, so an install that already warmed up does not do it again), then the inventory's facts.
    /// </summary>
    private static string Stamp(Godot.Collections.Dictionary inventory)
    {
        string build = STS2Mobile.GamePatcher.InstalledBuild.Read(GamePreparation.GameFolderOnDisk)?.WarmupId ?? "manifest-0";
        return $"{build} {inventory["particleScenesSha256"].AsString()} {inventory["canvasShaders"].AsStringArray().Length}";
    }

    /// <summary>Runs the warmup. Reports (done, total) steps.</summary>
    public async Task RunAsync(Action<int, int> progress, CancellationToken cancel)
    {
        using var inventory = Json.ParseString(FileAccess.GetFileAsString(Directory + "inventory.json")).AsGodotDictionary();
        string[] scenes = inventory["particleScenes"].AsStringArray(), shaders = inventory["canvasShaders"].AsStringArray();
        string stamp = Stamp(inventory);

        int done = 0, total = scenes.Length + (shaders.Length + CanvasBatch - 1) / CanvasBatch + 1;
        progress(done, total);
        var viewport = new SubViewport { Size = new Vector2I(64, 64), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Disable3D = true };
        AddChild(viewport);
        using Image pixel = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        pixel.SetPixel(0, 0, Colors.White);
        ImageTexture white = ImageTexture.CreateFromImage(pixel);
        try
        {
            // Loading a scene compiles its particle materials in one blocking step, so each scene is small.
            foreach (string scene in scenes)
            {
                cancel.ThrowIfCancellationRequested();
                using var packed = ResourceLoader.Load<PackedScene>(Directory + scene);
                Node batch = packed.Instantiate();
                foreach (Node child in batch.GetChildren())
                    if (child is GpuParticles2D particles) Show(particles, white);
                await DrawAsync(viewport, batch);
                progress(++done, total);
            }
            for (int start = 0; start < shaders.Length; start += CanvasBatch)
            {
                cancel.ThrowIfCancellationRequested();
                var batch = new Node2D();
                foreach (string path in shaders.AsSpan(start, Math.Min(CanvasBatch, shaders.Length - start)))
                    AddShader(batch, path, white);
                await DrawAsync(viewport, batch);
                progress(++done, total);
            }
            await DrawAsync(viewport, BuiltInMaterials(white));
            progress(++done, total);
            using var marker = FileAccess.Open(Marker, FileAccess.ModeFlags.Write);
            marker.StoreString(stamp);
        }
        finally { viewport.QueueFree(); }
    }

    /// <summary>Draws the nodes for a couple of frames, which is when their shaders and pipelines are built, then frees them.</summary>
    private async Task DrawAsync(SubViewport viewport, Node batch)
    {
        viewport.AddChild(batch);
        for (int frame = 0; frame < SettleFrames; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        batch.QueueFree();
    }

    private static void Show(GpuParticles2D particles, Texture2D texture)
    {
        particles.Texture = texture;
        particles.Position = new Vector2(32, 32);
        particles.Visible = true;
        particles.Emitting = true;
    }

    /// <summary>A canvas shader is drawn on the three kinds of geometry the game uses; a particle shader drives one emitter.</summary>
    private static void AddShader(Node2D batch, string path, Texture2D white)
    {
        if (!ResourceLoader.Exists(path)) return;
        var shader = ResourceLoader.Load<Shader>(path);
        if (shader == null) return;
        var material = new ShaderMaterial { Shader = shader };
        if (shader.GetMode() == Shader.Mode.Particles)
        {
            var particles = new GpuParticles2D { ProcessMaterial = material, Amount = 1 };
            Show(particles, white);
            batch.AddChild(particles);
        }
        else if (shader.GetMode() == Shader.Mode.CanvasItem) AddGeometry(batch, material, white);
    }

    private static void AddGeometry(Node2D batch, Material material, Texture2D white)
    {
        batch.AddChild(new Sprite2D { Texture = white, Material = material, Position = new Vector2(8, 8), Scale = new Vector2(8, 8) });
        batch.AddChild(new NinePatchRect { Texture = white, Material = material, Position = new Vector2(20, 0), Size = new Vector2(16, 16) });
        batch.AddChild(new Polygon2D { Texture = white, Material = material, Polygon = Square });
    }

    /// <summary>The engine's own canvas materials: every blend and light mode, plain and on animated particles.</summary>
    private static Node2D BuiltInMaterials(Texture2D white)
    {
        var batch = new Node2D();
        AddGeometry(batch, null!, white);
        foreach (CanvasItemMaterial.BlendModeEnum blend in Enum.GetValues<CanvasItemMaterial.BlendModeEnum>())
        {
            foreach (CanvasItemMaterial.LightModeEnum light in Enum.GetValues<CanvasItemMaterial.LightModeEnum>())
                AddGeometry(batch, new CanvasItemMaterial { BlendMode = blend, LightMode = light }, white);
            var particles = new GpuParticles2D
            {
                ProcessMaterial = new ParticleProcessMaterial(),
                Amount = 1,
                Material = new CanvasItemMaterial { BlendMode = blend, ParticlesAnimation = true }
            };
            Show(particles, white);
            batch.AddChild(particles);
        }
        return batch;
    }
}
