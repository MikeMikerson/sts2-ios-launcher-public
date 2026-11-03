using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SteamKit2;
using STS2Mobile.GamePatcher;
using STS2Mobile.Steam.Depot;

namespace STS2Mobile.Launcher;

public enum GameVersionProblem
{
    None,
    /// <summary>Steam has no such branch for this account, or it needs a password.</summary>
    NotOnSteam,
    /// <summary>The download and preparation cannot fit.</summary>
    NotEnoughSpace,
    /// <summary>
    /// Steam's Stable is a build this app has not been tested with. The host code is built against one game version, and an older Stable did
    /// not start, so Stable is offered only once its build is tested. An untested Beta is still offered.
    /// </summary>
    UntestedStable,
}

/// <summary>Whether one version can be downloaded, decided before anything is touched.</summary>
/// <param name="Profile">The tested version's profile, or null for a build this app has not tested, which is patched on the phone in tolerant mode.</param>
public sealed record GameVersionCheck(GameVersionProblem Problem, BranchBuild? Build, GameProfile? Profile, long RequiredBytes = 0, long AvailableBytes = 0)
{
    public bool Tested => Profile != null;
}

/// <summary>A download the player started and that has not finished. It survives the app: see <see cref="GameInstaller"/>.</summary>
/// <param name="Cleared">The game that was there is gone.</param>
/// <param name="Build">Steam's build number when it started, for the record of the finished game; 0 if not known.</param>
public sealed record PendingGameDownload(string Branch, ulong Manifest, bool Cleared, uint Build = 0);

/// <summary>
/// Replaces the game in the app's data folder: what can be downloaded, deleting the current game, and the download itself.
/// It starts only when the player asks. A record beside the game folder says a download is under
/// way, written before anything is deleted and removed when the last file is in place, so an interruption at any point leaves
/// a state the next launch continues from, and until then the folder never counts as a game. Everything outside the folder
/// (saves, the Steam sign-in, shader cache) is left alone. Any build Steam serves can be downloaded: one with a profile is
/// checked by its hashes too and is tested, any other is checked by Steam's and is untested.
/// </summary>
public sealed class GameInstaller(string gameFolder, IReadOnlyList<GameProfile> profiles, Func<long> freeBytes, Action<string>? excludeFromBackup = null)
{
    public const string NormalBranch = InstalledBuild.NormalBranch;
    public const string BetaBranch = InstalledBuild.BetaBranch;
    public const string RecordSuffix = ".download.json";
    /// <summary>
    /// What setup needs beyond the downloaded files, for each byte of Steam's pack: the converted textures and the game's pack sit beside it
    /// until it goes. At its fullest, setup measured about 1.48 times the pack's size.
    /// </summary>
    private const double PackOverhead = 1.5;
    private string RecordPath => gameFolder.TrimEnd(Path.DirectorySeparatorChar) + RecordSuffix;

    /// <summary>Whether the game folder holds anything, finished or not, that replacing it would delete.</summary>
    public bool HasGameFiles => Directory.Exists(gameFolder) && Directory.EnumerateFileSystemEntries(gameFolder).Any();

    /// <summary>The download under way, or null. A record this app cannot use counts as none.</summary>
    public PendingGameDownload? Pending
    {
        get
        {
            if (!File.Exists(RecordPath)) return null;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(RecordPath));
                var root = document.RootElement;
                uint build = root.TryGetProperty("build", out var number) ? number.GetUInt32() : 0;
                var pending = new PendingGameDownload(root.GetProperty("branch").GetString()!, ulong.Parse(root.GetProperty("manifest").GetString()!, CultureInfo.InvariantCulture),
                    root.GetProperty("cleared").GetBoolean(), build);
                // A delete under way names no version: there is nothing to carry on with.
                return pending.Manifest == 0 || pending.Branch.Length == 0 ? null : pending;
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
            {
                return null;
            }
        }
    }

    /// <summary>Steam's answer for a version, and whether this app has tested it. The space check needs the version's file list: see <see cref="CheckSpace"/>.</summary>
    public GameVersionCheck Check(BranchBuild? build)
    {
        if (build == null) return new GameVersionCheck(GameVersionProblem.NotOnSteam, null, null);
        var profile = ProfileFor(build.ManifestId);
        return new GameVersionCheck(IsOffered(build, profile) ? GameVersionProblem.None : GameVersionProblem.UntestedStable, build, profile);
    }

    /// <summary>Whether this build may be downloaded at all: any Beta, and a Stable only when it is tested (see <see cref="GameVersionProblem.UntestedStable"/>).</summary>
    public static bool IsOffered(BranchBuild build, GameProfile? profile) => profile != null || build.Branch != NormalBranch;

    /// <summary>The same for a download already under way: Steam's answer for its branch no longer matters, because the player chose this one.</summary>
    public GameVersionCheck CheckPending(PendingGameDownload pending) =>
        new(GameVersionProblem.None, new BranchBuild(pending.Branch, pending.Build, pending.Manifest), ProfileFor(pending.Manifest));

    /// <summary>The disk space check, before anything is deleted: free space plus what is in the game folder, which goes first.</summary>
    public GameVersionCheck CheckSpace(GameVersionCheck check, IReadOnlyList<DepotFile> plan)
    {
        long required = PeakDiskBytes(check.Profile, plan), available = freeBytes() + FolderBytes();
        return check with { Problem = available < required ? GameVersionProblem.NotEnoughSpace : GameVersionProblem.None, RequiredBytes = required, AvailableBytes = available };
    }

    /// <summary>
    /// What setup needs at its fullest, estimated from the manifest: the downloaded files, and the converted textures and the game's pack
    /// beside Steam's pack. A tested version's measured figure is the least it can be.
    /// </summary>
    public static long PeakDiskBytes(GameProfile? profile, IReadOnlyList<DepotFile> plan)
    {
        long files = plan.Sum(file => (long)file.Source.TotalSize);
        long pack = plan.Where(file => Path.GetFileName(file.Path) == GameFolder.GamePack).Sum(file => (long)file.Source.TotalSize);
        return Math.Max(profile?.Steam?.PeakDiskBytes ?? 0, files + (long)(pack * PackOverhead));
    }

    /// <summary>
    /// The files to fetch from this manifest, found by their paths (<see cref="GameLayout.Files"/>), smallest first so the pack that takes
    /// minutes comes last. Each is checked by Steam's hash, and by the profile's when there is one.
    /// </summary>
    /// <param name="profile">The tested version's profile, or null for an untested build.</param>
    /// <exception cref="InvalidDataException">The manifest does not hold a file every build needs, or one the profile pins.</exception>
    public IReadOnlyList<DepotFile> Plan(GameProfile? profile, DepotManifest manifest)
    {
        var inDepot = manifest.Files!.ToDictionary(file => file.FileName.Replace('\\', '/'), StringComparer.Ordinal);
        var plan = new List<DepotFile>();
        foreach (var file in GameLayout.Files)
        {
            string? pin = profile?.Pin(file.GamePath);
            if (inDepot.TryGetValue(file.DepotPath, out var source)) plan.Add(new DepotFile(source, Path.Combine(gameFolder, file.GamePath), pin));
            // Steam's normal version has no Sentry.Godot.dll; whether the game needed it, the patches say.
            else if (!file.Optional || pin != null) throw new InvalidDataException("Steam's file list has no " + file.DepotPath);
        }
        return plan.OrderBy(file => file.Source.TotalSize).ToList();
    }

    /// <summary>The bytes the plan's chunks take on the wire.</summary>
    public static long DownloadBytes(IEnumerable<DepotFile> plan) => plan.Sum(file => file.Source.Chunks.Sum(chunk => (long)chunk.CompressedLength));

    /// <summary>The point of no return: records the download, then deletes the current game. Calling it again carries on.</summary>
    public PendingGameDownload Begin(string branch, ulong manifest, uint build = 0)
    {
        var pending = Pending;
        // Another version than the one under way starts again: its files are no use.
        bool cleared = pending is { Cleared: true } && pending.Branch == branch && pending.Manifest == manifest;
        var current = new PendingGameDownload(branch, manifest, cleared, build == 0 && pending?.Manifest == manifest ? pending.Build : build);
        if (!cleared)
        {
            WriteRecord(current);
            if (Directory.Exists(gameFolder)) Directory.Delete(gameFolder, recursive: true);
            current = current with { Cleared = true };
        }
        WriteRecord(current);
        Directory.CreateDirectory(gameFolder);
        excludeFromBackup?.Invoke(gameFolder);
        return current;
    }

    /// <summary>
    /// Downloads the plan, records which build is now in the folder (<see cref="InstalledBuild"/>), and ends the download's record, after
    /// which the folder is a game to prepare.
    /// </summary>
    public async Task<DepotDownloadResult> DownloadAsync(IReadOnlyList<DepotFile> plan, IDepotChunkSource source, Action<long, long>? progress, CancellationToken cancellationToken)
    {
        var result = await new DepotDownload(source, plan, progress).RunAsync(cancellationToken).ConfigureAwait(false);
        if (Pending is { } pending) Installed(pending).Write(gameFolder);
        File.Delete(RecordPath);
        return result;
    }

    /// <summary>What a finished download of <paramref name="pending"/> is: its version from the release information it brought.</summary>
    private InstalledBuild Installed(PendingGameDownload pending)
    {
        var profile = ProfileFor(pending.Manifest);
        string version = InstalledBuild.ReadVersion(Path.Combine(gameFolder, GameLayout.ReleaseInfo)) ?? profile?.Version ?? "unknown";
        return new InstalledBuild(pending.Branch, pending.Manifest, pending.Build, version, profile != null);
    }

    /// <summary>What is in the game folder, on disk.</summary>
    public long GameBytes => FolderBytes();

    /// <summary>
    /// Deletes the game, finished or not, and any download under way. A record that names no version is there while the folder goes, so a kill
    /// partway leaves no game rather than part of one. Everything outside the folder stays.
    /// </summary>
    public void Delete()
    {
        WriteRecord(new PendingGameDownload("", 0, false));
        if (Directory.Exists(gameFolder)) Directory.Delete(gameFolder, recursive: true);
        File.Delete(RecordPath);
    }

    private GameProfile? ProfileFor(ulong manifest) => profiles.FirstOrDefault(profile => profile.Steam?.Manifest == manifest);

    private long FolderBytes() => Directory.Exists(gameFolder)
        ? new DirectoryInfo(gameFolder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)
        : 0;

    private void WriteRecord(PendingGameDownload pending)
    {
        var record = new JsonObject
        {
            ["branch"] = pending.Branch,
            ["manifest"] = pending.Manifest.ToString(CultureInfo.InvariantCulture),
            ["cleared"] = pending.Cleared,
            ["build"] = pending.Build,
        };
        string partial = RecordPath + ".partial";
        File.WriteAllText(partial, record.ToJsonString() + "\n");
        File.Move(partial, RecordPath, overwrite: true);
    }
}
