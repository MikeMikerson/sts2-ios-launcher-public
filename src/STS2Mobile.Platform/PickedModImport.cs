namespace STS2Mobile.Platform;

/// <summary>
/// What the launcher's Mods screen and the game's Mods screen share when the player imports a zip from Files: installing it with
/// <see cref="ModPackageImport"/>, asking each time the install needs the player's agreement, and deleting the picker's copy after.
/// </summary>
public static class PickedModImport
{
    /// <summary>
    /// Installs <paramref name="archive"/>, asking through <paramref name="ask"/> whenever the install needs agreement first (a mod with
    /// code, or one that replaces a mod already here), with that result's message. Null when the player said no. The install runs off the
    /// caller's thread and everything after it, <paramref name="ask"/> included, back on the caller's context.
    /// </summary>
    public static async Task<ModImportResult?> InstallAsync(string archive, string inputsDirectory, Func<ModImportResult, Task<bool>> ask)
    {
        bool code = false, replace = false;
        var result = await Task.Run(() => ModPackageImport.Install(archive, inputsDirectory, replace, code));
        while (result.Status is ModImportStatus.NeedsCodeConfirmation or ModImportStatus.NeedsReplaceConfirmation)
        {
            if (!await ask(result)) return null;
            if (result.Status == ModImportStatus.NeedsCodeConfirmation) code = true;
            else replace = true;
            result = await Task.Run(() => ModPackageImport.Install(archive, inputsDirectory, replace, code));
        }
        return result;
    }

    /// <summary>
    /// Deletes the copy of the zip the Files picker handed over, which is in the app's temporary folder. A file anywhere else is never
    /// deleted. Returns why the copy could not be deleted, or null.
    /// </summary>
    public static string? Discard(string archive) => Discard(archive, Path.GetTempPath());

    internal static string? Discard(string archive, string temporaryFolder)
    {
        try
        {
            // The separator keeps a sibling such as "tmp-other" from passing for "tmp".
            string folder = Path.GetFullPath(temporaryFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(archive);
            if (full.StartsWith(folder, StringComparison.Ordinal) && File.Exists(full)) File.Delete(full);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return error.Message;
        }
    }
}
