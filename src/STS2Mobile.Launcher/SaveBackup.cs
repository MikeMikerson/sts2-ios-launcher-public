using System.Globalization;

namespace STS2Mobile.Launcher;

/// <summary>
/// Copies the player's saves aside before a switch between Stable and Beta: a save the other version cannot read is then still there
/// to go back to. The saves themselves stay where they are, and nothing is ever deleted from the backups.
/// </summary>
public static class SaveBackup
{
    /// <summary>Where the game keeps every account's saves, under the user data folder (<c>UserDataPathProvider</c>, patched to "steam").</summary>
    public const string SavesFolder = "steam";
    public const string BackupsFolder = "save-backups";

    /// <summary>Copies <c>steam/</c> to <c>save-backups/&lt;stamp&gt;-before-&lt;branch&gt;/steam</c>, and returns that folder, or null if there are no saves.</summary>
    /// <param name="target">The branch being switched to, for the folder's name.</param>
    public static string? Copy(string userRoot, string target, DateTime utc)
    {
        string saves = Path.Combine(userRoot, SavesFolder);
        if (!Directory.Exists(saves) || !Directory.EnumerateFiles(saves, "*", SearchOption.AllDirectories).Any()) return null;
        string name = utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-before-" + (target == GameInstaller.BetaBranch ? "beta" : "stable");
        string backup = Path.Combine(userRoot, BackupsFolder, name);
        if (Directory.Exists(backup)) throw new IOException("A save backup with this time already exists.");
        // Whole or not at all: a backup is copied under a temporary name and renamed when every file is there.
        string partial = backup + ".partial";
        if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
        foreach (string file in Directory.EnumerateFiles(saves, "*", SearchOption.AllDirectories))
        {
            string copy = Path.Combine(partial, SavesFolder, Path.GetRelativePath(saves, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
        Directory.Move(partial, backup);
        return backup;
    }
}
