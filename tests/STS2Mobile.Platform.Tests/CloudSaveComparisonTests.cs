namespace STS2Mobile.Platform.Tests;

// The two sides are compared by SHA-1, file by file, and a Send would write what differs and remove only what is safe to remove.
public sealed class CloudSaveComparisonTests
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 26, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Friday = new(2026, 10, 30, 21, 30, 0, TimeSpan.Zero);

    private static SaveFileState File(string name, string sha1, DateTimeOffset time) => new(name, sha1, 10, time);

    [Fact]
    public void SameSavesAreInSyncWithNothingToSend()
    {
        SaveFileState[] saves = [File("profile.save", "aa", Monday), File("profile1/saves/progress.save", "bb", Friday)];
        var comparison = CloudSaveComparison.Compare(saves, saves.Select(save => save with { Sha1Hex = save.Sha1Hex.ToUpperInvariant() }));

        Assert.True(comparison.InSync);
        Assert.True(comparison.NothingToSend);
        Assert.Equal(2, comparison.Same);
        Assert.Equal((2, 2), (comparison.LocalFiles, comparison.CloudFiles));
        Assert.Equal(Friday, comparison.LocalNewest);
        Assert.Equal(Friday, comparison.CloudNewest);
    }

    [Fact]
    public void CountsEachKindOfDifferenceAndTheNewestOnEachSide()
    {
        var comparison = CloudSaveComparison.Compare(
            [
                File("profile.save", "aa", Monday),
                File("profile1/saves/progress.save", "phone", Friday),
                File("profile1/saves/prefs.save", "phone", Monday),
                File("profile1/saves/history/2.run", "run2", Friday),
            ],
            [
                File("profile.save", "aa", Monday),
                File("profile1/saves/progress.save", "cloud", Monday),
                File("profile1/saves/prefs.save", "cloud", Friday),
                File("profile1/saves/history/1.run", "run1", Monday),
            ]);

        Assert.Equal(1, comparison.Same);
        Assert.Equal(1, comparison.NewerHere);
        Assert.Equal(1, comparison.NewerInCloud);
        Assert.Equal(1, comparison.OnlyHere);
        Assert.Equal(1, comparison.OnlyInCloud);
        Assert.Equal(["profile1/saves/history/2.run", "profile1/saves/prefs.save", "profile1/saves/progress.save"], comparison.ToSend.Order(StringComparer.Ordinal));
        Assert.False(comparison.InSync);
    }

    [Fact]
    public void ARunFinishedHereRemovesTheCloudsCurrentRun()
    {
        var comparison = CloudSaveComparison.Compare(
            [File("profile1/saves/progress.save", "after", Friday)],
            [File("profile1/saves/progress.save", "before", Monday), File("profile1/saves/current_run.save", "run", Monday)]);

        Assert.Equal(["profile1/saves/current_run.save"], comparison.ToRemove);
        Assert.Equal(0, comparison.KeptInCloud);
    }

    [Fact]
    public void RunHistoryOtherProfilesAndAccountFilesAreNeverRemoved()
    {
        var comparison = CloudSaveComparison.Compare(
            [File("profile1/saves/progress.save", "same", Monday)],
            [
                File("profile1/saves/progress.save", "same", Monday),
                File("profile1/saves/history/1790000000.run", "run", Monday),
                File("profile1/saves/History/7.run", "run", Monday),
                File("profile2/saves/progress.save", "computer", Friday),
                File("profile.save", "computer", Friday),
            ]);

        Assert.Empty(comparison.ToRemove);
        Assert.Equal(4, comparison.KeptInCloud);
        Assert.True(comparison.NothingToSend);
        Assert.False(comparison.InSync);
    }

    [Fact]
    public void FilesThatNeverSyncAreLeftOutOnBothSides()
    {
        var comparison = CloudSaveComparison.Compare(
            [File("settings.save", "phone", Friday), File("modded/profile.save", "mod", Friday), File("profile1/saves/progress.save.backup", "b", Friday),
             File("profile1/saves/progress.save", "same", Monday)],
            [File("settings.save", "computer", Monday), File("profile1/replays/1.replay", "r", Friday), File("profile1/saves/progress.save", "same", Monday)]);

        Assert.Equal((1, 1), (comparison.LocalFiles, comparison.CloudFiles));
        Assert.True(comparison.InSync);
        Assert.Equal(Monday, comparison.LocalNewest);
    }

    [Fact]
    public void AnEmptySideHasNoNewest()
    {
        var comparison = CloudSaveComparison.Compare([], [File("profile.save", "aa", Monday)]);
        Assert.Null(comparison.LocalNewest);
        Assert.Equal(Monday, comparison.CloudNewest);
        Assert.Empty(comparison.ToRemove);
        Assert.True(comparison.NothingToSend);
    }

    [Fact]
    public void AListingThatNamesAFileTwiceIsRefused() =>
        Assert.Throws<InvalidDataException>(() => CloudSaveComparison.Compare([], [File("profile.save", "aa", Monday), File("profile.save", "bb", Monday)]));
}
