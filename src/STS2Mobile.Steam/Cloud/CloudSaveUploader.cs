namespace STS2Mobile.Steam.Cloud;

/// <summary>A file a Send writes to Steam Cloud: its cloud name, content and the time it was last changed on this iPhone.</summary>
public sealed record CloudUpload(string Name, byte[] Content, DateTimeOffset Timestamp);

/// <summary>How a Send went: files written, files Steam already had exactly, files removed.</summary>
public sealed record CloudSendResult(int Written, int AlreadyThere, int Removed);

/// <summary>A Send stopped partway. What got through before it stopped is in <see cref="Done"/>; the cause is the inner exception.</summary>
public sealed class CloudSendException(CloudSendResult done, Exception cause)
    : IOException("The Send to Steam Cloud stopped: " + cause.Message, cause)
{
    public CloudSendResult Done { get; } = done;
}

/// <summary>
/// Writes the player's saves to Steam Cloud as one batch: the files given, then the removals given, then the end of the batch. It
/// decides nothing: the caller passes exactly what the player confirmed on screen. Run history is never removed, whatever it is given.
/// </summary>
public sealed class CloudSaveUploader
{
    private readonly Func<IReadOnlyCollection<string>, IReadOnlyCollection<string>, CancellationToken, Task<ulong>> _begin;
    private readonly Func<CloudUpload, ulong, CancellationToken, Task<bool>> _upload;
    private readonly Func<string, ulong, CancellationToken, Task> _delete;
    private readonly Func<ulong, bool, Task> _complete;

    public CloudSaveUploader(CloudClient cloud)
        : this((uploads, deletes, cancellationToken) => cloud.BeginUploadBatchAsync(uploads, deletes).WaitAsync(cancellationToken),
               (file, batch, cancellationToken) => cloud.UploadAsync(file.Name, file.Content, file.Timestamp, batch, cancellationToken),
               (name, batch, cancellationToken) => cloud.DeleteAsync(name, batch, cancellationToken),
               cloud.CompleteUploadBatchAsync)
    { }

    // Internal injection allows tests to run without contacting Steam.
    internal CloudSaveUploader(Func<IReadOnlyCollection<string>, IReadOnlyCollection<string>, CancellationToken, Task<ulong>> begin,
        Func<CloudUpload, ulong, CancellationToken, Task<bool>> upload, Func<string, ulong, CancellationToken, Task> delete, Func<ulong, bool, Task> complete)
    {
        _begin = begin;
        _upload = upload;
        _delete = delete;
        _complete = complete;
    }

    /// <summary>A finished run's record (<c>profileN/saves/history/...</c>). A Send only ever adds these.</summary>
    public static bool IsRunHistory(string name) =>
        name.Replace('\\', '/').Split('/')[..^1].Any(part => part.Equals("history", StringComparison.OrdinalIgnoreCase));

    /// <summary>Sends <paramref name="uploads"/> in order, then removes <paramref name="deletes"/>, reporting the number finished after each.</summary>
    public async Task<CloudSendResult> SendAsync(IReadOnlyList<CloudUpload> uploads, IReadOnlyList<string> deletes, IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        if (uploads.Count == 0 && deletes.Count == 0) throw new ArgumentException("A Send needs something to send.");
        if (deletes.FirstOrDefault(IsRunHistory) is { } history) throw new ArgumentException("A Send never removes run history: " + history);
        var names = uploads.Select(file => file.Name).Concat(deletes).ToList();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count) throw new ArgumentException("A Send names a file twice.");

        ulong batch = await _begin(uploads.Select(file => file.Name).ToList(), deletes, cancellationToken).ConfigureAwait(false);
        int written = 0, alreadyThere = 0, removed = 0;
        bool succeeded = false;
        try
        {
            foreach (CloudUpload file in uploads)
            {
                if (await _upload(file, batch, cancellationToken).ConfigureAwait(false)) written++;
                else alreadyThere++;
                progress?.Report(written + alreadyThere);
            }
            // Removals come last, so a Send that stops partway has removed nothing.
            foreach (string name in deletes)
            {
                await _delete(name, batch, cancellationToken).ConfigureAwait(false);
                removed++;
                progress?.Report(written + alreadyThere + removed);
            }
            succeeded = true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new CloudSendException(new CloudSendResult(written, alreadyThere, removed), error);
        }
        finally
        {
            if (!succeeded)
            {
                // The cause of the stop matters more than whether Steam heard that the batch failed.
                try { await _complete(batch, false).ConfigureAwait(false); }
                catch (Exception) { }
            }
        }
        var result = new CloudSendResult(written, alreadyThere, removed);
        try { await _complete(batch, true).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new CloudSendException(result, error);
        }
        return result;
    }
}
