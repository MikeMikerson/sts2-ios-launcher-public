using System.Security.Cryptography;
using System.Text;
using STS2Mobile.GamePatcher;

namespace STS2Mobile.GamePatcher.Tests;

/// <summary>Verifying, caching and swapping files in, with a stand-in for the patching.</summary>
public sealed class GameAssemblyPreparationTests : IDisposable
{
    private const string Patcher = "patcher-1";
    private readonly string _game = Directory.CreateTempSubdirectory("game-preparation-").FullName;
    private readonly GameProfile _profile;
    private int _builds;

    public GameAssemblyPreparationTests()
    {
        _profile = new GameProfile("1.2.3",
            new Dictionary<string, string> { ["sts2.dll"] = Sha("game"), ["Sentry.dll"] = Sha("reporting") },
            new Dictionary<string, string> { ["Steamworks.NET.dll"] = Sha("steamworks") });
        Directory.CreateDirectory(Original);
    }

    private string Original => Path.Combine(_game, GameFolder.Original);

    private string Output => Path.Combine(_game, "sts2.dll");

    private string RecordPath => Path.Combine(_game, GameAssemblyPreparation.RecordName);

    public void Dispose() => Directory.Delete(_game, recursive: true);

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private void WriteInputs()
    {
        File.WriteAllText(Path.Combine(Original, "sts2.dll"), "game");
        File.WriteAllText(Path.Combine(Original, "Sentry.dll"), "reporting");
        File.WriteAllText(Path.Combine(_game, "Steamworks.NET.dll"), "steamworks");
    }

    private PreparationResult Run(string patcher = Patcher, string output = "patched")
    {
        return new GameAssemblyPreparation(_game, _profile, patcher, (input, work) =>
        {
            _builds++;
            Assert.Equal(Original, input);
            Assert.Empty(Directory.GetFiles(work));
            File.WriteAllText(Path.Combine(work, "sts2.dll"), output);
            File.WriteAllText(Path.Combine(work, GameFolder.ModCatalog), "{}");
        }).Run();
    }

    [Fact]
    public void NoGameFilesIsNotAnError()
    {
        Assert.Equal(PreparationStatus.NoGame, Run().Status);
        Assert.Equal(0, _builds);
    }

    [Fact]
    public void FirstRunPutsTheOutputsInPlaceAndClearsItsWork()
    {
        WriteInputs();
        var result = Run();
        Assert.Equal(PreparationStatus.Prepared, result.Status);
        Assert.Equal(Sha("game"), result.InputSha256);
        Assert.Equal(Sha("patched"), result.OutputSha256);
        Assert.Equal("patched", File.ReadAllText(Output));
        Assert.True(File.Exists(Path.Combine(_game, GameFolder.ModCatalog)));
        Assert.Equal("game", File.ReadAllText(Path.Combine(Original, "sts2.dll")));
        Assert.True(File.Exists(RecordPath));
        Assert.Equal([Original], Directory.GetDirectories(_game));
    }

    [Fact]
    public void RepeatRunSkipsTheWork()
    {
        WriteInputs();
        Run();
        var result = Run();
        Assert.Equal(PreparationStatus.Cached, result.Status);
        Assert.Equal(Sha("patched"), result.OutputSha256);
        Assert.Equal(1, _builds);
    }

    [Fact]
    public void AnotherPatcherMakesANewResult()
    {
        WriteInputs();
        Run();
        Assert.Equal(PreparationStatus.Prepared, Run(patcher: "patcher-2", output: "patched again").Status);
        Assert.Equal("patched again", File.ReadAllText(Output));
        Assert.Equal(PreparationStatus.Cached, Run(patcher: "patcher-2").Status);
    }

    [Theory]
    [InlineData("sts2.dll")]
    [InlineData(GameFolder.ModCatalog)]
    public void DamagedOrMissingOutputIsMadeAgain(string name)
    {
        WriteInputs();
        Run();
        File.Delete(Path.Combine(_game, name));
        Assert.Equal(PreparationStatus.Prepared, Run().Status);
        File.WriteAllText(Path.Combine(_game, name), "damaged");
        Assert.Equal(PreparationStatus.Prepared, Run().Status);
        Assert.Equal(3, _builds);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    public void UnreadableRecordIsNoResult(string record)
    {
        WriteInputs();
        Run();
        File.WriteAllText(RecordPath, record);
        Assert.Equal(PreparationStatus.Prepared, Run().Status);
    }

    [Fact]
    public void TheGamesOtherFilesAreLeftAlone()
    {
        WriteInputs();
        File.WriteAllText(Path.Combine(_game, "FullGame.pck"), "kept");
        Run();
        Assert.Equal("kept", File.ReadAllText(Path.Combine(_game, "FullGame.pck")));
    }

    [Fact]
    public void WrongGameIsRefusedAndNothingChanges()
    {
        WriteInputs();
        File.WriteAllText(Path.Combine(Original, "sts2.dll"), "another build");
        File.WriteAllText(Output, "earlier");
        var result = Run();
        Assert.Equal(PreparationStatus.Refused, result.Status);
        Assert.Contains("sts2.dll", result.Message);
        Assert.Contains("1.2.3", result.Message);
        Assert.Equal(0, _builds);
        Assert.Equal("earlier", File.ReadAllText(Output));
    }

    [Theory]
    [InlineData("original/Sentry.dll")]
    [InlineData("Steamworks.NET.dll")]
    public void MissingFileIsRefused(string name)
    {
        WriteInputs();
        File.Delete(Path.Combine(_game, name));
        var result = Run();
        Assert.Equal(PreparationStatus.Refused, result.Status);
        Assert.Contains(Path.GetFileName(name), result.Message);
    }

    [Fact]
    public void DamagedUnchangedAssemblyIsRefusedEvenWithAResult()
    {
        WriteInputs();
        Run();
        File.WriteAllText(Path.Combine(_game, "Steamworks.NET.dll"), "damaged");
        Assert.Equal(PreparationStatus.Refused, Run().Status);
    }

    [Fact]
    public void AFailedBuildKeepsTheEarlierResultAndLeavesNoHalfFiles()
    {
        WriteInputs();
        Run();
        var failing = new GameAssemblyPreparation(_game, _profile, "patcher-2", (_, work) =>
        {
            File.WriteAllText(Path.Combine(work, "sts2.dll"), "half");
            throw new IOException("disk full");
        });
        Assert.Throws<IOException>(failing.Run);
        Assert.Equal("patched", File.ReadAllText(Output));
        Assert.Equal(PreparationStatus.Cached, Run().Status);
    }

    private GameAssemblyPreparation Untested(Func<IReadOnlyList<PatchOutcome>> patch) => new(_game, null, Patcher, (input, work) =>
    {
        _builds++;
        var outcomes = patch();
        File.WriteAllText(Path.Combine(work, "sts2.dll"), "patched untested");
        return outcomes;
    }, "0.99.0");

    [Fact]
    public void AnUntestedBuildNeedsOnlyItsFilesAndIsReportedSo()
    {
        File.WriteAllText(Path.Combine(Original, "sts2.dll"), "another build");
        File.WriteAllText(Path.Combine(_game, "Steamworks.NET.dll"), "x");
        File.WriteAllText(Path.Combine(_game, "SmartFormat.dll"), "x");
        File.WriteAllText(Path.Combine(_game, "SmartFormat.ZString.dll"), "x");
        PatchOutcome[] report = [new("No mouse cursor", false, false, "gone"), new("No LAN play", true, true)];
        var result = Untested(() => report).Run();
        Assert.Equal(PreparationStatus.Prepared, result.Status);
        Assert.False(result.Tested);
        Assert.Equal(Sha("another build"), result.InputSha256);
        Assert.Equal(report, result.Patches);

        var again = Untested(() => throw new InvalidOperationException("not called")).Run();
        Assert.Equal(PreparationStatus.Cached, again.Status);
        Assert.Equal(report, again.Patches);
        Assert.Equal(1, _builds);
        // Against a profile they do not match, the same files are refused.
        Assert.Equal(PreparationStatus.Refused, Run().Status);
    }

    [Fact]
    public void AnUntestedBuildWithoutAnAssemblyItLoadsIsRefused()
    {
        File.WriteAllText(Path.Combine(Original, "sts2.dll"), "another build");
        var result = Untested(() => []).Run();
        Assert.Equal(PreparationStatus.Refused, result.Status);
        Assert.Contains("Steamworks.NET.dll", result.Message);
        Assert.Equal(0, _builds);
    }

    [Fact]
    public void ARequiredPatchThatDoesNotFitRefusesAndChangesNothing()
    {
        File.WriteAllText(Path.Combine(Original, "sts2.dll"), "another build");
        foreach (string name in new[] { "Steamworks.NET.dll", "SmartFormat.dll", "SmartFormat.ZString.dll" }) File.WriteAllText(Path.Combine(_game, name), "x");
        File.WriteAllText(Output, "earlier");
        var result = Untested(() => throw new GamePatchRefusedException("No LAN play", "Godot.ENetConnection is not in this game version.")).Run();
        Assert.Equal(PreparationStatus.Refused, result.Status);
        Assert.Contains("0.99.0", result.Message);
        Assert.Contains("\"No LAN play\"", result.Message);
        Assert.Contains("Game version", result.Message);
        Assert.Equal("earlier", File.ReadAllText(Output));
        Assert.False(Directory.Exists(Path.Combine(_game, ".preparing")));
    }

    [Fact]
    public void ADiskFailureInAnUntestedBuildIsNotARefusal()
    {
        File.WriteAllText(Path.Combine(Original, "sts2.dll"), "another build");
        foreach (string name in new[] { "Steamworks.NET.dll", "SmartFormat.dll", "SmartFormat.ZString.dll" }) File.WriteAllText(Path.Combine(_game, name), "x");
        Assert.Throws<IOException>(() => Untested(() => throw new IOException("disk full")).Run());
    }

    [Fact]
    public void LeftoversFromAnInterruptedRunAreCleared()
    {
        WriteInputs();
        Directory.CreateDirectory(Path.Combine(_game, ".preparing"));
        File.WriteAllText(Path.Combine(_game, ".preparing", "sts2.dll"), "half");
        // The stand-in asserts its work folder starts empty.
        Assert.Equal(PreparationStatus.Prepared, Run().Status);
    }

    [Fact]
    public void ChangedModInputsRebuildBothVariantsAndCatalog()
    {
        WriteInputs();
        PreparationResult Prepare(string pins) => new GameAssemblyPreparation(_game, _profile, Patcher, (_, work) =>
        {
            _builds++;
            File.WriteAllText(Path.Combine(work, "sts2.dll"), "woven:" + pins);
            File.WriteAllText(Path.Combine(work, "sts2.unmodded.dll"), "plain");
            File.WriteAllText(Path.Combine(work, GameFolder.ModCatalog), pins);
            return [];
        }, modInputs: pins).Run();

        Assert.Equal(PreparationStatus.Prepared, Prepare("mod-v1").Status);
        Assert.Equal(PreparationStatus.Cached, Prepare("mod-v1").Status);
        Assert.Equal(PreparationStatus.Prepared, Prepare("mod-v2").Status);
        Assert.Equal("woven:mod-v2", File.ReadAllText(Output));
        Assert.Equal("plain", File.ReadAllText(Path.Combine(_game, "sts2.unmodded.dll")));
        Assert.Equal("mod-v2", File.ReadAllText(Path.Combine(_game, GameFolder.ModCatalog)));
        Assert.Equal(PreparationStatus.Prepared, Prepare("no-mods").Status);
        Assert.Equal(3, _builds);
    }

    [Fact]
    public void RefusedModLeavesPreparedOutputsUnchangedOnATestedGame()
    {
        WriteInputs();
        Run();
        var result = new GameAssemblyPreparation(_game, _profile, "new-patcher", (_, work) =>
        {
            File.WriteAllText(Path.Combine(work, "sts2.dll"), "incomplete");
            throw new WeaveException("Example: transpilers are not supported.");
        }).Run();
        Assert.Equal(PreparationStatus.Refused, result.Status);
        Assert.True(result.Tested);
        Assert.Contains("Example", result.Message);
        Assert.Contains("Mods off", result.Message);
        Assert.Equal("patched", File.ReadAllText(Output));
        Assert.False(Directory.Exists(Path.Combine(_game, ".preparing")));
        Assert.Equal(PreparationStatus.Cached, Run().Status);
    }
}
