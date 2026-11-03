using System.Text.Json;
using STS2Mobile.Platform;

namespace STS2Mobile.GamePatcher;

/// <summary>One code mod to weave into the game, with the hashes its files must have (mods/builtin.json).</summary>
public sealed record BuiltInModInput(string Id, string DllPath, string ManifestPath, string DllSha256, string ManifestSha256, string? PckPath = null, string? PckSha256 = null)
{
    /// <summary>Imported sources are separate from the manifests and woven outputs the game reads.</summary>
    public static IReadOnlyList<BuiltInModInput> InImportedFolder(string folder)
    {
        ModPackageImport.Recover(folder);
        if (!Directory.Exists(folder)) return [];
        var result = new List<BuiltInModInput>();
        foreach (string directory in Directory.GetDirectories(folder).Order(StringComparer.Ordinal))
        {
            string id = Path.GetFileName(directory);
            // Atomic imports may leave a temporary folder after an interrupted copy.
            if (id.StartsWith('.')) continue;
            if (!ModFiles.IsValidId(id)) throw new InvalidDataException("Invalid imported mod id: " + id);
            if (ModPackageImport.IsReservedId(id)
                || GameLayout.Files.Any(file => Path.GetFileName(file.GamePath).Equals(id + ".dll", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"{id}: this assembly name is reserved by the game or app.");
            string manifestPath = Path.Combine(directory, id + ".json");
            var (manifestId, hasDll, hasPack) = ModPackageImport.ReadManifest(File.ReadAllText(manifestPath));
            if (manifestId != id) throw new InvalidDataException($"{id}: the manifest names another id.");
            if (!hasDll) continue; // Resource-only imports do not change the game's assembly.
            string dll = Path.Combine(directory, id + ".dll");
            string? pack = hasPack ? Path.Combine(directory, "prepared.pck") : null;
            result.Add(new BuiltInModInput(id, dll, manifestPath, Sha256File.Of(dll), Sha256File.Of(manifestPath),
                pack, pack == null ? null : Sha256File.Of(pack)));
        }
        return result;
    }

    /// <summary>Reject a source changed after the preparation snapshot, before publishing its outputs.</summary>
    public void VerifyUnchanged()
    {
        if (Sha256File.Of(DllPath) != DllSha256 || Sha256File.Of(ManifestPath) != ManifestSha256
            || PckPath != null && Sha256File.Of(PckPath) != PckSha256)
            throw new WeaveException($"{Id}: the imported files changed during preparation. Try again.");
    }

    /// <summary>
    /// The catalog this app was built with. Its mods sit in the original files (<c>mods/&lt;id&gt;.dll</c>, <c>.json</c> and an
    /// optional <c>.pck</c>), written there from the app's own copies. An optional mod (QuickMatch) counts only when this app
    /// carries it, so a build without it never weaves a copy an earlier build left behind.
    /// </summary>
    public static IReadOnlyList<BuiltInModInput> InOriginalFolder(string original) =>
        InOriginalFolder(original, EmbeddedData.ReadText("builtin.json"), name => typeof(EmbeddedData).Assembly.GetManifestResourceStream("mods/" + name));

    /// <param name="bundled">The app's copy of a mod file by name, or null when the app was built without it.</param>
    public static IReadOnlyList<BuiltInModInput> InOriginalFolder(string original, string catalogJson, Func<string, Stream?> bundled)
    {
        string folder = Path.Combine(original, "mods");
        return Parse(catalogJson, entry =>
        {
            string id = entry.GetProperty("id").GetString()!;
            if (!ModFiles.IsValidId(id)) throw new InvalidDataException("Invalid built-in mod id: " + id);
            var files = new List<(string Name, string Sha256)>
            {
                (id + ".dll", entry.GetProperty("dllSha256").GetString()!),
                (id + ".json", entry.GetProperty("manifestSha256").GetString()!),
            };
            if (entry.TryGetProperty("pckSha256", out var pack)) files.Add((id + ".pck", pack.GetString()!));
            bool carried = false;
            foreach (var (name, sha256) in files)
            {
                using var copy = bundled(name);
                if (copy == null) continue;
                carried = true;
                string path = Path.Combine(folder, name);
                if (File.Exists(path) && Sha256File.Of(path) == sha256) continue;
                Directory.CreateDirectory(folder);
                string partial = path + ".partial";
                using (var output = File.Create(partial)) copy.CopyTo(output);
                File.Move(partial, path, overwrite: true);
            }
            return carried || !IsOptional(entry);
        }, entry => (
            Path.Combine(folder, entry.GetProperty("id").GetString() + ".dll"),
            Path.Combine(folder, entry.GetProperty("id").GetString() + ".json")),
            entry => entry.TryGetProperty("pckSha256", out _) ? Path.Combine(folder, entry.GetProperty("id").GetString() + ".pck") : null);
    }

    /// <summary>A catalog on the Mac, whose entries name their files, with <c>~/</c> for the home folder and other paths from the
    /// working folder. An optional mod whose files setup did not fetch is left out.</summary>
    public static IReadOnlyList<BuiltInModInput> AtCatalogPaths(string catalogJson) =>
        Parse(catalogJson, entry => !IsOptional(entry) || File.Exists(Expand(entry.GetProperty("dll").GetString()!)),
            entry => (Expand(entry.GetProperty("dll").GetString()!), Expand(entry.GetProperty("manifest").GetString()!)),
            entry => entry.TryGetProperty("pck", out var pack) ? Expand(pack.GetString()!) : null);

    /// <summary>The ids of the optional mods in a catalog: a refusal of one of them leaves the game starting without it.</summary>
    public static IReadOnlySet<string> OptionalIds(string catalogJson)
    {
        using var catalog = JsonDocument.Parse(catalogJson);
        return catalog.RootElement.GetProperty("mods").EnumerateArray().Where(IsOptional)
            .Select(entry => entry.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The optional mods of the catalog this app was built with.</summary>
    public static IReadOnlySet<string> OptionalIds() => OptionalIds(EmbeddedData.ReadText("builtin.json"));

    private static bool IsOptional(JsonElement entry) => entry.TryGetProperty("optional", out var optional) && optional.GetBoolean();

    private static List<BuiltInModInput> Parse(string json, Func<JsonElement, bool> include, Func<JsonElement, (string Dll, string Manifest)> paths, Func<JsonElement, string?> packPath)
    {
        using var catalog = JsonDocument.Parse(json);
        var mods = new List<BuiltInModInput>();
        foreach (var entry in catalog.RootElement.GetProperty("mods").EnumerateArray())
        {
            if (!include(entry)) continue;
            var (dll, manifest) = paths(entry);
            mods.Add(new BuiltInModInput(entry.GetProperty("id").GetString()!, dll, manifest, entry.GetProperty("dllSha256").GetString()!, entry.GetProperty("manifestSha256").GetString()!,
                packPath(entry), entry.TryGetProperty("pckSha256", out var packSha) ? packSha.GetString() : null));
        }
        return mods;
    }

    private static string Expand(string path) =>
        Path.GetFullPath(path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]) : path);
}
