using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using STS2Mobile.Steam.Depot;

namespace STS2Mobile.Launcher;

/// <summary>
/// Steam's builds as the version screen last saw them, kept in the launcher's folder, so the home screen can say "Update available" without
/// asking Steam: nothing goes to Steam until the player asks. It is a hint, and the version screen asks again when opened.
/// </summary>
public static class SteamBuildsSeen
{
    public const string FileName = "steam-builds.json";

    public static void Write(string launcherFolder, BuildsOnSteam builds)
    {
        Directory.CreateDirectory(launcherFolder);
        var record = new JsonObject { ["normal"] = ToJson(builds.Normal), ["beta"] = ToJson(builds.Beta) };
        string path = Path.Combine(launcherFolder, FileName), partial = path + ".partial";
        File.WriteAllText(partial, record.ToJsonString() + "\n");
        File.Move(partial, path, overwrite: true);
    }

    /// <summary>What was seen, or null if nothing was or the file cannot be read.</summary>
    public static BuildsOnSteam? Read(string launcherFolder)
    {
        string path = Path.Combine(launcherFolder, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return new BuildsOnSteam(FromJson(root, "normal", GameInstaller.NormalBranch), FromJson(root, "beta", GameInstaller.BetaBranch));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IOException)
        {
            return null;
        }
    }

    private static JsonObject? ToJson(BranchBuild? build) =>
        build == null ? null : new JsonObject { ["build"] = build.BuildId, ["manifest"] = build.ManifestId.ToString(CultureInfo.InvariantCulture) };

    private static BranchBuild? FromJson(JsonElement root, string name, string branch) =>
        root.TryGetProperty(name, out var build) && build.ValueKind == JsonValueKind.Object
            ? new BranchBuild(branch, build.GetProperty("build").GetUInt32(), ulong.Parse(build.GetProperty("manifest").GetString()!, CultureInfo.InvariantCulture))
            : null;
}
