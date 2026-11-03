using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace STS2Mobile.GamePatcher.Tests;

/// <summary>
/// An optional built-in mod (QuickMatch): written into the original folder from the app's own copy, left out
/// when the app was built without it, and when it cannot be prepared, the game starts without it and the player is told.
/// </summary>
public sealed class OptionalBuiltInModTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("optional-built-in-").FullName;
    private static readonly byte[] Dll = Encoding.UTF8.GetBytes("mod code");
    private static readonly byte[] Manifest = Encoding.UTF8.GetBytes("""{"id":"Opt","has_dll":true}""");

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string Catalog(string dllSha256) => JsonSerializer.Serialize(new
    {
        mods = new[] { new { id = "Opt", optional = true, dll = "/nowhere/Opt.dll", manifest = "/nowhere/Opt.json", dllSha256, manifestSha256 = Sha(Manifest) } },
    });

    private static Stream? Bundled(string name) => name switch
    {
        "Opt.dll" => new MemoryStream(Dll),
        "Opt.json" => new MemoryStream(Manifest),
        _ => null,
    };

    [Fact]
    public void AnOptionalModTheAppDoesNotCarryIsLeftOut()
    {
        string original = Path.Combine(_work, "original");
        Directory.CreateDirectory(Path.Combine(original, "mods"));
        // A copy an earlier build left behind is not woven by a build without the mod.
        File.WriteAllBytes(Path.Combine(original, "mods", "Opt.dll"), Dll);
        Assert.Empty(BuiltInModInput.InOriginalFolder(original, Catalog(Sha(Dll)), _ => null));
    }

    [Fact]
    public void TheAppsCopiesAreWrittenIntoTheOriginalFolder()
    {
        string original = Path.Combine(_work, "original");
        Directory.CreateDirectory(Path.Combine(original, "mods"));
        File.WriteAllText(Path.Combine(original, "mods", "Opt.dll"), "an older build");
        var mod = Assert.Single(BuiltInModInput.InOriginalFolder(original, Catalog(Sha(Dll)), Bundled));
        Assert.Equal(Path.Combine(original, "mods", "Opt.dll"), mod.DllPath);
        Assert.Equal(Dll, File.ReadAllBytes(mod.DllPath));
        Assert.Equal(Manifest, File.ReadAllBytes(mod.ManifestPath));
        Assert.Empty(Directory.GetFiles(Path.Combine(original, "mods"), "*.partial"));
    }

    [Fact]
    public void TheMacLeavesOutAnOptionalModSetupDidNotFetch() =>
        Assert.Empty(BuiltInModInput.AtCatalogPaths(Catalog(Sha(Dll))));

    [Fact]
    public void TheRepositoryCatalogMarksQuickMatchOptional() =>
        Assert.Contains("QuickMatch", BuiltInModInput.OptionalIds());

    /// <summary>A game folder as a download leaves it, from the Steam install of v0.111.0.</summary>
    private string GameFolder()
    {
        string game = Path.Combine(_work, "game");
        string original = Path.Combine(game, STS2Mobile.GamePatcher.GameFolder.Original);
        Directory.CreateDirectory(original);
        foreach (string name in GameProfile.Current.Inputs.Keys) File.Copy(Path.Combine(InstalledGame.Data!, name), Path.Combine(original, name));
        foreach (string name in GameProfile.Current.Unchanged.Keys) File.Copy(Path.Combine(InstalledGame.Data!, name), Path.Combine(game, name));
        return game;
    }

    [InstalledGameFact]
    public void ARefusedOptionalModLeavesTheGameStartingWithoutIt()
    {
        string game = GameFolder();
        // A pin the app's copy does not match stands in for a mod that does not fit this game.
        string catalog = Catalog(new string('0', 64));
        IReadOnlyList<BuiltInModInput> BuiltIn(string original) => BuiltInModInput.InOriginalFolder(original, catalog, Bundled);
        var result = GameAssemblyPreparation.ForApp(game, includeImportedMods: true, BuiltIn, BuiltInModInput.OptionalIds(catalog)).Run();
        Assert.Equal(PreparationStatus.Prepared, result.Status);
        Assert.False(result.ModRefused);
        Assert.Contains("Opt could not be prepared for this game, so the game starts without it", result.Notice);
        Assert.Contains("is not the pinned", result.Notice);
        Assert.Empty(STS2Mobile.Platform.BuiltInMods.ParseCatalog(File.ReadAllText(Path.Combine(game, STS2Mobile.GamePatcher.GameFolder.ModCatalog))));
        Assert.False(File.Exists(Path.Combine(game, "Opt.dll")));

        var again = GameAssemblyPreparation.ForApp(game, includeImportedMods: true, BuiltIn, BuiltInModInput.OptionalIds(catalog)).Run();
        Assert.Equal(PreparationStatus.Cached, again.Status);
        Assert.Equal(result.Notice, again.Notice);
    }

    [InstalledGameFact]
    public void QuickMatchIsWovenInWhenSetupFetchedIt()
    {
        string fetched = Path.Combine(RepositoryRoot(), ".tools/builtin-mods");
        if (!File.Exists(Path.Combine(fetched, "QuickMatch.dll"))) return; // A checkout built without QuickMatch: nothing to weave.
        string game = GameFolder();
        var result = GameAssemblyPreparation.ForApp(game, includeImportedMods: true,
            original => BuiltInModInput.InOriginalFolder(original, File.ReadAllText(Path.Combine(RepositoryRoot(), "mods/builtin.json")),
                name => File.Exists(Path.Combine(fetched, name)) ? File.OpenRead(Path.Combine(fetched, name)) : null),
            BuiltInModInput.OptionalIds()).Run();
        Assert.Equal(PreparationStatus.Prepared, result.Status);
        Assert.Null(result.Notice);
        var woven = Assert.Single(STS2Mobile.Platform.BuiltInMods.ParseCatalog(File.ReadAllText(Path.Combine(game, STS2Mobile.GamePatcher.GameFolder.ModCatalog))));
        Assert.Equal(("QuickMatch", 30), (woven.Id, woven.Patches));
    }

    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder != null && !File.Exists(Path.Combine(folder.FullName, "mods/builtin.json"))) folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
