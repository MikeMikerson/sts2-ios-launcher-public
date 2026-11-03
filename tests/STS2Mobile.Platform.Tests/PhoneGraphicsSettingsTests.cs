using System.Text.Json;

namespace STS2Mobile.Platform.Tests;

public sealed class PhoneGraphicsSettingsTests : IDisposable
{
    private static readonly PhoneGraphicsSettings Defaults = PhoneGraphicsSettings.Default;
    private readonly string _directory = Directory.CreateTempSubdirectory("sts2-phone-graphics-").FullName;
    private string SettingsFile => Path.Combine(_directory, PhoneGraphicsSettings.FileName);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly PhoneGraphicsSettings Off = Defaults.WithPreset(BatteryPreset.Off);

    [Fact]
    public void DefaultsAreTheMaximumPreset()
    {
        Assert.True(Defaults is { Resolution: RenderResolution.Low, LowerIdleFrameRate: true, CoolDownWhenHot: true, MsaaCleared: false, Preset: BatteryPreset.Maximum });
        Assert.Equal(2.0 / 3.0, Defaults.ResolutionFactor, 1e-9);
    }

    [Fact]
    public void OffKeepsTheGamesBehaviour()
    {
        Assert.Equal(BatteryPreset.Off, Off.Preset);
        Assert.Equal(1, Off.ResolutionFactor);
    }

    [Fact]
    public void BalancedPreset()
    {
        var balanced = Off.WithPreset(BatteryPreset.Balanced);
        Assert.True(balanced is { Resolution: RenderResolution.Reduced, LowerIdleFrameRate: false, CoolDownWhenHot: true, Preset: BatteryPreset.Balanced });
    }

    [Fact]
    public void MaximumPreset()
    {
        var maximum = Off.WithPreset(BatteryPreset.Maximum);
        Assert.True(maximum is { Resolution: RenderResolution.Low, LowerIdleFrameRate: true, CoolDownWhenHot: true, Preset: BatteryPreset.Maximum });
    }

    [Fact]
    public void OtherCombinationsAreCustom()
    {
        Assert.Equal(BatteryPreset.Custom, (Off with { LowerIdleFrameRate = true }).Preset);
        Assert.Equal(Defaults, Defaults.WithPreset(BatteryPreset.Custom));
    }

    [Fact]
    public void MissingFileGivesTheDefaults() => Assert.Equal(Defaults, PhoneGraphicsSettings.Load(SettingsFile));

    [Fact]
    public void SettingsSurviveSaveAndLoad()
    {
        var chosen = Defaults.WithPreset(BatteryPreset.Off) with { MsaaCleared = true };
        chosen.Save(SettingsFile);
        Assert.Equal(chosen, PhoneGraphicsSettings.Load(SettingsFile));
        Assert.False(File.Exists(SettingsFile + ".partial"));
    }

    [Fact]
    public void NativeReaderGetsAPlainRenderScale()
    {
        Defaults.WithPreset(BatteryPreset.Maximum).Save(SettingsFile);
        var saved = JsonDocument.Parse(File.ReadAllText(SettingsFile)).RootElement;
        Assert.Equal(0.6667, saved.GetProperty("renderScale").GetDouble(), 1e-9);
    }

    [Fact]
    public void UnreadableFileGivesTheDefaults()
    {
        File.WriteAllText(SettingsFile, "{ not json");
        Assert.Equal(Defaults, PhoneGraphicsSettings.Load(SettingsFile));
    }

    [Fact]
    public void WronglyTypedValuesFallBackOneByOne()
    {
        File.WriteAllText(SettingsFile, "{\"resolution\":\"reduced\",\"coolDownWhenHot\":\"yes\"}");
        Assert.True(PhoneGraphicsSettings.Load(SettingsFile) is { Resolution: RenderResolution.Reduced, CoolDownWhenHot: false });
    }
}
