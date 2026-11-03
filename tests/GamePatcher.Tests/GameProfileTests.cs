using Mono.Cecil;
using STS2Mobile.GamePatcher;
using STS2Mobile.Platform;

namespace STS2Mobile.GamePatcher.Tests;

public sealed class GameProfileTests
{
    [Fact]
    public void PinsTheFilesTheAppReads()
    {
        var profile = GameProfile.Current;
        Assert.Equal("0.111.0", profile.Version);
        Assert.Equal(["Sentry.Godot.dll", "Sentry.dll", "sts2.dll"], profile.Inputs.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["SmartFormat.ZString.dll", "SmartFormat.dll", "Steamworks.NET.dll"], profile.Unchanged.Keys.Order(StringComparer.Ordinal));
        Assert.All(profile.Inputs.Values.Concat(profile.Unchanged.Values), hash => Assert.Equal(64, hash.Length));
    }

    [Theory]
    [InlineData("adapters/STS2Mobile.Platform.dll")]
    [InlineData("adapters/STS2Mobile.GameAdapters.dll")]
    public void AdaptersNameTheFrameworkVersionTheGameDoes(string resource)
    {
        using var stream = typeof(GameProfile).Assembly.GetManifestResourceStream(resource)!;
        using var data = new MemoryStream();
        stream.CopyTo(data);
        data.Position = 0;
        using var module = ModuleDefinition.ReadModule(data);
        Assert.Equal(9, module.AssemblyReferences.Single(r => r.Name == "System.Runtime").Version.Major);
    }

    [Fact]
    public void TheAppLooksForModsInTheOriginalFolder()
    {
        // QuickMatch is the one built-in mod, and optional: a build without its files has none.
        Assert.Empty(BuiltInModInput.InOriginalFolder("/original", """{"mods":[{"id":"QuickMatch","optional":true,"dllSha256":"d","manifestSha256":"m"}]}""", _ => null));
    }

    [Fact]
    public void TheMacReadsModsFromTheirCatalogPaths()
    {
        var mods = BuiltInModInput.AtCatalogPaths("""{"mods":[{"id":"A","dll":"~/x/A.dll","manifest":"/y/A.json","dllSha256":"d","manifestSha256":"m"}]}""");
        var mod = Assert.Single(mods);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "x/A.dll"), mod.DllPath);
        Assert.Equal("/y/A.json", mod.ManifestPath);
        Assert.Equal(("d", "m"), (mod.DllSha256, mod.ManifestSha256));
    }

    [Fact]
    public void FootprintIsReadable()
    {
        var (current, peak) = MemoryFootprint.Read();
        Assert.InRange(current, 1, long.MaxValue);
        Assert.InRange(peak, current, long.MaxValue);
    }
}
