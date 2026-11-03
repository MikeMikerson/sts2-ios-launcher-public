using System.Runtime.CompilerServices;
using ProtoBuf;
using SteamKit2.Internal;
using STS2Mobile.SteamWire;
using Wire = STS2Mobile.SteamWire.SteamWire;

int passed = 0;

Compare(new CMsgProtoBufHeader { steamid = 123, client_sessionid = 456, jobid_source = 789, target_job_name = "Cloud.EnumerateUserFiles#1" });
Compare(new CMsgClientHello { protocol_version = 65580 });
Compare(new CAuthentication_BeginAuthSessionViaQR_Request { device_friendly_name = "fixture", website_id = "Client" });
Compare(new CAuthentication_PollAuthSessionStatus_Response { account_name = "fixture", refresh_token = "noncredential-fixture" });
Compare(new CMsgClientGetAppOwnershipTicket { app_id = 2868840 });
Compare(new CCloud_EnumerateUserFiles_Request { appid = 2868840, extended_details = true, count = 500 });
Compare(new CCloud_ClientFileDownload_Request { appid = 2868840, filename = "profile1/saves/progress.save" });
Compare(new ContentManifestMetadata { depot_id = 123, gid_manifest = 456 });
// The calls a Send makes: batch, upload, commit, delete.
var batch = new CCloud_BeginAppUploadBatch_Request { appid = 2868840, machine_name = "iPhone" };
batch.files_to_upload.AddRange(["profile.save", "profile1/saves/progress.save"]);
batch.files_to_delete.Add("profile1/saves/current_run.save");
Compare(batch);
Compare(new CCloud_BeginAppUploadBatch_Response { batch_id = 9001, app_change_number = 42 });
Compare(new CCloud_ClientBeginFileUpload_Request
{
    appid = 2868840, filename = "profile1/saves/progress.save", file_size = 120, raw_file_size = 512, file_sha = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20],
    time_stamp = 1790845763, can_encrypt = false, is_shared_file = false, upload_batch_id = 9001
});
var blocks = new CCloud_ClientBeginFileUpload_Response { encrypt_file = false };
var block = new ClientCloudFileUploadBlockDetails
{
    url_host = "steamcloud-us-west3.s3.dualstack.us-west-2.amazonaws.com", url_path = "/2868840/fixture?X-Amz-Signature=fixture", use_https = true,
    http_method = 4, block_offset = 0, block_length = 120, may_parallelize = true
};
block.request_headers.Add(new ClientCloudFileUploadBlockDetails.HTTPHeaders { name = "Content-Type", value = "application/octet-stream" });
blocks.block_requests.Add(block);
blocks.block_requests.Add(new ClientCloudFileUploadBlockDetails { url_host = "steamcloud-us-west3.s3.dualstack.us-west-2.amazonaws.com", http_method = 3, explicit_body_data = [60, 47, 62] });
Compare(blocks);
Compare(new CCloud_ClientCommitFileUpload_Request { transfer_succeeded = true, appid = 2868840, file_sha = [9, 8, 7], filename = "profile1/saves/progress.save" });
Compare(new CCloud_ClientCommitFileUpload_Response { file_committed = true });
Compare(new CCloud_ClientDeleteFile_Request { appid = 2868840, filename = "profile1/saves/current_run.save", is_explicit_delete = true, upload_batch_id = 9001 });
Compare(new CCloud_CompleteAppUploadBatch_Request { appid = 2868840, batch_id = 9001, batch_eresult = 1 });
// Lobby chat: the app's own handler sends one and reads the other.
Compare(new CMsgClientMMSSendLobbyChatMsg { app_id = 2868840, steam_id_lobby = 109775241000000001, steam_id_target = 0, lobby_message = [1, 2, 3] });
Compare(new CMsgClientMMSLobbyChatMsg { app_id = 2868840, steam_id_lobby = 109775241000000001, steam_id_sender = 76561198000000002, lobby_message = [4, 5] });
UploadBlocksSurviveTheGeneratedReader();
BoundedHeaderStopsAtItsLength();

Console.WriteLine($"PASS: {passed} Steam wire compatibility checks. No account or external request used.");

// The generated code must write the bytes protobuf-net writes (where it can run) and read them back unchanged.
void Compare<T>(T input)
{
    using var generated = new MemoryStream();
    Wire.Serialize(generated, input);
    if (RuntimeFeature.IsDynamicCodeSupported)
    {
        using var original = new MemoryStream();
        Serializer.Serialize(original, input);
        Require(original.ToArray().AsSpan().SequenceEqual(generated.ToArray()), "Wire bytes differ for " + typeof(T).Name);
    }
    generated.Position = 0;
    T restored = Wire.Deserialize<T>(generated);
    using var roundTrip = new MemoryStream();
    Wire.Serialize(roundTrip, restored);
    Require(generated.ToArray().AsSpan().SequenceEqual(roundTrip.ToArray()), "Round trip differs for " + typeof(T).Name);
}

// What the upload reads from Steam's answer: the block's address, method, range, headers and explicit body.
void UploadBlocksSurviveTheGeneratedReader()
{
    using var buffer = new MemoryStream();
    Wire.Serialize(buffer, blocks);
    buffer.Position = 0;
    var read = Wire.Deserialize<CCloud_ClientBeginFileUpload_Response>(buffer);
    var first = read.block_requests[0];
    Require(read.block_requests.Count == 2 && first.url_host == block.url_host && first.url_path == block.url_path && first.use_https &&
        first.http_method == 4 && first.block_length == 120 && first.request_headers.Single().value == "application/octet-stream" &&
        read.block_requests[1].http_method == 3 && read.block_requests[1].explicit_body_data.AsSpan().SequenceEqual(new byte[] { 60, 47, 62 }),
        "Upload blocks differ after the generated reader.");
}

void BoundedHeaderStopsAtItsLength()
{
    using var buffer = new MemoryStream();
    Wire.Serialize(buffer, new CMsgProtoBufHeader { steamid = 123 });
    long length = buffer.Length;
    buffer.WriteByte(0xff);
    buffer.Position = 0;
    var result = Wire.Deserialize(buffer, new CMsgProtoBufHeader(), new object(), length);
    Require(result.steamid == 123 && buffer.Position == length, "Bounded header consumed message body.");
}

void Require(bool condition, string failure)
{
    if (!condition) throw new InvalidOperationException(failure);
    passed++;
}
