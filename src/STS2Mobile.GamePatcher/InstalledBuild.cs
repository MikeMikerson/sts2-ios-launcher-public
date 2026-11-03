using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace STS2Mobile.GamePatcher;

/// <summary>How one patch went on the game in the folder.</summary>
/// <param name="Name">What it does, in the player's words.</param>
/// <param name="Required">The game cannot run without it, so a build it does not fit is refused.</param>
/// <param name="Applied">It is in the prepared assembly.</param>
/// <param name="Note">Why it is missing, or how it differed from the tested build.</param>
public sealed record PatchOutcome(string Name, bool Required, bool Applied, string? Note = null);

/// <summary>
/// Which build of the game is in the folder: <c>Documents/game/installed.json</c>, written when a download finishes and updated by
/// preparation with how the patches went. An install from before the file existed is recognised by its <c>release_info.json</c>.
/// </summary>
/// <param name="Branch">Steam's branch it came from (<see cref="NormalBranch"/> or <see cref="BetaBranch"/>), or empty if not known.</param>
/// <param name="Manifest">The depot manifest it came from; 0 if not known.</param>
/// <param name="Build">Steam's build number when it was downloaded; 0 if not known.</param>
/// <param name="Version">The game's own version, from <c>release_info.json</c>, without the leading v.</param>
/// <param name="Tested">This app has a profile for it: its files were hash-checked and its patches are known to apply.</param>
/// <param name="Patches">How each patch went, once it was prepared.</param>
public sealed record InstalledBuild(string Branch, ulong Manifest, uint Build, string Version, bool Tested, IReadOnlyList<PatchOutcome>? Patches = null)
{
    public const string FileName = "installed.json";
    /// <summary>Steam's branches, as the app names them to the player: Stable and Beta (as the Android launcher does).</summary>
    public const string NormalBranch = "public";
    public const string BetaBranch = "public-beta";

    /// <summary>What the shader warmup's stamp starts with, so a different build warms up again.</summary>
    public string WarmupId => (Tested ? GameProfile.ForManifest(Manifest)?.WarmupId : null) ?? "manifest-" + Manifest.ToString(CultureInfo.InvariantCulture);

    /// <summary>The patches that did not go in.</summary>
    public IReadOnlyList<PatchOutcome> Skipped => Patches?.Where(patch => !patch.Applied).ToList() ?? [];

    public static string BranchName(string branch) => branch switch
    {
        NormalBranch => "Stable",
        BetaBranch => "Beta",
        _ => "",
    };

    /// <summary>The record in the folder, or one made from its <c>release_info.json</c> for an older install, or null with neither.</summary>
    public static InstalledBuild? Read(string gameFolder)
    {
        string path = Path.Combine(gameFolder, FileName);
        if (File.Exists(path))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                List<PatchOutcome>? patches = null;
                if (root.TryGetProperty("patches", out var list) && list.ValueKind == JsonValueKind.Array)
                    patches = list.EnumerateArray().Select(patch => new PatchOutcome(patch.GetProperty("name").GetString()!, patch.GetProperty("required").GetBoolean(),
                        patch.GetProperty("applied").GetBoolean(), Sha256File.ReadString(patch, "note"))).ToList();
                return new InstalledBuild(root.GetProperty("branch").GetString()!, ulong.Parse(root.GetProperty("manifest").GetString()!, CultureInfo.InvariantCulture),
                    root.GetProperty("build").GetUInt32(), root.GetProperty("version").GetString()!, root.GetProperty("tested").GetBoolean(), patches);
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
            {
                // Unreadable: the release information below still says what the game is.
            }
        }
        return FromReleaseInfo(gameFolder);
    }

    /// <summary>
    /// An install with no <c>installed.json</c>: a tested version is known by its release information's hash, and is taken to be its
    /// profile's Steam build on the beta branch. Anything else is an untested game of unknown origin.
    /// </summary>
    public static InstalledBuild? FromReleaseInfo(string gameFolder)
    {
        string path = Path.Combine(gameFolder, GameLayout.ReleaseInfo);
        if (!File.Exists(path)) return null;
        if (GameProfile.ForReleaseInfo(Sha256File.Of(path)) is { Steam: { } steam } profile)
            return new InstalledBuild(BetaBranch, steam.Manifest, 0, profile.Version, Tested: true);
        return ReadVersion(path) is { } version ? new InstalledBuild("", 0, 0, version, Tested: false) : null;
    }

    /// <summary>The game's version in a <c>release_info.json</c>, such as <c>0.111.0</c>, or null if it has none.</summary>
    public static string? ReadVersion(string releaseInfoPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(releaseInfoPath));
            string? version = Sha256File.ReadString(document.RootElement, "version");
            return string.IsNullOrWhiteSpace(version) ? null : version.TrimStart('v', 'V');
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Writes the record by temporary name and rename, so a crash leaves the old one or the new one.</summary>
    public void Write(string gameFolder)
    {
        var record = new JsonObject
        {
            ["branch"] = Branch,
            ["manifest"] = Manifest.ToString(CultureInfo.InvariantCulture),
            ["build"] = Build,
            ["version"] = Version,
            ["tested"] = Tested,
        };
        if (Patches != null)
        {
            var list = new JsonArray();
            foreach (var patch in Patches)
                list.Add(new JsonObject { ["name"] = patch.Name, ["required"] = patch.Required, ["applied"] = patch.Applied, ["note"] = patch.Note });
            record["patches"] = list;
        }
        string path = Path.Combine(gameFolder, FileName), partial = path + ".partial";
        File.WriteAllText(partial, record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(partial, path, overwrite: true);
    }

    /// <summary>After preparation: records whether the game was prepared as tested and how its patches went, if that changed.</summary>
    public static void RecordPreparation(string gameFolder, bool tested, IReadOnlyList<PatchOutcome> patches)
    {
        if (Read(gameFolder) is not { } installed) return;
        var updated = installed with { Tested = tested, Patches = patches };
        if (File.Exists(Path.Combine(gameFolder, FileName)) && installed.Tested == tested && installed.Patches is { } earlier && earlier.SequenceEqual(patches)) return;
        updated.Write(gameFolder);
    }
}
