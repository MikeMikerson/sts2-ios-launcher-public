using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using STS2Mobile.Platform;

namespace STS2Mobile.ModWeaver.Tests;

// Weaves the fixture mod into the fixture game. The woven code must behave as Harmony's patches do on desktop.
internal static class GoodModWeaving
{
    public static readonly string SourceSha256 = new('a', 64);
    private static readonly string Bin = AppContext.BaseDirectory;
    private static readonly string PlatformPath = typeof(BuiltInMods).Assembly.Location;

    public static (AssemblyDefinition Game, AssemblyDefinition Mod) Read()
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Bin);
        var parameters = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate, InMemory = true };
        var game = AssemblyDefinition.ReadAssembly(Path.Combine(Bin, "sts2.dll"), parameters);
        return (game, AssemblyDefinition.ReadAssembly(Path.Combine(Bin, "GoodMod.dll"), parameters));
    }

    public static WeaveReport Weave(AssemblyDefinition game, AssemblyDefinition mod, out AssemblyDefinition woven)
    {
        using var platform = ModuleDefinition.ReadModule(PlatformPath);
        (woven, var report) = new Weaver(game, platform).Weave("GoodMod", mod, SourceSha256);
        return report;
    }

    /// <summary>Weaves fresh copies into <paramref name="folder"/>.</summary>
    public static WeaveReport WeaveInto(string folder)
    {
        var (game, mod) = Read();
        var report = Weave(game, mod, out var woven);
        game.Write(Path.Combine(folder, "sts2.dll"));
        woven.Write(Path.Combine(folder, "GoodMod.dll"));
        return report;
    }
}

/// <summary>Loads the woven fixture copies; everything else (STS2Mobile.Platform, the runtime) is shared with the tests.</summary>
internal sealed class WovenFixtureContext(string folder) : AssemblyLoadContext("woven-fixture")
{
    protected override Assembly? Load(AssemblyName name)
    {
        string path = Path.Combine(folder, name.Name + ".dll");
        return name.Name is "sts2" or "GoodMod" && File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }
}

/// <summary>One woven copy of the fixture game and mod, loaded for running.</summary>
public sealed class WovenGoodMod : IDisposable
{
    public WovenGoodMod()
    {
        Report = GoodModWeaving.WeaveInto(Folder);
        var context = new WovenFixtureContext(Folder);
        Game = context.LoadFromAssemblyName(new AssemblyName("sts2"));
        Mod = context.LoadFromAssemblyName(new AssemblyName("GoodMod"));
    }

    public string Folder { get; } = Directory.CreateTempSubdirectory("modweaver-tests-").FullName;

    public WeaveReport Report { get; }

    public Assembly Game { get; }

    public Assembly Mod { get; }

    public string WovenModPath => Path.Combine(Folder, "GoodMod.dll");

    public void Dispose() => Directory.Delete(Folder, recursive: true);
}
