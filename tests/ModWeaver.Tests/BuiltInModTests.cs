using System.Reflection;
using System.Runtime.Loader;
using STS2Mobile.Platform;

namespace STS2Mobile.ModWeaver.Tests;

/// <summary>
/// BuiltInMods registers once per process, so the steps before and during registration run here once
/// and the facts check what each step did.
/// </summary>
public sealed class RegisteredGoodMod : IDisposable
{
    private readonly WovenGoodMod _woven = new();

    public RegisteredGoodMod()
    {
        ModLaunchSettings.Capture(_woven.Folder, "1");
        Initialize = _woven.Mod.GetType("GoodMod.Main")!.GetMethod("Initialize")!;
        InitializeBeforeRegistration = Record.Exception(() => Initialize.Invoke(null, null));
        MismatchedRegistration = Record.Exception(() => BuiltInMods.Register(
            [new BuiltInMod("GoodMod", "{\"id\":\"GoodMod\"}", new string('b', 64), new string('c', 64), 11)], _ => Mod, Log.Add));
        BuiltInMods.Register(
            [new BuiltInMod("GoodMod", "{\"id\":\"GoodMod\"}", GoodModWeaving.SourceSha256, new string('c', 64), 11)], _ => Mod, Log.Add);
    }

    public Assembly Mod => _woven.Mod;

    public MethodInfo Initialize { get; }

    public Exception? InitializeBeforeRegistration { get; }

    public Exception? MismatchedRegistration { get; }

    public List<string> Log { get; } = [];

    public void Dispose() => _woven.Dispose();
}

public sealed class BuiltInModTests(RegisteredGoodMod registered) : IClassFixture<RegisteredGoodMod>, IDisposable
{
    private readonly string _sandbox = Directory.CreateTempSubdirectory("modweaver-mods-").FullName;

    public void Dispose() => Directory.Delete(_sandbox, recursive: true);

    [Fact]
    public void UnregisteredModCannotConfirmItsPatches()
    {
        var error = Assert.IsType<TargetInvocationException>(registered.InitializeBeforeRegistration);
        Assert.IsType<NotSupportedException>(error.InnerException);
    }

    [Fact]
    public void ModWithAnotherSourceHashIsNotRegistered() => Assert.IsType<InvalidDataException>(registered.MismatchedRegistration);

    [Fact]
    public void RegisteredModConfirmsItsWovenPatches()
    {
        registered.Initialize.Invoke(null, null);
        Assert.Contains("11 patches are already woven", registered.Log[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void ParameterlessPatchAllConfirmsTheCallingModsWovenPatches()
    {
        registered.Mod.GetType("GoodMod.Main")!.GetMethod("InitializeWithoutAssembly")!.Invoke(null, null);
        Assert.Contains("GoodMod", registered.Log[^1], StringComparison.Ordinal);
        Assert.Contains("11 patches are already woven", registered.Log[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void LoaderReceivesTheCompiledInAssembly() =>
        Assert.Same(registered.Mod, BuiltInMods.LoadFromAssemblyPath(AssemblyLoadContext.Default, "/mods/GoodMod/GoodMod.dll"));

    [Fact]
    public void BuiltInCodeCountsAsPresent() => Assert.True(BuiltInMods.AssemblyFileExists(new object(), "/mods/GoodMod/GoodMod.dll"));

    [Fact]
    public void CodeOnDiskNeverCountsAsPresent() => Assert.False(BuiltInMods.AssemblyFileExists(new object(), "/mods/Other/Other.dll"));

    [Fact]
    public void CodeOnDiskIsNeverLoaded() =>
        Assert.Throws<NotSupportedException>(() => BuiltInMods.LoadFromAssemblyPath(AssemblyLoadContext.Default, "/mods/Other/Other.dll"));

    [Fact]
    public void ManifestIsWrittenForTheGamesLoader() =>
        Assert.Equal("{\"id\":\"GoodMod\"}", File.ReadAllText(Path.Combine(PrepareModsFolder(), "GoodMod", "GoodMod.json")));

    [Fact]
    public void BuiltInModNoLongerCompiledInIsRemoved() =>
        Assert.False(Directory.Exists(Path.Combine(PrepareModsFolder(), "Retired")));

    [Fact]
    public void OtherModsInTheFolderAreLeftAlone() =>
        Assert.True(File.Exists(Path.Combine(PrepareModsFolder(), "Imported", "Imported.json")));

    [Fact]
    public void NoCodeFileIsWritten() =>
        Assert.False(File.Exists(Path.Combine(PrepareModsFolder(), "GoodMod", "GoodMod.dll")));

    // A mods folder from an earlier start: a built-in mod that is gone and a mod imported on the phone.
    private string PrepareModsFolder()
    {
        string mods = Path.Combine(_sandbox, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "Retired"));
        File.WriteAllText(Path.Combine(mods, "Retired", "Retired.json"), "{\"id\":\"Retired\"}");
        File.WriteAllLines(Path.Combine(mods, ".builtin"), ["Retired"]);
        Directory.CreateDirectory(Path.Combine(mods, "Imported"));
        File.WriteAllText(Path.Combine(mods, "Imported", "Imported.json"), "{\"id\":\"Imported\"}");
        BuiltInMods.WriteManifests(mods);
        return mods;
    }
}
