using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace STS2Mobile.Platform;

/// <summary>Stores mod inputs separately from loader files. Code is inspected and woven on the next launch, never loaded here.</summary>
public static class ModPackageImport
{
    public const string FolderName = "mod-inputs";

    /// <summary>What the player is told before a mod with code is added: it runs with the app's access, as on a desktop.</summary>
    public static string CodeWarning(string id) =>
        $"{id} contains code. A code mod runs inside the app with the same access the app has, including your Steam sign-in, "
        + "as code mods do on a desktop. Only add it if you trust where it came from.";

    /// <summary>Checks and stores a mod's zip. A mod with code needs <paramref name="codeConfirmed"/>, and one that replaces
    /// an installed mod needs <paramref name="replaceConfirmed"/>; each missing answer returns its question instead.</summary>
    public static ModImportResult Install(string archivePath, string inputsDirectory, bool replaceConfirmed, bool codeConfirmed)
    {
        string? incoming = null;
        try
        {
            Recover(inputsDirectory);
            using var zip = ZipFile.OpenRead(archivePath);
            if (zip.Entries.Count > ModImport.MaxArchiveEntries) throw new InvalidDataException("The zip has too many files for one mod.");
            long total = 0;
            var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                string path = entry.FullName.Replace('\\', '/');
                if (path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(p => p is ".." or ".") || path.Contains('\0'))
                    throw new InvalidDataException("The zip contains a path outside its own folder.");
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("The zip contains a link.");
                if (entry.Length > ModImport.MaxUncompressedBytes - total) throw new InvalidDataException("The mod is larger than 1 GB.");
                total += entry.Length;
                if (path.EndsWith('/') || path.StartsWith("__MACOSX/", StringComparison.Ordinal) || Path.GetFileName(path) is ".DS_Store" or "Thumbs.db" or "desktop.ini") continue;
                if (!files.TryAdd(path, entry)) throw new InvalidDataException("The zip contains duplicate file names.");
            }
            var manifests = files.Where(f => f.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (manifests.Length != 1) throw new InvalidDataException("A mod needs exactly one manifest (.json).");
            var manifestEntry = manifests[0];
            if (manifestEntry.Value.Length > 65536) throw new InvalidDataException("The manifest is too large.");
            string manifest;
            using (var reader = new StreamReader(manifestEntry.Value.Open(), Encoding.UTF8)) manifest = reader.ReadToEnd();
            var (id, hasDll, hasPack) = ReadManifest(manifest);
            if (IsReservedId(id)) throw new InvalidDataException("This mod id conflicts with an app or framework assembly.");
            if (Path.GetFileName(manifestEntry.Key) != id + ".json") throw new InvalidDataException($"The manifest must be named {id}.json.");
            string parent = Path.GetDirectoryName(manifestEntry.Key) ?? "";
            var content = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var (path, entry) in files)
            {
                string name = Path.GetFileName(path);
                if (Path.GetExtension(name).ToLowerInvariant() is ".md" or ".txt" or ".pdb") continue;
                if ((Path.GetDirectoryName(path) ?? "") != parent || name != id + ".json" && !(hasDll && name == id + ".dll") && !(hasPack && name == id + ".pck"))
                    throw new InvalidDataException($"Unexpected file {path}: only the declared manifest, DLL and pack are supported.");
                content.Add(name, entry);
            }
            if (hasDll && !content.ContainsKey(id + ".dll")) throw new InvalidDataException($"The manifest declares code, but {id}.dll is missing.");
            if (hasPack && !content.ContainsKey(id + ".pck")) throw new InvalidDataException($"The manifest declares a pack, but {id}.pck is missing.");
            Directory.CreateDirectory(inputsDirectory);
            string target = Path.Combine(inputsDirectory, id);
            // Case aliases must not replace another id on the phone's case-insensitive filesystem.
            if (Directory.EnumerateDirectories(inputsDirectory).Any(d => Path.GetFileName(d).Equals(id, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(d) != id))
                throw new InvalidDataException("A mod with the same name but different capitalization is already installed.");
            if (hasDll && !codeConfirmed)
                return new(ModImportStatus.NeedsCodeConfirmation, id, CodeWarning(id));
            if (Directory.Exists(target) && !replaceConfirmed)
                return new(ModImportStatus.NeedsReplaceConfirmation, id, $"A mod named {id} is already installed. Replace it?");
            incoming = Path.Combine(inputsDirectory, ".incoming-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(incoming);
            foreach (var (name, entry) in content) entry.ExtractToFile(Path.Combine(incoming, name));
            IReadOnlyList<string> omitted = hasPack
                ? ModPackPreparation.Prepare(Path.Combine(incoming, id + ".pck"), Path.Combine(incoming, "prepared.pck")) : [];
            // Move the old folder aside so a failed publication can restore it.
            string backup = Path.Combine(inputsDirectory, ".previous-" + id);
            bool replacing = Directory.Exists(target);
            if (replacing) Directory.Move(target, backup);
            try { Directory.Move(incoming, target); }
            catch { if (replacing) Directory.Move(backup, target); throw; }
            if (replacing) Directory.Delete(backup, recursive: true);
            return new(ModImportStatus.Installed, id, $"{id} was added. Reopen the app to prepare it."
                + (omitted.Count > 0 ? $" Removed {omitted.Count} known export-only files from its prepared pack." : ""));
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
        {
            return new(ModImportStatus.Refused, null, error.Message);
        }
        finally { if (incoming != null && Directory.Exists(incoming)) Directory.Delete(incoming, recursive: true); }
    }

    public static (string Id, bool HasDll, bool HasPack) ReadManifest(string manifest)
    {
        using var document = JsonDocument.Parse(manifest);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String || !ModFiles.IsValidId(idValue.GetString()!))
            throw new InvalidDataException("The manifest has no usable id.");
        bool Flag(string name)
        {
            if (!root.TryGetProperty(name, out var value)) return false;
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException($"The manifest's {name} must be true or false.");
            return value.GetBoolean();
        }
        bool dll = Flag("has_dll"), pack = Flag("has_pck");
        if (!dll && !pack) throw new InvalidDataException("The mod declares neither code nor a pack.");
        return (idValue.GetString()!, dll, pack);
    }

    /// <summary>Finishes or rolls back a folder replacement interrupted between its two renames.</summary>
    public static void Recover(string inputsDirectory)
    {
        if (!Directory.Exists(inputsDirectory)) return;
        foreach (string previous in Directory.EnumerateDirectories(inputsDirectory, ".previous-*"))
        {
            string id = Path.GetFileName(previous)[".previous-".Length..];
            if (!ModFiles.IsValidId(id)) continue;
            string target = Path.Combine(inputsDirectory, id);
            if (Directory.Exists(target)) Directory.Delete(previous, recursive: true);
            else Directory.Move(previous, target);
        }
    }

    public static bool IsReservedId(string id) =>
        id.Equals("sts2", StringComparison.OrdinalIgnoreCase) || id.Equals("sts2.unmodded", StringComparison.OrdinalIgnoreCase)
        || id.Equals("GodotSharp", StringComparison.OrdinalIgnoreCase) || id.Equals("GodotSharpEditor", StringComparison.OrdinalIgnoreCase)
        || id.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) || id.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
        || id.Equals("0Harmony", StringComparison.OrdinalIgnoreCase) || id.Equals("Mono.Cecil", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("System", StringComparison.OrdinalIgnoreCase) || id.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("STS2Mobile.", StringComparison.OrdinalIgnoreCase);

    public static void Delete(string inputsDirectory, string id)
    {
        Recover(inputsDirectory);
        if (!ModFiles.IsValidId(id)) throw new InvalidDataException("Invalid mod id.");
        string path = Path.Combine(inputsDirectory, id);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    /// <summary>Publishes pack-only inputs for the game's loader; code mods are published from the woven catalog.</summary>
    public static void PublishResources(string inputsDirectory, string modsDirectory, IReadOnlyCollection<string>? codeIds = null)
    {
        Recover(inputsDirectory);
        Directory.CreateDirectory(modsDirectory);
        var current = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(inputsDirectory))
            foreach (string folder in Directory.EnumerateDirectories(inputsDirectory).Order(StringComparer.Ordinal))
            {
                string id = Path.GetFileName(folder);
                if (!ModFiles.IsValidId(id)) continue;
                string manifest = File.ReadAllText(Path.Combine(folder, id + ".json"));
                var info = ReadManifest(manifest);
                if (info.Id != id) throw new InvalidDataException("Imported mod folder and manifest differ.");
                if (info.HasDll) continue;
                if (codeIds?.Contains(id, StringComparer.OrdinalIgnoreCase) == true) throw new InvalidDataException($"{id}: a resource mod conflicts with a prepared code mod.");
                string pack = Path.Combine(folder, "prepared.pck");
                using (var stream = File.OpenRead(pack)) ModImport.CheckPack(stream);
                string target = Path.Combine(modsDirectory, id);
                Directory.CreateDirectory(target);
                File.Copy(pack, Path.Combine(target, id + ".pck"), overwrite: true);
                File.WriteAllText(Path.Combine(target, id + ".json"), manifest);
                current.Add(id);
            }
        string record = Path.Combine(modsDirectory, ".imported-resources");
        if (File.Exists(record))
            foreach (string id in File.ReadAllLines(record).Where(ModFiles.IsValidId).Where(id => !current.Contains(id) && codeIds?.Contains(id, StringComparer.OrdinalIgnoreCase) != true))
                // Leave a manifest that has become a prepared code mod.
                File.Delete(Path.Combine(modsDirectory, id, id + ".json"));
        File.WriteAllLines(record, current.Order(StringComparer.Ordinal));
    }
}
