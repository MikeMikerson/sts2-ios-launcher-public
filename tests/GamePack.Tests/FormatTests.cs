using System.Security.Cryptography;
using System.Text;
using STS2Mobile.GamePack;

namespace STS2Mobile.GamePack.Tests;

/// <summary>
/// The C# steps on made-up files, against what the Python tools make from the same files (the hashes). These run everywhere;
/// InstalledGameTests does the same with the real game where a Mac has it.
/// </summary>
public sealed class FormatTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("game-pack-format-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string Sha(string text) => Sha(Encoding.UTF8.GetBytes(text));

    private static string Emitter(string name, params string[] lines) => $"[node name=\"{name}\" type=\"GPUParticles2D\" parent=\".\"]\n" + string.Join("\n", lines) + "\n\n";

    private static readonly string Scene = "[gd_scene format=3]\n\n[node name=\"Bg\" type=\"Node2D\"]\n\n"
        + Emitter("clouds", "amount = 40", "lifetime = 400.0", "preprocess = 400.0", "fixed_fps = 0")
        + Emitter("fog", "preprocess = 100.0")
        + Emitter("embers", "preprocess = 6.0", "fixed_fps = 60")
        + Emitter("sparkle", "preprocess = 5.0")
        + Emitter("rigid", "preprocess = 100.0", "interpolate = false")
        + "[node name=\"Label\" type=\"Label\" parent=\".\"]\ntext = \"preprocess = 999.0\"\n";

    private string WritePack(params (string Name, byte[] Data)[] files)
    {
        string path = Path.Combine(_work, Guid.NewGuid() + ".pck");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        var writer = new PackWriter(stream);
        foreach (var (name, data) in files) writer.Add(name, data);
        writer.Finish();
        return path;
    }

    [Fact]
    public void WritesThePackPythonWrites()
    {
        string path = WritePack(("a.txt", "hello"u8.ToArray()), ("folder/longer-name.bin", Enumerable.Range(0, 200).Select(value => (byte)value).ToArray()), ("empty", []), ("nameé.txt", "x"u8.ToArray()));
        byte[] pack = File.ReadAllBytes(path);
        Assert.Equal(632, pack.Length);
        Assert.Equal("5b61c3827bf2597d316631cdf3701d9bb044ec9c4184d767255c4f391d0e0c22", Sha(pack));
    }

    [Fact]
    public void ReadsBackWhatItWrote()
    {
        using var reader = new PackReader(WritePack(("a.txt", "hello"u8.ToArray()), ("b/c.txt", "there"u8.ToArray())));
        Assert.Equal(["a.txt", "b/c.txt"], reader.Entries.Select(entry => entry.Name));
        Assert.Equal("there"u8.ToArray(), reader.Read(reader.Entries[1]));
    }

    [Fact]
    public void ACopiedEntryKeepsItsBytesAndChecksum()
    {
        string source = WritePack(("big.bin", new byte[3_000_000]), ("small.txt", "hello"u8.ToArray()));
        string direct = WritePack(("small.txt", "hello"u8.ToArray()), ("big.bin", new byte[3_000_000]));
        string copied = Path.Combine(_work, "copied.pck");
        using (var reader = new PackReader(source))
        using (var stream = new FileStream(copied, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            var writer = new PackWriter(stream);
            var buffer = new byte[1024];
            writer.Copy(reader, reader.Entries[1], buffer);
            writer.Copy(reader, reader.Entries[0], buffer);
            writer.Finish();
        }
        Assert.Equal(File.ReadAllBytes(direct), File.ReadAllBytes(copied));
    }

    [Fact]
    public void RefusesAnotherEngineVersionOrADamagedEntry()
    {
        string path = WritePack(("a.txt", "hello"u8.ToArray()));
        byte[] pack = File.ReadAllBytes(path);
        pack[128] ^= 0xFF;
        File.WriteAllBytes(path, pack);
        using (var reader = new PackReader(path)) Assert.Throws<PackException>(() => reader.Read(reader.Entries[0]));
        pack[8] = 9;
        File.WriteAllBytes(path, pack);
        Assert.Throws<PackException>(() => new PackReader(path).Dispose());
    }

    [Theory]
    [InlineData(0.1, 0, 300, null)]
    [InlineData(0.3, 4000, 300, 1000L)]
    [InlineData(7.0, 30, 300, null)]
    [InlineData(1.1, 300, 300, 272L)]
    [InlineData(400.0, 0, 300, 1L)]
    [InlineData(6.0, 60, 40, 6L)]
    public void LimitedRateIsPythons(double preprocess, long rate, int limit, long? expected) =>
        Assert.Equal(expected, ParticleLimits.LimitedRate(preprocess, rate, limit));

    /// <summary>The true quotient is just under 3000, which a floating point division rounds up. Python's <c>//</c> does not.</summary>
    [Fact]
    public void FloorDivisionIsPythonsAndNotARoundedDivision() => Assert.Equal(2999, ParticleLimits.FloorDivide(300, 0.1));

    [Fact]
    public void AdjustsAsPythonDoes()
    {
        var (text, changed) = ParticleLimits.Adjust(Scene);
        Assert.Equal(3, changed);
        Assert.Equal("10a90c03f09dffde2f4f1c1010986fdfe5cb16b1462451e16fdee731f51c5d4a", Sha(text));
        Assert.Equal(300, ParticleLimits.SceneLimit(Scene));
        Assert.Equal(4150, ParticleLimits.SceneSteps(Scene, 300));
        Assert.Equal(1150, ParticleLimits.SceneSteps(Scene, 300, adjustableOnly: true));
    }

    [Fact]
    public void ACrowdedSceneGetsThePerEmitterLimitPythonGivesIt()
    {
        string crowd = "[gd_scene format=3]\n\n" + string.Concat(Enumerable.Range(0, 40).Select(index =>
            Emitter("e" + index, "preprocess = " + PythonFloat(3 + index * 0.7))));
        int limit = ParticleLimits.SceneLimit(crowd);
        Assert.Equal(60, limit);
        var (text, changed) = ParticleLimits.Adjust(crowd, limit);
        Assert.Equal(40, changed);
        Assert.Equal("07a92b197bfb9c63f63054fdac9d50443dd71f75965d78ccbe6c1763d7664d4d", Sha(text));
        Assert.Equal(2098, ParticleLimits.SceneSteps(text, limit));
    }

    private static string PythonFloat(double value)
    {
        string text = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return text.Contains('.') ? text : text + ".0";
    }

    [Fact]
    public void ASceneWithNothingToChangeGivesNothing()
    {
        Assert.Null(ParticleLimits.AdjustScene("[gd_scene format=3]\n"u8.ToArray()));
        Assert.Null(ParticleLimits.AdjustScene(Encoding.UTF8.GetBytes(Emitter("sparkle", "preprocess = 5.0"))));
    }

    [Fact]
    public void BuildsTheInventoryPythonBuilds()
    {
        string scene = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "warmup-fixture.tscn"));
        var files = new Dictionary<string, string>
        {
            ["scenes/vfx/a.tscn"] = scene,
            ["scenes/vfx/b.tscn"] = scene.Replace("Mat_", "Other_"),
            // Python takes the second of these first, whichever way the whole strings sort, and the first of a material is the one kept.
            ["scenes/a-b.tscn"] = scene.Replace("Mat_", "Dash_").Replace("emission_shape = 3", "emission_shape = 1"),
            ["scenes/a/c.tscn"] = scene.Replace("Mat_", "Slash_").Replace("emission_shape = 3", "emission_shape = 1"),
            ["shaders/z.gdshader"] = "",
            ["shaders/glow.gdshader"] = "",
            ["shaders/shared.gdshaderinc"] = "",
        };
        var built = ShaderWarmupInventory.Build(files.Keys, name => files[name]);
        Assert.Equal(["warmup/inventory.json", "warmup/particles_000.tscn"], built.Select(file => file.Name));
        Assert.Equal("5fb015146861c551c120ab0e0414627f5442c79b5af1ea93b48ae66eea431a5d", Sha(built[0].Data));
        Assert.Equal("a2b927874945a8c977280f1c3070d37666d5daa9d2f4ba473dda30fc3518e06b", Sha(built[1].Data));
    }

    [Fact]
    public void RefusesAnEmptyInventory() =>
        Assert.Throws<PackException>(() => ShaderWarmupInventory.Build(["scenes/a.tscn"], _ => "[gd_scene format=3]\n"));
}
