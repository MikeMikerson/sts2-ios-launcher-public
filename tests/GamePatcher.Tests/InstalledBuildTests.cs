using STS2Mobile.GamePatcher;

namespace STS2Mobile.GamePatcher.Tests;

/// <summary><c>installed.json</c>, older installs without it, and the profiles as a list.</summary>
public sealed class InstalledBuildTests : IDisposable
{
    private readonly string _game = Directory.CreateTempSubdirectory("installed-build-").FullName;

    public void Dispose() => Directory.Delete(_game, recursive: true);

    [Fact]
    public void TheRecordReadsBackAsItWasWritten()
    {
        var patches = new[] { new PatchOutcome("No mouse cursor", false, false, "not there"), new PatchOutcome("No LAN play", true, true) };
        var written = new InstalledBuild(InstalledBuild.NormalBranch, 8653035385353091849, 23811903, "0.103.2", Tested: false, patches);
        written.Write(_game);
        var read = InstalledBuild.Read(_game)!;
        Assert.Equal(written with { Patches = null }, read with { Patches = null });
        Assert.Equal(patches, read.Patches);
        Assert.Equal("No mouse cursor", Assert.Single(read.Skipped).Name);
        Assert.False(File.Exists(Path.Combine(_game, InstalledBuild.FileName + ".partial")));
    }

    [Fact]
    public void NothingInTheFolderIsNoRecord() => Assert.Null(InstalledBuild.Read(_game));

    [Fact]
    public void AnOlderUntestedGameIsKnownByItsReleaseInformation()
    {
        File.WriteAllText(Path.Combine(_game, "release_info.json"), """{"commit":"abc","version":"v0.99.1","branch":"v0.99.1"}""");
        var installed = InstalledBuild.Read(_game)!;
        Assert.Equal("0.99.1", installed.Version);
        Assert.False(installed.Tested);
        Assert.Equal("", installed.Branch);
        Assert.Equal("manifest-0", installed.WarmupId);
    }

    [Fact]
    public void AnUnreadableRecordFallsBackToTheReleaseInformation()
    {
        File.WriteAllText(Path.Combine(_game, InstalledBuild.FileName), "{ not json");
        File.WriteAllText(Path.Combine(_game, "release_info.json"), """{"version":"v1.0.0"}""");
        Assert.Equal("1.0.0", InstalledBuild.Read(_game)!.Version);
    }

    [InstalledGameFact]
    public void AnOlderInstallOfTheTestedVersionIsTheBetaAndKeepsItsWarmup()
    {
        File.Copy(Path.Combine(Path.GetDirectoryName(InstalledGame.Data!)!, "release_info.json"), Path.Combine(_game, "release_info.json"));
        var installed = InstalledBuild.Read(_game)!;
        Assert.Equal(new InstalledBuild(InstalledBuild.BetaBranch, GameProfile.Current.Steam!.Manifest, 0, "0.113.0", Tested: true), installed);
        // The stamp the app used before profiles were a list, so an install that has warmed up does not do it again.
        Assert.Equal("v0.113.0-full-game-integration-v1", installed.WarmupId);
    }

    [Fact]
    public void AnUntestedBuildWarmsUpByItsManifest() =>
        Assert.Equal("manifest-42", new InstalledBuild(InstalledBuild.NormalBranch, 42, 1, "0.1", Tested: false).WarmupId);

    [Fact]
    public void PreparationRecordsHowThePatchesWent()
    {
        new InstalledBuild(InstalledBuild.NormalBranch, 42, 1, "0.1", Tested: false).Write(_game);
        var patches = new[] { new PatchOutcome("No mouse cursor", false, false, "gone") };
        InstalledBuild.RecordPreparation(_game, tested: false, patches);
        Assert.Equal(patches, InstalledBuild.Read(_game)!.Patches);
        var written = File.GetLastWriteTimeUtc(Path.Combine(_game, InstalledBuild.FileName));
        InstalledBuild.RecordPreparation(_game, tested: false, patches);
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(_game, InstalledBuild.FileName)));
    }

    [Fact]
    public void PreparationWritesNothingWhereNothingSaysWhatTheGameIs()
    {
        InstalledBuild.RecordPreparation(_game, tested: false, []);
        Assert.False(File.Exists(Path.Combine(_game, InstalledBuild.FileName)));
    }

    [Fact]
    public void TheProfilesAreAListWithTheNewestFirst()
    {
        Assert.Same(GameProfile.Current, GameProfile.Known[0]);
        var steam = GameProfile.Current.Steam!;
        Assert.Same(GameProfile.Current, GameProfile.ForManifest(steam.Manifest));
        Assert.Same(GameProfile.Current, GameProfile.ForAssembly(GameProfile.Current.Inputs[GameProfile.GameAssembly]));
        Assert.Same(GameProfile.Current, GameProfile.ForReleaseInfo(steam.ReleaseInfoSha256));
        Assert.Null(GameProfile.ForManifest(8653035385353091849));
    }

    [Fact]
    public void ThePreviousVersionStaysTested()
    {
        // A phone still on v0.111.0 keeps playing it as tested after the app moves on to v0.113.0.
        var previous = Assert.Single(GameProfile.Known, profile => profile.Version == "0.111.0");
        Assert.NotSame(GameProfile.Current, previous);
        Assert.Same(previous, GameProfile.ForManifest(5045320271505434676));
        Assert.Same(previous, GameProfile.ForAssembly("9cb4f1ad8c9f284aa8fec3122ffd6d780bbf543d875c817abdd12ff63fbf12b4"));
        Assert.Same(previous, GameProfile.ForReleaseInfo("97c0cef5a032ff6ed0826c161f5b72778753e9645c3f6dfe9503b5c3d03a8530"));
        Assert.Equal("v0.111.0-full-game-integration-v1", previous.WarmupId);
        Assert.Equal([".godot/imported/orobas.png-7744e6bafdffb9ae994beeceb9e505ae.bptc.ctex"], previous.TexturesKeptOriginal);
    }

    [Fact]
    public void ThePinsFollowTheLayout()
    {
        var profile = GameProfile.Current;
        Assert.All(GameLayout.Files, file => Assert.NotNull(profile.Pin(file.GamePath)));
        Assert.Equal(profile.PackSha256, profile.Pin("original/Slay the Spire 2.pck"));
        Assert.Equal(profile.Inputs["Sentry.Godot.dll"], profile.Pin("original/Sentry.Godot.dll"));
        Assert.Equal(profile.Unchanged["SmartFormat.dll"], profile.Pin("SmartFormat.dll"));
        Assert.Equal(["original/Sentry.dll", "original/Sentry.Godot.dll"], GameLayout.Files.Where(file => file.Optional).Select(file => file.GamePath));
        Assert.Equal(["Steamworks.NET.dll", "SmartFormat.dll", "SmartFormat.ZString.dll"], GameLayout.UnchangedAssemblies);
    }
}
