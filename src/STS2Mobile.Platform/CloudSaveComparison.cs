namespace STS2Mobile.Platform;

/// <summary>One save file as one side has it: its path below the account's save folder, SHA-1, size and last change.</summary>
public sealed record SaveFileState(string Name, string Sha1Hex, long Size, DateTimeOffset Time);

/// <summary>
/// The saves on this iPhone against the account's Steam Cloud saves, file by file by SHA-1, for the cloud saves screen to draw and
/// for a Send to act on. Only files that sync count on either side (<see cref="SaveImportStore.IsImportable"/>).
/// </summary>
/// <param name="LocalFiles">Save files on this iPhone.</param>
/// <param name="CloudFiles">Save files on Steam Cloud.</param>
/// <param name="LocalNewest">The last change to a save on this iPhone, or null with none.</param>
/// <param name="CloudNewest">The last change to a save on Steam Cloud, or null with none.</param>
/// <param name="Same">Files both sides have with the same content.</param>
/// <param name="NewerHere">Files both sides have, with different content, changed later on this iPhone.</param>
/// <param name="NewerInCloud">Files both sides have, with different content, changed later on Steam Cloud (or at the same time).</param>
/// <param name="OnlyHere">Files only this iPhone has.</param>
/// <param name="OnlyInCloud">Files only Steam Cloud has.</param>
/// <param name="ToSend">What a Send writes: every file that differs or that only this iPhone has, by name.</param>
/// <param name="ToRemove">
/// What a Send removes from Steam Cloud: files only Steam Cloud has, inside a profile this iPhone has, that are not run history. A run
/// finished on this iPhone deletes its <c>current_run.save</c>, and leaving the cloud's copy would bring the finished run back on a
/// computer. Run history is only ever added, and profiles this iPhone does not have are left as they are.
/// </param>
public sealed record CloudSaveComparison(
    int LocalFiles, int CloudFiles, DateTimeOffset? LocalNewest, DateTimeOffset? CloudNewest,
    int Same, int NewerHere, int NewerInCloud, int OnlyHere, int OnlyInCloud,
    IReadOnlyList<string> ToSend, IReadOnlyList<string> ToRemove)
{
    /// <summary>Both sides hold exactly the same saves.</summary>
    public bool InSync => NewerHere == 0 && NewerInCloud == 0 && OnlyHere == 0 && OnlyInCloud == 0;

    /// <summary>A Send would change nothing on Steam Cloud.</summary>
    public bool NothingToSend => ToSend.Count == 0 && ToRemove.Count == 0;

    /// <summary>Files only Steam Cloud has that a Send leaves there.</summary>
    public int KeptInCloud => OnlyInCloud - ToRemove.Count;

    public static CloudSaveComparison Compare(IEnumerable<SaveFileState> local, IEnumerable<SaveFileState> cloud)
    {
        var here = Index(local);
        var there = Index(cloud);
        var profilesHere = here.Keys.Where(name => name.Contains('/')).Select(name => name[..name.IndexOf('/')]).ToHashSet(StringComparer.Ordinal);
        int same = 0, newerHere = 0, newerInCloud = 0, onlyHere = 0;
        var toSend = new List<string>();
        foreach (var (name, mine) in here)
        {
            if (!there.TryGetValue(name, out var theirs)) { onlyHere++; toSend.Add(name); }
            else if (mine.Sha1Hex.Equals(theirs.Sha1Hex, StringComparison.OrdinalIgnoreCase)) same++;
            else
            {
                if (mine.Time > theirs.Time) newerHere++;
                else newerInCloud++;
                toSend.Add(name);
            }
        }
        var onlyInCloud = there.Keys.Where(name => !here.ContainsKey(name)).ToList();
        var toRemove = onlyInCloud.Where(name => name.Contains('/') && profilesHere.Contains(name[..name.IndexOf('/')]) && !IsRunHistory(name)).ToList();
        return new CloudSaveComparison(here.Count, there.Count, Newest(here.Values), Newest(there.Values),
            same, newerHere, newerInCloud, onlyHere, onlyInCloud.Count, toSend, toRemove);
    }

    /// <summary>A finished run's record (<c>profileN/saves/history/...</c>).</summary>
    public static bool IsRunHistory(string name) =>
        name.Split('/')[..^1].Any(part => part.Equals("history", StringComparison.OrdinalIgnoreCase));

    private static SortedDictionary<string, SaveFileState> Index(IEnumerable<SaveFileState> files)
    {
        var index = new SortedDictionary<string, SaveFileState>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!SaveImportStore.IsImportable(file.Name)) continue;
            if (!index.TryAdd(file.Name, file)) throw new InvalidDataException("A save listing names a file twice: " + file.Name);
        }
        return index;
    }

    private static DateTimeOffset? Newest(IEnumerable<SaveFileState> files) =>
        files.Select(file => (DateTimeOffset?)file.Time).DefaultIfEmpty(null).Max();
}
