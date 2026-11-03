using System.Text;
using System.Text.Json;

namespace STS2Mobile.GamePack;

/// <summary>
/// Makes the pack the game mounts, from the pack Steam delivers and the phone's textures, with what
/// tools/build_full_game_pack.py's build_game_pack writes. Every file goes through one at a time, so the app never holds the
/// pack in memory.
/// </summary>
public static class GamePackBuilder
{
    public const string TextureManifest = "texture-overlay.json";
    public const string TextureProfile = "v0.111.0-textures-astc4x4-v1";
    private const string Settings = "project.binary";
    private const string UidCache = ".godot/uid_cache.bin";
    private const string ClassCache = ".godot/global_script_class_cache.cfg";
    private const string ExtensionList = ".godot/extension_list.cfg";
    private const string ReportingAddon = "addons/sentry/";
    private const string Scene = ".tscn";
    /// <summary>Game entries that never ship: desktop-only payloads and registrations the host replaces.</summary>
    private static readonly string[] Skipped = ["steam/", "RiderTestRunner/", "SlayTheSpire2.app/"];

    /// <param name="gamePack">The pack as Steam delivers it. The caller has verified it.</param>
    /// <param name="texturesPack">The converted textures and their remap files (<see cref="TextureConversion"/>).</param>
    /// <param name="output">A new file. Nothing removes it if this throws.</param>
    /// <returns>The number of entries written.</returns>
    public static int Build(string gamePack, string texturesPack, string packSha256, string output)
    {
        using var game = new PackReader(gamePack);
        using var textures = new PackReader(texturesPack);
        var gameEntries = game.Entries.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        foreach (string required in new[] { Settings, UidCache, ClassCache })
            if (!gameEntries.ContainsKey(required)) throw new PackException("The game has no " + required);

        // The game's resources once the reporting SDK is gone, in the pack's own order.
        var resources = game.Entries.Where(entry => !entry.Name.StartsWith(ReportingAddon, StringComparison.Ordinal)).ToList();
        var inventory = ShaderWarmupInventory.Build(resources.Select(entry => entry.Name).ToList(), name => ReadText(game, gameEntries[name]));

        var overlay = ReadOverlay(textures, packSha256);
        var shipped = resources.Where(entry => Ships(entry.Name) && !overlay.Removed.Contains(entry.Name)).ToList();
        var names = shipped.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        if (!overlay.Removed.All(gameEntries.ContainsKey) || !overlay.Replaced.Keys.All(names.Contains) || overlay.Added.Any(entry => !names.Add(entry.Name)))
            throw new PackException("The converted textures do not fit this game's files.");
        foreach (var (name, _) in inventory)
            if (!names.Add(name)) throw new PackException("An added file shadows a game file: " + name);

        var adjusted = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in shipped.Where(entry => entry.Name.EndsWith(Scene, StringComparison.Ordinal)))
            if (ParticleLimits.AdjustScene(game.Read(entry)) is { } scene) adjusted[entry.Name] = scene;

        var buffer = new byte[PackFormat.CopyBufferBytes];
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, PackFormat.CopyBufferBytes);
        var writer = new PackWriter(stream);
        writer.Copy(game, gameEntries[Settings], buffer);
        foreach (var entry in shipped)
        {
            if (adjusted.TryGetValue(entry.Name, out var scene)) writer.Add(entry.Name, scene);
            else if (overlay.Replaced.TryGetValue(entry.Name, out var remap)) writer.Copy(textures, remap, buffer);
            else writer.Copy(game, entry, buffer);
        }
        foreach (var entry in overlay.Added) writer.Copy(textures, entry, buffer);
        foreach (var (name, data) in inventory) writer.Add(name, data);
        writer.Finish();
        stream.Flush(flushToDisk: true);
        return writer.Count;
    }

    private static bool Ships(string name) =>
        name != Settings && name != ExtensionList && !name.EndsWith(".gdextension", StringComparison.Ordinal)
        && !Skipped.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>As Python's <c>read_text(errors="replace")</c> reads it: universal newlines, and bad bytes replaced.</summary>
    private static string ReadText(PackReader pack, PackEntry entry) =>
        Encoding.UTF8.GetString(pack.Read(entry)).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private sealed record Overlay(HashSet<string> Removed, Dictionary<string, PackEntry> Replaced, List<PackEntry> Added);

    /// <summary>Which game files the converted textures take the place of, and the entries that do it.</summary>
    private static Overlay ReadOverlay(PackReader textures, string packSha256)
    {
        var entries = textures.Entries.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        if (!entries.TryGetValue(TextureManifest, out var manifest)) throw new PackException("The texture pack has no " + TextureManifest);
        using var document = JsonDocument.Parse(textures.Read(manifest));
        var root = document.RootElement;
        if (root.GetProperty("profile").GetString() != TextureProfile || root.GetProperty("assetPackSha256").GetString() != packSha256)
            throw new PackException("The texture pack was not made for this game version.");
        var overlay = new Overlay(new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, PackEntry>(StringComparer.Ordinal), []);
        foreach (var texture in root.GetProperty("textures").EnumerateArray())
        {
            overlay.Removed.Add(texture.GetProperty("source").GetString()!);
            string remap = texture.GetProperty("import").GetString()!;
            overlay.Replaced[remap] = entries[remap];
            overlay.Added.Add(entries[texture.GetProperty("output").GetString()!]);
        }
        return overlay;
    }
}
