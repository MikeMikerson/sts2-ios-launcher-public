using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
using STS2Mobile.GamePatcher;

namespace STS2Mobile.GamePatcher.Tests;

/// <summary>A game assembly of one version, from <c>scripts/prepare-game-files.sh</c>'s folder or Steam's install. Skipped when neither has it.</summary>
public sealed class GameAssemblyFactAttribute : FactAttribute
{
    public GameAssemblyFactAttribute(string version)
    {
        if (GameAssemblies.Folder(version) == null) Skip = $"No v{version} game assembly on this Mac.";
    }
}

internal static class GameAssemblies
{
    private static readonly Dictionary<string, string> Sha256 = new()
    {
        ["0.113.0"] = "d009478a54a9b7c2396924a89618c50f9dcbf1503ee3fdb1e8d4aeee3bd66f24",
        ["0.113.1"] = "a671e9efb86a7b80405133105665867b053c787670d475b4347cf5ccc15cb5fb",
    };

    /// <summary>A folder holding that version's <c>sts2.dll</c> with what it needs to be prepared.</summary>
    public static string? Folder(string version)
    {
        string? root = RepositoryRoot();
        string steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64");
        string?[] candidates = [Environment.GetEnvironmentVariable("STS2_GAME_DATA"), steam, root == null ? null : Path.Combine(root, "analysis/game-files/original")];
        return candidates.OfType<string>().FirstOrDefault(folder =>
        {
            string path = Path.Combine(folder, GameProfile.GameAssembly);
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream)) == Sha256[version];
        });
    }

    private static string? RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "STS2Mobile.slnx"))) return folder.FullName;
        return null;
    }
}

/// <summary>The host's game references, listed at build time and checked at preparation.</summary>
public sealed class GameReferenceTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("game-references-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private const string ThingDo = "method Game.Thing::Do(System.Int32) System.Void";
    private const string SpecialDo = "method Game.Special::Do(System.Int32) System.Void";
    private const string Count = "field Game.Thing::Count System.Int32";

    /// <summary>
    /// A small "sts2" with a nested type, a generic type, a generic method and a derived type, so no game file is needed. The change runs
    /// before it is written, standing in for a build that differs.
    /// </summary>
    private string WriteGame(string name, Action<ModuleDefinition>? change = null)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(GameReferenceList.GameAssemblyName, new Version(1, 0)), "sts2.dll", ModuleKind.Dll);
        var module = assembly.MainModule;
        var types = module.TypeSystem;
        var thing = new TypeDefinition("Game", "Thing", TypeAttributes.Public | TypeAttributes.Class, types.Object);
        module.Types.Add(thing);
        thing.Methods.Add(Body(new MethodDefinition("Do", MethodAttributes.Public, types.Void) { Parameters = { new ParameterDefinition(types.Int32) } }));
        var make = new MethodDefinition("Make", MethodAttributes.Public | MethodAttributes.Static, types.Void);
        var value = new GenericParameter("TValue", make);
        make.GenericParameters.Add(value);
        make.ReturnType = value;
        make.Parameters.Add(new ParameterDefinition(value));
        thing.Methods.Add(Body(make));
        thing.Fields.Add(new FieldDefinition("Count", FieldAttributes.Public, types.Int32));
        thing.NestedTypes.Add(new TypeDefinition("", "Inner", TypeAttributes.NestedPublic | TypeAttributes.Class, types.Object));
        var box = new TypeDefinition("Game", "Box`1", TypeAttributes.Public | TypeAttributes.Class, types.Object);
        var item = new GenericParameter("TItem", box);
        box.GenericParameters.Add(item);
        box.Methods.Add(Body(new MethodDefinition("Get", MethodAttributes.Public, item)));
        module.Types.Add(box);
        module.Types.Add(new TypeDefinition("Game", "Special", TypeAttributes.Public | TypeAttributes.Class, thing));
        change?.Invoke(module);
        string path = Path.Combine(_work, name, GameProfile.GameAssembly);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        assembly.Write(path);
        return path;
    }

    private static MethodDefinition Body(MethodDefinition method)
    {
        method.Body.GetILProcessor().Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>A host compiled against <paramref name="game"/>: one method using each kind of reference, as the compiler writes them.</summary>
    private string WriteHost(string game)
    {
        using var gameModule = ModuleDefinition.ReadModule(game);
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Host", new Version(1, 0)), "Host.dll", ModuleKind.Dll);
        var module = assembly.MainModule;
        var user = new TypeDefinition("Host", "User", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(user);
        var run = new MethodDefinition("Run", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        user.Methods.Add(run);
        var il = run.Body.GetILProcessor();
        var thing = gameModule.GetType("Game.Thing");
        il.Emit(OpCodes.Call, module.ImportReference(thing.Methods.Single(method => method.Name == "Do")));
        il.Emit(OpCodes.Ldsfld, module.ImportReference(thing.Fields.Single()));
        il.Emit(OpCodes.Ldtoken, module.ImportReference(thing.NestedTypes.Single()));
        il.Emit(OpCodes.Call, new GenericInstanceMethod(module.ImportReference(thing.Methods.Single(method => method.Name == "Make"))) { GenericArguments = { module.TypeSystem.String } });
        var box = gameModule.GetType("Game.Box`1");
        var get = module.ImportReference(box.Methods.Single());
        get.DeclaringType = new GenericInstanceType(module.ImportReference(box)) { GenericArguments = { module.TypeSystem.Int32 } };
        il.Emit(OpCodes.Call, get);
        // Found on the base type by the runtime, as Special::Do names it.
        il.Emit(OpCodes.Call, new MethodReference("Do", module.TypeSystem.Void, module.ImportReference(gameModule.GetType("Game.Special")))
            { HasThis = true, Parameters = { new ParameterDefinition(module.TypeSystem.Int32) } });
        il.Emit(OpCodes.Ret);
        string path = Path.Combine(_work, "Host.dll");
        assembly.Write(path);
        return path;
    }

    private static IReadOnlyList<string> Missing(string game, IReadOnlyList<string> lines)
    {
        using var module = ModuleDefinition.ReadModule(game);
        return GameReferenceList.Missing(module, lines);
    }

    private IReadOnlyList<string> HostLines()
    {
        using var host = ModuleDefinition.ReadModule(WriteHost(WriteGame("stub")));
        return GameReferenceList.Of([host]);
    }

    [Fact]
    public void AHostsReferencesAreListedWithGenericParametersByPosition()
    {
        Assert.Equal(new[]
        {
            Count,
            "method Game.Box`1::Get() !0",
            SpecialDo,
            ThingDo,
            "method Game.Thing::Make`1(!!0) !!0",
            "type Game.Box`1",
            "type Game.Special",
            "type Game.Thing",
            "type Game.Thing/Inner",
        }, HostLines());
    }

    [Fact]
    public void TheGameTheHostWasBuiltAgainstHasEveryReference() => Assert.Empty(Missing(WriteGame("same"), HostLines()));

    [Fact]
    public void AMissingRenamedOrChangedMemberIsNamed()
    {
        var lines = HostLines();
        string game = WriteGame("changed", module =>
        {
            var thing = module.GetType("Game.Thing");
            thing.Methods.Single(method => method.Name == "Do").Parameters[0].ParameterType = module.TypeSystem.Int64;
            thing.Fields.Single().Name = "Total";
            thing.NestedTypes.Clear();
        });
        // Special::Do was Thing::Do, so it goes with it.
        Assert.Equal(new[] { Count, SpecialDo, ThingDo, "type Game.Thing/Inner" }, Missing(game, lines));
    }

    [Fact]
    public void TheListReadsBackAsWritten()
    {
        var lines = HostLines();
        Assert.Equal(lines, GameReferenceList.Parse(GameReferenceList.Text(lines)));
    }

    [Fact]
    public void TheEmbeddedListNamesTheGamesMembers()
    {
        var lines = GameAssemblyPreparation.EmbeddedGameReferences();
        Assert.True(lines.Count > 50, $"{lines.Count} lines");
        Assert.All(lines, line => Assert.Matches("^(type|method|field) MegaCrit\\.", line));
        Assert.Equal(lines.Order(StringComparer.Ordinal), lines);
    }

    /// <summary>An untested game folder with the small game as its assembly, made by a stand-in that copies it.</summary>
    private GameAssemblyPreparation Preparation(string game, IReadOnlyList<string> references, string folder = "folder")
    {
        string gameFolder = Path.Combine(_work, folder);
        string original = Path.Combine(gameFolder, STS2Mobile.GamePatcher.GameFolder.Original);
        Directory.CreateDirectory(original);
        File.Copy(game, Path.Combine(original, GameProfile.GameAssembly), overwrite: true);
        foreach (string name in GameLayout.UnchangedAssemblies) File.WriteAllText(Path.Combine(gameFolder, name), "");
        return new GameAssemblyPreparation(gameFolder, null, "patcher", (input, output) =>
        {
            File.Copy(Path.Combine(input, GameProfile.GameAssembly), Path.Combine(output, GameProfile.GameAssembly));
            return [];
        }, "9.9.9", gameReferences: references);
    }

    [Fact]
    public void ABuildLackingAReferenceIsRefusedByNameAndNothingChanges()
    {
        var lines = HostLines();
        string game = WriteGame("changed", module => module.GetType("Game.Thing").Fields.Clear());
        var result = Preparation(game, lines).Run();
        Assert.Equal(PreparationStatus.Refused, result.Status);
        Assert.Contains("Game version 9.9.9 cannot run with this app: it lacks Game.Thing::Count System.Int32, which the app's code uses.", result.Message);
        Assert.Contains(GameAssemblyPreparation.RefusalNext, result.Message);
        string folder = Path.Combine(_work, "folder");
        Assert.False(File.Exists(Path.Combine(folder, GameProfile.GameAssembly)));
        Assert.False(File.Exists(Path.Combine(folder, GameAssemblyPreparation.RecordName)));
        Assert.False(Directory.Exists(Path.Combine(folder, ".preparing")));
    }

    [Fact]
    public void SeveralMissingAreCounted()
    {
        string game = WriteGame("changed", module => module.Types.Remove(module.GetType("Game.Box`1")));
        Assert.Contains("it lacks Game.Box`1::Get() !0 and 1 more,", Preparation(game, HostLines()).Run().Message);
    }

    [Fact]
    public void ACachedResultSkipsTheCheckUntilTheListChanges()
    {
        var lines = HostLines();
        string game = WriteGame("same");
        Assert.Equal(PreparationStatus.Prepared, Preparation(game, lines).Run().Status);
        Assert.Equal(PreparationStatus.Cached, Preparation(game, lines).Run().Status);
        var refused = Preparation(game, [.. lines, GameAssemblyPreparation.MissingOnPurpose]).Run();
        Assert.Equal(PreparationStatus.Refused, refused.Status);
        Assert.Contains("STS2Mobile.TestSwitch.NotInTheGame", refused.Message);
    }

    private static void HasEveryReference(string version)
    {
        string folder = GameAssemblies.Folder(version)!;
        var references = GameAssemblyPreparation.EmbeddedGameReferences();
        Assert.Empty(Missing(Path.Combine(folder, GameProfile.GameAssembly), references));
        // The prepared assembly is what runs, so that is the one preparation checks.
        string output = Directory.CreateTempSubdirectory("game-references-prepared-").FullName;
        try
        {
            GameAssemblyBuilder.Build(folder, [], output, tolerant: true);
            Assert.Empty(Missing(Path.Combine(output, GameProfile.GameAssembly), references));
        }
        finally { Directory.Delete(output, recursive: true); }
    }

    [GameAssemblyFact("0.113.0")]
    public void Version0_113_0HasEveryReference() => HasEveryReference("0.113.0");

    [GameAssemblyFact("0.113.1")]
    public void Version0_113_1HasEveryReference() => HasEveryReference("0.113.1");

    [GameAssemblyFact("0.113.1")]
    public void TheRealGameLackingAReferencedMemberIsRefused()
    {
        string folder = GameAssemblies.Folder("0.113.1")!;
        var references = GameAssemblyPreparation.EmbeddedGameReferences();
        string game = Path.Combine(_work, "real", GameProfile.GameAssembly);
        Directory.CreateDirectory(Path.GetDirectoryName(game)!);
        string removed;
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(folder);
        using (var module = ModuleDefinition.ReadModule(Path.Combine(folder, GameProfile.GameAssembly), new ReaderParameters { ReadingMode = ReadingMode.Immediate, AssemblyResolver = resolver }))
        {
            static string Type(string line) => line["method ".Length..line.IndexOf("::", StringComparison.Ordinal)];
            static string Name(string line) => line[(line.IndexOf("::", StringComparison.Ordinal) + 2)..line.IndexOf('(')];
            // The first method the list names that its own type declares, rather than inherits.
            removed = references.First(line => line.StartsWith("method ", StringComparison.Ordinal) && !Name(line).Contains('`')
                && module.GetType(Type(line))?.Methods.Any(method => method.Name == Name(line)) == true);
            // Renamed rather than removed, with any overloads, so whatever calls it inside the game still writes.
            foreach (var method in module.GetType(Type(removed)).Methods.Where(method => method.Name == Name(removed)))
                method.Name += "Elsewhere";
            module.Write(game);
        }
        var result = Preparation(game, references, "real-folder").Run();
        Assert.Equal(PreparationStatus.Refused, result.Status);
        Assert.Contains("it lacks " + removed["method ".Length..], result.Message);
    }
    /// <summary>A build's code alone, as the app fetches it before the rest: <c>sts2.dll</c> and the reporting SDK's assemblies beside it.</summary>
    private string CodeOnly(string version)
    {
        string folder = GameAssemblies.Folder(version)!, original = Path.Combine(_work, "code-" + version, STS2Mobile.GamePatcher.GameFolder.Original);
        Directory.CreateDirectory(original);
        foreach (string name in GameProfile.ReportingAssemblies.Append(GameProfile.GameAssembly))
            if (File.Exists(Path.Combine(folder, name))) File.Copy(Path.Combine(folder, name), Path.Combine(original, name));
        return original;
    }

    [GameAssemblyFact("0.113.1")]
    public void AnUntestedBuildsCodeAloneIsCheckedAndNothingIsLeft()
    {
        string original = CodeOnly("0.113.1");
        var before = Directory.GetFileSystemEntries(Path.GetDirectoryName(original)!, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        var result = GameAssemblyPreparation.CheckCode(original);
        Assert.Null(result.Reason);
        Assert.Empty(result.Skipped);
        Assert.Equal(before, Directory.GetFileSystemEntries(Path.GetDirectoryName(original)!, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
    }

    [GameAssemblyFact("0.113.1")]
    public void AnUntestedBuildsCodeLackingAReferenceIsRefusedByName()
    {
        var result = GameAssemblyPreparation.CheckCode(CodeOnly("0.113.1"), GameAssemblyPreparation.EmbeddedGameReferences(GameAssemblyPreparation.MissingOnPurpose));
        Assert.Equal("it lacks STS2Mobile.TestSwitch.NotInTheGame, which the app's code uses", result.Reason);
    }
}
