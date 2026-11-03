using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.GamePack;
using STS2Mobile.GamePatcher;
using STS2Mobile.Launcher;
using STS2Mobile.Platform;

namespace STS2Mobile.FullGame;

/// <summary>Makes the game's assembly and pack ready before anything loads them, off the main thread so the launcher keeps drawing.</summary>
internal static class GamePreparation
{
    private const string GameFolderPath = "user://game";
    /// <summary>The game's folder in the app's data folder.</summary>
    public static string GameFolderOnDisk => ProjectSettings.GlobalizePath(GameFolderPath);
    // Cecil walks nested types and IL recursively, and a secondary thread's default stack on iOS is small.
    private const int StackBytes = 16 * 1024 * 1024;
    private const long Megabyte = 1024 * 1024;
    // Stands in for a failed preparation, once per launch, to test the way back with nobody there.
    private static bool _failOnce = OS.GetEnvironment("STS2_PREPARE_FAILURE") == "1";

    /// <param name="textureProgress">Textures done and their total, called on the preparation's thread. When they are all done, the pack is written next.</param>
    public static Task<PreparationResult> RunAsync(Action<int, int> textureProgress)
    {
        string folder = GameFolderOnDisk;
        bool fail = _failOnce;
        _failOnce = false;
        var finished = new TaskCompletionSource<PreparationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                if (fail) throw new IOException("Test of a failed preparation.");
                finished.SetResult(Run(folder, textureProgress));
            }
            catch (Exception error) { finished.SetException(error); }
        }, StackBytes)
        { IsBackground = true, Name = "Game preparation" }.Start();
        return finished.Task;
    }

    /// <returns>The assembly's result if it stopped there, otherwise the pack's: Prepared means the player has to relaunch.</returns>
    private static PreparationResult Run(string folder, Action<int, int> textureProgress)
    {
        // A download under way: what is there so far is not a game, and the player carries on or starts again.
        if (File.Exists(folder + GameInstaller.RecordSuffix)) return new PreparationResult(PreparationStatus.NoGame);
        // Files copied in, or kept from before this existed. A game has no business in the player's iCloud backup.
        if (Directory.Exists(folder)) DeviceStorage.ExcludeFromBackup(folder);
        // Test hook: a zip copied into Documents exercises the same importer as the Files picker. Whoever sets it has
        // answered the code question already.
        string archive = OS.GetEnvironment("STS2_IMPORT_MOD");
        if (archive.Length > 0)
        {
            if (Path.GetFileName(archive) != archive || !archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return new PreparationResult(PreparationStatus.Refused, "The test import must name a zip in Documents.");
            string documents = Path.GetDirectoryName(folder)!;
            var imported = ModPackageImport.Install(Path.Combine(documents, archive), Path.Combine(documents, ModPackageImport.FolderName),
                replaceConfirmed: false, codeConfirmed: true);
            GD.Print($"Mod import {imported.Status}: {imported.Message}");
            if (imported.Status != ModImportStatus.Installed)
                return new PreparationResult(PreparationStatus.Refused, imported.Message);
        }
        var assembly = Measured("assembly", GameAssemblyPreparation.ForApp(folder, includeImportedMods: ModLaunchSettings.Enabled).Run);
        if (assembly.Status is PreparationStatus.NoGame or PreparationStatus.Refused) return assembly;
        LogPatches(assembly);
        if (assembly.Patches is { } patches) InstalledBuild.RecordPreparation(folder, assembly.Tested, patches);
        // The pack follows the assembly: tested against the same profile, or untested with it.
        var installed = InstalledBuild.Read(folder);
        var profile = assembly.Tested ? GameProfile.ForFolder(folder, installed) : null;
        var options = TextureConversionOptions.Parse(OS.GetEnvironment("STS2_TEXTURE_OPTIONS"));
        return Measured("pack", GamePackPreparation.ForApp(folder, profile, installed?.Version ?? "unknown", options, Watched(textureProgress), Report).Run);
    }

    /// <summary>For an untested build, what each patch did: the report the version screen also shows.</summary>
    private static void LogPatches(PreparationResult assembly)
    {
        if (assembly.Tested || assembly.Patches == null) return;
        int applied = assembly.Patches.Count(patch => patch.Applied);
        GD.Print($"Untested build: {applied} of {assembly.Patches.Count} patches applied.");
        foreach (var patch in assembly.Patches.Where(patch => !patch.Applied || patch.Note != null))
            GD.Print($"Patch {(patch.Applied ? "applied with a difference" : "missing")}: {patch.Name}{(patch.Required ? " (required)" : "")}: {patch.Note}");
    }

    /// <summary>A line about every tenth of the textures, with what heat and memory are doing at that point.</summary>
    private static Action<int, int> Watched(Action<int, int> progress)
    {
        var clock = Stopwatch.StartNew();
        int reported = -1;
        return (done, total) =>
        {
            progress(done, total);
            int tenth = done * 10 / total;
            if (tenth == reported) return;
            reported = tenth;
            var (footprint, peak) = MemoryFootprint.Read();
            GD.Print($"Textures: {done} of {total} after {clock.ElapsedMilliseconds} ms, at {DateTime.Now:HH:mm:ss}. Footprint {footprint / Megabyte} MB, peak {peak / Megabyte} MB, thermal state {DevicePower.ThermalState()}.");
        };
    }

    private static void Report(TextureConversionResult result)
    {
        var (footprint, peak) = MemoryFootprint.Read();
        GD.Print($"Textures: {(result.AlreadyDone ? "already done" : "converted")}, {result.Total} in the pack, {result.Resumed} from an earlier run. {(long)result.Elapsed.TotalMilliseconds} ms: "
            + $"reading {(long)result.Reading.TotalMilliseconds}, encoding {(long)result.Encoding.TotalMilliseconds}, writing {(long)result.Writing.TotalMilliseconds}. "
            + $"{result.OutputBytes / Megabyte} MB. Footprint {footprint / Megabyte} MB, peak {peak / Megabyte} MB, thermal state {DevicePower.ThermalState()}.");
    }

    private static PreparationResult Measured(string what, Func<PreparationResult> step)
    {
        var clock = Stopwatch.StartNew();
        var (before, _) = MemoryFootprint.Read();
        var result = step();
        // The game starts next, and patching leaves a few hundred megabytes of Cecil's objects behind.
        if (result.Status == PreparationStatus.Prepared) GC.Collect();
        var (after, peak) = MemoryFootprint.Read();
        GD.Print($"Game {what} preparation: {result.Status} in {clock.ElapsedMilliseconds} ms (work {(long)result.Elapsed.TotalMilliseconds} ms). "
            + $"Input {result.InputSha256 ?? "-"}, output {result.OutputSha256 ?? "-"}. "
            + $"Footprint {before / Megabyte} MB before, {after / Megabyte} MB after, peak {peak / Megabyte} MB.");
        return result;
    }
}
