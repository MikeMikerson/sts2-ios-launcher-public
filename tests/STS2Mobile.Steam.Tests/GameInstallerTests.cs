using System.Diagnostics;
using System.Security.Cryptography;
using SteamKit2;
using STS2Mobile.GamePatcher;
using STS2Mobile.Launcher;
using STS2Mobile.Steam.Depot;

namespace STS2Mobile.Steam.Tests;

public sealed class GameInstallerTests : IDisposable
{
    private const ulong Manifest = 500;
    private const long Peak = 1000;
    private readonly string _root = Directory.CreateTempSubdirectory("game-installer-").FullName;
    private long _free = 2000;
    private readonly List<string> _excluded = [];

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Game => Path.Combine(_root, "game");

    private string Record => Game + GameInstaller.RecordSuffix;

    private static readonly GameProfile Profile = new("1.2.3", new Dictionary<string, string> { ["sts2.dll"] = Hash("dll") },
        new Dictionary<string, string> { ["SmartFormat.dll"] = Hash("other") }, Hash("pack"), [], new SteamGameBuild(7, Manifest, Peak, Hash("info")));

    private GameInstaller Installer() => new(Game, [Profile], () => _free, _excluded.Add);

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private void WriteGame(long bytes = 100)
    {
        Directory.CreateDirectory(Path.Combine(Game, "original"));
        File.WriteAllBytes(Path.Combine(Game, "FullGame.pck"), new byte[bytes]);
        File.WriteAllText(Path.Combine(Game, "original/sts2.dll"), "old");
    }

    private const string Resources = "SlayTheSpire2.app/Contents/Resources/";
    private const string Assemblies = Resources + "data_sts2_macos_arm64/";

    /// <summary>A manifest with every file of the layout, sized as given, less those named in <paramref name="without"/>.</summary>
    private static DepotManifest ManifestOf(ulong pack = 1000, params string[] without)
    {
        var manifest = new DepotManifest { Files = [] };
        foreach (var (name, size) in new (string, ulong)[]
                 {
                     (Resources + "Slay the Spire 2.pck", pack), (Resources + "release_info.json", 1), (Assemblies + "sts2.dll", 20), (Assemblies + "Sentry.dll", 8),
                     (Assemblies + "Sentry.Godot.dll", 7), (Assemblies + "Steamworks.NET.dll", 6), (Assemblies + "SmartFormat.dll", 10), (Assemblies + "SmartFormat.ZString.dll", 9),
                     (Assemblies + "GodotSharp.dll", 5),
                 })
            if (!without.Any(name.EndsWith)) manifest.Files.Add(new DepotManifest.FileData { FileName = name, TotalSize = size, Chunks = [] });
        return manifest;
    }

    [Fact]
    public void ABuildWithoutAProfileIsOfferedAsUntestedAndNothingHappens()
    {
        WriteGame();
        var check = Installer().Check(new BranchBuild("public-beta", 9, 999));
        Assert.Equal(GameVersionProblem.None, check.Problem);
        Assert.False(check.Tested);
        Assert.Equal(9u, check.Build!.BuildId);
        Assert.True(File.Exists(Path.Combine(Game, "FullGame.pck")));
        Assert.False(File.Exists(Record));
    }

    /// <summary>The host code is built against one game version, and an older Stable did not start: Stable waits for a tested build.</summary>
    [Fact]
    public void AnUntestedStableIsNotOfferedButATestedOneIs()
    {
        var untested = Installer().Check(new BranchBuild(GameInstaller.NormalBranch, 9, 999));
        Assert.Equal(GameVersionProblem.UntestedStable, untested.Problem);
        Assert.False(untested.Tested);
        Assert.Contains("does not run with this app yet", GameInstallMessages.Problem(untested, "Stable"));
        Assert.Contains("Nothing was downloaded or deleted", GameInstallMessages.Problem(untested, "Stable"));
        var tested = Installer().Check(new BranchBuild(GameInstaller.NormalBranch, 9, Manifest));
        Assert.Equal(GameVersionProblem.None, tested.Problem);
        Assert.True(tested.Tested);
    }

    [Fact]
    public void AVersionSteamDoesNotOfferIsSaidSo() =>
        Assert.Equal(GameVersionProblem.NotOnSteam, Installer().Check(null).Problem);

    [Fact]
    public void ABuildWithTheProfilesManifestIsTestedWhateverItsBuildNumber()
    {
        var check = Installer().Check(new BranchBuild("public-beta", 12345, Manifest));
        Assert.Equal(GameVersionProblem.None, check.Problem);
        Assert.Same(Profile, check.Profile);
    }

    [Fact]
    public void TooLittleSpaceIsSaidBeforeAnythingIsDeleted()
    {
        WriteGame(100);
        _free = 500;
        var installer = Installer();
        var check = installer.Check(new BranchBuild("public-beta", 1, Manifest));
        check = installer.CheckSpace(check, installer.Plan(Profile, ManifestOf(pack: 10)));
        Assert.Equal(GameVersionProblem.NotEnoughSpace, check.Problem);
        Assert.Equal(Peak, check.RequiredBytes);
        Assert.Equal(603, check.AvailableBytes);
        Assert.True(File.Exists(Path.Combine(Game, "FullGame.pck")));
    }

    [Fact]
    public void TheCurrentGameCountsAsSpaceBecauseItGoesFirst()
    {
        WriteGame(600);
        _free = 500;
        var installer = Installer();
        var check = installer.CheckSpace(installer.Check(new BranchBuild("public-beta", 1, Manifest)), installer.Plan(Profile, ManifestOf(pack: 10)));
        Assert.Equal(GameVersionProblem.None, check.Problem);
    }

    [Fact]
    public void PeakDiskIsEstimatedFromTheManifestAndNeverBelowTheProfilesFigure()
    {
        var installer = Installer();
        var untested = installer.Plan(null, ManifestOf(pack: 10_000));
        // The files, then the converted textures and the game's pack beside Steam's pack.
        long files = untested.Sum(file => (long)file.Source.TotalSize);
        Assert.Equal(files + 15_000, GameInstaller.PeakDiskBytes(null, untested));
        Assert.Equal(Peak, GameInstaller.PeakDiskBytes(Profile, installer.Plan(Profile, ManifestOf(pack: 10))));
        Assert.Equal(files + 15_000, GameInstaller.PeakDiskBytes(Profile, installer.Plan(Profile, ManifestOf(pack: 10_000))));
    }

    [Fact]
    public void BeginDeletesOnlyTheGameFolderAndRecordsTheDownload()
    {
        WriteGame();
        File.WriteAllText(Path.Combine(_root, "profile.save"), "save");
        Directory.CreateDirectory(Path.Combine(_root, "launcher"));
        File.WriteAllText(Path.Combine(_root, "launcher/credentials"), "sign-in");

        var installer = Installer();
        installer.Begin("public-beta", Manifest, 77);

        Assert.Empty(Directory.EnumerateFileSystemEntries(Game));
        Assert.Equal("save", File.ReadAllText(Path.Combine(_root, "profile.save")));
        Assert.Equal("sign-in", File.ReadAllText(Path.Combine(_root, "launcher/credentials")));
        Assert.Equal(new PendingGameDownload("public-beta", Manifest, true, 77), installer.Pending);
        Assert.Equal([Game], _excluded);
    }

    [Fact]
    public void BeginOfADownloadUnderWayKeepsWhatItFetched()
    {
        var installer = Installer();
        installer.Begin("public-beta", Manifest);
        File.WriteAllText(Path.Combine(Game, "file.partial"), "fetched");
        installer.Begin("public-beta", Manifest);
        Assert.True(File.Exists(Path.Combine(Game, "file.partial")));
    }

    [Fact]
    public void AnInterruptedDeleteIsFinished()
    {
        WriteGame();
        File.WriteAllText(Record, """{"branch":"public-beta","manifest":"500","cleared":false}""");
        Installer().Begin("public-beta", Manifest);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Game));
    }

    [Fact]
    public void ADownloadOfAnotherVersionStartsAgain()
    {
        var installer = Installer();
        installer.Begin("public-beta", Manifest);
        File.WriteAllText(Path.Combine(Game, "file.partial"), "fetched");
        installer.Begin("public", Manifest);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Game));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"branch":"public-beta"}""")]
    [InlineData("""{"branch":"","manifest":"0","cleared":false}""")]
    public void ARecordThisAppCannotUseCountsAsNone(string record)
    {
        File.WriteAllText(Record, record);
        Assert.Null(Installer().Pending);
    }

    [Fact]
    public void AnUntestedDownloadUnderWayCarriesOn()
    {
        File.WriteAllText(Record, """{"branch":"public","manifest":"12","cleared":true}""");
        var pending = Installer().Pending!;
        Assert.Equal(new PendingGameDownload("public", 12, true), pending);
        Assert.False(Installer().CheckPending(pending).Tested);
    }

    [Fact]
    public void ACheckOfADownloadUnderWayKeepsItsBuild()
    {
        var installer = Installer();
        installer.Begin("public-beta", Manifest, 31);
        var check = installer.CheckPending(installer.Pending!);
        Assert.Equal(GameVersionProblem.None, check.Problem);
        Assert.Equal(new BranchBuild("public-beta", 31, Manifest), check.Build);
    }

    [Fact]
    public async Task TheRecordEndsOnlyWithTheLastFileAndTheGameIsRecorded()
    {
        var installer = Installer();
        installer.Begin("public-beta", Manifest, 24724944);
        var content = "the game"u8.ToArray();
        var chunks = new FakeChunks(content, 4);
        var plan = new[] { new DepotFile(chunks.File(), Path.Combine(Game, "original/sts2.dll"), new string('0', 64)) };
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.DownloadAsync(plan, chunks.Source, null, CancellationToken.None));
        Assert.NotNull(installer.Pending);
        Assert.Null(InstalledBuild.Read(Game));

        plan = [plan[0] with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)) }];
        await installer.DownloadAsync(plan, chunks.Source, null, CancellationToken.None);
        Assert.Null(installer.Pending);
        Assert.Equal(content, File.ReadAllBytes(plan[0].Path));
        Assert.Equal(new InstalledBuild("public-beta", Manifest, 24724944, "1.2.3", Tested: true), InstalledBuild.Read(Game));
    }

    [Fact]
    public async Task AnUntestedBuildIsCheckedBySteamsHashAndRecordedWithItsOwnVersion()
    {
        var installer = Installer();
        installer.Begin("public", 999, 23811903);
        var info = """{"version":"v0.103.2"}"""u8.ToArray();
        var chunks = new FakeChunks(info, 8);
        var source = chunks.File();
        source.FileHash = SHA1.HashData("something else"u8);
        var plan = new[] { new DepotFile(source, Path.Combine(Game, "release_info.json"), null) };
        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => installer.DownloadAsync(plan, chunks.Source, null, CancellationToken.None));
        Assert.Contains("Steam's hash", refused.Message);
        Assert.False(File.Exists(plan[0].Path));

        source.FileHash = SHA1.HashData(info);
        await installer.DownloadAsync(plan, chunks.Source, null, CancellationToken.None);
        Assert.Equal(new InstalledBuild("public", 999, 23811903, "0.103.2", Tested: false), InstalledBuild.Read(Game));
    }

    [Fact]
    public void APlanNamesWhereEachFileGoesSmallestFirst()
    {
        var plan = Installer().Plan(Profile, ManifestOf());

        Assert.Equal(new[] { "release_info.json", "Steamworks.NET.dll", "original/Sentry.Godot.dll", "original/Sentry.dll", "SmartFormat.ZString.dll", "SmartFormat.dll", "original/sts2.dll", "original/Slay the Spire 2.pck" },
            plan.Select(file => Path.GetRelativePath(Game, file.Path)));
        Assert.Equal(Hash("info"), plan[0].Sha256);
        Assert.Equal(Hash("dll"), plan[6].Sha256);
        Assert.Equal(Hash("pack"), plan[7].Sha256);
        // The profile pins no Sentry: Steam's hash alone checks it.
        Assert.Null(plan[3].Sha256);
    }

    [Fact]
    public void AnUntestedPlanIsCheckedBySteamsHashesAlone() =>
        Assert.All(Installer().Plan(null, ManifestOf()), file => Assert.Null(file.Sha256));

    [Fact]
    public void AManifestWithoutAFileEveryBuildNeedsIsRefused()
    {
        var refused = Assert.Throws<InvalidDataException>(() => Installer().Plan(null, ManifestOf(1000, "SmartFormat.dll")));
        Assert.Contains("SmartFormat.dll", refused.Message);
    }

    [Fact]
    public void StablesMissingReportingAssemblyIsAllowedUnlessTheProfilePinsIt()
    {
        var plan = Installer().Plan(null, ManifestOf(1000, "Sentry.Godot.dll"));
        Assert.Equal(7, plan.Count);
        Assert.DoesNotContain(plan, file => file.Path.EndsWith("Sentry.Godot.dll"));

        var pinning = Profile with { Inputs = new Dictionary<string, string> { ["sts2.dll"] = Hash("dll"), ["Sentry.Godot.dll"] = Hash("godot") } };
        Assert.Throws<InvalidDataException>(() => Installer().Plan(pinning, ManifestOf(1000, "Sentry.Godot.dll")));
    }
}

/// <summary>A real manifest from Steam's cache on this Mac. Skipped where there is none.</summary>
public sealed class CachedManifestFactAttribute : FactAttribute
{
    public CachedManifestFactAttribute(ulong manifest = 0)
    {
        if (!File.Exists(InstalledManifest.PathOf(manifest == 0 ? GameProfile.Current.Steam!.Manifest : manifest))) Skip = "No such Steam manifest on this Mac.";
    }
}

/// <summary>The real manifest of the tested version, from Steam's cache on this Mac, with the real pack. Skipped where there is none.</summary>
public sealed class InstalledManifestFactAttribute : FactAttribute
{
    public InstalledManifestFactAttribute()
    {
        if (!File.Exists(InstalledManifest.Path)) Skip = "No Steam manifest of the supported version on this Mac.";
    }
}

internal static class InstalledManifest
{
    /// <summary>A manifest of Steam's normal version (Stable) for a build this app is not tested with (build 23811903).</summary>
    public const ulong Stable = 8653035385353091849;
    private static readonly string Steam = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Application Support/Steam");
    public static string Path => PathOf(GameProfile.Current.Steam!.Manifest);
    public static string PathOf(ulong manifest) => System.IO.Path.Combine(Steam, "depotcache", $"{GameLayout.Depot}_{manifest}.manifest");
    public static string Game => System.IO.Path.Combine(Steam, "steamapps/common/Slay the Spire 2");
}

public sealed class RealManifestTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("real-manifest-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [InstalledManifestFact]
    public void TheProfilesFilesAreAllInTheManifestWithTheirInstalledSizes()
    {
        var manifest = DepotManifest.LoadFromFile(InstalledManifest.Path)!;
        Assert.Equal(GameProfile.Current.Steam!.Manifest, manifest.ManifestGID);
        var installer = new GameInstaller(_folder, GameProfile.Known, () => 0);
        var plan = installer.Plan(GameProfile.Current, manifest);
        Assert.Equal(8, plan.Count);
        Assert.All(plan, file => Assert.NotNull(file.Sha256));
        Assert.All(plan, file => Assert.NotNull(file.SteamSha1));
        foreach (var file in plan)
        {
            string installed = Path.Combine(InstalledManifest.Game, file.Source.FileName);
            Assert.Equal(new FileInfo(installed).Length, (long)file.Source.TotalSize);
        }
        Assert.InRange(GameInstaller.DownloadBytes(plan), 1_100_000_000, 1_300_000_000);
        // The estimate from the manifest covers what v0.111.0 was measured to need.
        Assert.InRange(GameInstaller.PeakDiskBytes(null, plan), GameProfile.Current.Steam.PeakDiskBytes, 5_100_000_000);
    }

    /// <summary>Steam's normal version has no profile and no <c>Sentry.Godot.dll</c>: its plan is Steam's hashes alone, with that file left out.</summary>
    [CachedManifestFact(InstalledManifest.Stable)]
    public void StablesPlanComesFromItsManifestByPath()
    {
        var manifest = DepotManifest.LoadFromFile(InstalledManifest.PathOf(InstalledManifest.Stable))!;
        var installer = new GameInstaller(_folder, GameProfile.Known, () => 0);
        Assert.False(installer.Check(new BranchBuild(GameInstaller.NormalBranch, 23811903, manifest.ManifestGID)).Tested);
        var plan = installer.Plan(null, manifest);
        Assert.Equal(7, plan.Count);
        Assert.DoesNotContain(plan, file => file.Path.EndsWith("Sentry.Godot.dll"));
        Assert.All(plan, file => Assert.Null(file.Sha256));
        Assert.All(plan, file => Assert.NotNull(file.SteamSha1));
        Assert.InRange(GameInstaller.PeakDiskBytes(null, plan), 4_600_000_000, 4_900_000_000);
    }

    [InstalledManifestFact]
    public async Task EveryFileOfTheRealGameCheckedAsAResumeIsFoundWhole()
    {
        var manifest = DepotManifest.LoadFromFile(InstalledManifest.Path)!;
        var installer = new GameInstaller(_folder, GameProfile.Known, () => 0);
        var plan = installer.Plan(GameProfile.Current, manifest);
        foreach (var file in plan)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
            // A clone: it takes no space and no time, and the installed game is only read.
            using var copy = Process.Start("cp", ["-c", Path.Combine(InstalledManifest.Game, file.Source.FileName), file.Path + DepotDownload.PartialSuffix]);
            await copy.WaitForExitAsync();
            Assert.Equal(0, copy.ExitCode);
        }
        // Both hashes are checked as each file is finished: Steam's SHA-1 and the profile's SHA-256.
        var result = await new DepotDownload(new NoServer(), plan).RunAsync(CancellationToken.None);
        Assert.Equal(0, result.DownloadedChunks);
        Assert.Equal(plan.Sum(file => file.Source.Chunks.Count), result.ReusedChunks);
        Assert.All(plan, file => Assert.True(File.Exists(file.Path)));
    }

    private sealed class NoServer : IDepotChunkSource
    {
        public Task<int> ReadAsync(DepotManifest.ChunkData chunk, byte[] destination, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Everything was already there.");
    }
}
