using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using OverlayDisk.Cloud.Contracts;

internal static partial class Program
{
    private static async Task ObjectSizeMultipart()
    {
        foreach (int size in new[] { 4, 8, 16 }.Select(n => n * 1024 * 1024))
        {
            byte[] input = new byte[size]; new Random(size + 73).NextBytes(input); byte[] expected = input.ToArray();
            var descriptor = new ImmutableObjectDescriptor("/geometry/object-" + size, size, Sha(input));
            using var source = new CountedSource(input);
            using var prepared = await PreparedUpload.CreateAsync(descriptor, source);
            string[] parts = Enumerable.Range(0, size / PreparedUpload.PartLength)
                .Select(i => Convert.ToHexString(MD5.HashData(expected.AsSpan(i * PreparedUpload.PartLength, PreparedUpload.PartLength))).ToLowerInvariant()).ToArray();
            Assert(prepared.PartMd5.SequenceEqual(parts) && prepared.Md5 == Md5(expected) && source.BytesRead == size,
                "Factory hashes did not cover the exact object/4 MiB boundaries in one source read");
            Array.Fill(input, (byte)0xAF);
            using var server = new FakeBaidu { OpaqueMd5 = true }; await using var client = Cached(server);
            await client.CreateDirectoryAsync("/geometry"); server.Calls.Clear();
            var attempts = new Dictionary<int, int>(); int rejectedPart = parts.Length > 1 ? 1 : 0;
            server.Override = async (request, ct) =>
            {
                string path = request.RequestUri!.AbsolutePath;
                if (path is "/api/precreate" or "/api/create")
                {
                    var form = GeometryForm(await request.Content!.ReadAsStringAsync(ct));
                    Assert(long.Parse(form["size"]) == size && JsonSerializer.Deserialize<string[]>(form["block_list"])!.SequenceEqual(parts),
                        "Precreate/create used the whole-object MD5 as one oversized part");
                }
                if (path != "/rest/2.0/pcs/superfile2") return null;
                var query = GeometryForm(request.RequestUri.Query); int index = int.Parse(query["partseq"]);
                attempts[index] = attempts.GetValueOrDefault(index) + 1;
                var body = (MultipartFormDataContent)request.Content!;
                long? declared = body.Headers.ContentLength;
                using var serialized = new MemoryStream(); await body.CopyToAsync(serialized, ct);
                Assert(declared is > PreparedUpload.PartLength && serialized.Length == declared, "Multipart lost exact outer Content-Length");
                byte[] payload = await body.Single().ReadAsByteArrayAsync(ct);
                Assert(payload.Length == PreparedUpload.PartLength && payload.AsSpan().SequenceEqual(expected.AsSpan(index * PreparedUpload.PartLength, PreparedUpload.PartLength)),
                    "Part position/retry changed the immutable bytes");
                return index == rejectedPart && attempts[index] == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null;
            };
            var receipt = await client.PutPreparedAsync(prepared);
            Assert(receipt.Length == size && server.Files[descriptor.Path].SequenceEqual(expected) && server.Downloads == 0,
                "Multipart merge changed length/content or downloaded a successful new upload");
            Assert(Calls(server, "precreate") == 1 && Calls(server, "create") == 1 && Calls(server, "locate-upload") == 1
                && attempts.Count == parts.Length && attempts.All(p => p.Value == (p.Key == rejectedPart ? 2 : 1)),
                "A failed part restarted/retransmitted the whole object");
            server.Calls.Clear();
            Assert((await client.TryGetConfirmedReceiptAsync(descriptor))?.Length == size && server.Calls.Count == 0,
                "Confirmed receipt lost the actual large object length");
            await using var read = prepared.OpenRead(); Assert(Convert.ToHexString(await SHA256.HashDataAsync(read)).Equals(descriptor.Sha256, StringComparison.OrdinalIgnoreCase), "HTTP retries mutated the prepared owner");
        }
    }

    private static async Task ObjectSizeMultipartRejects()
    {
        byte[] bytes = new byte[8 * 1024 * 1024]; new Random(881).NextBytes(bytes);
        using var prepared = await PreparedUpload.CreateAsync(new("/geometry/reject", bytes.Length, Sha(bytes)), new MemoryStream(bytes, false));
        foreach (bool invalidIndex in new[] { true, false })
        {
            using var server = new FakeBaidu(); await using var client = Client(server);
            server.Override = (request, _) =>
            {
                string path = request.RequestUri!.AbsolutePath;
                if (invalidIndex && path == "/api/precreate") return Task.FromResult<HttpResponseMessage?>(FakeBaidu.Raw("{\"errno\":0,\"return_type\":1,\"uploadid\":\"fixture\",\"block_list\":[2]}"));
                if (!invalidIndex && path == "/rest/2.0/pcs/superfile2" && GeometryForm(request.RequestUri.Query)["partseq"] == "1")
                    return Task.FromResult<HttpResponseMessage?>(FakeBaidu.Raw("{\"md5\":\"00000000000000000000000000000000\"}"));
                return Task.FromResult<HttpResponseMessage?>(null);
            };
            await Error(() => client.PutPreparedAsync(prepared), invalidIndex ? "InvalidUploadParts" : "UploadChecksumMismatch");
            Assert(Calls(server, "create") == 0 && server.Downloads == 0, "Invalid multipart input reached commit or readback");
        }
        await Error(async () => { using var _ = await PreparedUpload.CreateAsync(prepared.Descriptor with { Length = 4 * 1024 * 1024 }, new MemoryStream(bytes, false)); }, "InputLengthMismatch");
    }

    private static async Task ObjectSizeKnownReads()
    {
        foreach (int size in new[] { 8, 16 }.Select(n => n * 1024 * 1024))
        {
            byte[] bytes = new byte[size]; new Random(size).NextBytes(bytes);
            using var server = new FakeBaidu(); server.Files["/geometry/read"] = bytes;
            await using var client = Client(server); await client.ValidateAsync(); server.Calls.Clear();
            await using var body = await client.OpenReadKnownAsync(new("/geometry/read", size, Sha(bytes)));
            Assert(Convert.ToHexString(await SHA256.HashDataAsync(body)).Equals(Sha(bytes), StringComparison.OrdinalIgnoreCase), "Large known download content differs");
            Assert(server.Calls.Count == 2 && Calls(server, "list") == 0, "Large known read introduced parent metadata probes");
        }
    }
    private static Dictionary<string, string> GeometryForm(string value) => value.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(v => v.Split('=', 2)).ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p.Length == 2 ? p[1] : ""));
}
