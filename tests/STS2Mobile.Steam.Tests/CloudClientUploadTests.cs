using System.Net;
using System.Security.Cryptography;
using System.Text;
using ProtoBuf;
using SteamKit2;
using SteamKit2.Internal;
using STS2Mobile.Steam.Abstractions;
using STS2Mobile.Steam.Cloud;
using STS2Mobile.Steam.Connection;
using STS2Mobile.Steam.Network;
using STS2Mobile.Tests;
using CloudClient = STS2Mobile.Steam.Cloud.CloudClient;

namespace STS2Mobile.Steam.Tests;

// The upload, batch and delete calls send what other SteamKit2-based Steam Cloud clients send, through the upload client's policy, and a file
// counts as sent only when Steam says it kept it.
public sealed class CloudClientUploadTests : IDisposable
{
    private const string StorageHost = "steamcloud-us-west3.s3.dualstack.us-west-2.amazonaws.com";
    private static readonly DateTimeOffset Changed = DateTimeOffset.FromUnixTimeSeconds(1_790_845_763);
    private static readonly byte[] Small = "progress"u8.ToArray();
    private readonly FakeCloud _steam = new();
    private readonly RecordingTransport _storage = new();
    private readonly List<TimeSpan> _waits = [];
    private readonly CloudClient _cloud;

    public CloudClientUploadTests()
    {
        _cloud = new CloudClient(_steam, new HttpClient(new SteamOnlyHandler(new RecordingTransport(), CloudStorageAccess.Download)),
            new HttpClient(new SteamOnlyHandler(_storage, CloudStorageAccess.Upload)), new NullLogger(),
            (pause, _) => { _waits.Add(pause); return Task.CompletedTask; });
        _steam.Answer<CCloud_ClientBeginFileUpload_Request>(request => Blocks(request.file_size));
        _steam.Answer<CCloud_ClientCommitFileUpload_Request>(_ => new CCloud_ClientCommitFileUpload_Response { file_committed = true });
    }

    public void Dispose() => _cloud.Dispose();

    [Fact]
    public async Task UploadBeginsSendsAndCommits()
    {
        Assert.True(await _cloud.UploadAsync(@"user://profile1\saves\progress.save", Small, Changed, 77, CancellationToken.None));

        var begin = _steam.Single<CCloud_ClientBeginFileUpload_Request>("ClientBeginFileUpload");
        Assert.Equal(SteamApp.Id, begin.appid);
        Assert.Equal("profile1/saves/progress.save", begin.filename);
        Assert.Equal((uint)Small.Length, begin.raw_file_size);
        Assert.Equal((uint)Small.Length, begin.file_size);
        Assert.Equal(SHA1.HashData(Small), begin.file_sha);
        Assert.Equal((ulong)Changed.ToUnixTimeSeconds(), begin.time_stamp);
        Assert.Equal(77UL, begin.upload_batch_id);
        Assert.False(begin.can_encrypt);
        Assert.False(begin.is_shared_file);

        var sent = Assert.Single(_storage.Requests);
        Assert.Equal(HttpMethod.Put, sent.Method);
        Assert.Equal($"https://{StorageHost}/2868840/block?X-Amz-Signature=secret", sent.Uri.ToString());
        Assert.Equal(Small, sent.Body);
        Assert.Equal("application/octet-stream", sent.Headers["Content-Type"]);
        Assert.Equal("signed", sent.Headers["x-amz-meta-test"]);

        var commit = _steam.Single<CCloud_ClientCommitFileUpload_Request>("ClientCommitFileUpload");
        Assert.True(commit.transfer_succeeded);
        Assert.Equal("profile1/saves/progress.save", commit.filename);
        Assert.Equal(SHA1.HashData(Small), commit.file_sha);
        Assert.Equal(["ClientBeginFileUpload", "ClientCommitFileUpload"], _steam.Methods);
    }

    [Fact]
    public async Task LargeFileIsSentZippedWithBothSizes()
    {
        byte[] large = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"gold\":99,\"relics\":[]}", 400)));
        await _cloud.UploadAsync("profile1/saves/current_run.save", large, Changed, 0, CancellationToken.None);

        var begin = _steam.Single<CCloud_ClientBeginFileUpload_Request>("ClientBeginFileUpload");
        Assert.Equal((uint)large.Length, begin.raw_file_size);
        Assert.True(begin.file_size < begin.raw_file_size);
        Assert.Equal(SHA1.HashData(large), begin.file_sha);
        Assert.Equal(0UL, begin.upload_batch_id);
        byte[] body = Assert.Single(_storage.Requests).Body!;
        Assert.True(CloudCompression.IsZip(body));
        Assert.Equal(large, CloudCompression.Decompress(body));
    }

    [Fact]
    public async Task BlocksAreCutFromTheBodyAndExplicitBodiesSentAsGiven()
    {
        byte[] content = Enumerable.Range(0, 64).Select(index => (byte)(index * 37)).ToArray();
        _steam.Answer<CCloud_ClientBeginFileUpload_Request>(_ =>
        {
            var response = new CCloud_ClientBeginFileUpload_Response();
            response.block_requests.Add(Block(4, 0, 10));
            response.block_requests.Add(Block(4, 10, 54));
            var finish = Block(3, 0, 0);
            finish.explicit_body_data = "<CompleteMultipartUpload/>"u8.ToArray();
            response.block_requests.Add(finish);
            return response;
        });
        await _cloud.UploadAsync("profile1/saves/progress.save", content, Changed, 0, CancellationToken.None);

        Assert.Equal([HttpMethod.Put, HttpMethod.Put, HttpMethod.Post], _storage.Requests.Select(request => request.Method));
        Assert.Equal(content[..10], _storage.Requests[0].Body);
        Assert.Equal(content[10..], _storage.Requests[1].Body);
        Assert.Equal("<CompleteMultipartUpload/>"u8.ToArray(), _storage.Requests[2].Body);
    }

    [Fact]
    public async Task FileSteamAlreadyHasIsNotSent()
    {
        _steam.Fail<CCloud_ClientBeginFileUpload_Request>("ClientBeginFileUpload", EResult.DuplicateRequest);
        Assert.False(await _cloud.UploadAsync("profile.save", Small, Changed, 0, CancellationToken.None));
        Assert.Empty(_storage.Requests);
        Assert.Equal(["ClientBeginFileUpload"], _steam.Methods);
    }

    [Fact]
    public async Task FailedTransferIsReportedToSteamAndThrown()
    {
        _storage.Status = HttpStatusCode.Forbidden;
        await Assert.ThrowsAsync<HttpRequestException>(() => _cloud.UploadAsync("profile.save", Small, Changed, 0, CancellationToken.None));
        Assert.False(_steam.Single<CCloud_ClientCommitFileUpload_Request>("ClientCommitFileUpload").transfer_succeeded);
    }

    [Fact]
    public async Task FileSteamDidNotKeepIsAFailure()
    {
        _steam.Answer<CCloud_ClientCommitFileUpload_Request>(_ => new CCloud_ClientCommitFileUpload_Response { file_committed = false });
        await Assert.ThrowsAsync<IOException>(() => _cloud.UploadAsync("profile.save", Small, Changed, 0, CancellationToken.None));
    }

    [Fact]
    public async Task UnconfirmedCommitIsAFailure()
    {
        _steam.Fail<CCloud_ClientCommitFileUpload_Request>("ClientCommitFileUpload", EResult.Fail);
        await Assert.ThrowsAsync<CloudRequestException>(() => _cloud.UploadAsync("profile.save", Small, Changed, 0, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task UnknownHttpMethodSendsNothing(int method)
    {
        _steam.Answer<CCloud_ClientBeginFileUpload_Request>(request =>
        {
            var response = new CCloud_ClientBeginFileUpload_Response();
            response.block_requests.Add(Block(method, 0, request.file_size));
            return response;
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => _cloud.UploadAsync("profile.save", Small, Changed, 0, CancellationToken.None));
        Assert.Empty(_storage.Requests);
        Assert.False(_steam.Single<CCloud_ClientCommitFileUpload_Request>("ClientCommitFileUpload").transfer_succeeded);
    }

    [Theory]
    [InlineData("attacker.test", true)]
    [InlineData("attacker.s3.dualstack.us-west-2.amazonaws.com", true)]
    [InlineData(StorageHost, false)]
    public async Task UploadHostOutsideThePolicyIsRefused(string host, bool https)
    {
        _steam.Answer<CCloud_ClientBeginFileUpload_Request>(request =>
        {
            var response = new CCloud_ClientBeginFileUpload_Response();
            var block = Block(4, 0, request.file_size);
            block.url_host = host;
            block.use_https = https;
            response.block_requests.Add(block);
            return response;
        });
        await Assert.ThrowsAsync<NetworkPolicyException>(() => _cloud.UploadAsync("profile.save", Small, Changed, 0, CancellationToken.None));
        Assert.Empty(_storage.Requests);
        Assert.False(_steam.Single<CCloud_ClientCommitFileUpload_Request>("ClientCommitFileUpload").transfer_succeeded);
    }

    [Fact]
    public async Task BlockOutsideTheFileIsRefused()
    {
        _steam.Answer<CCloud_ClientBeginFileUpload_Request>(request =>
        {
            var response = new CCloud_ClientBeginFileUpload_Response();
            response.block_requests.Add(Block(4, 4, request.file_size));
            return response;
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => _cloud.UploadAsync("profile.save", Small, Changed, 0, CancellationToken.None));
        Assert.Empty(_storage.Requests);
    }

    [Fact]
    public async Task BusyCloudIsWaitedOutThenGivenUp()
    {
        _steam.Fail<CCloud_ClientBeginFileUpload_Request>("ClientBeginFileUpload", EResult.TooManyPending);
        var error = await Assert.ThrowsAsync<CloudRequestException>(() => _cloud.UploadAsync("profile.save", Small, Changed, 0, CancellationToken.None));
        Assert.Equal(EResult.TooManyPending, error.Result);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], _waits);
        Assert.Equal(3, _steam.Methods.Count);
    }

    [Fact]
    public async Task BusyCloudOnceStillSends()
    {
        int tries = 0;
        _steam.Answer<CCloud_ClientBeginFileUpload_Request>(request =>
            ++tries == 1 ? throw new CloudRequestException("ClientBeginFileUpload", EResult.TooManyPending) : Blocks(request.file_size));
        Assert.True(await _cloud.UploadAsync("profile.save", Small, Changed, 0, CancellationToken.None));
        Assert.Single(_storage.Requests);
    }

    [Fact]
    public async Task DeleteNamesTheFileAndBatch()
    {
        _steam.Answer<CCloud_ClientDeleteFile_Request>(_ => new CCloud_ClientDeleteFile_Response());
        await _cloud.DeleteAsync(@"profile1\saves\current_run.save", 77, CancellationToken.None);
        var delete = _steam.Single<CCloud_ClientDeleteFile_Request>("ClientDeleteFile");
        Assert.Equal(SteamApp.Id, delete.appid);
        Assert.Equal("profile1/saves/current_run.save", delete.filename);
        Assert.Equal(77UL, delete.upload_batch_id);
        Assert.True(delete.is_explicit_delete);
        Assert.Empty(_storage.Requests);
    }

    [Fact]
    public async Task BatchListsItsFilesAndEndsWithItsResult()
    {
        _steam.Answer<CCloud_BeginAppUploadBatch_Request>(_ => new CCloud_BeginAppUploadBatch_Response { batch_id = 91 });
        _steam.Answer<CCloud_CompleteAppUploadBatch_Request>(_ => new CCloud_CompleteAppUploadBatch_Response());

        Assert.Equal(91UL, await _cloud.BeginUploadBatchAsync(["profile.save", @"profile1\saves\progress.save"], ["profile1/saves/current_run.save"]));
        await _cloud.CompleteUploadBatchAsync(91, succeeded: true);
        await _cloud.CompleteUploadBatchAsync(91, succeeded: false);

        var begin = _steam.Single<CCloud_BeginAppUploadBatch_Request>("BeginAppUploadBatch");
        Assert.Equal(SteamApp.Id, begin.appid);
        Assert.Equal("iPhone", begin.machine_name);
        Assert.Equal(["profile.save", "profile1/saves/progress.save"], begin.files_to_upload);
        Assert.Equal(["profile1/saves/current_run.save"], begin.files_to_delete);
        var completes = _steam.All<CCloud_CompleteAppUploadBatch_Request>("CompleteAppUploadBatchBlocking");
        Assert.Equal([91UL, 91UL], completes.Select(complete => complete.batch_id));
        Assert.Equal([(uint)EResult.OK, (uint)EResult.Fail], completes.Select(complete => complete.batch_eresult));
        Assert.Empty(_storage.Requests);
    }

    private static CCloud_ClientBeginFileUpload_Response Blocks(uint size)
    {
        var response = new CCloud_ClientBeginFileUpload_Response();
        var block = Block(4, 0, size);
        block.request_headers.Add(new ClientCloudFileUploadBlockDetails.HTTPHeaders { name = "x-amz-meta-test", value = "signed" });
        block.request_headers.Add(new ClientCloudFileUploadBlockDetails.HTTPHeaders { name = "Content-Type", value = "application/octet-stream" });
        response.block_requests.Add(block);
        return response;
    }

    private static ClientCloudFileUploadBlockDetails Block(int method, ulong offset, uint length) => new()
    {
        url_host = StorageHost,
        url_path = "/2868840/block?X-Amz-Signature=secret",
        use_https = true,
        http_method = method,
        block_offset = offset,
        block_length = length,
    };

    /// <summary>Stands in for Steam's Cloud service: records each request and answers as told.</summary>
    private sealed class FakeCloud : ICloudCalls
    {
        private readonly Dictionary<Type, Func<object, object>> _answers = [];
        private readonly List<(string Method, object Request)> _sent = [];

        public List<string> Methods => _sent.Select(call => call.Method).ToList();

        public void Answer<TRequest>(Func<TRequest, object> answer) => _answers[typeof(TRequest)] = request => answer((TRequest)request);

        public void Fail<TRequest>(string method, EResult result) => _answers[typeof(TRequest)] = _ => throw new CloudRequestException(method, result);

        public T Single<T>(string method) => Assert.Single(All<T>(method));

        public List<T> All<T>(string method) => _sent.Where(call => call.Method == method).Select(call => (T)call.Request).ToList();

        public Task<TResult> SendAsync<TRequest, TResult>(string method, TRequest request)
            where TRequest : IExtensible, new()
            where TResult : IExtensible, new()
        {
            _sent.Add((method, request));
            try { return Task.FromResult((TResult)_answers[typeof(TRequest)](request)); }
            catch (Exception error) { return Task.FromException<TResult>(error); }
        }
    }

    private sealed class NullLogger : ILogger
    {
        public void Info(string tag, string message, Exception? exception = null) { }
        public void Warn(string tag, string message, Exception? exception = null) { }
        public void Error(string tag, string message, Exception? exception = null) { }
    }
}
