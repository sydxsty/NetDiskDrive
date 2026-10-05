using System.Net;
using System.Text.Json;
using OverlayDisk.Cloud.Contracts;

internal static partial class Program
{
    private static readonly (string Name, Func<Task> Run)[] EncodedObjectTests =
    [
        ("encoded-object: canonical receipts survive restart and skip payload and cloud probes", EncodedReceipts),
        ("encoded-object: incompressible wire uses five exact parts and retries only its failed part", EncodedMultipart),
        ("encoded-object: bounded downloads use locate and transfer only and reject short extra or oversized data", EncodedDownloads),
        ("encoded-object: uncertain completion and failed mutations never invent canonical receipts", EncodedRecovery)
    ];
    private static async Task EncodedReceipts()
    {
        string folder = CacheFolder(); using var server = new FakeBaidu { OpaqueMd5 = true };
        byte[] raw = new byte[8 << 20]; new Random(442).NextBytes(raw.AsSpan(0, 16384));
        var descriptor = new CanonicalObjectDescriptor("/encoded/confirmed.obj", raw.Length, Sha(raw));
        byte[] wire;
        try
        {
            await using (var client = Cached(server, folder))
            {
                await client.CreateDirectoryAsync("/encoded"); server.Calls.Clear();
                Assert(await client.TryGetEncodedReceiptAsync(descriptor) is null && server.Calls.Count == 0, "Canonical receipt miss probed remote metadata");
                using var source = new CountedSource(raw);
                using var upload = await PreparedObjectUpload.CreateAsync(descriptor, source);
                wire = await TransportWireAsync(upload);
                var result = await client.PutEncodedAsync(upload);
                Assert(!result.ReusedExisting && result.Length == wire.Length && source.BytesRead == raw.Length && server.Downloads == 0 && server.Calls.Count == 4,
                    "Fresh compressed upload changed its source count, wire length or four-request acknowledgment path");
                server.Calls.Clear();
                Assert(await client.TryGetEncodedReceiptAsync(descriptor) is { ReusedExisting: true } && server.Calls.Count == 0, "Canonical receipt did not skip source and requests");
                await Error(async () => _ = await client.TryGetEncodedReceiptAsync(descriptor with { Sha256 = new string('0', 64) }), "ObjectConflict");
                // A generic wire proof alone must not grant canonical identity.
                string generic = "/encoded/generic.obj";
                await Put(client, generic, wire); server.Calls.Clear();
                Assert(await client.TryGetEncodedReceiptAsync(descriptor with { Path = generic }) is null && server.Calls.Count == 0, "Wire checksum was treated as an authenticated canonical receipt");
                byte[] json = "{\"ordinary\":true}"u8.ToArray(); await Put(client, "/encoded/owner.json", json);
                Assert(server.Files["/encoded/owner.json"].SequenceEqual(json), "JSON was silently compressed");
            }
            server.Calls.Clear();
            await using (var client = Cached(server, folder))
            {
                var result = await client.TryGetEncodedReceiptAsync(descriptor);
                Assert(result is { ReusedExisting: true } && result.Length == wire.Length && server.Calls.Count == 1 && Calls(server, "account") == 1,
                    "Restart lost canonical proof or did more than account scope validation");
                await client.DeleteAsync(descriptor.Path); server.Calls.Clear();
                Assert(await client.TryGetEncodedReceiptAsync(descriptor) is null && server.Calls.Count == 0, "Manual deletion retained a reusable canonical proof");
            }
            server.AccountId = 999; server.Calls.Clear();
            await using (var different = Cached(server, folder))
                Assert(await different.TryGetEncodedReceiptAsync(descriptor) is null && server.Calls.Count == 1 && Calls(server, "account") == 1, "Encoded receipt crossed account identity");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private static async Task EncodedMultipart()
    {
        byte[] raw = new byte[16 << 20]; new Random(799).NextBytes(raw);
        using var source = new CountedSource(raw);
        using var upload = await PreparedObjectUpload.CreateAsync(new("/encoded/random.obj", raw.Length, Sha(raw)), source);
        byte[] wire = await TransportWireAsync(upload); Assert(upload.Wire.PartMd5.Count == 5, "Encoded ciphertext overhead did not retain fifth part");
        using var server = new FakeBaidu { OpaqueMd5 = true }; await using var client = Cached(server);
        await client.CreateDirectoryAsync("/encoded"); server.Calls.Clear(); var attempts = new Dictionary<int, int>();
        server.Override = async (request, ct) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path is "/api/precreate" or "/api/create")
            {
                var form = GeometryForm(await request.Content!.ReadAsStringAsync(ct));
                Assert(long.Parse(form["size"]) == wire.Length && JsonSerializer.Deserialize<string[]>(form["block_list"])!.SequenceEqual(upload.Wire.PartMd5), "Multipart control fields described canonical instead of wire data");
            }
            if (path != "/rest/2.0/pcs/superfile2") return null;
            int part = int.Parse(GeometryForm(request.RequestUri.Query)["partseq"]); attempts[part] = attempts.GetValueOrDefault(part) + 1;
            var body = (MultipartFormDataContent)request.Content!; long? length = body.Headers.ContentLength;
            using var serialized = new MemoryStream(); await body.CopyToAsync(serialized, ct);
            byte[] payload = await body.Single().ReadAsByteArrayAsync(ct);
            int offset = part * PreparedUpload.PartLength, count = Math.Min(PreparedUpload.PartLength, wire.Length - offset);
            Assert(length.HasValue && serialized.Length == length && payload.AsSpan().SequenceEqual(wire.AsSpan(offset, count)), "Encoded multipart framing/part offsets were incorrect");
            return part == 3 && attempts[part] == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null;
        };
        var result = await client.PutEncodedAsync(upload);
        Assert(result.Length == wire.Length && server.Files[upload.Canonical.Path].SequenceEqual(wire) && server.Downloads == 0 && source.BytesRead == raw.Length, "Encoded upload reread input/downloaded/changed wire");
        Assert(attempts.Count == 5 && attempts.All(p => p.Value == (p.Key == 3 ? 2 : 1)) && Calls(server, "precreate") == 1 && Calls(server, "create") == 1 && Calls(server, "list") == 0, "Failed fragment restarted a complete upload or added parent probes");
    }
    private static async Task EncodedDownloads()
    {
        byte[] raw = new byte[4 << 20]; raw[7000] = 61; var descriptor = new CanonicalObjectDescriptor("/encoded/read.obj", raw.Length, Sha(raw));
        using var upload = await PreparedObjectUpload.CreateAsync(descriptor, new MemoryStream(raw, false)); byte[] wire = await TransportWireAsync(upload);
        using var server = new FakeBaidu(); server.Files[descriptor.Path] = wire;
        await using var client = Client(server); await client.ValidateAsync();
        int maximum = ObjectTransport.MaxWireLength(raw.Length);
        foreach (bool chunked in new[] { false, true })
        {
            server.Calls.Clear(); server.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(chunked && request.RequestUri!.AbsolutePath == "/content"
                ? new(HttpStatusCode.OK) { Content = new StreamContent(new ForwardStream(wire)) } : null);
            await using var body = await client.OpenReadBoundedAsync(descriptor.Path, maximum); using var copy = new MemoryStream(); await body.CopyToAsync(copy);
            Assert(ObjectTransport.Decode(copy.ToArray(), descriptor).SequenceEqual(raw) && server.Calls.Count == 2 && Calls(server, "list") == 0, "Bounded transfer probed metadata or failed supported HTTP length framing");
        }
        foreach (string mode in new[] { "short", "extra", "oversized", "declared-short" })
        {
            server.Override = (request, _) =>
            {
                if (request.RequestUri!.AbsolutePath != "/content") return Task.FromResult<HttpResponseMessage?>(null);
                byte[] bytes = mode == "short" ? wire[..^1] : mode == "extra" ? [.. wire, 0] : mode == "oversized" ? new byte[maximum + 1] : wire[..^1];
                HttpContent content = mode == "oversized" ? new StreamContent(new ForwardStream(bytes)) : new ByteArrayContent(bytes);
                if (mode == "declared-short") content.Headers.ContentLength = wire.Length;
                return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK) { Content = content });
            };
            bool rejected = false;
            try { await using var body = await client.OpenReadBoundedAsync(descriptor.Path, maximum); using var copy = new MemoryStream(); await body.CopyToAsync(copy); _ = ObjectTransport.Decode(copy.ToArray(), descriptor); }
            catch (IOException) { rejected = true; }
            Assert(rejected, "Invalid compressed HTTP body accepted: " + mode);
        }
        server.Calls.Clear(); bool invalid = false;
        try { await client.OpenReadBoundedAsync(descriptor.Path, ObjectTransport.MaximumWireLength + 1); } catch (ArgumentOutOfRangeException) { invalid = true; }
        Assert(invalid && server.Calls.Count == 0, "Unbounded compressed request reached network");
    }
    private static async Task EncodedRecovery()
    {
        byte[] raw = new byte[4 << 20]; raw[77] = 55;
        var descriptor = new CanonicalObjectDescriptor("/encoded/recover.obj", raw.Length, Sha(raw));
        using var upload = await PreparedObjectUpload.CreateAsync(descriptor, new MemoryStream(raw, false));
        foreach (bool truncated in new[] { false, true })
        {
            string folder = CacheFolder(); using var server = new FakeBaidu { LoseCommitReply = true, DownloadLengthDelta = truncated ? -1 : 0 };
            try
            {
                await using (var client = Cached(server, folder))
                {
                    await client.CreateDirectoryAsync("/encoded");
                    if (truncated) await Error(() => client.PutEncodedAsync(upload), "TruncatedDownload");
                    else _ = await client.PutEncodedAsync(upload);
                    Assert(server.Downloads == (truncated ? 2 : 1), "Uncertain completion did not use its bounded wire conflict verification attempts: " + server.Downloads);
                }
                server.Calls.Clear();
                await using var reopened = Cached(server, folder);
                Assert((await reopened.TryGetEncodedReceiptAsync(descriptor) is not null) == !truncated && Calls(server, "list") == 0 && Calls(server, "download") == 0,
                    "Failed verification invented a durable canonical receipt");
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
    }
}
