using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace STS2Mobile.GamePatcher;

/// <summary>
/// The game files one tested version must have, as SHA-256 hashes (tools/dependencies.json, game). A build Steam serves with no profile
/// is still played: it is patched on the phone in tolerant mode and shown as untested.
/// </summary>
/// <param name="Inputs">The files the patched assembly is made from, in <see cref="GameFolder.Original"/>.</param>
/// <param name="Unchanged">The assemblies the game loads as they are, in the game folder.</param>
/// <param name="PackSha256">The game's resource pack, also in <see cref="GameFolder.Original"/> (STS2Mobile.GamePack).</param>
/// <param name="TexturesKeptOriginalList">Textures that stay in their desktop format, because ASTC falls below the quality bar. Found on the Mac, as the profile's other facts are.</param>
/// <param name="Steam">Where Steam delivers this version.</param>
public sealed record GameProfile(string Version, IReadOnlyDictionary<string, string> Inputs, IReadOnlyDictionary<string, string> Unchanged, string PackSha256 = "", IReadOnlyList<string>? TexturesKeptOriginalList = null, SteamGameBuild? Steam = null)
{
    public IReadOnlyList<string> TexturesKeptOriginal => TexturesKeptOriginalList ?? [];

    /// <summary>Part of the shader warmup's stamp, so another build warms up again.</summary>
    public string WarmupId => $"v{Version}-full-game-integration-v1";

    /// <summary>The pin for a file of <see cref="GameLayout.Files"/>, by where it goes in the game folder, or null if this profile names none.</summary>
    public string? Pin(string gamePath)
    {
        if (gamePath == GameLayout.ReleaseInfo) return Steam?.ReleaseInfoSha256;
        if (gamePath == GameFolder.Original + "/" + GameFolder.GamePack) return PackSha256.Length > 0 ? PackSha256 : null;
        var pins = gamePath.StartsWith(GameFolder.Original + "/", StringComparison.Ordinal) ? Inputs : Unchanged;
        return pins.TryGetValue(Path.GetFileName(gamePath), out string? sha256) ? sha256 : null;
    }

    public const string GameAssembly = "sts2.dll";
    /// <summary>The reporting SDK's assemblies: the patches read which of its types are enums and structs to erase them.</summary>
    public static readonly string[] ReportingAssemblies = ["Sentry.dll", "Sentry.Godot.dll"];

    /// <summary>The newest tested version: the one the Mac tools build and check against.</summary>
    public static GameProfile Current => Known[0];

    /// <summary>
    /// Every version this app has tested, newest first: <c>game</c> in tools/dependencies.json, then any in <c>previous_game_profiles</c>
    /// (the same shape), so a build that is no longer the newest profile stays tested.
    /// </summary>
    public static IReadOnlyList<GameProfile> Known { get; } = ReadAll();

    public static GameProfile? ForManifest(ulong manifest) => Known.FirstOrDefault(profile => profile.Steam?.Manifest == manifest);

    /// <summary>The tested version whose game assembly has this hash.</summary>
    public static GameProfile? ForAssembly(string sha256) => Known.FirstOrDefault(profile => profile.Inputs.GetValueOrDefault(GameAssembly) == sha256);

    public static GameProfile? ForReleaseInfo(string sha256) => Known.FirstOrDefault(profile => profile.Steam?.ReleaseInfoSha256 == sha256);

    /// <summary>
    /// The profile the game in <paramref name="gameFolder"/> is prepared with, or null to prepare it in tolerant mode. A game recorded as
    /// tested keeps its profile, so a damaged file is refused rather than patched as some other build. Otherwise the game assembly decides:
    /// an app that has since gained a profile for an untested build treats it as tested.
    /// </summary>
    public static GameProfile? ForFolder(string gameFolder, InstalledBuild? installed)
    {
        if (installed is { Tested: true } && ForManifest(installed.Manifest) is { } recorded) return recorded;
        string assembly = Path.Combine(gameFolder, GameFolder.Original, GameAssembly);
        return File.Exists(assembly) ? ForAssembly(Sha256File.Of(assembly)) : null;
    }

    private static List<GameProfile> ReadAll()
    {
        using var stream = EmbeddedData.Open("dependencies.json");
        var root = JsonNode.Parse(stream)!;
        var profiles = new List<GameProfile> { Read(root["game"]!) };
        if (root["previous_game_profiles"] is JsonArray previous) profiles.AddRange(previous.Select(game => Read(game!)));
        return profiles;
    }

    private static GameProfile Read(JsonNode game)
    {
        var others = game["other_assemblies_sha256"]!.AsObject().ToDictionary(pin => pin.Key, pin => pin.Value!.GetValue<string>(), StringComparer.Ordinal);
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GameAssembly] = game["assembly_sha256"]!.GetValue<string>(),
        };
        foreach (string name in ReportingAssemblies)
            if (others.Remove(name, out string? pin)) inputs[name] = pin;
        var kept = game["textures_kept_original"]!.AsArray().Select(name => name!.GetValue<string>()).ToList();
        var steam = new SteamGameBuild(game["steam_depot"]!.GetValue<uint>(), ulong.Parse(game["steam_manifest"]!.GetValue<string>()),
            game["peak_disk_bytes"]!.GetValue<long>(), game["release_info_sha256"]!.GetValue<string>());
        return new GameProfile(game["version"]!.GetValue<string>(), inputs, others, game["pack_sha256"]!.GetValue<string>(), kept, steam);
    }
}

/// <summary>Where Steam delivers one tested version. The manifest, not Steam's build number, says the files are the ones profiled: a build number also moves when only another system's depot changed.</summary>
/// <param name="Depot">The macOS depot.</param>
/// <param name="Manifest">The depot's files for this version.</param>
/// <param name="PeakDiskBytes">What setup needs at its fullest: Steam's pack, the converted textures and the game's pack, side by side.</param>
/// <param name="ReleaseInfoSha256">The one downloaded file no other pin covers.</param>
public sealed record SteamGameBuild(uint Depot, ulong Manifest, long PeakDiskBytes, string ReleaseInfoSha256);

/// <summary>Where the macOS depot holds what the app wants, and where each file goes in the game folder, whatever the build.</summary>
public static class GameLayout
{
    /// <summary>The macOS depot: the one the app's game files come from, and whose files the profiles' hashes are of. No other depot's files match them.</summary>
    public const uint Depot = 2868842;
    public const string ReleaseInfo = "release_info.json";
    private const string DepotResources = "SlayTheSpire2.app/Contents/Resources/";
    private const string DepotAssemblies = DepotResources + "data_sts2_macos_arm64/";

    /// <summary>
    /// The files to download: the pack, the assemblies the app reads or loads, and <c>release_info.json</c>. The reporting SDK's are optional,
    /// because Steam's normal version (Stable, build 23811903) has no <c>Sentry.Godot.dll</c>. A profile that pins one makes it required,
    /// and for any other build the patches say whether the game needed it.
    /// </summary>
    public static IReadOnlyList<GameLayoutFile> Files { get; } =
    [
        new(DepotResources + ReleaseInfo, ReleaseInfo),
        new(DepotResources + GameFolder.GamePack, GameFolder.Original + "/" + GameFolder.GamePack),
        new(DepotAssemblies + GameProfile.GameAssembly, GameFolder.Original + "/" + GameProfile.GameAssembly),
        new(DepotAssemblies + "Sentry.dll", GameFolder.Original + "/Sentry.dll", Optional: true),
        new(DepotAssemblies + "Sentry.Godot.dll", GameFolder.Original + "/Sentry.Godot.dll", Optional: true),
        new(DepotAssemblies + "Steamworks.NET.dll", "Steamworks.NET.dll"),
        new(DepotAssemblies + "SmartFormat.dll", "SmartFormat.dll"),
        new(DepotAssemblies + "SmartFormat.ZString.dll", "SmartFormat.ZString.dll"),
    ];

    /// <summary>The assemblies the game loads as they are, which every build must have in the game folder.</summary>
    public static IEnumerable<string> UnchangedAssemblies =>
        Files.Where(file => !file.GamePath.Contains('/') && file.GamePath.EndsWith(".dll", StringComparison.Ordinal)).Select(file => file.GamePath);
}

/// <summary>One file of <see cref="GameLayout.Files"/>.</summary>
/// <param name="DepotPath">Its name in the depot's manifest.</param>
/// <param name="GamePath">Where it goes under the game folder.</param>
/// <param name="Optional">A build may lack it.</param>
public sealed record GameLayoutFile(string DepotPath, string GamePath, bool Optional = false);

/// <summary>The layout of the game's folder in the app's data folder.</summary>
public static class GameFolder
{
    /// <summary>The game's files as they came, which the assembly hook cannot see, so a re-patch needs no new download.</summary>
    public const string Original = "original";
    public const string GamePack = "Slay the Spire 2.pck";
    public const string ModCatalog = "builtin-mods.json";
}

internal static class EmbeddedData
{
    public static Stream Open(string name) =>
        typeof(EmbeddedData).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " is not embedded.");

    public static string ReadText(string name)
    {
        using var reader = new StreamReader(Open(name));
        return reader.ReadToEnd();
    }
}

internal static class Sha256File
{
    public static string Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string OfText(string text) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    public static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
