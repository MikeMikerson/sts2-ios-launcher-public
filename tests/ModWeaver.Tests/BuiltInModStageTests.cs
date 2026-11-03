using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using STS2Mobile.Platform;
using STS2Mobile.Platform.Tests;

namespace STS2Mobile.ModWeaver.Tests;

/// <summary>The step around the weaver: the pins, the manifest, and what it writes for the app.</summary>
public sealed class BuiltInModStageTests : IDisposable
{
    private static readonly string Bin = AppContext.BaseDirectory;
    private readonly string _folder = Directory.CreateTempSubdirectory("modweaver-stage-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private BuiltInModInput Input(string manifest = """{"id":"GoodMod","has_dll":true}""")
    {
        string manifestPath = Path.Combine(_folder, "GoodMod.json"), dll = Path.Combine(Bin, "GoodMod.dll");
        File.WriteAllText(manifestPath, manifest);
        return new BuiltInModInput("GoodMod", dll, manifestPath, Sha(dll), Sha(manifestPath));
    }

    private List<BuiltMod> Stage(BuiltInModInput input)
    {
        var (game, _) = GoodModWeaving.Read();
        using (game)
        using (var platform = ModuleDefinition.ReadModule(typeof(BuiltInMods).Assembly.Location))
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Bin);
            return BuiltInModStage.WriteOutputs(BuiltInModStage.Weave(game, platform, [input], resolver), _folder);
        }
    }

    [Fact]
    public void WritesTheModCopyAndACatalogTheAppCanRead()
    {
        var built = Assert.Single(Stage(Input()));
        string dll = Path.Combine(_folder, "GoodMod.dll");
        Assert.Equal(Sha(dll), built.WovenSha256);
        using (var module = ModuleDefinition.ReadModule(dll))
            Assert.DoesNotContain(module.AssemblyReferences, reference => reference.Name == "0Harmony");
        var entry = Assert.Single(BuiltInMods.ParseCatalog(File.ReadAllText(Path.Combine(_folder, GameFolder.ModCatalog))));
        Assert.Equal("GoodMod", entry.Id);
        Assert.Equal(built.WovenSha256, entry.WovenSha256);
        Assert.Equal(built.Report.Patches.Count, entry.Patches);
    }

    [Fact]
    public void ADllThatIsNotThePinnedOneStops()
    {
        var error = Assert.Throws<WeaveException>(() => Stage(Input() with { DllSha256 = new string('0', 64) }));
        Assert.Contains("DLL hash", error.Message);
    }

    [Fact]
    public void AManifestThatIsNotThePinnedOneStops()
    {
        var error = Assert.Throws<WeaveException>(() => Stage(Input() with { ManifestSha256 = new string('0', 64) }));
        Assert.Contains("manifest hash", error.Message);
    }

    private BuiltInModInput InputWithPack(string resource = "images/mod.png", bool declared = true)
    {
        var input = Input(declared
            ? """{"id":"GoodMod","has_dll":true,"has_pck":true}"""
            : """{"id":"GoodMod","has_dll":true}""");
        string pack = Path.Combine(Directory.CreateDirectory(Path.Combine(_folder, "source")).FullName, "GoodMod.pck");
        File.WriteAllBytes(pack, TestPack.Build((resource, new byte[] { 1, 2, 3 })));
        return input with { PckPath = pack, PckSha256 = Sha(pack) };
    }

    [Fact]
    public void CopiesADeclaredPackAndPinsItInTheCatalog()
    {
        var input = InputWithPack();
        Assert.Single(Stage(input));
        string copied = Path.Combine(_folder, "GoodMod.pck");
        Assert.Equal(File.ReadAllBytes(input.PckPath!), File.ReadAllBytes(copied));
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, GameFolder.ModCatalog)));
        Assert.Equal(Sha(copied), catalog.RootElement.GetProperty("mods")[0].GetProperty("pckSha256").GetString());
    }

    [Fact]
    public void APackThatIsNotThePinnedOneStops()
    {
        var input = InputWithPack() with { PckSha256 = new string('0', 64) };
        var error = Assert.Throws<WeaveException>(() => Stage(input));
        Assert.Contains(".pck hash", error.Message);
    }

    [Theory]
    [InlineData("scripts/mod.gd", "code")]
    [InlineData("project.godot", "start")]
    public void APackContainingCodeOrStartupOverridesStops(string resource, string reason)
    {
        var error = Assert.Throws<InvalidDataException>(() => Stage(InputWithPack(resource)));
        Assert.Contains(reason, error.Message);
        Assert.False(File.Exists(Path.Combine(_folder, "GoodMod.dll")));
        Assert.False(File.Exists(Path.Combine(_folder, "GoodMod.pck")));
    }

    [Fact]
    public void AnUndeclaredPackStops()
    {
        var error = Assert.Throws<WeaveException>(() => Stage(InputWithPack(declared: false)));
        Assert.Contains("not declared", error.Message);
    }

    [Theory]
    [InlineData("""{"id":"Other","has_dll":true}""", "another id")]
    [InlineData("""{"id":"GoodMod"}""", "has_dll")]
    [InlineData("""{"id":"GoodMod","has_dll":true,"has_pck":true}""", ".pck")]
    public void AManifestTheAppCannotUseStops(string manifest, string reason)
    {
        var error = Assert.Throws<WeaveException>(() => Stage(Input(manifest)));
        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void TheCatalogIsValidJsonWithNoMods()
    {
        BuiltInModStage.WriteOutputs([], _folder);
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, GameFolder.ModCatalog)));
        Assert.Equal(0, catalog.RootElement.GetProperty("mods").GetArrayLength());
    }
}
