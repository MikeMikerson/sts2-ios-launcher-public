using System.Security.Cryptography;
using STS2Mobile.Steam.Cloud;

namespace STS2Mobile.Steam.Tests;

public class CloudSaveDownloaderTests
{
    private static readonly byte[] First = "one"u8.ToArray();
    private static readonly byte[] Second = "second"u8.ToArray();

    private readonly List<CloudFileInfo> _listed =
    [
        new("profile.save", First.Length, DateTimeOffset.FromUnixTimeSeconds(10), Sha1Hex(First)),
        new("profile1/saves/progress.save", Second.Length, DateTimeOffset.FromUnixTimeSeconds(20), Sha1Hex(Second).ToUpperInvariant()),
    ];

    private readonly Dictionary<string, byte[]> _stored = new()
    {
        ["profile.save"] = First,
        ["profile1/saves/progress.save"] = Second,
    };

    [Fact]
    public async Task KeepsListedOrderContentAndTime()
    {
        var downloaded = await Downloader((name, _) => Task.FromResult(_stored[name])).DownloadAsync(_listed, null, CancellationToken.None);

        Assert.Equal(_listed.Select(file => file.Name), downloaded.Select(file => file.Name));
        Assert.Equal(Second, downloaded[1].Content);
        Assert.Equal(Sha1Hex(Second), downloaded[1].Sha1Hex);
        Assert.Equal(_listed[1].Timestamp, downloaded[1].Timestamp);
    }

    [Fact]
    public async Task ReportsFinishedFiles()
    {
        var reports = new List<int>();
        await Downloader((name, _) => Task.FromResult(_stored[name])).DownloadAsync(_listed, new RecordingProgress(reports), CancellationToken.None);
        Assert.Equal([1, 2], reports.Order());
    }

    [Fact]
    public async Task ContentDifferentFromTheListingFailsTheDownload()
    {
        _stored["profile1/saves/progress.save"] = "tampered"u8.ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Downloader((name, _) => Task.FromResult(_stored[name])).DownloadAsync(_listed, null, CancellationToken.None));
    }

    [Fact]
    public async Task OneFailedFileFailsTheDownload()
    {
        var downloader = Downloader((name, _) => name == "profile1/saves/progress.save"
            ? Task.FromException<byte[]>(new HttpRequestException("storage refused"))
            : Task.FromResult(First));
        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(_listed, null, CancellationToken.None));
    }

    [Fact]
    public async Task NoFileIsRequestedAfterOneFails()
    {
        var files = Enumerable.Range(0, 40)
            .Select(i => new CloudFileInfo($"profile1/saves/history/{i}.run", First.Length, DateTimeOffset.UnixEpoch, Sha1Hex(First)))
            .ToList();
        var failing = new TaskCompletionSource<byte[]>();
        bool failed = false;
        int requestedAfterFailure = 0;
        var downloader = Downloader((name, cancellationToken) =>
        {
            if (failed) requestedAfterFailure++;
            // The other downloads stay in flight until the failure cancels them.
            return name == files[0].Name ? failing.Task : new TaskCompletionSource<byte[]>().Task.WaitAsync(cancellationToken);
        });

        var download = downloader.DownloadAsync(files, null, CancellationToken.None);
        failed = true;
        failing.SetException(new HttpRequestException("storage refused"));

        await Assert.ThrowsAsync<HttpRequestException>(() => download);
        Assert.Equal(0, requestedAfterFailure);
    }

    private CloudSaveDownloader Downloader(Func<string, CancellationToken, Task<byte[]>> download) =>
        new(_ => Task.FromResult<IReadOnlyList<CloudFileInfo>>(_listed), download);

    private static string Sha1Hex(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));

    private sealed class RecordingProgress(List<int> reports) : IProgress<int>
    {
        public void Report(int value)
        {
            lock (reports) reports.Add(value);
        }
    }
}
