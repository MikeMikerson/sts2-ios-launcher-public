using System.Text.Json;

namespace STS2Mobile.GamePatcher.Tests;

public sealed class ImportedModInputTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("imported-mod-input-").FullName;
    private string Inputs => Path.Combine(_root, "mod-inputs");
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Add(string id, bool code = true, bool pack = true)
    {
        string folder = Path.Combine(Inputs, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, id + ".json"), JsonSerializer.Serialize(new { id, has_dll = code, has_pck = pack }));
        if (code) File.WriteAllText(Path.Combine(folder, id + ".dll"), "source code metadata");
        if (pack)
        {
            File.WriteAllText(Path.Combine(folder, id + ".pck"), "original pack");
            File.WriteAllText(Path.Combine(folder, "prepared.pck"), "cleaned pack");
        }
        return folder;
    }

    [Fact]
    public void ReadsCodeInputsInStableOrderAndUsesTheCleanedPack()
    {
        Add("Zed");
        Add("Resources", code: false);
        Add("Alpha", pack: false);
        Directory.CreateDirectory(Path.Combine(Inputs, ".incoming-interrupted"));
        var inputs = BuiltInModInput.InImportedFolder(Inputs);
        Assert.Equal(["Alpha", "Zed"], inputs.Select(mod => mod.Id));
        Assert.Null(inputs[0].PckPath);
        Assert.Equal(Path.Combine(Inputs, "Zed", "prepared.pck"), inputs[1].PckPath);
        foreach (var input in inputs) input.VerifyUnchanged();
    }

    [Theory]
    [InlineData("Example.dll")]
    [InlineData("Example.json")]
    [InlineData("prepared.pck")]
    public void ChangingAnyConsumedFileInvalidatesItsSnapshot(string name)
    {
        string folder = Add("Example");
        var before = Assert.Single(BuiltInModInput.InImportedFolder(Inputs));
        File.AppendAllText(Path.Combine(folder, name), " ");
        var after = Assert.Single(BuiltInModInput.InImportedFolder(Inputs));
        Assert.NotEqual(before, after);
        Assert.Throws<WeaveException>(before.VerifyUnchanged);
    }

    [Theory]
    [InlineData("sts2")]
    [InlineData("sts2.unmodded")]
    [InlineData("GodotSharp")]
    [InlineData("STS2Mobile.Platform")]
    public void RefusesGameOrAppAssemblyNames(string id)
    {
        Add(id);
        Assert.Throws<InvalidDataException>(() => BuiltInModInput.InImportedFolder(Inputs));
    }

    [Fact]
    public void InvalidImportsBecomeAResultInsteadOfThrowingFromForApp()
    {
        string folder = Add("Broken");
        File.Delete(Path.Combine(folder, "Broken.dll"));
        string game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(game, "original"));
        File.WriteAllText(Path.Combine(game, "original", "sts2.dll"), "game");
        var preparation = InstalledGame.WithoutBuiltIns(game);
        var result = preparation.Run();
        Assert.Equal(PreparationStatus.Refused, result.Status);
        Assert.Contains("imported mods", result.Message);
        Assert.Contains("Broken.dll", result.Message);
    }
}
