using System.Diagnostics;
using System.Security.Cryptography;
using SteamKit2;
using SteamKit2.CDN;
using STS2Mobile.Steam.Depot;

namespace STS2Mobile.Steam.Tests;

/// <summary>A stand-in for Steam's content servers, over a known file cut into chunks.</summary>
internal sealed class FakeChunks(byte[] content, int chunkBytes)
{
    public List<ulong> Reads { get; } = [];
    public Func<DepotManifest.ChunkData, int, Exception?> FailWith { get; set; } = (_, _) => null;
    public Func<DepotManifest.ChunkData, byte[], bool> Corrupt { get; set; } = (_, _) => false;
    private readonly Dictionary<ulong, int> _attempts = [];

    public DepotManifest.FileData File()
    {
        var file = new DepotManifest.FileData { TotalSize = (ulong)content.Length, Chunks = [] };
        for (int offset = 0; offset < content.Length; offset += chunkBytes)
        {
            var bytes = content.AsSpan(offset, Math.Min(chunkBytes, content.Length - offset));
            file.Chunks.Add(new DepotManifest.ChunkData(SHA1.HashData(bytes), DepotChunk.AdlerHash(bytes), (ulong)offset, (uint)bytes.Length / 2, (uint)bytes.Length));
        }
        return file;
    }

    public IDepotChunkSource Source => new Reader(this);

    private sealed class Reader(FakeChunks owner) : IDepotChunkSource
    {
        public Task<int> ReadAsync(DepotManifest.ChunkData chunk, byte[] destination, CancellationToken cancellationToken)
        {
            lock (owner)
            {
                owner._attempts[chunk.Offset] = owner._attempts.GetValueOrDefault(chunk.Offset) + 1;
                owner.Reads.Add(chunk.Offset);
                if (owner.FailWith(chunk, owner._attempts[chunk.Offset]) is { } failure) throw failure;
            }
            owner.Content.AsSpan((int)chunk.Offset, (int)chunk.UncompressedLength).CopyTo(destination);
            owner.Corrupt(chunk, destination);
            return Task.FromResult((int)chunk.UncompressedLength);
        }
    }

    private byte[] Content => content;
}

public sealed class DepotDownloadTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("depot-download-").FullName;
    private readonly byte[] _content = RandomNumberGenerator.GetBytes(10_000);
    private readonly FakeChunks _chunks;
    private readonly List<TimeSpan> _waits = [];

    public DepotDownloadTests() => _chunks = new FakeChunks(_content, 1000);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Output => Path.Combine(_folder, "deep/er/file.bin");

    private string Partial => Output + DepotDownload.PartialSuffix;

    private DepotDownload Download(string? sha = null, Action<long, long>? progress = null) =>
        new(_chunks.Source, [new DepotFile(_chunks.File(), Output, sha ?? Convert.ToHexStringLower(SHA256.HashData(_content)))], progress, 3,
            (wait, _) => { lock (_waits) _waits.Add(wait); return Task.CompletedTask; });

    private void WriteHalfOfTheFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Output)!);
        var half = (byte[])_content.Clone();
        half.AsSpan(5000).Clear();
        File.WriteAllBytes(Partial, half);
    }

    [Fact]
    public async Task DownloadsTheFileAndRenamesItIntoPlace()
    {
        var result = await Download().RunAsync(CancellationToken.None);
        Assert.Equal(_content, File.ReadAllBytes(Output));
        Assert.False(File.Exists(Partial));
        Assert.Equal(new DepotDownloadResult(10_000, 10, 0, 0), result);
    }

    [Fact]
    public async Task ReportsProgressUpToTheWhole()
    {
        var seen = new List<long>();
        await Download(progress: (done, total) => { Assert.Equal(10_000, total); lock (seen) seen.Add(done); }).RunAsync(CancellationToken.None);
        Assert.Equal(10_000, seen.Max());
    }

    [Fact]
    public async Task ResumesFetchingOnlyWhatIsMissing()
    {
        WriteHalfOfTheFile();
        var result = await Download().RunAsync(CancellationToken.None);
        Assert.Equal(_content, File.ReadAllBytes(Output));
        Assert.Equal(5, result.ReusedChunks);
        Assert.All(_chunks.Reads, offset => Assert.True(offset >= 5000));
        Assert.Equal(5, _chunks.Reads.Count);
    }

    [Fact]
    public async Task AChunkHalfWrittenOrDamagedIsFetchedAgain()
    {
        WriteHalfOfTheFile();
        var partial = File.ReadAllBytes(Partial);
        partial[1500] ^= 0xFF;
        File.WriteAllBytes(Partial, partial);
        await Download().RunAsync(CancellationToken.None);
        Assert.Equal(_content, File.ReadAllBytes(Output));
        Assert.Contains(1000ul, _chunks.Reads);
        Assert.DoesNotContain(2000ul, _chunks.Reads);
    }

    [Fact]
    public async Task APartialFileShorterThanItsChunkIsNotTrusted()
    {
        WriteHalfOfTheFile();
        using (var stream = File.OpenWrite(Partial)) stream.SetLength(1500);
        var result = await Download().RunAsync(CancellationToken.None);
        Assert.Equal(_content, File.ReadAllBytes(Output));
        Assert.Equal(1, result.ReusedChunks);
    }

    [Fact]
    public async Task AFinishedFileIsNotFetchedAgain()
    {
        await Download().RunAsync(CancellationToken.None);
        _chunks.Reads.Clear();
        var result = await Download().RunAsync(CancellationToken.None);
        Assert.Empty(_chunks.Reads);
        Assert.Equal(1, result.ReusedFiles);
    }

    [Fact]
    public async Task AFailedChunkIsTriedAgainWithAGrowingWait()
    {
        _chunks.FailWith = (chunk, attempt) => chunk.Offset == 3000 && attempt < 3 ? new HttpRequestException("dropped") : null;
        await Download().RunAsync(CancellationToken.None);
        Assert.Equal(_content, File.ReadAllBytes(Output));
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], _waits);
    }

    [Fact]
    public async Task ADamagedChunkFromAServerIsTriedAgain()
    {
        int damaged = 0;
        _chunks.Corrupt = (chunk, bytes) =>
        {
            if (chunk.Offset != 3000 || Interlocked.Increment(ref damaged) > 1) return false;
            bytes[0] ^= 0xFF;
            return true;
        };
        await Download().RunAsync(CancellationToken.None);
        Assert.Equal(_content, File.ReadAllBytes(Output));
        Assert.Single(_waits);
    }

    [Fact]
    public async Task GivingUpKeepsWhatWasFetchedForTheNextRun()
    {
        _chunks.FailWith = (chunk, _) => chunk.Offset == 9000 ? new HttpRequestException("gone") : null;
        await Assert.ThrowsAsync<HttpRequestException>(() => Download().RunAsync(CancellationToken.None));
        Assert.False(File.Exists(Output));
        Assert.True(File.Exists(Partial));
        Assert.Equal(DepotDownload.ReadAttempts, _chunks.Reads.Count(offset => offset == 9000));

        _chunks.FailWith = (_, _) => null;
        _chunks.Reads.Clear();
        var result = await Download().RunAsync(CancellationToken.None);
        Assert.Equal(_content, File.ReadAllBytes(Output));
        Assert.Equal(9, result.ReusedChunks);
        Assert.Equal([9000ul], _chunks.Reads);
    }

    [Fact]
    public async Task CancellingStopsWithoutRetryingAndKeepsThePartialFile()
    {
        using var cancel = new CancellationTokenSource();
        _chunks.FailWith = (_, _) =>
        {
            cancel.Cancel();
            return new OperationCanceledException(cancel.Token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Download().RunAsync(cancel.Token));
        Assert.Empty(_waits);
        Assert.True(File.Exists(Partial));
    }

    [Fact]
    public async Task AFileWithTheWrongHashIsRefusedAndNotKept()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(sha: new string('0', 64)).RunAsync(CancellationToken.None));
        Assert.False(File.Exists(Output));
        Assert.False(File.Exists(Partial));
    }

    [Fact]
    public async Task AFinishedFileWithTheWrongHashIsReplaced()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Output)!);
        var wrong = (byte[])_content.Clone();
        wrong[0] ^= 0xFF;
        File.WriteAllBytes(Output, wrong);
        await Download().RunAsync(CancellationToken.None);
        Assert.Equal(_content, File.ReadAllBytes(Output));
    }

    [Fact]
    public void AServerThePolicyWouldRefuseIsNeverUsed()
    {
        Assert.True(SteamDepot.IsUsable(true, "cache1-tyo3.steamcontent.com", 443, false, false, [], 7));
        Assert.True(SteamDepot.IsUsable(true, "fastly.cdn.steampipe.steamcontent.com", 443, false, false, null, 7));
        Assert.False(SteamDepot.IsUsable(false, "alibaba.cdn.steampipe.steamcontent.com", 80, false, false, [], 7));
        Assert.False(SteamDepot.IsUsable(true, "steampipe.akamaized.net", 443, false, false, [], 7));
        Assert.False(SteamDepot.IsUsable(true, "cache1-tyo3.steamcontent.com", 8443, false, false, [], 7));
        Assert.False(SteamDepot.IsUsable(true, "cache1-tyo3.steamcontent.com", 443, true, false, [], 7));
        Assert.False(SteamDepot.IsUsable(true, "cache1-tyo3.steamcontent.com", 443, false, true, [], 7));
        Assert.False(SteamDepot.IsUsable(true, "cache1-tyo3.steamcontent.com", 443, false, false, [8], 7));
        Assert.True(SteamDepot.IsUsable(true, "cache1-tyo3.steamcontent.com", 443, false, false, [7, 8], 7));
    }
}

public sealed class BranchBuildTests
{
    private const string Depots = """
        "depots"
        {
            "2868842"
            {
                "config" { "oslist" "macos" }
                "manifests"
                {
                    "public" { "gid" "8653035385353091849" "size" "2323747842" }
                    "public-beta" { "gid" "5045320271505434676" }
                    "older" "111"
                    "locked" { "gid" "222" }
                }
            }
            "branches"
            {
                "public" { "buildid" "23811903" }
                "public-beta" { "buildid" "24724944" "description" "The sts2 public beta branch" }
                "older" { "buildid" "5" }
                "locked" { "buildid" "6" "pwdrequired" "1" }
            }
        }
        """;

    private static BranchBuild? Read(string branch, uint depot = 2868842) => BranchBuild.Read(KeyValue.LoadFromString(Depots)!, depot, branch);

    [Fact]
    public void ReadsTheManifestAndBuildOfEachBranch()
    {
        Assert.Equal(new BranchBuild("public", 23811903, 8653035385353091849), Read("public"));
        Assert.Equal(new BranchBuild("public-beta", 24724944, 5045320271505434676), Read("public-beta"));
    }

    [Fact]
    public void ReadsSteamsOlderForm() => Assert.Equal(111ul, Read("older")!.ManifestId);

    [Fact]
    public void ABranchThatIsMissingOrNeedsAPasswordIsNotOffered()
    {
        Assert.Null(Read("nothing"));
        Assert.Null(Read("locked"));
        Assert.Null(Read("public", depot: 1));
    }
}
