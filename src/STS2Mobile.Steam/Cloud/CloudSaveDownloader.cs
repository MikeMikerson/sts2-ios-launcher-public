using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

namespace STS2Mobile.Steam.Cloud;

/// <summary>
/// Reads the signed-in player's own Steam Cloud files for this game. It writes nothing to the cloud;
/// a Send is <see cref="CloudSaveUploader"/>'s. Every file must match the size and SHA-1 Steam lists for it, or the whole
/// download fails. Choosing which files count as saves, and putting them in place, is the caller's job.
/// </summary>
public sealed class CloudSaveDownloader
{
    // Each file is one Steam request (the connection sends those one at a time) and one storage
    // download; a few in flight let the downloads overlap without queuing hundreds of requests.
    private const int InFlight = 4;
    private readonly Func<CancellationToken, Task<IReadOnlyList<CloudFileInfo>>> _list;
    private readonly Func<string, CancellationToken, Task<byte[]>> _download;

    public CloudSaveDownloader(CloudClient cloud)
        : this(cancellationToken => cloud.EnumerateAsync().WaitAsync(cancellationToken),
               (name, cancellationToken) => cloud.DownloadAsync(name).WaitAsync(cancellationToken))
    { }

    // Internal injection allows tests to run without contacting Steam.
    internal CloudSaveDownloader(Func<CancellationToken, Task<IReadOnlyList<CloudFileInfo>>> list,
        Func<string, CancellationToken, Task<byte[]>> download)
    {
        _list = list;
        _download = download;
    }

    public Task<IReadOnlyList<CloudFileInfo>> ListAsync(CancellationToken cancellationToken) => _list(cancellationToken);

    /// <summary>Downloads <paramref name="files"/> in their order, reporting the number finished after each file.</summary>
    public async Task<IReadOnlyList<DownloadedCloudFile>> DownloadAsync(IReadOnlyList<CloudFileInfo> files,
        IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var results = new DownloadedCloudFile[files.Count];
        int finished = 0;
        using var slots = new SemaphoreSlim(InFlight);
        using var failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task DownloadOneAsync(CloudFileInfo file, int index)
        {
            await slots.WaitAsync(failed.Token).ConfigureAwait(false);
            try
            {
                // A slot freed by a failing download can reach a waiter before its cancellation does;
                // without this check every remaining file would still be requested from Steam.
                failed.Token.ThrowIfCancellationRequested();
                byte[] content = await _download(file.Name, failed.Token).ConfigureAwait(false);
                string sha1 = Convert.ToHexStringLower(SHA1.HashData(content));
                if (content.LongLength != file.Size || !sha1.Equals(file.Sha1Hex, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A downloaded cloud file does not match what Steam Cloud lists for it.");
                results[index] = new DownloadedCloudFile(file.Name, content, sha1, file.Timestamp);
            }
            catch
            {
                // One bad file fails the whole download; stop starting the others.
                failed.Cancel();
                throw;
            }
            finally { slots.Release(); }
            progress?.Report(Interlocked.Increment(ref finished));
        }
        Task all = Task.WhenAll(files.Select(DownloadOneAsync));
        try { await all.ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Report the failure that stopped the download, not the cancellations it caused.
            Exception? cause = all.Exception?.InnerExceptions.FirstOrDefault(error => error is not OperationCanceledException);
            if (cause != null) ExceptionDispatchInfo.Throw(cause);
            throw;
        }
        return results;
    }
}
