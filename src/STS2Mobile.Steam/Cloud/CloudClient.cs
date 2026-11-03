using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using SteamKit2;
using SteamKit2.Internal;
using STS2Mobile.Steam.Abstractions;
using STS2Mobile.Steam.Connection;
using STS2Mobile.Steam.Network;

namespace STS2Mobile.Steam.Cloud;

/// <summary>
/// The signed-in player's Steam Cloud files for this game: listing, downloading, and the upload, batch and delete calls a Send uses
/// (the same requests other SteamKit2-based Steam Cloud clients send). Nothing here decides what to send: the write calls are only reached through <see cref="CloudSaveUploader"/>, after the player confirms a Send on screen.
/// </summary>
public sealed class CloudClient : IDisposable
{
    private const string Tag = "Cloud";
    private const uint PageSize = 500;
    // What Steam shows on a computer as where the files last came from.
    private const string MachineName = "iPhone";
    private const int Attempts = 3;
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(30);
    private readonly ICloudCalls _calls;
    private readonly ILogger _logger;
    private readonly HttpClient _downloads;
    private readonly HttpClient _uploads;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;

    public CloudClient(SteamConnection connection, ILogger logger)
        : this(new ConnectionCloudCalls(connection), SteamHttpClient.CreateForCloudDownloads(TransferTimeout),
            SteamHttpClient.CreateForCloudUploads(TransferTimeout), logger, Task.Delay)
    { }

    // Internal injection allows tests to run without contacting Steam.
    internal CloudClient(ICloudCalls calls, HttpClient downloads, HttpClient uploads, ILogger logger, Func<TimeSpan, CancellationToken, Task> wait)
    {
        _calls = calls;
        _downloads = downloads;
        _uploads = uploads;
        _logger = logger;
        _wait = wait;
    }

    public async Task<IReadOnlyList<CloudFileInfo>> EnumerateAsync()
    {
        var files = new List<CloudFileInfo>();
        while (true)
        {
            var page = await _calls.SendAsync<CCloud_EnumerateUserFiles_Request, CCloud_EnumerateUserFiles_Response>(
                "EnumerateUserFiles",
                new CCloud_EnumerateUserFiles_Request { appid = SteamApp.Id, extended_details = true, start_index = (uint)files.Count, count = PageSize })
                .ConfigureAwait(false);
            if (page.files is not { Count: > 0 }) break;
            files.AddRange(page.files.Select(file => new CloudFileInfo(
                file.filename, (long)file.file_size, DateTimeOffset.FromUnixTimeSeconds((long)file.timestamp), file.file_sha ?? "")));
            if (page.files.Count < PageSize) break;
        }
        _logger.Info(Tag, $"Enumerated {files.Count} cloud files");
        return files;
    }

    public async Task<byte[]> DownloadAsync(string cloudName)
    {
        string name = Canonicalize(cloudName);
        var file = await _calls.SendAsync<CCloud_ClientFileDownload_Request, CCloud_ClientFileDownload_Response>(
            "ClientFileDownload", new CCloud_ClientFileDownload_Request { appid = SteamApp.Id, filename = name })
            .ConfigureAwait(false);
        if (file.file_size == 0 && file.raw_file_size == 0) return [];
        if (file.appid != SteamApp.Id || string.IsNullOrEmpty(file.url_host))
            throw new InvalidOperationException("Cloud download failed for " + name);

        string url = (file.use_https ? "https" : "http") + "://" + file.url_host + file.url_path;
        LogRefusal("download", url);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var header in file.request_headers) request.Headers.TryAddWithoutValidation(header.name, header.value);
        using var timeout = new CancellationTokenSource(TransferTimeout);
        using var response = await _downloads.SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        byte[] content = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        _logger.Info(Tag, $"Downloaded {name} ({content.Length} bytes, encrypted={file.encrypted}, file_size={file.file_size}, raw_file_size={file.raw_file_size})");

        // Steam stores larger files as a one-entry zip and reports both sizes.
        if (file.raw_file_size != 0 && file.raw_file_size != file.file_size && CloudCompression.IsZip(content))
        {
            int compressedLength = content.Length;
            content = CloudCompression.Decompress(content, file.raw_file_size);
            _logger.Info(Tag, $"Unzipped {name} ({compressedLength} -> {content.Length} bytes)");
        }
        return content;
    }

    /// <summary>Tells Steam which files a Send is about to write and remove, so a computer sees them as one change. Returns the batch id.</summary>
    public async Task<ulong> BeginUploadBatchAsync(IReadOnlyCollection<string> uploads, IReadOnlyCollection<string> deletes)
    {
        var request = new CCloud_BeginAppUploadBatch_Request { appid = SteamApp.Id, machine_name = MachineName };
        request.files_to_upload.AddRange(uploads.Select(Canonicalize));
        request.files_to_delete.AddRange(deletes.Select(Canonicalize));
        var batch = await _calls.SendAsync<CCloud_BeginAppUploadBatch_Request, CCloud_BeginAppUploadBatch_Response>("BeginAppUploadBatch", request)
            .ConfigureAwait(false);
        _logger.Info(Tag, $"Began upload batch {batch.batch_id}: {uploads.Count} to write, {deletes.Count} to remove");
        return batch.batch_id;
    }

    /// <summary>Ends the batch <see cref="BeginUploadBatchAsync"/> began, saying whether all of it went through.</summary>
    public async Task CompleteUploadBatchAsync(ulong batchId, bool succeeded)
    {
        await _calls.SendAsync<CCloud_CompleteAppUploadBatch_Request, CCloud_CompleteAppUploadBatch_Response>("CompleteAppUploadBatchBlocking",
            new CCloud_CompleteAppUploadBatch_Request { appid = SteamApp.Id, batch_id = batchId, batch_eresult = (uint)(succeeded ? EResult.OK : EResult.Fail) })
            .ConfigureAwait(false);
        _logger.Info(Tag, $"Completed upload batch {batchId} (succeeded={succeeded})");
    }

    /// <summary>
    /// Writes one file. Returns false when Steam already has exactly this file and nothing was sent. Throws when the file is not in
    /// the cloud afterwards, whatever the reason.
    /// </summary>
    public Task<bool> UploadAsync(string cloudName, byte[] content, DateTimeOffset timestamp, ulong batchId, CancellationToken cancellationToken)
    {
        string name = Canonicalize(cloudName);
        return RetryAsync(() => UploadOnceAsync(name, content, timestamp, batchId, cancellationToken), "Upload of " + name, cancellationToken);
    }

    /// <summary>Removes one file. Only a Send the player confirmed reaches this, and never for run history (<see cref="CloudSaveUploader"/>).</summary>
    public Task DeleteAsync(string cloudName, ulong batchId, CancellationToken cancellationToken)
    {
        string name = Canonicalize(cloudName);
        return RetryAsync(async () =>
        {
            var request = new CCloud_ClientDeleteFile_Request { appid = SteamApp.Id, filename = name, is_explicit_delete = true };
            if (batchId != 0) request.upload_batch_id = batchId;
            await _calls.SendAsync<CCloud_ClientDeleteFile_Request, CCloud_ClientDeleteFile_Response>("ClientDeleteFile", request).ConfigureAwait(false);
            _logger.Info(Tag, "Deleted " + name);
            return true;
        }, "Delete of " + name, cancellationToken);
    }

    public void Dispose()
    {
        _downloads.Dispose();
        _uploads.Dispose();
    }

    private static string Canonicalize(string cloudName) => cloudName.Replace("user://", "").Replace('\\', '/');

    /// <summary>Steam refuses a request with TooManyPending when the player's cloud is busy; that one is worth waiting out.</summary>
    private async Task<T> RetryAsync<T>(Func<Task<T>> action, string what, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await action().ConfigureAwait(false); }
            catch (CloudRequestException error) when (error.Result == EResult.TooManyPending && attempt < Attempts)
            {
                var pause = TimeSpan.FromSeconds(2 * attempt);
                _logger.Info(Tag, $"{what} throttled by Steam, trying again in {pause.TotalSeconds:0} s");
                await _wait(pause, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> UploadOnceAsync(string name, byte[] content, DateTimeOffset timestamp, ulong batchId, CancellationToken cancellationToken)
    {
        byte[] sha1 = SHA1.HashData(content);
        var (body, compressed) = CloudCompression.Compress(content);
        var begin = new CCloud_ClientBeginFileUpload_Request
        {
            appid = SteamApp.Id,
            filename = name,
            file_size = (uint)body.Length,
            raw_file_size = (uint)content.Length,
            file_sha = sha1,
            time_stamp = (ulong)timestamp.ToUnixTimeSeconds(),
            can_encrypt = false,
            is_shared_file = false,
        };
        if (batchId != 0) begin.upload_batch_id = batchId;
        CCloud_ClientBeginFileUpload_Response blocks;
        try
        {
            blocks = await _calls.SendAsync<CCloud_ClientBeginFileUpload_Request, CCloud_ClientBeginFileUpload_Response>("ClientBeginFileUpload", begin)
                .ConfigureAwait(false);
        }
        catch (CloudRequestException error) when (error.Result == EResult.DuplicateRequest)
        {
            _logger.Info(Tag, $"Skipped {name}: Steam Cloud already has it");
            return false;
        }

        ExceptionDispatchInfo? failure = null;
        try
        {
            foreach (var block in blocks.block_requests) await SendBlockAsync(name, block, body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = ExceptionDispatchInfo.Capture(error);
        }

        // Steam is told how the transfer went either way, so a failed one is not left open.
        CCloud_ClientCommitFileUpload_Response commit;
        try
        {
            commit = await _calls.SendAsync<CCloud_ClientCommitFileUpload_Request, CCloud_ClientCommitFileUpload_Response>("ClientCommitFileUpload",
                new CCloud_ClientCommitFileUpload_Request { transfer_succeeded = failure == null, appid = SteamApp.Id, file_sha = sha1, filename = name })
                .ConfigureAwait(false);
        }
        catch (Exception error) when (failure != null)
        {
            _logger.Warn(Tag, $"Telling Steam the transfer of {name} failed did not work either: {error.GetType().Name}");
            failure.Throw();
            throw;
        }
        failure?.Throw();
        if (!commit.file_committed) throw new IOException($"Steam Cloud did not keep {name}.");
        _logger.Info(Tag, $"Wrote {name} ({content.Length} bytes, {blocks.block_requests.Count} blocks, compressed={compressed})");
        return true;
    }

    private async Task SendBlockAsync(string name, ClientCloudFileUploadBlockDetails block, byte[] body, CancellationToken cancellationToken)
    {
        // Steam's HTTP method numbers (EHTTPMethod): 3 is POST, 4 is PUT. The storage hosts' signed addresses take PUT. The Android
        // launcher reads 2 as POST and anything else as PUT; any value other than these two is refused here before a byte is sent.
        HttpMethod method = block.http_method switch
        {
            3 => HttpMethod.Post,
            4 => HttpMethod.Put,
            _ => throw new InvalidDataException($"Steam asked for an upload with HTTP method {block.http_method}, which this app does not send."),
        };
        string url = (block.use_https ? "https" : "http") + "://" + block.url_host + block.url_path;
        LogRefusal("upload", url);
        byte[] data;
        if (block.explicit_body_data is { Length: > 0 } explicitBody) data = explicitBody;
        else if (block.block_offset <= (ulong)body.Length && block.block_length <= (ulong)body.Length - block.block_offset)
            data = body.AsSpan((int)block.block_offset, (int)block.block_length).ToArray();
        else throw new InvalidDataException($"Steam asked for a part of {name} that is not there.");
        // The host only, so the first real uploads show which storage hosts Steam uses (NETWORK.md); the path and query are signed.
        _logger.Info(Tag, $"Sending {data.Length} bytes of {name} by {method.Method} to {(Uri.TryCreate(url, UriKind.Absolute, out Uri? to) ? to.IdnHost : "an invalid address")}");

        using var request = new HttpRequestMessage(method, url) { Content = new ByteArrayContent(data) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        foreach (var header in block.request_headers)
        {
            // A header such as Content-Type belongs to the body; the length is always the body's own.
            if (request.Headers.TryAddWithoutValidation(header.name, header.value) ||
                header.name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            request.Content.Headers.Remove(header.name);
            request.Content.Headers.TryAddWithoutValidation(header.name, header.value);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TransferTimeout);
        using var response = await _uploads.SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Logs why the network policy is about to refuse a Steam-supplied transfer address. The HTTP handler still enforces the policy;
    /// this only makes the refusal diagnosable, with the scheme, host and port only (the rest is signed).
    /// </summary>
    private void LogRefusal(string transfer, string url)
    {
        Uri? destination = Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ? parsed : null;
        if (SteamNetworkPolicy.IsCloudStorage(destination)) return;
        if (SteamNetworkPolicy.DescribeRefusal(destination) is { } refusal)
            _logger.Info(Tag, $"The {transfer} destination is outside the Steam network policy: " + refusal);
    }
}
