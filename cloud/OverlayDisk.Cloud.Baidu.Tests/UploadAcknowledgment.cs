using System.Text.Json;
using OverlayDisk.Cloud.Contracts;

internal static partial class Program
{
    private static readonly HashSet<string> UploadAckTests = new(StringComparer.Ordinal)
    {
        nameof(UploadAckSuccess), nameof(UploadAckRapid), nameof(UploadAckMalformed),
        nameof(Upload), nameof(HtmlMimeUpload), nameof(PartRetry), nameof(Existing), nameof(LostCommit),
        nameof(PreparedRecovery), nameof(CacheUploadFailures), nameof(FixedLengthMultipartRetry)
    };

    private static async Task UploadAckSuccess()
    {
        using var server = new FakeBaidu(); await using var client = Cached(server);
        await client.CreateDirectoryAsync("/ack");
        var replies = new (string Name, Func<string, long, HttpResponseMessage> Reply)[]
        {
            ("opaque", (path, size) => FakeBaidu.Raw(JsonSerializer.Serialize(new { errno = 0, path, size, isdir = 0, fs_id = 77, md5 = "opaque-checksum" }))),
            ("different32", (path, size) => FakeBaidu.Raw(JsonSerializer.Serialize(new { errno = 0, path, size, isdir = 0, fs_id = 77, md5 = new string('f', 32) }))),
            ("missing-md5", (path, size) => FakeBaidu.Raw(JsonSerializer.Serialize(new { errno = 0, path, size, isdir = 0, fs_id = 77 }))),
            ("partial", (path, _) => FakeBaidu.Raw(JsonSerializer.Serialize(new { errno = 0, path }))),
            ("status-only", (_, _) => FakeBaidu.Raw("{\"errno\":0}"))
        };
        foreach (bool preparedMode in new[] { false, true })
            foreach (var reply in replies)
            {
                string path = "/ack/" + reply.Name + (preparedMode ? "-prepared" : "-general");
                byte[] bytes = reply.Name == "opaque" ? new byte[CloudObjectGeometry.DefaultSize] : [1, 7, 2, 8];
                if (bytes.Length > 4) new Random(42).NextBytes(bytes);
                var descriptor = new ImmutableObjectDescriptor(path, bytes.Length, Sha(bytes));
                server.CreateReply = reply.Reply; server.MissingPartMd5 = reply.Name == "status-only";
                server.Calls.Clear(); int downloads = server.Downloads;
                CloudObjectInfo result;
                if (preparedMode)
                {
                    using var prepared = await PreparedUpload.CreateAsync(descriptor, new MemoryStream(bytes, false));
                    result = await client.PutPreparedAsync(prepared);
                }
                else result = await Put(client, path, bytes);
                Assert(result.Path == path && result.Length == bytes.Length && !result.ReusedExisting && server.Files[path].SequenceEqual(bytes), "Successful upload acknowledgment lost its requested identity/content");
                Assert(server.Downloads == downloads && Calls(server, "locate-download") == 0 && Calls(server, "download") == 0 && Calls(server, "list") == 0,
                    "A successful upload acknowledged by Baidu reread or listed its payload");
                server.Calls.Clear();
                Assert(await client.TryGetConfirmedReceiptAsync(descriptor) is { ReusedExisting: true } && server.Calls.Count == 0,
                    "Provider-success acknowledgment did not retain its reusable local receipt");
            }
    }

    private static async Task UploadAckRapid()
    {
        foreach (bool hasMetadata in new[] { false, true })
        {
            byte[] bytes = [7, 4, 1, 9]; using var server = new FakeBaidu(); await using var client = Cached(server);
            await client.CreateDirectoryAsync("/rapid"); server.Calls.Clear();
            server.Override = (request, _) =>
            {
                if (request.RequestUri!.AbsolutePath != "/api/precreate") return Task.FromResult<HttpResponseMessage?>(null);
                server.Files["/rapid/object"] = bytes.ToArray();
                return Task.FromResult<HttpResponseMessage?>(FakeBaidu.Raw(hasMetadata
                    ? "{\"errno\":0,\"return_type\":2,\"info\":{\"path\":\"/rapid/object\",\"size\":4,\"isdir\":0,\"md5\":\"opaque\"}}"
                    : "{\"errno\":0,\"return_type\":2}"));
            };
            using var prepared = await PreparedUpload.CreateAsync(new("/rapid/object", bytes.Length, Sha(bytes)), new MemoryStream(bytes, false));
            var receipt = await client.PutPreparedAsync(prepared);
            Assert(receipt.ReusedExisting && receipt.Length == bytes.Length && Calls(server, "precreate") == 1 && server.Calls.Count == 1 && server.Downloads == 0,
                "Successful rapid upload triggered a redundant read/part/create or lost its acknowledgment");
        }
    }

    private static async Task UploadAckMalformed()
    {
        foreach (var (reply, code) in new[]
        {
            ("{\"errno\":0,\"path\":\"/different\"}", "CreatedMetadataMismatch"),
            ("{\"errno\":0,\"path\":null}", "CreatedMetadataMismatch"),
            ("{\"errno\":0,\"size\":999}", "CreatedMetadataMismatch"),
            ("{\"errno\":0,\"size\":\"not-a-number\"}", "CreatedMetadataMismatch"),
            ("{\"errno\":0,\"isdir\":1}", "CreatedMetadataMismatch"),
            ("{\"errno\":0,\"info\":{\"path\":\"/different\"}}", "CreatedMetadataMismatch"),
            ("{\"errno\":0,\"data\":[]}", "MalformedResponse"),
            ("{\"errno\":0,\"fs_id\":false}", "MalformedResponse"),
            ("{}", "MissingStatus"), ("[]", "MalformedResponse"),
            ("<html>login</html>", "UnexpectedHtml"), ("{\"errno\":12}", "Baidu:12")
        })
        {
            using var server = new FakeBaidu { CreateReply = (_, _) => FakeBaidu.Raw(reply) }; await using var client = Client(server);
            await Error(async () => _ = await Put(client, "/object", [4, 3, 2, 1]), code);
            // Even when the mock stored the bytes before an invalid/failed reply,
            // this explicit contradiction must fail, not be rewritten into success by GET.
            Assert(Calls(server, "create") == 1 && server.Downloads == 0 && Calls(server, "locate-download") == 0,
                "An invalid/failed create was accepted through a fallback download");
        }
        using var rapidServer = new FakeBaidu();
        rapidServer.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath == "/api/precreate"
            ? FakeBaidu.Raw("{\"errno\":0,\"return_type\":2,\"info\":{\"size\":7}}") : null);
        await using var rapid = Client(rapidServer);
        await Error(async () => _ = await Put(rapid, "/object", [1, 2]), "CreatedMetadataMismatch");
        Assert(rapidServer.Downloads == 0 && rapidServer.UploadedParts == 0 && Calls(rapidServer, "create") == 0,
            "Contradictory rapid acknowledgment was repaired by a payload read");
    }
}
