using System.Text.Json;
using System.Text.Json.Nodes;
using Mono.Cecil;
using Mono.Cecil.Cil;
using STS2Mobile.Platform;

namespace STS2Mobile.GamePatcher;

/// <summary>One change made to the game, for the Mac tool's report. Either a method or a type.</summary>
public sealed record PatchChange(string? Method, string? Type, string Reason);

/// <summary>A woven mod in the output.</summary>
public sealed record BuiltMod(string Id, string SourceSha256, string WovenSha256, WeaveReport Report);

/// <summary>What <see cref="GameAssemblyBuilder.Build"/> did.</summary>
/// <param name="Patches">Each patch and whether it went in.</param>
public sealed record BuiltGame(IReadOnlyList<PatchChange> Changes, int RetainedTypes, int ScriptTypes, IReadOnlyList<BuiltMod> Mods, IReadOnlyList<PatchOutcome> Patches);

/// <summary>
/// Makes the assembly the phone runs from the game's own: the phone patches, then any code mods woven in. The same code runs in
/// the app and in tools/FullGamePatcher, and gives the same bytes on both for the same inputs.
/// </summary>
public static class GameAssemblyBuilder
{
    private const string Platform = "adapters/STS2Mobile.Platform.dll";
    private const string GameAdapters = "adapters/STS2Mobile.GameAdapters.dll";

    /// <param name="inputFolder">Holds the game's <c>sts2.dll</c>, and the reporting assemblies the patches read (<see cref="GameProfile.ReportingAssemblies"/>).</param>
    /// <param name="outputFolder">An empty folder. Gets <c>sts2.dll</c>, <c>builtin-mods.json</c> and one <c>&lt;id&gt;.dll</c> per mod.</param>
    /// <param name="tolerant">
    /// For a build with no profile: each patch that does not fit is skipped and reported, unless the game cannot run without it, which
    /// throws <see cref="GamePatchRefusedException"/>. Without it, any patch that does not fit throws, as for a tested build.
    /// </param>
    /// <param name="modsOn">
    /// The launcher's mods switch is on, so <c>sts2.dll</c> also takes the launcher's switch as the game's mods agreement
    /// (<see cref="ModLoaderPatches.AgreeToMods"/>). <c>sts2.unmodded.dll</c> never does.
    /// </param>
    public static BuiltGame Build(string inputFolder, IReadOnlyList<BuiltInModInput> mods, string outputFolder, bool tolerant = false, bool modsOn = false)
    {
        using var resolver = new PreparationAssemblyResolver(inputFolder);
        using var platform = ReadEmbedded(Platform);
        using var patched = new MemoryStream();
        var (changes, retainedTypes, scriptTypes, outcomes) = Patch(inputFolder, resolver, platform, patched, tolerant);

        // Not optional. SentryRemoval renames reporting types to System.Int32 and System.Guid, and Cecil writes those references as
        // they stand: `valuetype System.Int32` in a signature, which the interpreter refuses when it compiles the method ("Store
        // local stack type mismatch", an abort). Reading the file back makes them the real primitive, and the next write says int32.
        patched.Position = 0;
        using var game = AssemblyDefinition.ReadAssembly(patched, new ReaderParameters { AssemblyResolver = resolver });
        resolver.Register(game);
        game.Write(Path.Combine(outputFolder, "sts2.unmodded.dll"));
        var woven = BuiltInModStage.Weave(game, platform, mods, resolver);
        if (modsOn) changes.AddRange(ModLoaderPatches.AgreeToMods(game, platform, tolerant));
        string destination = Path.Combine(outputFolder, GameProfile.GameAssembly);
        game.Write(destination);
        using (var check = ModuleDefinition.ReadModule(destination))
            if (check.GetTypeReferences().Any(r => r.Scope.Name.StartsWith("Sentry") || r.Scope.Name == "0Harmony" || r.Namespace.StartsWith("HarmonyLib") || r.Namespace == "Sentry" || r.Namespace.StartsWith("Sentry.")))
                throw new InvalidOperationException("Removed SDK metadata remains.");
        return new BuiltGame(changes, retainedTypes, scriptTypes, BuiltInModStage.WriteOutputs(woven, outputFolder), outcomes);
    }

    /// <summary>Every patch, in the order they are applied. The order is part of the output: imports get their tokens as they are made.</summary>
    private static IEnumerable<PatchStep> Steps(ModuleDefinition gameAdapters) =>
        StartupPatches.Steps.Concat(ModLoaderPatches.Steps).Concat(UploadPatches.Steps).Concat(PhonePatches.Steps)
            .Concat(NetworkPatches.Steps).Concat(SentryRemoval.Steps).Concat(AdapterPatches.Steps(gameAdapters));

    /// <summary>The patches' names, in order, with whether the game cannot run without each: the short list a build has to fit.</summary>
    public static IReadOnlyList<(string Name, bool Required)> PatchList()
    {
        using var gameAdapters = ReadEmbedded(GameAdapters);
        return Steps(gameAdapters).Select(step => (step.Name, step.Required)).ToList();
    }

    private static (List<PatchChange> Changes, int RetainedTypes, int ScriptTypes, List<PatchOutcome> Outcomes) Patch(string inputFolder, PreparationAssemblyResolver resolver, ModuleDefinition platform, Stream output, bool tolerant)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Path.Combine(inputFolder, GameProfile.GameAssembly), new ReaderParameters { AssemblyResolver = resolver });
        resolver.Register(assembly);
        using var gameAdapters = ReadEmbedded(GameAdapters);
        var patch = new GamePatch(assembly, platform) { Tolerant = tolerant };
        var outcomes = new List<PatchOutcome>();
        foreach (var step in Steps(gameAdapters))
        {
            patch.Note = null;
            if (!tolerant)
            {
                step.Apply(patch);
                outcomes.Add(new PatchOutcome(step.Name, step.Required, Applied: true));
                continue;
            }
            try
            {
                step.Apply(patch);
                outcomes.Add(new PatchOutcome(step.Name, step.Required, Applied: true, patch.Note));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (step.Required) throw new GamePatchRefusedException(step.Name, error.Message);
                outcomes.Add(new PatchOutcome(step.Name, step.Required, Applied: false, error.Message));
            }
        }
        var module = patch.Module;
        module.Architecture = TargetArchitecture.I386;
        module.Attributes = ModuleAttributes.ILOnly;
        module.EntryPoint = null;
        assembly.Write(output);
        return (patch.Changes, CecilWalk.Flatten(module.Types).Count(),
            patch.Types.Count(t => t.CustomAttributes.Any(a => a.AttributeType.Name == "ScriptPathAttribute")), outcomes);
    }

    private static ModuleDefinition ReadEmbedded(string name)
    {
        var data = new MemoryStream();
        using (var stream = EmbeddedData.Open(name)) stream.CopyTo(data);
        data.Position = 0;
        return ModuleDefinition.ReadModule(data);
    }


}

/// <summary>The code mods step: each mod's files must match their pins, and a refused mod stops everything.</summary>
public static class BuiltInModStage
{
    public sealed record WovenMod(string Id, AssemblyDefinition Mod, string Manifest, string SourceSha256, WeaveReport Report, string? PackPath = null, string? PackSha256 = null);

    public static List<WovenMod> Weave(AssemblyDefinition game, ModuleDefinition platform, IReadOnlyList<BuiltInModInput> mods, IAssemblyResolver resolver)
    {
        var weaver = new Weaver(game, platform);
        var woven = new List<WovenMod>();
        foreach (var entry in mods)
        {
            if (!ModFiles.IsValidId(entry.Id)) throw new WeaveException("Invalid mod id: " + entry.Id);
            string? packPath = null, packSha = null;
            string dllSha = Sha256File.Of(entry.DllPath), manifestSha = Sha256File.Of(entry.ManifestPath);
            if (dllSha != entry.DllSha256) throw new WeaveException($"{entry.Id}: DLL hash {dllSha} is not the pinned {entry.DllSha256}. Check the new build before pinning it.");
            if (manifestSha != entry.ManifestSha256) throw new WeaveException($"{entry.Id}: manifest hash {manifestSha} is not the pinned {entry.ManifestSha256}.");
            string manifest = File.ReadAllText(entry.ManifestPath);
            using (var document = JsonDocument.Parse(manifest))
            {
                var root = document.RootElement;
                if (root.GetProperty("id").GetString() != entry.Id) throw new WeaveException($"{entry.Id}: the manifest names another id.");
                if (!root.TryGetProperty("has_dll", out var hasDll) || !hasDll.GetBoolean()) throw new WeaveException($"{entry.Id}: a built-in mod must declare has_dll.");
                if (root.TryGetProperty("has_pck", out var hasPck) && hasPck.GetBoolean())
                {
                    packPath = entry.PckPath;
                    if (packPath == null || !File.Exists(packPath) || entry.PckSha256 == null)
                        throw new WeaveException($"{entry.Id}: the declared .pck needs a file and pinned hash.");
                    packSha = Sha256File.Of(packPath);
                    if (packSha != entry.PckSha256) throw new WeaveException($"{entry.Id}: .pck hash does not match its pin.");
                    using var pack = File.OpenRead(packPath);
                    ModImport.CheckPack(pack);
                }
                else if (entry.PckPath != null || entry.PckSha256 != null)
                    throw new WeaveException($"{entry.Id}: a .pck was supplied but not declared in the manifest.");
            }
            var mod = AssemblyDefinition.ReadAssembly(entry.DllPath, new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate, InMemory = true });
            var (result, report) = weaver.Weave(entry.Id, mod, dllSha);
            woven.Add(new WovenMod(entry.Id, result, manifest, dllSha, report, packPath, packSha));
        }
        return woven;
    }

    /// <summary>Writes each mod copy without Harmony, and the catalog the app reads.</summary>
    public static List<BuiltMod> WriteOutputs(IReadOnlyList<WovenMod> woven, string outputFolder)
    {
        var built = new List<BuiltMod>();
        var catalog = new JsonArray();
        foreach (var (id, mod, manifest, sourceSha, report, packPath, packSha) in woven)
        {
            string dll = Path.Combine(outputFolder, id + ".dll");
            mod.Write(dll);
            mod.Dispose();
            using (var check = ModuleDefinition.ReadModule(dll))
                if (check.AssemblyReferences.Any(r => r.Name == "0Harmony") || check.GetTypeReferences().Any(r => r.Scope.Name == "0Harmony"))
                    throw new WeaveException($"{id}: Harmony metadata remains in the woven copy.");
            if (packPath != null)
            {
                string destination = Path.Combine(outputFolder, id + ".pck");
                File.Copy(packPath, destination, overwrite: true);
                if (Sha256File.Of(destination) != packSha) throw new WeaveException($"{id}: .pck changed while preparing it.");
            }
            string wovenSha = Sha256File.Of(dll);
            built.Add(new BuiltMod(id, sourceSha, wovenSha, report));
            catalog.Add(new JsonObject
            {
                ["id"] = id,
                ["manifest"] = manifest,
                ["sourceSha256"] = sourceSha,
                ["wovenSha256"] = wovenSha,
                ["patches"] = report.Patches.Count,
                ["pckSha256"] = packSha,
            });
        }
        var document = new JsonObject { ["mods"] = catalog };
        File.WriteAllText(Path.Combine(outputFolder, GameFolder.ModCatalog), document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return built;
    }
}
