using System.Security.Cryptography;
using System.Text;
using STS2Mobile.GamePack;
using STS2Mobile.GamePatcher;

namespace STS2Mobile.GamePack.Tests;

/// <summary>Verifying, caching and swapping the pack in, with a stand-in for building it.</summary>
public sealed class PreparationTests : IDisposable
{
    private const string Preparer = "preparer-1";
    private readonly string _game = Directory.CreateTempSubdirectory("pack-preparation-").FullName;
    private int _builds;
    private int _conversions;

    public PreparationTests() => Directory.CreateDirectory(Path.Combine(_game, GameFolder.Original));

    public void Dispose() => Directory.Delete(_game, recursive: true);

    private string InOriginal(string name) => Path.Combine(_game, GameFolder.Original, name);

    private string InGame(string name) => Path.Combine(_game, name);

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private void WriteInputs()
    {
        File.WriteAllText(InOriginal(GamePackPreparation.GamePack), "steam pack");
    }

    private PreparationResult Run(string preparer = Preparer, string output = "made", Action? during = null, Action? converting = null) =>
        new GamePackPreparation(_game, "1.2.3", Sha("steam pack"), preparer, pack =>
        {
            _conversions++;
            Assert.Equal(InOriginal(GamePackPreparation.GamePack), pack);
            converting?.Invoke();
            return InGame("converted");
        }, (pack, textures, pin, partial) =>
        {
            _builds++;
            Assert.Equal(InOriginal(GamePackPreparation.GamePack), pack);
            Assert.Equal(InGame("converted"), textures);
            Assert.Equal(Sha("steam pack"), pin);
            File.WriteAllText(partial, output);
            during?.Invoke();
            return 1;
        }).Run();

    /// <summary>An untested build: no pin, so the pack's own hash is the one the conversion and the build get.</summary>
    private PreparationResult RunUntested() =>
        new GamePackPreparation(_game, "0.99.0", null, Preparer, (pack, pin) =>
        {
            _conversions++;
            Assert.Equal(Sha("an untested pack"), pin);
            return InGame("converted");
        }, (pack, textures, pin, partial) =>
        {
            _builds++;
            Assert.Equal(Sha("an untested pack"), pin);
            File.WriteAllText(partial, "made untested");
            return 1;
        }).Run();

    [Fact]
    public void AnUntestedPackIsPinnedByItsOwnHashAndCached()
    {
        File.WriteAllText(InOriginal(GamePackPreparation.GamePack), "an untested pack");
        var result = RunUntested();
        Assert.Equal(PreparationStatus.Prepared, result.Status);
        Assert.False(result.Tested);
        Assert.Equal(Sha("an untested pack"), result.InputSha256);
        Assert.False(File.Exists(InOriginal(GamePackPreparation.GamePack)));
        var again = RunUntested();
        Assert.Equal(PreparationStatus.Cached, again.Status);
        Assert.Equal((1, 1), (_conversions, _builds));
    }

    [Fact]
    public void AMissingPackIsRefused()
    {
        var result = Run();
        Assert.Equal(PreparationStatus.Refused, result.Status);
        Assert.Contains(GamePackPreparation.GamePack, result.Message);
        Assert.Equal(0, _conversions);
        Assert.Equal(0, _builds);
    }

    [Fact]
    public void AWrongPackIsRefusedAndNothingChanges()
    {
        WriteInputs();
        File.WriteAllText(InOriginal(GamePackPreparation.GamePack), "another version");
        File.WriteAllText(InGame(GamePackPreparation.OutputName), "earlier");
        File.WriteAllText(InGame(GamePackPreparation.SettingsName), "settings");
        var result = Run();
        Assert.Equal(PreparationStatus.Refused, result.Status);
        Assert.Contains("1.2.3", result.Message);
        Assert.Equal(0, _conversions);
        Assert.Equal(0, _builds);
        Assert.Equal("earlier", File.ReadAllText(InGame(GamePackPreparation.OutputName)));
        Assert.True(File.Exists(InGame(GamePackPreparation.SettingsName)));
    }

    [Fact]
    public void FirstRunPutsThePackInPlaceAndDeletesTheOldSettings()
    {
        WriteInputs();
        File.WriteAllText(InGame(GamePackPreparation.SettingsName), "from an older pack");
        var result = Run();
        Assert.Equal(PreparationStatus.Prepared, result.Status);
        Assert.Equal(Sha("made"), result.OutputSha256);
        Assert.Equal("made", File.ReadAllText(InGame(GamePackPreparation.OutputName)));
        Assert.False(File.Exists(InGame(GamePackPreparation.SettingsName)));
        Assert.False(File.Exists(InGame(GamePackPreparation.OutputName + ".partial")));
        Assert.True(File.Exists(InGame(GamePackPreparation.RecordName)));
    }

    [Fact]
    public void ARepeatLaunchDoesNoWorkAndKeepsTheSettings()
    {
        WriteInputs();
        Run();
        File.WriteAllText(InGame(GamePackPreparation.SettingsName), "settings");
        // Different content of the same size at the same time would pass: only size and time are checked on a repeat launch.
        var result = Run();
        Assert.Equal(PreparationStatus.Cached, result.Status);
        Assert.Equal(Sha("made"), result.OutputSha256);
        Assert.Equal(1, _conversions);
        Assert.Equal(1, _builds);
        Assert.True(File.Exists(InGame(GamePackPreparation.SettingsName)));
    }

    [Fact]
    public void ANewAppMakesANewPackFromInputsThatAreThere()
    {
        WriteInputs();
        Run();
        WriteInputs();
        Assert.Equal(PreparationStatus.Prepared, Run(preparer: "preparer-2", output: "other").Status);
        Assert.Equal("other", File.ReadAllText(InGame(GamePackPreparation.OutputName)));
    }

    [Fact]
    public void AChangedPackIsMadeAgainWhenTheInputsAreThere()
    {
        WriteInputs();
        Run();
        WriteInputs();
        File.AppendAllText(InGame(GamePackPreparation.OutputName), "more");
        Assert.Equal(PreparationStatus.Prepared, Run().Status);
        Assert.Equal(2, _builds);
    }

    [Fact]
    public void TheInputsGoOnceThePackIsRecordedAndTheAssembliesStay()
    {
        WriteInputs();
        File.WriteAllText(InOriginal("sts2.dll"), "assembly");
        File.WriteAllText(InGame(TextureConversion.OutputName), "textures");
        File.WriteAllText(InGame(TextureConversion.JournalName), "journal");
        Assert.Equal(PreparationStatus.Prepared, Run().Status);
        Assert.False(File.Exists(InOriginal(GamePackPreparation.GamePack)));
        Assert.False(File.Exists(InGame(TextureConversion.OutputName)));
        Assert.False(File.Exists(InGame(TextureConversion.JournalName)));
        Assert.True(File.Exists(InOriginal("sts2.dll")));
        Assert.True(File.Exists(InGame(GamePackPreparation.RecordName)));
    }

    [Fact]
    public void WithItsInputsGoneThePackIsCachedFromItsRecord()
    {
        WriteInputs();
        Run();
        var result = Run();
        Assert.Equal(PreparationStatus.Cached, result.Status);
        Assert.Equal(1, _builds);
        Assert.Equal(1, _conversions);
    }

    [Fact]
    public void ACrashBeforeTheInputsWentIsFinishedOnTheNextLaunch()
    {
        WriteInputs();
        var mark = File.GetLastWriteTimeUtc(InOriginal(GamePackPreparation.GamePack));
        Run();
        WriteInputs();
        File.SetLastWriteTimeUtc(InOriginal(GamePackPreparation.GamePack), mark);
        File.WriteAllText(InGame(TextureConversion.OutputName), "textures");
        Assert.Equal(PreparationStatus.Cached, Run().Status);
        Assert.False(File.Exists(InOriginal(GamePackPreparation.GamePack)));
        Assert.False(File.Exists(InGame(TextureConversion.OutputName)));
    }

    [Fact]
    public void ADamagedPackWithItsInputsGoneAsksForTheGameAgainAndTouchesNothing()
    {
        WriteInputs();
        Run();
        File.AppendAllText(InGame(GamePackPreparation.OutputName), "more");
        var result = Run();
        Assert.Equal(PreparationStatus.Refused, result.Status);
        Assert.Contains("needs the game's files again", result.Message);
        Assert.Equal(1, _builds);
        Assert.Equal("mademore", File.ReadAllText(InGame(GamePackPreparation.OutputName)));
    }

    [Fact]
    public void NewPackStepsWithTheInputsGoneAskForTheGameAgain()
    {
        WriteInputs();
        Run();
        var result = Run(preparer: "preparer-2");
        Assert.Equal(PreparationStatus.Refused, result.Status);
        Assert.Contains("needs the game's files again", result.Message);
        Assert.Equal(1, _builds);
    }

    [Fact]
    public void AppBuildsOfTheSameStepsShareOnePack() =>
        Assert.StartsWith("pack-steps-", GamePackPreparation.StepsId);

    [Fact]
    public void AFailedConversionLeavesTheEarlierResultAlone()
    {
        WriteInputs();
        Run();
        WriteInputs();
        File.WriteAllText(InGame(GamePackPreparation.SettingsName), "settings");
        Assert.Throws<PackException>(() => Run(preparer: "preparer-2", output: "half", converting: () => throw new PackException("no memory")));
        Assert.Equal(1, _builds);
        Assert.Equal("made", File.ReadAllText(InGame(GamePackPreparation.OutputName)));
        Assert.True(File.Exists(InGame(GamePackPreparation.SettingsName)));
        File.Delete(InOriginal(GamePackPreparation.GamePack));
        Assert.Equal(PreparationStatus.Cached, Run().Status);
    }

    [Fact]
    public void AMissingPackIsMadeAgain()
    {
        WriteInputs();
        Run();
        WriteInputs();
        File.Delete(InGame(GamePackPreparation.OutputName));
        Assert.Equal(PreparationStatus.Prepared, Run().Status);
    }

    [Fact]
    public void AChangedSteamPackIsVerifiedAgainAndRefused()
    {
        WriteInputs();
        Run();
        File.WriteAllText(InOriginal(GamePackPreparation.GamePack), "steam pack, and more");
        Assert.Equal(PreparationStatus.Refused, Run().Status);
    }

    [Fact]
    public void AFailedBuildLeavesTheEarlierResultAlone()
    {
        WriteInputs();
        Run();
        WriteInputs();
        File.WriteAllText(InGame(GamePackPreparation.SettingsName), "settings");
        Assert.Throws<IOException>(() => Run(preparer: "preparer-2", output: "half", during: () => throw new IOException("disk full")));
        Assert.Equal("made", File.ReadAllText(InGame(GamePackPreparation.OutputName)));
        Assert.True(File.Exists(InGame(GamePackPreparation.SettingsName)));
        Assert.False(File.Exists(InGame(GamePackPreparation.OutputName + ".partial")));
        File.Delete(InOriginal(GamePackPreparation.GamePack));
        Assert.Equal(PreparationStatus.Cached, Run().Status);
    }

    [Fact]
    public void AnInterruptedRunIsRepeated()
    {
        WriteInputs();
        File.WriteAllText(InGame(GamePackPreparation.OutputName + ".partial"), "left by a crash");
        Assert.Equal(PreparationStatus.Prepared, Run().Status);
        Assert.Equal("made", File.ReadAllText(InGame(GamePackPreparation.OutputName)));
    }

    [Fact]
    public void ADamagedRecordIsTreatedAsNone()
    {
        WriteInputs();
        Run();
        WriteInputs();
        File.WriteAllText(InGame(GamePackPreparation.RecordName), "{ not json");
        Assert.Equal(PreparationStatus.Prepared, Run().Status);
    }

    [Fact]
    public void TheProfileNamesTheSupportedPack() =>
        Assert.Matches("^[0-9a-f]{64}$", GameProfile.Current.PackSha256);
}
