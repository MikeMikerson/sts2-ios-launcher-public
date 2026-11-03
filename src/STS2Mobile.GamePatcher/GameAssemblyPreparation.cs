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
/// <param name="Notice">For an assembly made or cached without an optional built-in mod that was refused (QuickMatch), what to tell the player.</param>
public sealed record PreparationResult(PreparationStatus Status, string? Message = null, string? InputSha256 = null, string? OutputSha256 = null, TimeSpan Elapsed = default,
    bool Tested = true, IReadOnlyList<PatchOutcome>? Patches = null, bool ModRefused = false, string? Notice = null);

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
public sealed class GameAssemblyPreparation(string gameFolder, GameProfile? profile, string patcherId, Func<string, string, IReadOnlyList<PatchOutcome>> build, string? version = null,
    string? modInputs = null, string? modInputRefusal = null, Func<string?>? notice = null)
{
    public const string RecordName = "assembly-preparation.json";
    private const string WorkFolder = ".preparing";

    /// <summary>A tested build, with a stand-in for the patching that reports nothing.</summary>
    public GameAssemblyPreparation(string gameFolder, GameProfile profile, string patcherId, Action<string, string> build)
        : this(gameFolder, profile, patcherId, (input, output) => { build(input, output); return []; })
    {
    }

    private string Version => profile?.Version ?? version ?? "unknown";

    /// <summary>The app's preparation: the running patcher, the catalog it was built with, and the profile for the build in the folder, if any.</summary>
    public static GameAssemblyPreparation ForApp(string gameFolder, bool includeImportedMods = true) =>
        ForApp(gameFolder, includeImportedMods, BuiltInModInput.InOriginalFolder, BuiltInModInput.OptionalIds());

    /// <param name="builtIn">The built-in mods' inputs, from the original folder.</param>
    /// <param name="optional">Built-in mods the game starts without when they cannot be prepared (QuickMatch).</param>
    public static GameAssemblyPreparation ForApp(string gameFolder, bool includeImportedMods, Func<string, IReadOnlyList<BuiltInModInput>> builtIn, IReadOnlySet<string> optional)
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
        IReadOnlyList<PatchOutcome> Build(string input, string output, IReadOnlyList<BuiltInModInput> woven)
        {
            try
            {
                var result = GameAssemblyBuilder.Build(input, woven, output, tolerant: profile == null, modsOn: includeImportedMods);
                foreach (var mod in woven) mod.VerifyUnchanged();
                return result.Patches;
            }
            catch (Exception error) when (woven.Count > 0 && error is BadImageFormatException or AssemblyResolutionException
                or InvalidDataException or FileNotFoundException or DirectoryNotFoundException)
            {
                throw new WeaveException("An imported mod could not be read: " + error.Message);
            }
        }
        return new GameAssemblyPreparation(gameFolder, profile, patcher, (input, output) =>
        {
            try { return Build(input, output, mods); }
            catch (WeaveException error) when (mods.Any(mod => optional.Contains(mod.Id)))
            {
                // An optional mod that does not fit this game, such as QuickMatch after a game update: the game starts without it. If
                // the other mods are refused too, that refusal is theirs, as before.
                foreach (string file in Directory.GetFiles(output)) File.Delete(file);
                var without = mods.Where(mod => !optional.Contains(mod.Id)).ToArray();
                var patches = Build(input, output, without);
                notice = BuiltInNotice(mods.Where(mod => optional.Contains(mod.Id)).Select(mod => mod.Id), error.Message);
                return patches;
            }
        }, installed?.Version, fingerprint, refusal, () => notice);
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
        if (ReadRecord(recordPath) is { } record && record.Input == input && record.Patcher == patcherId && record.Tested == tested && record.ModInputs == modInputs
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
        clock.Stop();

        var files = Directory.GetFiles(work).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)
            .ToDictionary(name => name, name => Sha256File.Of(Path.Combine(work, name)), StringComparer.Ordinal);
        // No record while the files change, so a crash here is seen as no result.
        File.Delete(recordPath);
        foreach (string name in files.Keys) File.Move(Path.Combine(work, name), Path.Combine(gameFolder, name), overwrite: true);
        string? builtInNotice = notice?.Invoke();
        WriteRecord(recordPath, input, tested, patches, files, clock.Elapsed, builtInNotice);
        Directory.Delete(work, recursive: true);
        return new PreparationResult(PreparationStatus.Prepared, null, input, files[GameProfile.GameAssembly], clock.Elapsed, tested, patches, Notice: builtInNotice);
    }

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

    private string Refusal(Exception error)
    {
        string why = error is GamePatchRefusedException refused
            ? $"the change \"{refused.Patch}\" does not fit it ({refused.Reason})"
            : $"its code could not be patched ({error.GetType().Name}: {error.Message})";
        return $"Game version {Version} has not been tested with this app, and it cannot be prepared for this iPhone: {why}. The game cannot run without that change, "
            + "so nothing was changed.\nChoose the other version under Game version, or wait for an app update that supports this build.";
    }

    private sealed record Record(string? Input, string? Patcher, bool Tested, IReadOnlyList<PatchOutcome>? Patches, Dictionary<string, string> Files, string? ModInputs, string? Notice);

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
                Sha256File.ReadString(root, "modInputs"), Sha256File.ReadString(root, "notice")) : null;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
