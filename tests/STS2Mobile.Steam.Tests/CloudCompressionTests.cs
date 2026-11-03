using STS2Mobile.Steam.Cloud;

namespace STS2Mobile.Steam.Tests;

// A zipped cloud file unzips to at most the raw size Steam listed for it.
public sealed class CloudCompressionTests
{
    private static readonly byte[] Raw = [.. Enumerable.Range(0, 200_000).Select(i => (byte)(i % 7))];

    [Fact]
    public void FileOfItsListedSizeUnzips()
    {
        var (zipped, compressed) = CloudCompression.Compress(Raw);
        Assert.True(compressed);
        Assert.Equal(Raw, CloudCompression.Decompress(zipped, Raw.Length));
    }

    [Fact]
    public void FileLargerThanItsListedSizeIsRefused()
    {
        var (zipped, _) = CloudCompression.Compress(Raw);
        Assert.Throws<InvalidDataException>(() => CloudCompression.Decompress(zipped, Raw.Length - 1));
    }

    [Fact]
    public void WithNoListedSizeTheFixedCapApplies()
    {
        Assert.Equal(256L * 1024 * 1024, CloudCompression.DefaultMaxBytes);
        var (zipped, _) = CloudCompression.Compress(Raw);
        Assert.Equal(Raw, CloudCompression.Decompress(zipped));
    }
}
