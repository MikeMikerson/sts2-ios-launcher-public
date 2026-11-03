using Mono.Cecil;
using STS2Mobile.GamePatcher;

namespace STS2Mobile.GamePatcher.Tests;

/// <summary>
/// Tolerant mode on the real game. A build with no profile is stood in for by v0.111.0 itself and by copies with one method renamed,
/// since no other build's files are on this Mac. Skipped where there is no Steam install of v0.111.0.
/// </summary>
public sealed class UntestedBuildTests : IDisposable
{
    private const string PlayedSha256 = "4897d2067b695faf5d193a5e0e4ab763c62c75b157f6f6e6c75ecd2b1c4a800a";
    private readonly string _work = Directory.CreateTempSubdirectory("untested-build-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    /// <summary>A game folder as a download leaves it, with the game assembly changed by <paramref name="change"/>.</summary>
    private string GameFolder(Action<ModuleDefinition>? change = null)
    {
        string game = Path.Combine(_work, "game");
        string original = Path.Combine(game, STS2Mobile.GamePatcher.GameFolder.Original);
        Directory.CreateDirectory(original);
        foreach (string name in GameProfile.Current.Inputs.Keys) File.Copy(Path.Combine(InstalledGame.Data!, name), Path.Combine(original, name));
        foreach (string name in GameProfile.Current.Unchanged.Keys) File.Copy(Path.Combine(InstalledGame.Data!, name), Path.Combine(game, name));
        if (change != null)
        {
            string assembly = Path.Combine(original, GameProfile.GameAssembly);
            using var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(InstalledGame.Data!);
            using (var module = ModuleDefinition.ReadModule(Path.Combine(InstalledGame.Data!, GameProfile.GameAssembly), new ReaderParameters { ReadingMode = ReadingMode.Immediate, AssemblyResolver = resolver }))
            {
                change(module);
                module.Write(assembly + ".changed");
            }
            File.Move(assembly + ".changed", assembly, overwrite: true);
        }
        return game;
    }

    private static void Rename(ModuleDefinition module, string type, string method) =>
        module.GetType(type).Methods.Single(m => m.Name == method).Name = method + "Elsewhere";

    [InstalledGameFact]
    public void TheTestedBuildInTolerantModeGetsEveryPatchAndTheSameBytes()
    {
        string output = Path.Combine(_work, "tolerant");
        Directory.CreateDirectory(output);
        var built = GameAssemblyBuilder.Build(InstalledGame.Data!, [], output, tolerant: true);
        Assert.Equal(PlayedSha256, InstalledGame.Sha(Path.Combine(output, GameProfile.GameAssembly)));
        Assert.All(built.Patches, patch => Assert.True(patch.Applied, patch.Name));
        Assert.All(built.Patches, patch => Assert.Null(patch.Note));
        Assert.Equal(GameAssemblyBuilder.PatchList().Select(step => step.Name), built.Patches.Select(patch => patch.Name));
    }

    [InstalledGameFact]
    public void APatchTheGameCanDoWithoutIsSkippedAndReported()
    {
        string game = GameFolder(module => Rename(module, "MegaCrit.Sts2.Core.Nodes.CommonUi.NCursorManager", "UpdateCursor"));
        var result = InstalledGame.WithoutBuiltIns(game).Run();
        Assert.Equal(PreparationStatus.Prepared, result.Status);
        Assert.False(result.Tested);
        var skipped = Assert.Single(result.Patches!, patch => !patch.Applied);
        Assert.Equal("No mouse cursor", skipped.Name);
        Assert.False(skipped.Required);
        Assert.Contains("UpdateCursor is not in this game version", skipped.Note);
        Assert.NotEqual(PlayedSha256, result.OutputSha256);

        var again = InstalledGame.WithoutBuiltIns(game).Run();
        Assert.Equal(PreparationStatus.Cached, again.Status);
        Assert.Equal(result.Patches, again.Patches);
    }

    [InstalledGameFact]
    public void APatchTheGameCannotRunWithoutRefusesWithAPlainMessageAndChangesNothing()
    {
        string game = GameFolder(module => Rename(module, "MegaCrit.Sts2.Core.Nodes.NGame", "InitializePlatform"));
        var result = InstalledGame.WithoutBuiltIns(game).Run();
        Assert.Equal(PreparationStatus.Refused, result.Status);
        Assert.False(result.Tested);
        Assert.Contains("Steam ownership checked by the launcher", result.Message);
        Assert.Contains("has not been tested", result.Message);
        Assert.False(File.Exists(Path.Combine(game, GameProfile.GameAssembly)));
        Assert.False(File.Exists(Path.Combine(game, GameAssemblyPreparation.RecordName)));
        Assert.False(Directory.Exists(Path.Combine(game, ".preparing")));
    }

    [InstalledGameFact]
    public void TheTestedBuildIsKnownByItsAssemblyWithNoRecord()
    {
        string game = GameFolder();
        var result = InstalledGame.WithoutBuiltIns(game).Run();
        Assert.True(result.Tested);
        Assert.Equal(InstalledGameTests.ModsOnSha256, result.OutputSha256);
        Assert.Equal(PlayedSha256, InstalledGame.Sha(Path.Combine(game, "sts2.unmodded.dll")));
    }

    [InstalledGameFact]
    public void ABuildRecordedAsTestedKeepsItsProfileSoADamagedFileIsRefused()
    {
        string game = GameFolder(module => Rename(module, "MegaCrit.Sts2.Core.Nodes.CommonUi.NCursorManager", "UpdateCursor"));
        new InstalledBuild(InstalledBuild.BetaBranch, GameProfile.Current.Steam!.Manifest, 1, GameProfile.Current.Version, Tested: true).Write(game);
        var result = InstalledGame.WithoutBuiltIns(game).Run();
        Assert.Equal(PreparationStatus.Refused, result.Status);
        Assert.Contains("sts2.dll", result.Message);
    }

    [Fact]
    public void TheShortListOfPatchesTheGameCannotRunWithout()
    {
        var steps = GameAssemblyBuilder.PatchList();
        Assert.Equal(steps.Count, steps.Select(step => step.Name).Distinct().Count());
        Assert.Equal(
        [
            "No second engine entry point", "Steam ownership checked by the launcher", "Steam Cloud through the launcher", "Player id from the signed-in account",
            "Player name from the signed-in account", "Saves in the app's Steam folder", "Links stay inside the app", "No desktop system report", "No desktop Steamworks start",
            "No Steam Workshop check", "Mod code only from the app", "No Harmony at run time", "No gameplay metrics sent", "No logs or screenshots sent as feedback",
            "No LAN play", "Crash reporting SDK removed", "The game's web requests go through the app's guard", "Save upgrades without run-time JSON code",
            "Texture atlases read without run-time JSON code",
        ], steps.Where(step => step.Required).Select(step => step.Name));
        Assert.Equal(
        [
            "No JIT warmup", "No Steam invite dialog", "Mods from the app's own folder", "Mods screen opens Files", "Fewer background loads at once, to bound memory",
            "No mouse cursor", "A card let go below the play zone goes back", "The game fills the screen", "The game's version read from its folder",
        ], steps.Where(step => !step.Required).Select(step => step.Name));
    }
}
