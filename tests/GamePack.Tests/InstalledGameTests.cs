using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using STS2Mobile.GamePack;
using STS2Mobile.GamePatcher;

namespace STS2Mobile.GamePack.Tests;

/// <summary>A fact about the real game. Skipped where this Mac has no Steam install of the supported version or no <c>scripts/prepare-game-files.sh</c> output.</summary>
public sealed class InstalledGameFactAttribute : FactAttribute
{
    public InstalledGameFactAttribute()
    {
        if (InstalledGame.Pack == null) Skip = "No prepared game files of the supported version on this Mac.";
    }
}

internal static class InstalledGame
{
    public static readonly string Root = FindRoot();
    public static readonly string? Pack = Find();

    /// <summary>The converted textures as the Python tools make them.</summary>
    public static string ReferenceTextures => Path.Combine(Root, "analysis/full-game-reference", TextureConversion.OutputName);

    public static string Analysis(string name) => Path.Combine(Root, "analysis", name);

    private static string FindRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder != null && !File.Exists(Path.Combine(folder.FullName, "STS2Mobile.slnx"))) folder = folder.Parent;
        return folder?.FullName ?? AppContext.BaseDirectory;
    }

    private static string? Find()
    {
        string pack = Environment.GetEnvironmentVariable("STS2_GAME_PACK") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources", GamePackPreparation.GamePack);
        return File.Exists(pack) && File.Exists(ReferenceTextures) && Sha(pack) == GameProfile.Current.PackSha256 ? pack : null;
    }

    public static string Sha(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

/// <summary>What a patch profile has to hold true of the game's settings.</summary>
public sealed class InstalledGameSettingsTests
{
    /// <summary>The engine loads these before the pack is mounted, as it does the audio bus layout, so a game that names one would not get it.</summary>
    [InstalledGameFact]
    public void TheGameNamesNoCustomThemeFontOrTranslations()
    {
        using var pack = new PackReader(InstalledGame.Pack!);
        var settings = Encoding.UTF8.GetString(pack.Read(pack.Entries.Single(entry => entry.Name == "project.binary")));
        Assert.DoesNotContain("gui/theme/custom", settings);
        Assert.DoesNotContain("internationalization/locale/translations", settings);
    }
}

/// <summary>All the game's textures, converted once for the tests that need them.</summary>
public sealed class ConvertedTextures : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("installed-textures-").FullName;
    private readonly Lazy<TextureConversionResult> _result;

    public ConvertedTextures() => _result = new(() =>
        new TextureConversion(_folder, GameProfile.Current.PackSha256, GameProfile.Current.TexturesKeptOriginal).Run(InstalledGame.Pack!));

    public TextureConversionResult Result => _result.Value;

    public string Pack
    {
        get
        {
            _ = _result.Value;
            return Path.Combine(_folder, TextureConversion.OutputName);
        }
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}

/// <summary>
/// The app's pack on v0.113.0 against what the Python tools make. Update the pin when a step changes on purpose, and only after
/// a fight on the phone: nothing else tells whether the new pack plays.
/// </summary>
public sealed class InstalledGameTests(ConvertedTextures textures) : IDisposable, IClassFixture<ConvertedTextures>
{
    /// <summary>What <c>build_game_pack</c> wrote: the pack first played on the phone.</summary>
    private const string PlayedSha256 = "fefb38650f03041d981cc85fc42e6c9969202b1b6ea3a3593351649e01ae0ce7";
    private const int PlayedEntries = 13704;

    private readonly string _work = Directory.CreateTempSubdirectory("installed-pack-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    [InstalledGameFact]
    public void TheAppsPackIsThePythonToolsPackByteForByte()
    {
        string output = Path.Combine(_work, "FullGame.pck");
        Assert.Equal(PlayedEntries, GamePackBuilder.Build(InstalledGame.Pack!, textures.Pack, GameProfile.Current.PackSha256, output));
        string sha = InstalledGame.Sha(output);
        Assert.Equal(PlayedSha256, sha);
        string reference = InstalledGame.Analysis("full-game-reference/FullGame.pck");
        if (File.Exists(reference)) Assert.Equal(InstalledGame.Sha(reference), sha);
    }

    /// <summary>Holds -ffp-contract=off and the rest of what the bytes depend on, over every texture.</summary>
    [InstalledGameFact]
    public void TheConvertedTexturesAreThePythonToolsByteForByte()
    {
        Assert.Equal(1566, textures.Result.Total);
        Assert.Equal(InstalledGame.Sha(InstalledGame.ReferenceTextures), InstalledGame.Sha(textures.Pack));
    }

    /// <summary>The list the app goes by is what the Mac's quality bar left out.</summary>
    [InstalledGameFact]
    public void TheProfileKeepsWhatTheQualityBarKept()
    {
        using var report = JsonDocument.Parse(File.ReadAllBytes(InstalledGame.Analysis("full-game-textures-astc-v1/texture-report.json")));
        var kept = report.RootElement.GetProperty("keptOriginal").EnumerateArray().Select(texture => texture.GetProperty("source").GetString()!);
        Assert.Equal(kept, GameProfile.Current.TexturesKeptOriginal);
    }

    /// <summary>Each step alone against the Python tool's own files, which says which step moved when the pack's hash does.</summary>
    [InstalledGameFact]
    public void ScenesAreThoseOfThePythonTool()
    {
        string folder = InstalledGame.Analysis("full-game-scenes-phone-v1");
        using var game = new PackReader(InstalledGame.Pack!);
        var expected = Directory.GetFiles(folder, "*.tscn", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(folder, path), StringComparer.Ordinal);
        Assert.NotEmpty(expected);
        var made = new List<string>();
        foreach (var entry in game.Entries.Where(entry => entry.Name.EndsWith(".tscn", StringComparison.Ordinal)))
        {
            if (ParticleLimits.AdjustScene(game.Read(entry)) is not { } scene) continue;
            made.Add(entry.Name);
            Assert.True(File.ReadAllBytes(expected[entry.Name]).AsSpan().SequenceEqual(scene), entry.Name);
        }
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), made.Order(StringComparer.Ordinal));
    }

    [InstalledGameFact]
    public void TheWarmupInventoryIsThatOfThePythonTool()
    {
        string folder = InstalledGame.Analysis("full-game-warmup");
        using var game = new PackReader(InstalledGame.Pack!);
        var entries = game.Entries.Where(entry => !entry.Name.StartsWith("addons/sentry/", StringComparison.Ordinal)).ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        var built = ShaderWarmupInventory.Build(entries.Keys, name => Encoding.UTF8.GetString(game.Read(entries[name])));
        var expected = Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(expected, built.Select(file => file.Name[ShaderWarmupInventory.Folder.Length..]));
        foreach (var (name, data) in built)
            Assert.True(File.ReadAllBytes(Path.Combine(folder, name[ShaderWarmupInventory.Folder.Length..])).AsSpan().SequenceEqual(data), name);
    }
}
