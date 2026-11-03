using STS2Mobile.Steam.Cloud;

namespace STS2Mobile.Steam.Tests;

// A Send writes exactly what it is given as one batch, removes last, never removes run history, and says how far it got.
public sealed class CloudSaveUploaderTests
{
    private static readonly DateTimeOffset Changed = DateTimeOffset.FromUnixTimeSeconds(1_790_845_763);
    private readonly List<string> _calls = [];
    private readonly HashSet<string> _alreadyThere = new(StringComparer.Ordinal);
    private string? _failOn;

    private CloudSaveUploader Uploader() => new(
        (uploads, deletes, _) => { _calls.Add($"begin {string.Join(",", uploads)} | {string.Join(",", deletes)}"); return Task.FromResult(5UL); },
        (file, batch, _) =>
        {
            _calls.Add($"upload {file.Name} @{batch}");
            if (file.Name == _failOn) throw new HttpRequestException("storage refused");
            return Task.FromResult(!_alreadyThere.Contains(file.Name));
        },
        (name, batch, _) =>
        {
            _calls.Add($"delete {name} @{batch}");
            if (name == _failOn) throw new HttpRequestException("delete refused");
            return Task.CompletedTask;
        },
        (batch, succeeded) => { _calls.Add($"complete @{batch} {succeeded}"); return Task.CompletedTask; });

    private static CloudUpload Upload(string name) => new(name, [1, 2, 3], Changed);

    [Fact]
    public async Task WritesThenRemovesAsOneBatch()
    {
        var reports = new List<int>();
        var result = await Uploader().SendAsync([Upload("profile.save"), Upload("profile1/saves/progress.save")], ["profile1/saves/current_run.save"],
            new SynchronousProgress(reports), CancellationToken.None);

        Assert.Equal(new CloudSendResult(2, 0, 1), result);
        Assert.Equal([
            "begin profile.save,profile1/saves/progress.save | profile1/saves/current_run.save",
            "upload profile.save @5",
            "upload profile1/saves/progress.save @5",
            "delete profile1/saves/current_run.save @5",
            "complete @5 True"], _calls);
        Assert.Equal([1, 2, 3], reports);
    }

    [Fact]
    public async Task CountsFilesSteamAlreadyHad()
    {
        _alreadyThere.Add("profile.save");
        var result = await Uploader().SendAsync([Upload("profile.save"), Upload("profile1/saves/progress.save")], [], null, CancellationToken.None);
        Assert.Equal(new CloudSendResult(1, 1, 0), result);
    }

    [Theory]
    [InlineData("profile1/saves/history/1790845763.run")]
    [InlineData("profile2/saves/History/2.run")]
    [InlineData(@"profile1\saves\history\3.run")]
    public async Task NeverRemovesRunHistory(string history)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Uploader().SendAsync([Upload("profile.save")], [history], null, CancellationToken.None));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task RefusesAnEmptySendOrADoubledName()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Uploader().SendAsync([], [], null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => Uploader().SendAsync([Upload("profile.save")], ["profile.save"], null, CancellationToken.None));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task StopRemovesNothingAndSaysHowFarItGot()
    {
        _failOn = "profile1/saves/progress.save";
        var error = await Assert.ThrowsAsync<CloudSendException>(() => Uploader().SendAsync(
            [Upload("profile.save"), Upload("profile1/saves/progress.save"), Upload("profile1/saves/prefs.save")], ["profile1/saves/current_run.save"],
            null, CancellationToken.None));

        Assert.Equal(new CloudSendResult(1, 0, 0), error.Done);
        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Equal([
            "begin profile.save,profile1/saves/progress.save,profile1/saves/prefs.save | profile1/saves/current_run.save",
            "upload profile.save @5",
            "upload profile1/saves/progress.save @5",
            "complete @5 False"], _calls);
    }

    [Fact]
    public async Task CancelledSendEndsTheBatchAsFailed()
    {
        using var cancel = new CancellationTokenSource();
        var uploader = new CloudSaveUploader(
            (_, _, _) => Task.FromResult(5UL),
            (_, _, _) => { cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); return Task.FromResult(true); },
            (_, _, _) => Task.CompletedTask,
            (batch, succeeded) => { _calls.Add($"complete @{batch} {succeeded}"); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uploader.SendAsync([Upload("profile.save")], [], null, cancel.Token));
        Assert.Equal(["complete @5 False"], _calls);
    }

    [Fact]
    public void RunHistoryIsTheHistoryFolderOnly()
    {
        Assert.True(CloudSaveUploader.IsRunHistory("profile1/saves/history/1.run"));
        Assert.False(CloudSaveUploader.IsRunHistory("profile1/saves/current_run.save"));
        Assert.False(CloudSaveUploader.IsRunHistory("history"));
    }

    private sealed class SynchronousProgress(List<int> reports) : IProgress<int>
    {
        public void Report(int value) => reports.Add(value);
    }
}
