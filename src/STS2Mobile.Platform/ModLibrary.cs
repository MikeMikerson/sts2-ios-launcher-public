using System.Text.Json;

namespace STS2Mobile.Platform;

/// <summary>A read-only list of imported inputs. Preparation status describes this launch, never a promise of mod compatibility.</summary>
public sealed record ModLibraryItem(string Id, string Name, string Status, bool Refused = false);

public static class ModLibrary
{
    public static IReadOnlyList<ModLibraryItem> Read(string inputsDirectory, bool enabled, bool prepared)
    {
        if (!Directory.Exists(inputsDirectory)) return [];
        return Directory.EnumerateDirectories(inputsDirectory).Order(StringComparer.Ordinal)
            .Where(folder => ModFiles.IsValidId(Path.GetFileName(folder)))
            .Select(folder => ReadItem(folder, enabled, prepared)).ToArray();
    }

    private static ModLibraryItem ReadItem(string folder, bool enabled, bool prepared)
    {
        string id = Path.GetFileName(folder);
        try
        {
            string manifest = File.ReadAllText(Path.Combine(folder, id + ".json"));
            var info = ModPackageImport.ReadManifest(manifest);
            if (info.Id != id) throw new InvalidDataException("The manifest names a different mod.");
            if (info.HasDll && !File.Exists(Path.Combine(folder, id + ".dll")))
                throw new InvalidDataException("The mod's code file is missing. Import its zip again.");
            if (info.HasPack && !File.Exists(Path.Combine(folder, "prepared.pck")))
                throw new InvalidDataException("The mod's prepared pack is missing. Import its zip again.");
            using var json = JsonDocument.Parse(manifest);
            string name = json.RootElement.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : id;
            if (string.IsNullOrWhiteSpace(name)) name = id;
            return new(id, name, !enabled ? "Off for this launch" : prepared ? "Prepared for this launch" : "Waiting for preparation");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            string reason = error is JsonException ? "The manifest could not be read. Import its zip again." : error.Message;
            return new(id, id, reason, Refused: true);
        }
    }
}
