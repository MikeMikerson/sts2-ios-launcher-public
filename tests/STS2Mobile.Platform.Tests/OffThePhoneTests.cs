namespace STS2Mobile.Platform.Tests;

public class OffThePhoneTests
{
    [Fact]
    public void PowerReadingsAreUnknown()
    {
        Assert.Equal(DevicePower.UnknownThermalState, DevicePower.ThermalState());
        Assert.Equal(1, DevicePower.RenderScale());
        Assert.Equal(DevicePower.UnknownBatteryPercent, DevicePower.BatteryPercent());
        Assert.Equal("unknown", DevicePower.BatteryState());
        Assert.Equal((-1d, -1d), DevicePower.TaskUsage());
    }

    [Fact]
    public void FilesPickerIsUnavailableAndReportsNoFile()
    {
        Assert.False(FilesPicker.Available);
        Assert.False(FilesPicker.Open());
        Assert.Equal(FilesPickerResult.Failed, FilesPicker.Poll(out string? path));
        Assert.Null(path);
    }

    [Fact]
    public void ModImportRequestReachesTheBoundHostOnly()
    {
        ModImportRequests.Request();
        int presses = 0;
        ModImportRequests.Bind(() => presses++);
        ModImportRequests.Request();
        Assert.Equal(1, presses);
    }
}
