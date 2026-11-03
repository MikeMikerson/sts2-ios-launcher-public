using System.Text;

namespace STS2Mobile.Platform.Tests;

public class GameSettingsTextTests
{
    private static byte[] Binary(params (string Key, string Value)[] settings)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("ECFG"u8.ToArray());
        writer.Write(settings.Length);
        foreach (var (key, value) in settings)
        {
            writer.Write(Encoding.UTF8.GetByteCount(key));
            writer.Write(Encoding.UTF8.GetBytes(key));
            writer.Write(Encoding.UTF8.GetByteCount(value));
            writer.Write(Encoding.UTF8.GetBytes(value));
        }
        return stream.ToArray();
    }

    private static string Write(byte[] game, byte[] host) =>
        GameSettingsText.Write(game, host, value => $"<{Encoding.UTF8.GetString(value)}>");

    [Fact]
    public void StartsWithTheCurrentConfigVersionAndKeepsTheGamesOrder()
    {
        var text = Write(Binary(("input/up", "a"), ("display/window/stretch/mode", "b")), Binary());
        Assert.Equal("config_version=5\n\ninput/up=<a>\ndisplay/window/stretch/mode=<b>\n", text);
    }

    [Fact]
    public void HostSettingsWin()
    {
        var text = Write(Binary(("application/run/main_scene", "game"), ("input/up", "a")), Binary(("application/run/main_scene", "host")));
        Assert.DoesNotContain("main_scene", text);
        Assert.Contains("input/up=<a>", text);
    }

    [Fact]
    public void ReportingSettingsAndCustomFeaturesAreDropped()
    {
        var text = Write(Binary(("sentry/config/dsn", "a"), ("autoload/SentryBootstrap", "b"), ("_custom_features", "c"), ("autoload/FmodManager", "d")), Binary());
        Assert.Equal("config_version=5\n\nautoload/FmodManager=<d>\n", text);
    }

    [Theory]
    [InlineData("with space")]
    [InlineData("a=b")]
    [InlineData("[section]")]
    public void RefusesANameTheParserWouldMisread(string key) =>
        Assert.Throws<InvalidDataException>(() => Write(Binary((key, "a")), Binary()));

    [Fact]
    public void RefusesAnotherFile() =>
        Assert.Throws<InvalidDataException>(() => GameSettingsText.Read("GDPC0000"u8.ToArray()));

    [Fact]
    public void RefusesATruncatedFile()
    {
        var data = Binary(("input/up", "value"));
        Assert.Throws<InvalidDataException>(() => GameSettingsText.Read(data[..^2]));
    }

    [Fact]
    public void RefusesTrailingBytes()
    {
        var data = Binary(("input/up", "value"));
        Assert.Throws<InvalidDataException>(() => GameSettingsText.Read([.. data, 0]));
    }
}
