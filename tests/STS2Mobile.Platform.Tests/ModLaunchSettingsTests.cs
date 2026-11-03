using STS2Mobile.Platform;

namespace STS2Mobile.Platform.Tests;

public sealed class ModLaunchSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mod-choice-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingOrInvalidChoiceLeavesModsOff()
    {
        Assert.False(ModLaunchSettings.Read(_folder));
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, ModLaunchSettings.FileName), "unexpected");
        Assert.False(ModLaunchSettings.Read(_folder));
    }

    [Fact]
    public void ChoiceSurvivesReopeningAndCanBeTurnedOffAgain()
    {
        ModLaunchSettings.Write(_folder, true);
        Assert.True(ModLaunchSettings.Read(_folder));
        ModLaunchSettings.Write(_folder, false);
        Assert.False(ModLaunchSettings.Read(_folder));
        Assert.False(File.Exists(Path.Combine(_folder, ModLaunchSettings.FileName + ".partial")));
    }

    [Fact]
    public void OffModeDoesNotReadCodeOrExposeExistingPacksToTheGame()
    {
        string mods = Path.Combine(_folder, "mods");
        Directory.CreateDirectory(mods);
        string pack = Path.Combine(mods, "Existing.pck");
        File.WriteAllText(pack, "existing pack");
        ModLaunchSettings.Capture(_folder, "0");
        bool loaded = false;
        Assert.Throws<InvalidOperationException>(() => BuiltInMods.Register([], _ =>
        {
            loaded = true;
            return typeof(ModLaunchSettingsTests).Assembly;
        }, _ => { }));
        Assert.False(loaded);
        Assert.False(BuiltInMods.AssemblyFileExists(new object(), Path.Combine(mods, "Existing.dll")));
        Assert.Throws<InvalidOperationException>(() => BuiltInMods.LoadFromAssemblyPath(
            System.Runtime.Loader.AssemblyLoadContext.Default, Path.Combine(mods, "Existing.dll")));
        string anchor = BuiltInMods.ModLoaderAnchor();
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(anchor)!, "mods")));
        Assert.NotEqual(_folder, Path.GetDirectoryName(anchor));
        BuiltInMods.WriteManifests(mods);
        Assert.Equal("existing pack", File.ReadAllText(pack));
        Assert.Single(Directory.EnumerateFiles(mods));
        // A choice for the next launch cannot change an assembly already selected in this one.
        ModLaunchSettings.Write(_folder, true);
        Assert.False(ModLaunchSettings.Enabled);
        Assert.Equal(ModLaunchSettings.PlainAssembly, ModLaunchSettings.AssemblyFile);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
