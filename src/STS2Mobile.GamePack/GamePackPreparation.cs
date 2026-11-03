using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using STS2Mobile.GamePatcher;

namespace STS2Mobile.GamePack;

/// <summary>
/// Verifies the game's pack and makes the one the game mounts, once per input and app.
/// A crash at any point leaves a state the next launch repeats from: the pack is renamed into place when finished, the
/// settings made from an older pack are gone before it arrives, the record is written next, and only then do Steam's pack and
/// the converted textures go. With them gone the record and the output are the whole story, and only a download brings them back.
/// </summary>
/// <param name="packSha256">The tested version's pin for Steam's pack, or null for an untested build, whose pack Steam's hashes checked as it arrived: its own hash is then the pin.</param>
/// <param name="preparerId">Identifies what the steps make. See <see cref="StepsId"/>.</param>
/// <param name="convert">Makes the converted textures from the game's pack and its pin, resuming if it can, and returns the file.</param>
/// <param name="build">Makes the pack: the game's pack, the texture pack, the pin and the new file.</param>
public sealed class GamePackPreparation(string gameFolder, string version, string? packSha256, string preparerId, Func<string, string, string> convert, Func<string, string, string, string, int> build)
{
    /// <summary>A tested version, whose pin the conversion already knows.</summary>
    public GamePackPreparation(string gameFolder, string version, string packSha256, string preparerId, Func<string, string> convert, Func<string, string, string, string, int> build)
        : this(gameFolder, version, packSha256, preparerId, (pack, _) => convert(pack), build)
    {
    }

    public const string GamePack = GameFolder.GamePack;
    public const string OutputName = "FullGame.pck";
    /// <summary>What the host writes from the mounted pack's settings (GameMount), which has to follow the pack.</summary>
    public const string SettingsName = "game-settings.godot";
    public const string RecordName = "pack-preparation.json";
    /// <summary>
    /// Change this only when the pack's bytes should change: a new one makes every player download the game again, because the
    /// inputs are gone. tests/GamePack.Tests pins the pack's hash, so a change in the bytes fails there until this is looked at.
    /// </summary>
    private const string Steps = "pack-steps-1";
    public const string StepsId = Steps + "/" + TextureConversion.Encoder;

    /// <summary>The app's preparation: the running code, and the profile of the build in the folder or null for an untested one.</summary>
    /// <param name="version">The game's version, for the player's messages.</param>
    /// <param name="progress">Textures done and their total, as they are converted.</param>
    /// <param name="converted">Called when the textures are ready, before the pack is written.</param>
    public static GamePackPreparation ForApp(string gameFolder, GameProfile? profile, string version, TextureConversionOptions? options = null, Action<int, int>? progress = null, Action<TextureConversionResult>? converted = null) => new(
        gameFolder, profile?.Version ?? version, profile?.PackSha256, StepsId,
        (pack, pin) =>
        {
            // An untested build keeps the tested versions' textures that it has in their desktop format too: the same art, the same quality bar.
            var kept = profile?.TexturesKeptOriginal ?? GameProfile.Known.SelectMany(known => known.TexturesKeptOriginal).Distinct(StringComparer.Ordinal).ToList();
            var result = new TextureConversion(gameFolder, pin, kept, options, progress, keptMustExist: profile != null).Run(pack);
            converted?.Invoke(result);
            return Path.Combine(gameFolder, TextureConversion.OutputName);
        },
        GamePackBuilder.Build);

    public PreparationResult Run()
    {
        string pack = Path.Combine(gameFolder, GameFolder.Original, GamePack);
        string output = Path.Combine(gameFolder, OutputName);
        string recordPath = Path.Combine(gameFolder, RecordName);
        var earlier = ReadRecord(recordPath);
        // Hashing 2 GB takes seconds, so a repeat launch trusts the files' size and time, as they were when they were hashed.
        // Steam's pack is normally gone by then. One that is there and has changed is new input.
        // An untested build has no pin to match, and the marks say the same pack made the output.
        if (earlier is { } record && (packSha256 == null || record.Input == packSha256) && record.Preparer == preparerId
            && File.Exists(output) && record.Output == FileMark.Of(output) && (!File.Exists(pack) || record.Pack == FileMark.Of(pack)))
        {
            DeleteInputs(pack);
            return new PreparationResult(PreparationStatus.Cached, null, record.Input, record.OutputSha256, Tested: packSha256 != null);
        }
        if (!File.Exists(pack))
        {
            return new PreparationResult(PreparationStatus.Refused, earlier == null
                ? $"The game files are incomplete: {GamePack} is missing."
                : "This version of the app needs the game's files again, because the game's pack on this iPhone is damaged or was made differently.");
        }

        string pin = Sha256Of(pack);
        if (packSha256 != null && pin != packSha256) return new PreparationResult(PreparationStatus.Refused, $"{GamePack} is not the file of game version {version}, the version this app has tested.");

        var clock = Stopwatch.StartNew();
        // Outside the try below: a failure here keeps what was converted, for the next launch to carry on from.
        string textures = convert(pack, pin);
        string partial = output + ".partial";
        File.Delete(partial);
        string outputSha256;
        try
        {
            build(pack, textures, pin, partial);
            outputSha256 = Sha256Of(partial);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
        clock.Stop();
        // No record while the files change, so a crash here is seen as no result.
        File.Delete(recordPath);
        File.Delete(Path.Combine(gameFolder, SettingsName));
        File.Move(partial, output, overwrite: true);
        WriteRecord(recordPath, pin, pack, output, outputSha256, clock.Elapsed);
        DeleteInputs(pack);
        return new PreparationResult(PreparationStatus.Prepared, null, pin, outputSha256, clock.Elapsed, Tested: packSha256 != null);
    }

    /// <summary>Last, so an interrupted preparation still has its inputs. The assemblies stay: re-patching needs no download.</summary>
    private void DeleteInputs(string pack)
    {
        File.Delete(pack);
        File.Delete(Path.Combine(gameFolder, TextureConversion.OutputName));
        File.Delete(Path.Combine(gameFolder, TextureConversion.JournalName));
    }

    private readonly record struct FileMark(long Bytes, long Modified)
    {
        public static FileMark Of(string path)
        {
            var info = new FileInfo(path);
            return new FileMark(info.Length, info.LastWriteTimeUtc.Ticks);
        }

        public JsonObject ToJson() => new() { ["bytes"] = Bytes, ["modified"] = Modified };

        public static FileMark Read(JsonElement element) => new(element.GetProperty("bytes").GetInt64(), element.GetProperty("modified").GetInt64());
    }

    private sealed record Record(string? Input, string? Preparer, FileMark Pack, FileMark Output, string? OutputSha256);

    private void WriteRecord(string path, string input, string pack, string output, string outputSha256, TimeSpan elapsed)
    {
        var record = new JsonObject
        {
            ["version"] = version,
            ["input"] = input,
            ["preparer"] = preparerId,
            ["pack"] = FileMark.Of(pack).ToJson(),
            ["output"] = FileMark.Of(output).ToJson(),
            ["outputSha256"] = outputSha256,
            ["milliseconds"] = (long)elapsed.TotalMilliseconds,
        };
        string partial = path + ".partial";
        File.WriteAllText(partial, record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(partial, path, overwrite: true);
    }

    private static Record? ReadRecord(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return new Record(root.GetProperty("input").GetString(), root.GetProperty("preparer").GetString(), FileMark.Read(root.GetProperty("pack")),
                FileMark.Read(root.GetProperty("output")), root.GetProperty("outputSha256").GetString());
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string Sha256Of(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, PackFormat.CopyBufferBytes);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
