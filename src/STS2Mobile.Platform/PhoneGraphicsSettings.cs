using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace STS2Mobile.Platform;

/// <summary>
/// The phone's own battery and heat choices. They live in the host's file, not in the game's
/// settings save, so the save keeps the desktop schema. The render resolution is also read by the
/// app's native code before the engine starts (<c>native/power_state.mm</c> reads
/// <c>renderScale</c>), so a change to it takes effect at the next launch.
/// </summary>
public sealed record PhoneGraphicsSettings(RenderResolution Resolution, bool LowerIdleFrameRate, bool CoolDownWhenHot, bool MsaaCleared)
{
    public const string FileName = "phone-graphics.json";
    private const int Format = 1;

    /// <summary>
    /// A new install's choices, the Maximum preset: Low resolution, 30 fps when idle and cooling down when hot.
    /// The first launch still draws at full resolution, since the file is written once the
    /// game has started and the native code reads it before the next launch's engine starts.
    /// </summary>
    public static readonly PhoneGraphicsSettings Default = new(RenderResolution.Low, true, true, false);

    /// <summary>Fraction of the screen's resolution drawn: 3x, 2.5x or 2x pixels per point on a 3x screen.</summary>
    public double ResolutionFactor => FactorOf(Resolution);

    public BatteryPreset Preset
    {
        get
        {
            foreach (BatteryPreset preset in NamedPresets)
                if (Matches(WithPreset(preset))) return preset;
            return BatteryPreset.Custom;
        }
    }

    private static readonly BatteryPreset[] NamedPresets = [BatteryPreset.Off, BatteryPreset.Balanced, BatteryPreset.Maximum];

    /// <summary>The same settings with the preset's choices; Custom changes nothing.</summary>
    public PhoneGraphicsSettings WithPreset(BatteryPreset preset) => preset switch
    {
        BatteryPreset.Off => this with { Resolution = RenderResolution.Full, LowerIdleFrameRate = false, CoolDownWhenHot = false },
        BatteryPreset.Balanced => this with { Resolution = RenderResolution.Reduced, LowerIdleFrameRate = false, CoolDownWhenHot = true },
        BatteryPreset.Maximum => this with { Resolution = RenderResolution.Low, LowerIdleFrameRate = true, CoolDownWhenHot = true },
        _ => this,
    };

    public static double FactorOf(RenderResolution resolution) => resolution switch
    {
        RenderResolution.Reduced => 5.0 / 6.0,
        RenderResolution.Low => 2.0 / 3.0,
        _ => 1,
    };

    /// <summary>Reads the file; a missing or unreadable file gives the defaults.</summary>
    public static PhoneGraphicsSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return Default;
            return new PhoneGraphicsSettings(
                Enum.TryParse(Text(root, "resolution"), ignoreCase: true, out RenderResolution resolution) ? resolution : RenderResolution.Full,
                Flag(root, "lowerIdleFrameRate"), Flag(root, "coolDownWhenHot"), Flag(root, "msaaCleared"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return Default;
        }
    }

    /// <summary>Writes the file in one step, so the native reader never sees half of it.</summary>
    public void Save(string path)
    {
        string partial = path + ".partial";
        using (var stream = File.Create(partial))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", Format);
            writer.WriteString("resolution", Resolution.ToString().ToLowerInvariant());
            // Read by native code before the engine starts; keep it a plain number.
            writer.WriteNumber("renderScale", Math.Round(ResolutionFactor, 4));
            writer.WriteBoolean("lowerIdleFrameRate", LowerIdleFrameRate);
            writer.WriteBoolean("coolDownWhenHot", CoolDownWhenHot);
            writer.WriteBoolean("msaaCleared", MsaaCleared);
            writer.WriteEndObject();
        }
        File.Move(partial, path, overwrite: true);
    }

    /// <summary>A one-line summary for the log.</summary>
    public string Describe(double appliedFactor) => string.Create(CultureInfo.InvariantCulture,
        $"{Preset.ToString().ToLowerInvariant()} resolution={Resolution.ToString().ToLowerInvariant()} applied={appliedFactor:0.###} idle30={LowerIdleFrameRate} cool={CoolDownWhenHot}");

    private bool Matches(PhoneGraphicsSettings other) =>
        Resolution == other.Resolution && LowerIdleFrameRate == other.LowerIdleFrameRate && CoolDownWhenHot == other.CoolDownWhenHot;

    private static string? Text(JsonObject root, string name) =>
        root[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static bool Flag(JsonObject root, string name) =>
        root[name] is JsonValue value && value.TryGetValue(out bool flag) && flag;
}
