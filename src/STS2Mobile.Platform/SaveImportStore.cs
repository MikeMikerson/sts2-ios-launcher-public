using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace STS2Mobile.Platform;

/// <summary>
/// Replaces the saves in the app's sandbox with a set that was copied into a staging folder
/// (<c>save-import/&lt;stamp&gt;/manifest.json</c> plus <c>files/</c>): the player's Steam Cloud copy
/// over the cable, or the player's cloud saves downloaded by the launcher (<see cref="Stage"/>). It
/// also lists and reads the saves on this iPhone for a Send, and keeps a copy of what a Send replaces
/// on Steam Cloud (<see cref="BackUpCloud"/>).
/// Nothing is used unless every file matches the manifest, and the saves it replaces are moved to
/// a dated backup, never deleted. It reads and writes only below the user data directory it is
/// given and never touches the network.
/// </summary>
public sealed class SaveImportStore
{
    private const string ManifestName = "manifest.json";
    private const string DeclinedManifestName = "manifest.declined.json";
    private const string FilesDirectory = "files";
    private const string ModdedDirectory = "modded";
    // The phone's own settings are not cloud files; they stay with the phone.
    private static readonly string[] DeviceFiles = ["settings.save", "settings.save.backup"];
    private readonly string _userRoot;

    public SaveImportStore(string userRoot) => _userRoot = Path.GetFullPath(userRoot);

    private string ImportRoot => Path.Combine(_userRoot, "save-import");

    /// <summary>
    /// The newest staged import for <paramref name="account"/>, fully verified, or null when there is
    /// none. A staged import that fails verification throws; it is never partly used.
    /// </summary>
    public PendingSaveImport? FindPending(string account)
    {
        if (!Directory.Exists(ImportRoot)) return null;
        foreach (string directory in Directory.GetDirectories(ImportRoot).OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            string manifest = Path.Combine(directory, ManifestName);
            if (File.Exists(manifest)) return Verify(directory, manifest, account);
        }
        return null;
    }

    /// <summary>
    /// Moves the current saves of the import's account to <c>save-backups/&lt;stamp&gt;</c> and the
    /// staged files into their place. Returns the backup directory. If the staged files cannot be
    /// moved in, the previous saves are moved back.
    /// </summary>
    public string Apply(PendingSaveImport pending, DateTime utc)
    {
        string saves = AccountSaves(pending.Account);
        string backup = Path.Combine(_userRoot, "save-backups", utc.ToString("yyyyMMdd'T'HHmmss'Z'"));
        if (Directory.Exists(backup)) throw new IOException("A save backup with this time already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        Directory.CreateDirectory(Path.GetDirectoryName(saves)!);
        bool hadSaves = Directory.Exists(saves);
        if (hadSaves) Directory.Move(saves, backup);
        else Directory.CreateDirectory(backup);
        try { Directory.Move(Path.Combine(pending.Directory, FilesDirectory), saves); }
        catch
        {
            if (hadSaves) Directory.Move(backup, saves);
            throw;
        }
        foreach (string name in DeviceFiles)
        {
            string previous = Path.Combine(backup, name);
            if (File.Exists(previous)) File.Copy(previous, Path.Combine(saves, name));
        }
        // The modded mirror never syncs, so an import has none. What the game keeps there besides the profiles (mods' own files) stays;
        // the profiles become the imported ones, so they show with mods on too. The old mirror stays in the backup.
        string modded = Path.Combine(backup, ModdedDirectory);
        if (Directory.Exists(modded)) CopyDirectory(modded, Path.Combine(saves, ModdedDirectory));
        ReplaceModdedProfiles(saves);
        File.Move(Path.Combine(pending.Directory, ManifestName), Path.Combine(backup, "import-manifest.json"));
        Directory.Delete(pending.Directory);
        return backup;
    }

    /// <summary>
    /// Writes <paramref name="files"/> and their manifest into a new staging folder for
    /// <see cref="FindPending"/> to verify. The manifest is written last, so an interrupted staging is
    /// never offered; on failure the folder is removed. Each file keeps its source's time, so the
    /// newest save on this iPhone means the same after a Get as on Steam Cloud. Returns the staging folder.
    /// </summary>
    public string Stage(string account, IReadOnlyList<StagedSave> files, string source, DateTime utc)
    {
        string stamp = utc.ToString("yyyyMMdd'T'HHmmss'Z'");
        string directory = Path.Combine(ImportRoot, stamp);
        if (Directory.Exists(directory)) throw new IOException("A save import with this time already exists.");
        if (files.Count == 0 || files.Any(file => !IsImportable(file.Name)) || files.Select(file => file.Name).Distinct(StringComparer.Ordinal).Count() != files.Count)
            throw new InvalidDataException("The saves to stage include a file an import may not contain.");
        WriteSet(directory, account, files, source, stamp);
        return directory;
    }

    /// <summary>
    /// Keeps a copy of Steam Cloud's files that a Send is about to replace or remove, in <c>save-backups/&lt;stamp&gt;-steam-cloud</c>
    /// with a manifest like a staged import's, so they can be put back by hand. Returns the folder.
    /// </summary>
    public string BackUpCloud(string account, IReadOnlyList<StagedSave> files, DateTime utc)
    {
        string stamp = utc.ToString("yyyyMMdd'T'HHmmss'Z'");
        string directory = Path.Combine(_userRoot, "save-backups", stamp + "-steam-cloud");
        if (Directory.Exists(directory)) throw new IOException("A cloud backup with this time already exists.");
        if (files.Count == 0 || files.Any(file => !IsImportable(file.Name)) || files.Select(file => file.Name).Distinct(StringComparer.Ordinal).Count() != files.Count)
            throw new InvalidDataException("The cloud files to back up include a file a backup may not contain.");
        WriteSet(directory, account, files, "Steam Cloud before a Send from this iPhone", stamp);
        return directory;
    }

    /// <summary>The account's saves on this iPhone that sync, with their SHA-1 and last change (to the second, as Steam keeps it).</summary>
    public IReadOnlyList<SaveFileState> ListSaves(string account)
    {
        string saves = AccountSaves(account);
        var found = new List<SaveFileState>();
        foreach (var (path, name) in SyncedFiles(saves))
        {
            byte[] content = File.ReadAllBytes(path);
            var time = DateTimeOffset.FromUnixTimeSeconds(new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds());
            found.Add(new SaveFileState(name, Convert.ToHexStringLower(SHA1.HashData(content)), content.LongLength, time));
        }
        return found.OrderBy(file => file.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Whether the saves the game uses with mods on (<c>modded/</c>) hold the same profiles as the saves that sync. With mods on the game reads only
    /// those, and it copies the synced ones there only on the first modded launch.
    /// </summary>
    public bool ModdedMatchesSynced(string account)
    {
        string saves = AccountSaves(account);
        string modded = Path.Combine(saves, ModdedDirectory);
        static Dictionary<string, string> Hashes(IEnumerable<(string Path, string Name)> files) =>
            files.Where(file => IsProfileFile(file.Name))
                .ToDictionary(file => file.Name, file => Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(file.Path))), StringComparer.Ordinal);
        var synced = Hashes(SyncedFiles(saves));
        var mirror = Hashes(SyncedFiles(modded));
        return synced.Count == mirror.Count && synced.All(file => mirror.TryGetValue(file.Key, out string? hash) && hash == file.Value);
    }

    /// <summary>
    /// Makes the profiles the game uses with mods on the same as the saves that sync, after copying the old ones to
    /// <c>save-backups/&lt;stamp&gt;-modded</c>. Returns that folder, or null when there was no modded folder to keep.
    /// </summary>
    public string? CopySyncedToModded(string account, DateTime utc)
    {
        string saves = AccountSaves(account);
        string modded = Path.Combine(saves, ModdedDirectory);
        string? backup = null;
        if (Directory.Exists(modded))
        {
            backup = Path.Combine(_userRoot, "save-backups", utc.ToString("yyyyMMdd'T'HHmmss'Z'") + "-modded");
            if (Directory.Exists(backup)) throw new IOException("A save backup with this time already exists.");
            CopyDirectory(modded, backup);
        }
        ReplaceModdedProfiles(saves);
        return backup;
    }

    // The profile list and the profiles' own files, the part of the saves the game copies into its modded mirror.
    private static bool IsProfileFile(string name) =>
        name.Split('/')[0] is var first && (first == "profile.save" || first.Length > 7 && first.StartsWith("profile", StringComparison.Ordinal) && first[7..].All(char.IsAsciiDigit));

    // The modded mirror's profile list and profiles (with their history and replays) give way to copies of the synced ones; anything else
    // there, such as a mod's own settings, stays.
    private static void ReplaceModdedProfiles(string saves)
    {
        string modded = Path.Combine(saves, ModdedDirectory);
        if (Directory.Exists(modded))
        {
            foreach (string file in Directory.GetFiles(modded, "profile.save*")) File.Delete(file);
            foreach (string directory in Directory.GetDirectories(modded, "profile*"))
                if (Path.GetFileName(directory) is { Length: > 7 } name && name[7..].All(char.IsAsciiDigit)) Directory.Delete(directory, recursive: true);
        }
        foreach (var (path, name) in SyncedFiles(saves).Where(file => IsProfileFile(file.Name)).ToList())
        {
            string target = Path.Combine(modded, name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(path));
        }
    }

    /// <summary>Whether this iPhone has any save of the account's that syncs.</summary>
    public bool HasSaves(string account) => SyncedFiles(AccountSaves(account)).Any();

    /// <summary>One of the account's save files, read for a Send.</summary>
    public byte[] ReadSave(string account, string name)
    {
        if (!IsImportable(name)) throw new ArgumentException("Not a save file that syncs.", nameof(name));
        return File.ReadAllBytes(Path.Combine(AccountSaves(account), name));
    }

    private string AccountSaves(string account)
    {
        if (account.Length == 0 || !account.All(char.IsAsciiDigit)) throw new ArgumentException("A Steam account id is a number.", nameof(account));
        return Path.Combine(_userRoot, "steam", account);
    }

    private static IEnumerable<(string Path, string Name)> SyncedFiles(string saves)
    {
        if (!Directory.Exists(saves)) yield break;
        foreach (string path in Directory.EnumerateFiles(saves, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetRelativePath(saves, path).Replace(Path.DirectorySeparatorChar, '/');
            if (IsImportable(name)) yield return (path, name);
        }
    }

    /// <summary>
    /// The files, each with the time its source gave it, then the manifest last, so an interrupted write is never taken as whole. On
    /// failure the folder is removed.
    /// </summary>
    private static void WriteSet(string directory, string account, IReadOnlyList<StagedSave> files, string source, string stamp)
    {
        try
        {
            foreach (StagedSave file in files)
            {
                string path = Path.Combine(directory, FilesDirectory, file.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, file.Content);
                if (file.UnixTime > 0) File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeSeconds(file.UnixTime).UtcDateTime);
            }
            using var manifest = new MemoryStream();
            using (var writer = new Utf8JsonWriter(manifest, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("format", 1);
                writer.WriteString("account", account);
                writer.WriteString("source", source);
                writer.WriteString("createdUtc", stamp);
                writer.WriteStartArray("files");
                foreach (StagedSave file in files.OrderBy(file => file.Name, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", file.Name);
                    writer.WriteNumber("size", file.Content.LongLength);
                    writer.WriteString("sha1", file.Sha1Hex.ToLowerInvariant());
                    writer.WriteNumber("time", file.UnixTime);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            File.WriteAllBytes(Path.Combine(directory, ManifestName), manifest.ToArray());
        }
        catch
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (string path in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(path));
        }
    }

    /// <summary>A fingerprint of a cloud listing (names and SHA-1, in any order), so each state of the cloud is offered once.</summary>
    public static string Fingerprint(IEnumerable<(string Name, string Sha1Hex)> files)
    {
        var text = new StringBuilder();
        foreach (var (name, sha1) in files.OrderBy(file => file.Name, StringComparer.Ordinal))
            text.Append(name).Append('\n').Append(sha1.ToLowerInvariant()).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Whether the player has already answered for this state of their cloud saves.</summary>
    public bool WasOffered(string account, string fingerprint)
    {
        string path = OfferPath(account);
        return File.Exists(path) && File.ReadAllText(path).Trim() == fingerprint;
    }

    public void RememberOffer(string account, string fingerprint)
    {
        Directory.CreateDirectory(ImportRoot);
        File.WriteAllText(OfferPath(account), fingerprint + "\n");
    }

    private string OfferPath(string account)
    {
        if (account.Length == 0 || !account.All(char.IsAsciiDigit)) throw new ArgumentException("A Steam account id is a number.", nameof(account));
        return Path.Combine(ImportRoot, "cloud-offered-" + account + ".txt");
    }

    /// <summary>How many profiles and finished runs a set of save paths holds.</summary>
    public static (int Profiles, int FinishedRuns) Describe(IEnumerable<string> names)
    {
        var profiles = new HashSet<string>(StringComparer.Ordinal);
        int finishedRuns = 0;
        foreach (string name in names)
        {
            string[] parts = name.Split('/');
            if (parts.Length > 1) profiles.Add(parts[0]);
            if (parts.Length > 2 && parts[^2] == "history") finishedRuns++;
        }
        return (profiles.Count, finishedRuns);
    }

    /// <summary>Keeps the staged files but stops offering them.</summary>
    public void Decline(PendingSaveImport pending) =>
        File.Move(Path.Combine(pending.Directory, ManifestName), Path.Combine(pending.Directory, DeclinedManifestName), overwrite: true);

    private static PendingSaveImport Verify(string directory, string manifestPath, string account)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        JsonElement root = manifest.RootElement;
        if (root.GetProperty("format").GetInt32() != 1) throw new InvalidDataException("The save import has an unknown format.");
        if (root.GetProperty("account").GetString() != account) throw new InvalidDataException("The save import is for a different Steam account.");
        string files = Path.Combine(directory, FilesDirectory);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        long newest = 0;
        foreach (JsonElement entry in root.GetProperty("files").EnumerateArray())
        {
            string name = entry.GetProperty("name").GetString() ?? "";
            if (!IsImportable(name) || !listed.Add(name)) throw new InvalidDataException("The save import lists a file it may not contain.");
            string path = Path.Combine(files, name);
            if (!File.Exists(path) || new FileInfo(path).Length != entry.GetProperty("size").GetInt64() ||
                !Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(path))).Equals(entry.GetProperty("sha1").GetString(), StringComparison.Ordinal))
                throw new InvalidDataException("A file in the save import does not match its manifest.");
            if (entry.TryGetProperty("time", out JsonElement time)) newest = Math.Max(newest, time.GetInt64());
        }
        if (listed.Count == 0) throw new InvalidDataException("The save import is empty.");
        int present = Directory.Exists(files) ? Directory.EnumerateFiles(files, "*", SearchOption.AllDirectories).Count() : 0;
        if (present != listed.Count) throw new InvalidDataException("The save import contains files its manifest does not list.");
        var (profiles, finishedRuns) = Describe(listed);
        return new PendingSaveImport(directory, account, listed.Count, profiles, finishedRuns,
            newest > 0 ? DateTimeOffset.FromUnixTimeSeconds(newest).UtcDateTime : null);
    }

    /// <summary>
    /// A relative save path that stays inside the save folder and is not one of the files that never
    /// sync or the modded mirror.
    /// </summary>
    public static bool IsImportable(string name)
    {
        if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':')) return false;
        string[] parts = name.Split('/');
        if (parts.Any(part => part is "" or "." or "..")) return false;
        string file = parts[^1].ToLowerInvariant();
        return file != "settings.save" && !file.EndsWith(".backup", StringComparison.Ordinal) && !file.EndsWith(".corrupt", StringComparison.Ordinal)
            && !parts[..^1].Any(part => part.Equals("replays", StringComparison.OrdinalIgnoreCase))
            && !parts[0].Equals("modded", StringComparison.OrdinalIgnoreCase);
    }
}
