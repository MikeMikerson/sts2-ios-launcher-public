using System.Security.Cryptography;
using System.Text;

namespace STS2Mobile.Platform.Tests;

// Staged saves are used only once they verify, and the saves they replace are kept.
public sealed class SaveImportStoreTests : IDisposable
{
    private const string Account = "76561198000000001";
    private const string OtherAccount = "76561198000000002";
    private readonly string _sandbox = Directory.CreateTempSubdirectory("sts2-save-import-").FullName;
    private readonly SaveImportStore _imports;

    public SaveImportStoreTests()
    {
        _imports = new SaveImportStore(_sandbox);
        Put($"steam/{Account}/settings.save", "phone settings");
        Put($"steam/{Account}/profile1/saves/progress.save", "phone progress");
        Put($"steam/{Account}/profile1/saves/current_run.save", "phone run");
    }

    public void Dispose() => Directory.Delete(_sandbox, recursive: true);

    [Fact]
    public void NothingIsPendingWithoutAStagedImport() => Assert.Null(_imports.FindPending(Account));

    [Fact]
    public void StagedImportIsVerifiedAndDescribed()
    {
        StageCloudSaves();
        var pending = _imports.FindPending(Account)!;
        Assert.Equal(4, pending.Files);
        Assert.Equal(2, pending.Profiles);
        Assert.Equal(2, pending.FinishedRuns);
        Assert.Equal(new DateTime(2026, 10, 1, 9, 9, 23, DateTimeKind.Utc), pending.LastChangedUtc);
    }

    [Fact]
    public void StagedImportForAnotherAccountIsRefused()
    {
        StageCloudSaves();
        Assert.Throws<InvalidDataException>(() => _imports.FindPending(OtherAccount));
    }

    [Fact]
    public void ReplacedSavesAreKeptInADatedBackup()
    {
        StageCloudSaves();
        string backup = _imports.Apply(_imports.FindPending(Account)!, new DateTime(2026, 10, 10, 4, 5, 6, DateTimeKind.Utc));
        Assert.EndsWith(Path.Combine("save-backups", "20261010T040506Z"), backup);
        Assert.Equal("phone run", File.ReadAllText(Path.Combine(backup, "profile1/saves/current_run.save")));
        Assert.True(File.Exists(Path.Combine(backup, "import-manifest.json")));
    }

    [Fact]
    public void ImportedSavesCompletelyReplaceThePreviousOnes()
    {
        StageCloudSaves();
        _imports.Apply(_imports.FindPending(Account)!, DateTime.UtcNow);
        Assert.Equal("cloud progress", Read($"steam/{Account}/profile2/saves/progress.save"));
        Assert.False(Directory.Exists(Path.Combine(_sandbox, $"steam/{Account}/profile1")));
    }

    [Fact]
    public void PhoneKeepsItsOwnSettings()
    {
        StageCloudSaves();
        _imports.Apply(_imports.FindPending(Account)!, DateTime.UtcNow);
        Assert.Equal("phone settings", Read($"steam/{Account}/settings.save"));
    }

    [Fact]
    public void AppliedImportIsNotOfferedAgain()
    {
        string staged = StageCloudSaves();
        _imports.Apply(_imports.FindPending(Account)!, DateTime.UtcNow);
        Assert.Null(_imports.FindPending(Account));
        Assert.False(Directory.Exists(staged));
    }

    [Theory]
    [InlineData("changed file")]
    [InlineData("unlisted file")]
    [InlineData("path outside the save folder")]
    [InlineData("device settings")]
    [InlineData("modded mirror")]
    public void InvalidStagedImportIsRefusedAndChangesNothing(string problem)
    {
        _ = problem switch
        {
            "changed file" => Stage("20261011T000000Z", Account, ("profile1/saves/progress.save", "tampered", "original")),
            "unlisted file" => StageWithExtraFile(),
            "path outside the save folder" => Stage("20261013T000000Z", Account, ("../escape.save", "x", "x")),
            "device settings" => Stage("20261014T000000Z", Account, ("settings.save", "x", "x")),
            "modded mirror" => Stage("20261015T000000Z", Account, ("modded/profile.save", "x", "x")),
            _ => throw new ArgumentOutOfRangeException(nameof(problem)),
        };
        Assert.Throws<InvalidDataException>(() => _imports.FindPending(Account));
        Assert.Equal("phone progress", Read($"steam/{Account}/profile1/saves/progress.save"));
    }

    [Fact]
    public void DeclinedImportIsKeptButNotOfferedAgain()
    {
        string staged = Stage("20261016T000000Z", Account, ("profile.save", "p", "p"));
        _imports.Decline(_imports.FindPending(Account)!);
        Assert.Null(_imports.FindPending(Account));
        Assert.True(File.Exists(Path.Combine(staged, "files/profile.save")));
    }

    [Fact]
    public void StagedDownloadIsVerifiedLikeACableImport()
    {
        string directory = StageDownload();
        var pending = _imports.FindPending(Account)!;
        Assert.Equal(directory, pending.Directory);
        Assert.Equal(2, pending.Files);
        Assert.Equal(1, pending.Profiles);
        Assert.Equal(1, pending.FinishedRuns);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790845763).UtcDateTime, pending.LastChangedUtc);
    }

    [Fact]
    public void StagedDownloadReplacesTheSavesAndKeepsThePhoneSettings()
    {
        StageDownload();
        _imports.Apply(_imports.FindPending(Account)!, new DateTime(2026, 10, 17, 1, 2, 4, DateTimeKind.Utc));
        Assert.Equal("downloaded", Read($"steam/{Account}/profile1/saves/progress.save"));
        Assert.Equal("phone settings", Read($"steam/{Account}/settings.save"));
    }

    [Theory]
    [InlineData("device settings")]
    [InlineData("path outside the save folder")]
    [InlineData("a doubled name")]
    [InlineData("no files")]
    public void InvalidDownloadIsNotStaged(string problem)
    {
        StagedSave[] files = problem switch
        {
            "device settings" => [Save("settings.save", "x")],
            "path outside the save folder" => [Save("../escape.save", "x")],
            "a doubled name" => [Save("profile.save", "x"), Save("profile.save", "y")],
            "no files" => [],
            _ => throw new ArgumentOutOfRangeException(nameof(problem)),
        };
        Assert.Throws<InvalidDataException>(() => _imports.Stage(Account, files, "test", new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc)));
        Assert.False(Directory.Exists(Path.Combine(_sandbox, "save-import/20261018T000000Z")));
    }

    [Fact]
    public void DownloadWhoseContentDiffersFromItsListedHashIsRefused()
    {
        _imports.Stage(Account, [new StagedSave("profile.save", Bytes("x"), Sha1("y"), 1)], "test", new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc));
        Assert.Throws<InvalidDataException>(() => _imports.FindPending(Account));
    }

    [Fact]
    public void CloudFingerprintIgnoresOrderAndCaseButNotContent()
    {
        string fingerprint = SaveImportStore.Fingerprint([("b", "AA"), ("a", "bb")]);
        Assert.Equal(fingerprint, SaveImportStore.Fingerprint([("a", "BB"), ("b", "aa")]));
        Assert.NotEqual(fingerprint, SaveImportStore.Fingerprint([("a", "bb"), ("b", "ab")]));
    }

    [Fact]
    public void EachCloudStateIsOfferedOnce()
    {
        string fingerprint = SaveImportStore.Fingerprint([("a", "bb")]);
        Assert.False(_imports.WasOffered(Account, fingerprint));
        _imports.RememberOffer(Account, fingerprint);
        Assert.True(_imports.WasOffered(Account, fingerprint));
        Assert.False(_imports.WasOffered(Account, SaveImportStore.Fingerprint([("a", "cc")])));
        Assert.False(_imports.WasOffered(OtherAccount, fingerprint));
    }

    [Fact]
    public void OfferRecordStaysInTheImportFolder() =>
        Assert.Throws<ArgumentException>(() => _imports.RememberOffer("../x", SaveImportStore.Fingerprint([("a", "bb")])));

    [Fact]
    public void ProfilesAndFinishedRunsAreCountedFromSavePaths() =>
        Assert.Equal((2, 2), SaveImportStore.Describe(["profile.save", "profile1/saves/history/1.run", "profile2/saves/history/2.run", "profile2/saves/progress.save"]));

    [Fact]
    public void AppliedImportBecomesTheModdedProfilesTooAndKeepsModFiles()
    {
        Put($"steam/{Account}/modded/profile1/saves/progress.save", "modded progress");
        Put($"steam/{Account}/modded/profile3/saves/current_run.save", "modded only run");
        Put($"steam/{Account}/modded/quickmatch_voice.json", "voice settings");
        StageCloudSaves();
        string backup = _imports.Apply(_imports.FindPending(Account)!, DateTime.UtcNow);
        Assert.Equal("cloud progress", Read($"steam/{Account}/modded/profile2/saves/progress.save"));
        Assert.Equal("run two", Read($"steam/{Account}/modded/profile3/saves/history/2.run"));
        Assert.False(File.Exists(Path.Combine(_sandbox, $"steam/{Account}/modded/profile1/saves/progress.save")));
        Assert.False(File.Exists(Path.Combine(_sandbox, $"steam/{Account}/modded/profile3/saves/current_run.save")));
        Assert.Equal("voice settings", Read($"steam/{Account}/modded/quickmatch_voice.json"));
        Assert.Equal("modded progress", File.ReadAllText(Path.Combine(backup, "modded/profile1/saves/progress.save")));
        Assert.True(_imports.ModdedMatchesSynced(Account));
    }

    [Fact]
    public void SyncedSavesAreCopiedToTheModdedProfilesWithABackup()
    {
        Put($"steam/{Account}/modded/profile1/saves/progress.save", "modded progress");
        Put($"steam/{Account}/modded/quickmatch_run.json", "run");
        Assert.False(_imports.ModdedMatchesSynced(Account));
        string backup = _imports.CopySyncedToModded(Account, new DateTime(2026, 11, 3, 1, 2, 3, DateTimeKind.Utc))!;
        Assert.EndsWith("20261103T010203Z-modded", backup);
        Assert.Equal("modded progress", File.ReadAllText(Path.Combine(backup, "profile1/saves/progress.save")));
        Assert.Equal("phone progress", Read($"steam/{Account}/modded/profile1/saves/progress.save"));
        Assert.Equal("phone run", Read($"steam/{Account}/modded/profile1/saves/current_run.save"));
        Assert.Equal("run", Read($"steam/{Account}/modded/quickmatch_run.json"));
        Assert.True(_imports.ModdedMatchesSynced(Account));
    }

    [Fact]
    public void StagedDownloadKeepsTheCloudsTimes()
    {
        StageDownload();
        _imports.Apply(_imports.FindPending(Account)!, DateTime.UtcNow);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790845763).UtcDateTime,
            File.GetLastWriteTimeUtc(Path.Combine(_sandbox, $"steam/{Account}/profile1/saves/progress.save")));
    }

    [Fact]
    public void ListsOnlyTheSavesThatSync()
    {
        Put($"steam/{Account}/modded/profile.save", "modded");
        Put($"steam/{Account}/profile1/saves/progress.save.backup", "backup");
        string progress = Path.Combine(_sandbox, $"steam/{Account}/profile1/saves/progress.save");
        File.SetLastWriteTimeUtc(progress, new DateTime(2026, 10, 30, 21, 30, 5, 700, DateTimeKind.Utc));

        var saves = _imports.ListSaves(Account);

        Assert.Equal(["profile1/saves/current_run.save", "profile1/saves/progress.save"], saves.Select(save => save.Name));
        var listed = saves[1];
        Assert.Equal(Sha1("phone progress"), listed.Sha1Hex);
        Assert.Equal("phone progress".Length, listed.Size);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 21, 30, 5, TimeSpan.Zero), listed.Time);
        Assert.Equal(Bytes("phone progress"), _imports.ReadSave(Account, "profile1/saves/progress.save"));
    }

    [Fact]
    public void SettingsAndModdedSavesAloneAreNoSaves()
    {
        Assert.True(_imports.HasSaves(Account));
        Put($"steam/{OtherAccount}/settings.save", "settings");
        Put($"steam/{OtherAccount}/modded/profile.save", "modded");
        Assert.False(_imports.HasSaves(OtherAccount));
        Assert.False(_imports.HasSaves("76561198000000003"));
        Assert.Empty(_imports.ListSaves("76561198000000003"));
    }

    [Theory]
    [InlineData("settings.save")]
    [InlineData("../escape.save")]
    [InlineData("modded/profile.save")]
    public void ReadsOnlySavesThatSync(string name) => Assert.Throws<ArgumentException>(() => _imports.ReadSave(Account, name));

    [Fact]
    public void SavesOfAnAccountThatIsNotANumberAreNotRead() => Assert.Throws<ArgumentException>(() => _imports.ListSaves("../x"));

    [Fact]
    public void CloudBackupKeepsTheFilesWithAManifest()
    {
        string backup = _imports.BackUpCloud(Account,
            [new StagedSave("profile1/saves/progress.save", Bytes("cloud progress"), Sha1("cloud progress"), 1790845763)],
            new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc));

        Assert.EndsWith(Path.Combine("save-backups", "20261102T090000Z-steam-cloud"), backup);
        Assert.Equal("cloud progress", File.ReadAllText(Path.Combine(backup, "files/profile1/saves/progress.save")));
        string manifest = File.ReadAllText(Path.Combine(backup, "manifest.json"));
        Assert.Contains(Sha1("cloud progress"), manifest);
        Assert.Contains("Steam Cloud before a Send", manifest);
        Assert.Equal("phone progress", Read($"steam/{Account}/profile1/saves/progress.save"));
        Assert.Null(_imports.FindPending(Account));
    }

    [Fact]
    public void CloudBackupRefusesAnEmptyOrDoubledSet()
    {
        var at = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
        Assert.Throws<InvalidDataException>(() => _imports.BackUpCloud(Account, [], at));
        Assert.Throws<InvalidDataException>(() => _imports.BackUpCloud(Account, [Save("profile.save", "a"), Save("profile.save", "b")], at));
    }

    private string StageCloudSaves() => Stage("20261010T000000Z", Account,
        ("profile.save", "p", "p"),
        ("profile2/saves/progress.save", "cloud progress", "cloud progress"),
        ("profile2/saves/history/1.run", "run one", "run one"),
        ("profile3/saves/history/2.run", "run two", "run two"));

    private string StageWithExtraFile()
    {
        string directory = Stage("20261012T000000Z", Account, ("profile.save", "p", "p"));
        Put("save-import/20261012T000000Z/files/extra.save", "x");
        return directory;
    }

    private string StageDownload() => _imports.Stage(Account,
        [new StagedSave("profile1/saves/progress.save", Bytes("downloaded"), Sha1("downloaded").ToUpperInvariant(), 1790845763),
         new StagedSave("profile1/saves/history/3.run", Bytes("run three"), Sha1("run three"), 1790845000)],
        "test", new DateTime(2026, 10, 17, 1, 2, 3, DateTimeKind.Utc));

    // Writes a staged import as the Mac's cable import does: the files, and a manifest listing what they should be.
    private string Stage(string stamp, string account, params (string Name, string Text, string Listed)[] files)
    {
        foreach (var file in files) Put($"save-import/{stamp}/files/{file.Name}", file.Text);
        string listed = string.Join(",", files.Select(file =>
            $"{{\"name\":\"{file.Name}\",\"size\":{file.Listed.Length},\"sha1\":\"{Sha1(file.Listed)}\",\"time\":1790845763}}"));
        Put($"save-import/{stamp}/manifest.json", $"{{\"format\":1,\"account\":\"{account}\",\"files\":[{listed}]}}");
        return Path.Combine(_sandbox, "save-import", stamp);
    }

    private void Put(string relative, string text)
    {
        string path = Path.Combine(_sandbox, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_sandbox, relative));

    private static StagedSave Save(string name, string text) => new(name, Bytes(text), Sha1(text), 1);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Sha1(string text) => Convert.ToHexStringLower(SHA1.HashData(Bytes(text)));
}
