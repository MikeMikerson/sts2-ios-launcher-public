using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mono.Cecil;
using STS2Mobile.Platform;

namespace STS2Mobile.GamePatcher;

public enum PreparationStatus
{
    /// <summary>The game's files are not here yet.</summary>
    NoGame,
    /// <summary>A file is there, but it is not one the tested version has, or an untested build lacks something the game needs. Nothing was changed.</summary>
    Refused,
    /// <summary>The earlier result still holds for these inputs and this app.</summary>
    Cached,
    /// <summary>The assembly was made just now.</summary>
    Prepared,
}

/// <param name="Message">For <see cref="PreparationStatus.Refused"/>, what to tell the player.</param>
/// <param name="Tested">The game was prepared against a profile.</param>
/// <param name="Patches">How each patch went, for an assembly that was made or cached.</param>
/// <param name="Notice">For an assembly made or cached without an optional built-in mod that was refused, what to tell the player.</param>
public sealed record PreparationResult(PreparationStatus Status, string? Message = null, string? InputSha256 = null, string? OutputSha256 = null, TimeSpan Elapsed = default,
    bool Tested = true, IReadOnlyList<PatchOutcome>? Patches = null, bool ModRefused = false, string? Notice = null);

/// <summary>What <see cref="GameAssemblyPreparation.CheckCode"/> found in a build's code before the rest of it was downloaded.</summary>
/// <param name="Reason">Why the build cannot run with this app, as the end of a sentence ("it lacks ..."), or null when it can.</param>
/// <param name="Skipped">The changes the game can run without that do not fit it, by name: what it plays without.</param>
/// <param name="LeftOut">The optional built-in mods preparation would leave out of this build, each with why.</param>
public sealed record CodeCheck(string? Reason, IReadOnlyList<string> Skipped, IReadOnlyList<ModLeftOut>? LeftOut = null);

/// <summary>An optional built-in mod a build's game would start without, by the name its manifest gives the player, and why it does not fit.</summary>
public sealed record ModLeftOut(string Id, string Name, string Reason);

/// <summary>
/// Verifies the game's files and makes the assembly the game runs, once per input and app. A crash at
/// any point leaves a state the next launch repeats from: finished files are renamed into place, and the record of a result is written only
/// once all of it is there. A tested build is checked against its profile and any patch that does not fit stops the build; any other build is
/// prepared in tolerant mode, where a patch the game can run without is skipped and reported, and one it cannot is a refusal.
/// </summary>
/// <param name="gameFolder">The folder the assembly hook loads from.</param>
/// <param name="profile">The tested version's pins, or null for tolerant mode.</param>
/// <param name="patcherId">Identifies the code doing the patching. A new app with other patches makes a new result.</param>
/// <param name="build">Makes the outputs from the input folder, into an empty folder, and says how each patch went.</param>
/// <param name="version">The game's version, for the player's messages; the profile's when there is one.</param>
/// <param name="gameReferences">The game members the app's code uses (<see cref="GameReferenceList"/>): a prepared assembly that lacks one
/// is refused, tested or not. Null checks nothing.</param>
public sealed class GameAssemblyPreparation(string gameFolder, GameProfile? profile, string patcherId, Func<string, string, IReadOnlyList<PatchOutcome>> build, string? version = null,
    string? modInputs = null, string? modInputRefusal = null, Func<string?>? notice = null, IReadOnlyList<string>? gameReferences = null)
{
    public const string RecordName = "assembly-preparation.json";
    private const string WorkFolder = ".preparing";

    /// <summary>A tested build, with a stand-in for the patching that reports nothing.</summary>
    public GameAssemblyPreparation(string gameFolder, GameProfile profile, string patcherId, Action<string, string> build)
        : this(gameFolder, profile, patcherId, (input, output) => { build(input, output); return []; })
    {
    }

    private string Version => profile?.Version ?? version ?? "unknown";

    /// <summary>Which list the result was checked against, so a cached result needs no check and another list makes a new result.</summary>
    private string? References => gameReferences == null ? null : Sha256File.OfText(string.Join("\n", gameReferences));

    /// <summary>A line no game has, for <c>STS2_MISSING_GAME_REFERENCE=1</c>: the refusal of a build that lacks a member, on purpose.</summary>
    public const string MissingOnPurpose = "type STS2Mobile.TestSwitch.NotInTheGame";

    /// <summary>The list the app was built with, plus <paramref name="extra"/>.</summary>
    public static IReadOnlyList<string> EmbeddedGameReferences(params string[] extra) =>
        [.. GameReferenceList.Parse(EmbeddedData.ReadText(GameReferenceList.ResourceName)), .. extra];

    /// <summary>The app's preparation: the running patcher, the catalog it was built with, and the profile for the build in the folder, if any.</summary>
    /// <param name="missingOnPurpose">Checks for <see cref="MissingOnPurpose"/> too, so the build is refused (a test switch).</param>
    public static GameAssemblyPreparation ForApp(string gameFolder, bool includeImportedMods = true, bool missingOnPurpose = false) =>
        ForApp(gameFolder, includeImportedMods, BuiltInModInput.InOriginalFolder, BuiltInModInput.OptionalIds(),
            EmbeddedGameReferences(missingOnPurpose ? new[] { MissingOnPurpose } : []));

    /// <param name="builtIn">The built-in mods' inputs, from the original folder.</param>
    /// <param name="optional">Built-in mods the game starts without when they cannot be prepared.</param>
    /// <param name="gameReferences">What the prepared assembly must have; the embedded list when null.</param>
    public static GameAssemblyPreparation ForApp(string gameFolder, bool includeImportedMods, Func<string, IReadOnlyList<BuiltInModInput>> builtIn, IReadOnlySet<string> optional,
        IReadOnlyList<string>? gameReferences = null)
    {
        string patcher = typeof(GameAssemblyPreparation).Module.ModuleVersionId.ToString("N");
        var installed = InstalledBuild.Read(gameFolder);
        var profile = GameProfile.ForFolder(gameFolder, installed);
        IReadOnlyList<BuiltInModInput> mods = [];
        string? refusal = null, notice = null;
        if (includeImportedMods)
        {
            IReadOnlyList<BuiltInModInput> builtInMods = [];
            try { builtInMods = builtIn(Path.Combine(gameFolder, GameFolder.Original)); }
            catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException
                or InvalidOperationException or KeyNotFoundException)
            {
                // The app writes these files itself, so this is a damaged folder or a full disk: the game starts without them.
                notice = BuiltInNotice(optional.Count > 0 ? optional.Order(StringComparer.Ordinal).ToArray() : ["The built-in mods"], error.Message);
            }
            try
            {
                mods = builtInMods.Concat(BuiltInModInput.InImportedFolder(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gameFolder))!, ModPackageImport.FolderName)))
                    .OrderBy(mod => mod.Id, StringComparer.Ordinal).ToArray();
                if (mods.Select(mod => mod.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != mods.Count)
                    throw new InvalidDataException("An imported mod has the same id as another mod or a built-in mod.");
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or FileNotFoundException or DirectoryNotFoundException
                or InvalidOperationException or KeyNotFoundException)
            {
                refusal = "The imported mods cannot be prepared: " + error.Message + " Play with Mods off, or replace or remove the affected mod.";
            }
        }
        string fingerprint = Sha256File.OfText(JsonSerializer.Serialize(new
        {
            policy = ModPackPreparation.PolicyVersion,
            enabled = includeImportedMods,
            mods = mods.Select(mod => new { mod.Id, mod.DllSha256, mod.ManifestSha256, mod.PckSha256 }),
        }));
        return new GameAssemblyPreparation(gameFolder, profile, patcher, (input, output) =>
        {
            var (patches, leftOut, why) = BuildWithMods(input, output, mods, optional, tolerant: profile == null, modsOn: includeImportedMods);
            if (leftOut.Count > 0) notice = BuiltInNotice(leftOut.Select(mod => mod.Id), why!);
            return patches;
        }, installed?.Version, fingerprint, refusal, () => notice, gameReferences ?? EmbeddedGameReferences());
    }

    /// <summary>
    /// Builds the game with <paramref name="mods"/> woven in. When they do not all fit and some are optional, it builds again without the optional ones, which come back as left out with why: the game starts without them. If the
    /// other mods are refused too, that refusal is theirs (a <see cref="WeaveException"/>). Preparation and the code check both build this way.
    /// </summary>
    private static (IReadOnlyList<PatchOutcome> Patches, IReadOnlyList<BuiltInModInput> LeftOut, string? Why) BuildWithMods(string input, string output,
        IReadOnlyList<BuiltInModInput> mods, IReadOnlySet<string> optional, bool tolerant, bool modsOn)
    {
        IReadOnlyList<PatchOutcome> Build(IReadOnlyList<BuiltInModInput> woven)
        {
            try
            {
                var result = GameAssemblyBuilder.Build(input, woven, output, tolerant, modsOn);
                foreach (var mod in woven) mod.VerifyUnchanged();
                return result.Patches;
            }
            catch (Exception error) when (woven.Count > 0 && error is BadImageFormatException or AssemblyResolutionException
                or InvalidDataException or FileNotFoundException or DirectoryNotFoundException)
            {
                throw new WeaveException("An imported mod could not be read: " + error.Message);
            }
        }
        try { return (Build(mods), [], null); }
        catch (WeaveException error) when (mods.Any(mod => optional.Contains(mod.Id)))
        {
            foreach (string file in Directory.GetFiles(output)) File.Delete(file);
            var patches = Build(mods.Where(mod => !optional.Contains(mod.Id)).ToArray());
            return (patches, mods.Where(mod => optional.Contains(mod.Id)).ToArray(), error.Message);
        }
    }

    /// <summary>
    /// Checks an untested build's code alone, before the rest of it is downloaded: the patch list in tolerant mode, the built-in mods
    /// woven in as preparation weaves them, and the game references on the result, as <see cref="Run"/> does. Nothing is kept: the
    /// outputs and the mods' files go to a folder beside <paramref name="originalFolder"/> that is deleted again.
    /// </summary>
    /// <param name="originalFolder">Holds the build's <c>sts2.dll</c> and the reporting SDK's assemblies it has, as <see cref="GameFolder.Original"/> does.</param>
    /// <param name="gameReferences">What the prepared assembly must have; the embedded list when null.</param>
    /// <param name="builtIn">The built-in mods' inputs, written into the folder it is given (<see cref="BuiltInModInput.InOriginalFolder(string)"/>); none when null.</param>
    /// <param name="optional">Built-in mods the game starts without when they do not fit: those are <see cref="CodeCheck.LeftOut"/>, and any other is a refusal.</param>
    public static CodeCheck CheckCode(string originalFolder, IReadOnlyList<string>? gameReferences = null, Func<string, IReadOnlyList<BuiltInModInput>>? builtIn = null,
        IReadOnlySet<string>? optional = null)
    {
        string work = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(originalFolder))!, WorkFolder), output = Path.Combine(work, "output");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(output);
        try
        {
            var mods = builtIn?.Invoke(Path.Combine(work, "mods-source")) ?? [];
            IReadOnlyList<PatchOutcome> patches;
            IReadOnlyList<BuiltInModInput> leftOut;
            string? why;
            try { (patches, leftOut, why) = BuildWithMods(originalFolder, output, mods, optional ?? new HashSet<string>(), tolerant: true, modsOn: false); }
            catch (Exception error) when (error is GamePatchRefusedException or InvalidOperationException or BadImageFormatException or AssemblyResolutionException)
            {
                return new CodeCheck(RefusalReason(error), []);
            }
            catch (WeaveException error)
            {
                return new CodeCheck($"the built-in mods could not be prepared for it ({error.Message})", []);
            }
            using var module = ModuleDefinition.ReadModule(Path.Combine(output, GameProfile.GameAssembly));
            var missing = GameReferenceList.Missing(module, gameReferences ?? EmbeddedGameReferences());
            return new CodeCheck(missing.Count > 0 ? MissingReason(missing) : null, patches.Where(patch => !patch.Applied).Select(patch => patch.Name).ToArray(),
                leftOut.Select(mod => new ModLeftOut(mod.Id, DisplayName(mod), Reason(mod, why!))).ToArray());
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    /// <summary>Why a mod was left out, as the end of a sentence: the weaver's message without the mod's own id in front or its full stop.</summary>
    private static string Reason(BuiltInModInput mod, string why) =>
        (why.StartsWith(mod.Id + ": ", StringComparison.Ordinal) ? why[(mod.Id.Length + 2)..] : why).TrimEnd('.');

    /// <summary>The name a mod's manifest gives the player, or its id.</summary>
    private static string DisplayName(BuiltInModInput mod)
    {
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(mod.ManifestPath));
            return manifest.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } text ? text : mod.Id;
        }
        catch (Exception error) when (error is JsonException or IOException) { return mod.Id; }
    }

    /// <summary>What a refused player does next: Download the game asks Steam again and offers only a version whose code passed this check.</summary>
    public const string RefusalNext = "Press Download the game to get a version this app can run, or wait for an app update that supports this build.";

    /// <summary>Why a build's code cannot be patched, as the end of a sentence.</summary>
    public static string RefusalReason(Exception error) => error is GamePatchRefusedException refused
        ? $"the change \"{refused.Patch}\" does not fit it ({refused.Reason}), and the game cannot run without that change"
        : $"its code could not be patched ({error.GetType().Name}: {error.Message})";

    /// <summary>Which game members the app's code uses that a build lacks, as the end of a sentence.</summary>
    public static string MissingReason(IReadOnlyList<string> missing)
    {
        // "method X::Y(...) Z" names a member as "X::Y(...) Z".
        static string Member(string line) => line[(line.IndexOf(' ') + 1)..];
        return $"it lacks {Member(missing[0])}{(missing.Count > 1 ? $" and {missing.Count - 1} more" : "")}, which the app's code uses";
    }

    private static string BuiltInNotice(IEnumerable<string> ids, string why) =>
        $"{string.Join(" and ", ids)} could not be prepared for this game, so the game starts without it: {why}";

    public PreparationResult Run()
    {
        string original = Path.Combine(gameFolder, GameFolder.Original);
        if (!File.Exists(Path.Combine(original, GameProfile.GameAssembly))) return new PreparationResult(PreparationStatus.NoGame);
        bool tested = profile != null;
        if (modInputRefusal != null) return new PreparationResult(PreparationStatus.Refused, modInputRefusal, Tested: tested, ModRefused: true);
        string? refusal = tested ? Verify(original, profile!) : VerifyPresent();
        if (refusal != null) return new PreparationResult(PreparationStatus.Refused, refusal, Tested: tested);

        string input = tested ? profile!.Inputs[GameProfile.GameAssembly] : Sha256File.Of(Path.Combine(original, GameProfile.GameAssembly));
        string recordPath = Path.Combine(gameFolder, RecordName);
        var record = ReadRecord(recordPath);
        if (record != null && record.Input == input && record.Patcher == patcherId && record.Tested == tested && record.ModInputs == modInputs
            && record.References == References
            && record.Files.All(file => File.Exists(Path.Combine(gameFolder, file.Key)) && Sha256File.Of(Path.Combine(gameFolder, file.Key)) == file.Value))
            return new PreparationResult(PreparationStatus.Cached, null, input, record.Files[GameProfile.GameAssembly], Tested: tested, Patches: record.Patches, Notice: record.Notice);

        string work = Path.Combine(gameFolder, WorkFolder);
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        var clock = Stopwatch.StartNew();
        IReadOnlyList<PatchOutcome> patches;
        try { patches = build(original, work); }
        catch (WeaveException error)
        {
            Directory.Delete(work, recursive: true);
            return new PreparationResult(PreparationStatus.Refused,
                "The mods cannot be prepared: " + error.Message + " Play with Mods off, or replace or remove the affected mod.", input, Tested: tested, ModRefused: true);
        }
        catch (Exception error) when (!tested && error is GamePatchRefusedException or InvalidOperationException or BadImageFormatException or AssemblyResolutionException)
        {
            // What the patches cannot work with in an untested build is the build's, not a fault to try again: say so, and change nothing.
            Directory.Delete(work, recursive: true);
            return new PreparationResult(PreparationStatus.Refused, Refusal(error), input, Tested: false);
        }
        if (gameReferences != null && MissingReferences(Path.Combine(work, GameProfile.GameAssembly)) is { Count: > 0 } missing)
        {
            Directory.Delete(work, recursive: true);
            return new PreparationResult(PreparationStatus.Refused, MissingRefusal(missing), input, Tested: tested);
        }
        clock.Stop();

        var files = Directory.GetFiles(work).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)
            .ToDictionary(name => name, name => Sha256File.Of(Path.Combine(work, name)), StringComparer.Ordinal);
        // Mono can resolve a DLL by name even after its mod leaves the catalog.
        if (record != null)
        {
            foreach (string name in record.Files.Keys.Except(files.Keys, StringComparer.OrdinalIgnoreCase))
            {
                string id = Path.GetFileNameWithoutExtension(name);
                if (Path.GetFileName(name) == name && Path.GetExtension(name) is ".dll" or ".pck"
                    && ModFiles.IsValidId(id) && !ModPackageImport.IsReservedId(id)
                    && !GameLayout.UnchangedAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase))
                    File.Delete(Path.Combine(gameFolder, name));
            }
        }
        // No record while the files change, so a crash here is seen as no result.
        File.Delete(recordPath);
        foreach (string name in files.Keys) File.Move(Path.Combine(work, name), Path.Combine(gameFolder, name), overwrite: true);
        string? builtInNotice = notice?.Invoke();
        WriteRecord(recordPath, input, tested, patches, files, clock.Elapsed, builtInNotice);
        Directory.Delete(work, recursive: true);
        return new PreparationResult(PreparationStatus.Prepared, null, input, files[GameProfile.GameAssembly], clock.Elapsed, tested, patches, Notice: builtInNotice);
    }

    private IReadOnlyList<string> MissingReferences(string assembly)
    {
        // Names only: nothing here is resolved, so the game's other assemblies are not read.
        using var module = ModuleDefinition.ReadModule(assembly);
        return GameReferenceList.Missing(module, gameReferences!);
    }

    private string MissingRefusal(IReadOnlyList<string> missing) =>
        $"Game version {Version} cannot run with this app: {MissingReason(missing)}. Nothing was changed.\n{RefusalNext}";

    private string? Verify(string original, GameProfile tested)
    {
        foreach (var (folder, pins) in new[] { (original, tested.Inputs), (gameFolder, tested.Unchanged) })
            foreach (var (name, sha256) in pins)
            {
                string path = Path.Combine(folder, name);
                if (!File.Exists(path)) return $"The game files are incomplete: {name} is missing.";
                if (Sha256File.Of(path) != sha256) return $"{name} is not the file of game version {tested.Version}, the version this app has tested.";
            }
        return null;
    }

    /// <summary>An untested build has no pins: Steam's hashes checked its files as they arrived, so here they only have to be there.</summary>
    private string? VerifyPresent() =>
        GameLayout.UnchangedAssemblies.FirstOrDefault(name => !File.Exists(Path.Combine(gameFolder, name))) is { } missing
            ? $"The game files are incomplete: {missing} is missing."
            : null;

    private string Refusal(Exception error) =>
        $"Game version {Version} has not been tested with this app, and it cannot be prepared for this iPhone: {RefusalReason(error)}. Nothing was changed.\n{RefusalNext}";

    private sealed record Record(string? Input, string? Patcher, bool Tested, IReadOnlyList<PatchOutcome>? Patches, Dictionary<string, string> Files, string? ModInputs, string? Notice,
        string? References);

    private void WriteRecord(string path, string input, bool tested, IReadOnlyList<PatchOutcome> patches, Dictionary<string, string> files, TimeSpan elapsed, string? builtInNotice)
    {
        var outputs = new JsonObject();
        foreach (var (name, sha256) in files) outputs[name] = sha256;
        var record = new JsonObject
        {
            ["version"] = Version,
            ["input"] = input,
            ["patcher"] = patcherId,
            ["tested"] = tested,
            ["modInputs"] = modInputs,
            ["gameReferences"] = References,
            ["files"] = outputs,
            ["milliseconds"] = (long)elapsed.TotalMilliseconds,
        };
        if (builtInNotice != null) record["notice"] = builtInNotice;
        if (patches.Count > 0)
        {
            var list = new JsonArray();
            foreach (var patch in patches)
                list.Add(new JsonObject { ["name"] = patch.Name, ["required"] = patch.Required, ["applied"] = patch.Applied, ["note"] = patch.Note });
            record["patches"] = list;
        }
        string partial = path + ".partial";
        File.WriteAllText(partial, record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(partial, path, overwrite: true);
    }

    private static Record? ReadRecord(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in root.GetProperty("files").EnumerateObject()) files[file.Name] = file.Value.GetString() ?? "";
            // A record from before untested builds were prepared is of a tested one.
            bool tested = !root.TryGetProperty("tested", out var flag) || flag.GetBoolean();
            List<PatchOutcome>? patches = root.TryGetProperty("patches", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(patch => new PatchOutcome(patch.GetProperty("name").GetString()!, patch.GetProperty("required").GetBoolean(),
                    patch.GetProperty("applied").GetBoolean(), Sha256File.ReadString(patch, "note"))).ToList()
                : null;
            return files.ContainsKey(GameProfile.GameAssembly) ? new Record(Sha256File.ReadString(root, "input"), Sha256File.ReadString(root, "patcher"), tested, patches, files,
                Sha256File.ReadString(root, "modInputs"), Sha256File.ReadString(root, "notice"), Sha256File.ReadString(root, "gameReferences")) : null;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
