using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.Loader;
using System.Text.Json;

namespace STS2Mobile.Platform;

/// <summary>
/// Prepared code mods on the phone. The catalog pins each woven assembly. The host registers
/// those interpreted assemblies before the game's loader starts; the loader controls initialization,
/// dependencies and versions. The launcher chooses the plain assembly when mods are off.
/// </summary>
public static class BuiltInMods
{
    private static readonly Dictionary<string, BuiltInMod> Catalog = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Assembly> Assemblies = new(StringComparer.Ordinal);
    private static Action<string> _log = _ => { };
    private static string? _modsDirectory;
    private static readonly string DisabledAnchor = Path.Combine(Path.GetTempPath(), "sts2-mods-disabled-" + Guid.NewGuid().ToString("N"), "app");

    public static IReadOnlyCollection<BuiltInMod> All => Catalog.Values;

    /// <summary>Reads the catalog preparation wrote beside the game's assembly. An empty or missing catalog means no code mods.</summary>
    public static IReadOnlyList<BuiltInMod> ParseCatalog(string json)
    {
        using var document = JsonDocument.Parse(json);
        var mods = new List<BuiltInMod>();
        foreach (var entry in document.RootElement.GetProperty("mods").EnumerateArray())
        {
            string id = entry.GetProperty("id").GetString() ?? "";
            if (!ModFiles.IsValidId(id)) throw new InvalidDataException("Invalid built-in mod id: " + id);
            string manifest = entry.GetProperty("manifest").GetString() ?? throw new InvalidDataException("Built-in mod without manifest: " + id);
            if (ModFiles.ManifestId(manifest) != id) throw new InvalidDataException("Built-in mod manifest id differs: " + id);
            mods.Add(new BuiltInMod(id, manifest,
                RequireSha(entry.GetProperty("sourceSha256").GetString(), id), RequireSha(entry.GetProperty("wovenSha256").GetString(), id),
                entry.GetProperty("patches").GetInt32(),
                entry.TryGetProperty("pckSha256", out var packSha) && packSha.ValueKind != JsonValueKind.Null ? RequireSha(packSha.GetString(), id) : null));
        }
        if (mods.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != mods.Count) throw new InvalidDataException("Duplicate built-in mod id.");
        return mods;
    }

    /// <summary>
    /// Makes prepared mods available to the loader. Each assembly must carry the weaver's mark
    /// for the same source hash; anything else stops the start.
    /// </summary>
    public static void Register(IEnumerable<BuiltInMod> mods, Func<string, Assembly> compiledIn, Action<string> log)
    {
        if (!ModLaunchSettings.Enabled) throw new InvalidOperationException("Mods are off for this launch.");
        if (Catalog.Count != 0) throw new InvalidOperationException("Built-in mods are already registered.");
        _log = log;
        foreach (var mod in mods)
        {
            var assembly = compiledIn(mod.Id);
            var mark = assembly.GetCustomAttribute<WovenModAttribute>()
                ?? throw new InvalidDataException($"Built-in mod {mod.Id} was not prepared by the weaver.");
            if (mark.ModId != mod.Id || mark.SourceSha256 != mod.SourceSha256 || mark.Patches != mod.Patches)
                throw new InvalidDataException($"Built-in mod {mod.Id} does not match its catalog entry.");
            Catalog.Add(mod.Id, mod);
            Assemblies.Add(mod.Id, assembly);
            log($"Built-in mod {mod.Id}: {mod.Patches} patches woven at build time, source {mod.SourceSha256[..12]}.");
        }
    }

    /// <summary>
    /// Writes each built-in mod's manifest into the game's mods folder, so the game's loader finds it
    /// as it would on desktop. Manifests of built-in mods that are no longer compiled in are removed;
    /// nothing else in the folder is touched.
    /// </summary>
    public static void WriteManifests(string modsDirectory, string? preparedFolder = null)
    {
        if (!ModLaunchSettings.Enabled) return;
        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(modsDirectory)) != "mods")
            throw new ArgumentException("The game's loader looks for a folder named mods.", nameof(modsDirectory));
        Directory.CreateDirectory(modsDirectory);
        _modsDirectory = Path.TrimEndingDirectorySeparator(modsDirectory);
        string listPath = Path.Combine(modsDirectory, ".builtin");
        var previous = File.Exists(listPath) ? File.ReadAllLines(listPath).Where(ModFiles.IsValidId).ToHashSet(StringComparer.Ordinal) : [];
        foreach (string id in previous.Where(id => !Catalog.ContainsKey(id)))
        {
            string stale = Path.Combine(modsDirectory, id, id + ".json");
            if (File.Exists(stale)) File.Delete(stale);
            string folder = Path.Combine(modsDirectory, id);
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        foreach (var mod in Catalog.Values)
        {
            string folder = Path.Combine(modsDirectory, mod.Id);
            Directory.CreateDirectory(folder);
            if (mod.PckSha256 != null)
            {
                if (preparedFolder == null) throw new InvalidDataException($"Mod {mod.Id}: prepared pack folder is missing.");
                string pack = Path.Combine(preparedFolder, mod.Id + ".pck");
                using (var input = File.OpenRead(pack))
                {
                    string hash = Convert.ToHexStringLower(SHA256.HashData(input));
                    if (hash != mod.PckSha256) throw new InvalidDataException($"Mod {mod.Id}: prepared pack hash differs.");
                    ModImport.CheckPack(input);
                }
                File.Copy(pack, Path.Combine(folder, mod.Id + ".pck"), overwrite: true);
            }
            string path = Path.Combine(folder, mod.Id + ".json");
            if (!File.Exists(path) || File.ReadAllText(path) != mod.Manifest) File.WriteAllText(path, mod.Manifest);
        }
        File.WriteAllLines(listPath, Catalog.Keys.Order(StringComparer.Ordinal));
    }

    // ---- Called from the game's mod loader (src/STS2Mobile.GamePatcher redirects these calls). ----

    /// <summary>
    /// Replaces the executable path in the loader's folder scan. The loader looks for "mods" next to
    /// the executable, which on iOS is inside the read-only, signed app bundle; this answers with a
    /// path whose folder holds the app's own mods folder.
    /// </summary>
    public static string ModLoaderAnchor() =>
        !ModLaunchSettings.Enabled ? DisabledAnchor :
        Path.Combine(Path.GetDirectoryName(_modsDirectory ?? throw new InvalidOperationException("The mods folder was not prepared before the game started."))!, "app");

    /// <summary>Replaces the loader's "is the mod's DLL on disk" check: only compiled-in code exists.</summary>
    public static bool AssemblyFileExists(object fileIo, string path)
    {
        if (!ModLaunchSettings.Enabled) return false;
        if (Catalog.ContainsKey(IdFromAssemblyPath(path))) return true;
        _log($"Mod code at {path} is not loaded: code mods must be built into the app on the Mac.");
        return false;
    }

    /// <summary>Replaces AssemblyLoadContext.GetLoadContext, which has no meaning for compiled-in code.</summary>
    public static AssemblyLoadContext LoadContext(Assembly unused) => AssemblyLoadContext.Default;

    /// <summary>Replaces LoadFromAssemblyPath: hands out the compiled-in assembly, never a file.</summary>
    public static Assembly LoadFromAssemblyPath(AssemblyLoadContext unused, string path)
    {
        if (!ModLaunchSettings.Enabled) throw new InvalidOperationException("Mods are off for this launch.");
        string id = IdFromAssemblyPath(path);
        return Assemblies.TryGetValue(id, out var assembly) ? assembly
            : throw new NotSupportedException($"Mod {id}: code mods must be built into the app on the Mac.");
    }

    /// <summary>Replaces <c>new Harmony(id)</c> in the game's loader and in woven mods.</summary>
    public static object CreatePatcher(string harmonyId) => harmonyId;

    /// <summary>Replaces <c>Harmony.PatchAll(assembly)</c>: the patches were woven at build time, so only confirm that.</summary>
    public static void PatchAll(object patcher, Assembly assembly)
    {
        if (!ModLaunchSettings.Enabled) throw new InvalidOperationException("Mods are off for this launch.");
        var mark = assembly.GetCustomAttribute<WovenModAttribute>();
        if (mark == null || !Assemblies.TryGetValue(mark.ModId, out var registered) || registered != assembly)
            throw new NotSupportedException($"{assembly.GetName().Name}: runtime Harmony patching is not available on iOS.");
        _log($"Mod {mark.ModId} ({patcher}): {mark.Patches} patches are already woven into the game.");
    }

    private static string IdFromAssemblyPath(string path) => Path.GetFileNameWithoutExtension(path);

    private static string RequireSha(string? value, string id) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : throw new InvalidDataException("Invalid hash for built-in mod " + id);
}
