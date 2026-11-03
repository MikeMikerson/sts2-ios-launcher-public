using System.Buffers;
using System.Security.Cryptography;
using SteamKit2;
using SteamKit2.CDN;

namespace STS2Mobile.Steam.Depot;

/// <summary>Where a depot's chunks come from.</summary>
public interface IDepotChunkSource
{
    /// <summary>Writes the chunk's plain bytes to <paramref name="destination"/>, at least its uncompressed length long, and returns how many.</summary>
    Task<int> ReadAsync(DepotManifest.ChunkData chunk, byte[] destination, CancellationToken cancellationToken);
}

/// <summary>One file to download from a depot.</summary>
/// <param name="Source">The file in the depot's manifest. Its SHA-1 is Steam's hash of the whole file, and the finished file has to match it.</param>
/// <param name="Path">Where it goes.</param>
/// <param name="Sha256">What the finished file has to hash to as well, from a tested version's profile; null for a build with none.</param>
public sealed record DepotFile(DepotManifest.FileData Source, string Path, string? Sha256)
{
    /// <summary>Steam's SHA-1 of the file, or null if the manifest gives none.</summary>
    public string? SteamSha1 => Source.FileHash is { Length: 20 } hash && hash.Any(b => b != 0) ? Convert.ToHexStringLower(hash) : null;
}

/// <param name="Bytes">The files' whole size.</param>
/// <param name="DownloadedChunks">The chunks fetched this run.</param>
/// <param name="ReusedChunks">The chunks an earlier run left, found good by their checksums.</param>
/// <param name="ReusedFiles">The files an earlier run finished.</param>
public sealed record DepotDownloadResult(long Bytes, int DownloadedChunks, int ReusedChunks, int ReusedFiles);

/// <summary>
/// Downloads files from a depot, each chunk checked as it arrives and each finished file against its hash. An interruption
/// loses nothing: a run looks at what the last one left in <c>.partial</c> by the manifest's chunk checksums, so a chunk half
/// written counts as missing, and it needs no journal to fall out of step with the file. A finished file is renamed into place.
/// </summary>
public sealed class DepotDownload(IDepotChunkSource source, IReadOnlyList<DepotFile> files, Action<long, long>? progress = null,
    int workers = DepotDownload.DefaultWorkers, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const string PartialSuffix = ".partial";
    public const int DefaultWorkers = 8;
    // Long enough to ride out a dropped connection, with the source trying another server each time: 1 + 2 + 4 + 8 + 16 s of waiting.
    public const int ReadAttempts = 6;
    private const int CheckBufferBytes = 1024 * 1024;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private long _done;
    private int _downloaded;
    private int _reusedChunks;

    public async Task<DepotDownloadResult> RunAsync(CancellationToken cancellationToken)
    {
        long total = files.Sum(file => (long)file.Source.TotalSize);
        int reusedFiles = 0;
        progress?.Invoke(0, total);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await Task.Run(() => IsFinished(file), cancellationToken).ConfigureAwait(false))
            {
                reusedFiles++;
                Advance((long)file.Source.TotalSize, total);
                continue;
            }
            await DownloadAsync(file, total, cancellationToken).ConfigureAwait(false);
        }
        return new DepotDownloadResult(total, _downloaded, _reusedChunks, reusedFiles);
    }

    private static bool IsFinished(DepotFile file) =>
        File.Exists(file.Path) && new FileInfo(file.Path).Length == (long)file.Source.TotalSize && Mismatch(file, file.Path) == null;

    private async Task DownloadAsync(DepotFile file, long total, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
        string partial = file.Path + PartialSuffix;
        using (var handle = File.OpenHandle(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite))
        {
            var missing = await Task.Run(() => MissingChunks(file, handle, total, cancellationToken), cancellationToken).ConfigureAwait(false);
            RandomAccess.SetLength(handle, (long)file.Source.TotalSize);
            await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken }, async (chunk, token) =>
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent((int)chunk.UncompressedLength);
                try
                {
                    int length = await ReadVerifiedAsync(chunk, buffer, token).ConfigureAwait(false);
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, length), (long)chunk.Offset, token).ConfigureAwait(false);
                    Interlocked.Increment(ref _downloaded);
                    Advance(length, total);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }).ConfigureAwait(false);
            RandomAccess.FlushToDisk(handle);
        }
        // Every chunk matched the manifest, so a wrong file means the manifest is not the one the profile pinned the hash from.
        if (await Task.Run(() => Mismatch(file, partial), cancellationToken).ConfigureAwait(false) is { } which)
        {
            File.Delete(partial);
            throw new InvalidDataException(Path.GetFileName(file.Path) + $" does not match {which}.");
        }
        File.Move(partial, file.Path, overwrite: true);
    }

    /// <summary>Which hash the file at <paramref name="path"/> fails, Steam's or the profile's, or null if it matches both. One read for both.</summary>
    private static string? Mismatch(DepotFile file, string path)
    {
        string? sha1 = file.SteamSha1;
        using var steam = sha1 == null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var profile = file.Sha256 == null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CheckBufferBytes);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                steam?.AppendData(buffer, 0, read);
                profile?.AppendData(buffer, 0, read);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        if (steam != null && Convert.ToHexStringLower(steam.GetHashAndReset()) != sha1) return "Steam's hash for it";
        if (profile != null && Convert.ToHexStringLower(profile.GetHashAndReset()) != file.Sha256) return "the hash this app expects";
        return null;
    }

    /// <summary>The chunks the partial file does not hold yet. The good ones count as progress as they are found.</summary>
    private List<DepotManifest.ChunkData> MissingChunks(DepotFile file, Microsoft.Win32.SafeHandles.SafeFileHandle handle, long total, CancellationToken cancellationToken)
    {
        long existing = RandomAccess.GetLength(handle);
        var missing = new List<DepotManifest.ChunkData>();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CheckBufferBytes);
        try
        {
            foreach (var chunk in file.Source.Chunks.OrderBy(chunk => chunk.Offset))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasChunk(handle, chunk, existing, ref buffer))
                {
                    _reusedChunks++;
                    Advance(chunk.UncompressedLength, total);
                }
                else missing.Add(chunk);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return missing;
    }

    private static bool HasChunk(Microsoft.Win32.SafeHandles.SafeFileHandle handle, DepotManifest.ChunkData chunk, long existing, ref byte[] buffer)
    {
        int length = (int)chunk.UncompressedLength;
        if ((long)chunk.Offset + length > existing) return false;
        if (buffer.Length < length)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = ArrayPool<byte>.Shared.Rent(length);
        }
        var bytes = buffer.AsSpan(0, length);
        int read = 0;
        while (read < length)
        {
            int count = RandomAccess.Read(handle, bytes[read..], (long)chunk.Offset + read);
            if (count == 0) return false;
            read += count;
        }
        return DepotChunk.AdlerHash(bytes) == chunk.Checksum;
    }

    private async Task<int> ReadVerifiedAsync(DepotManifest.ChunkData chunk, byte[] buffer, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                int length = await source.ReadAsync(chunk, buffer, cancellationToken).ConfigureAwait(false);
                if (length != chunk.UncompressedLength || DepotChunk.AdlerHash(buffer.AsSpan(0, length)) != chunk.Checksum)
                    throw new InvalidDataException("A chunk of the game did not match its checksum.");
                return length;
            }
            // A timeout looks like a cancellation, so only the caller's own token ends the wait for one.
            catch (Exception) when (attempt < ReadAttempts && !cancellationToken.IsCancellationRequested)
            {
                await _delay(TimeSpan.FromSeconds(1 << (attempt - 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void Advance(long bytes, long total)
    {
        long done = Interlocked.Add(ref _done, bytes);
        progress?.Invoke(done, total);
    }
}
