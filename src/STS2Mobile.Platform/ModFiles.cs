using System.Text.Json;

namespace STS2Mobile.Platform;

/// <summary>Rules for mod ids and manifests shared by built-in mods and imported .pck mods.</summary>
public static class ModFiles
{
    // The game loads <id>.dll and <id>.pck next to the manifest, so an id is also a file name.
    public static bool IsValidId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') && id[0] != '.' && !id.Contains("..");

    /// <summary>The <c>id</c> of a manifest, or null when the text is not a manifest the game would accept.</summary>
    public static string? ManifestId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
