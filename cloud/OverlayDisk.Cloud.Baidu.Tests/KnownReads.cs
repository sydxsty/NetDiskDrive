using System.Net;
using System.Security.Cryptography;
using OverlayDisk.Cloud.Contracts;

internal static partial class Program
{
    private static async Task KnownReadCounts()
    {
        byte[] bytes = new byte[4 * 1024 * 1024]; new Random(42).NextBytes(bytes);
        using var server = new FakeBaidu(); server.Files["/known/opaque.obj"] = bytes;
        // No directory is seeded: even a single hidden Head/LIST would reject.
        await using var client = Client(server); await client.ValidateAsync(); server.Calls.Clear();
        ICloudKnownObjectReader reader = client;
        await using (var stream = await reader.OpenReadKnownAsync(new("/known/opaque.obj", bytes.Length, Sha(bytes))))
            Assert(Convert.ToHexString(await SHA256.HashDataAsync(stream)).Equals(Sha(bytes), StringComparison.OrdinalIgnoreCase), "Known object bytes changed");
        Assert(server.Calls.Count == 2 && Calls(server, "locate-download") == 1 && Calls(server, "download") == 1 && Calls(server, "list") == 0,
            "Warm known descriptor required a metadata request");
        using var coldServer = new FakeBaidu(); coldServer.Files["/known/opaque.obj"] = bytes;
        await using var cold = Client(coldServer);
        await using (var stream = await ((ICloudKnownObjectReader)cold).OpenReadKnownAsync(new("/known/opaque.obj", bytes.Length, Sha(bytes))))
            await stream.CopyToAsync(Stream.Null);
        Assert(coldServer.Calls.Count == 3 && Calls(coldServer, "account") == 1 && Calls(coldServer, "list") == 0,
            "Cold known descriptor scanned its parent instead of only validating the session");
    }

    private static async Task KnownReadValidation()
    {
        using var server = new FakeBaidu(); await using var client = Client(server); ICloudKnownObjectReader reader = client;
        var valid = new ImmutableObjectDescriptor("/known/object", 4 * 1024 * 1024, new string('a', 64));
        foreach (var descriptor in new ImmutableObjectDescriptor?[] { null, valid with { Path = "relative" }, valid with { Path = "/known/../object" },
            valid with { Length = 0 }, valid with { Length = PreparedUpload.MaximumLength + 1L }, valid with { Sha256 = new string('g', 64) }, valid with { Sha256 = "abcd" } })
        {
            try { await reader.OpenReadKnownAsync(descriptor!); throw new Exception("Invalid descriptor reached download"); }
            catch (ArgumentException) { }
        }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await reader.OpenReadKnownAsync(valid, cancellation.Token); throw new Exception("Canceled known read started"); }
        catch (OperationCanceledException) { }
        Assert(server.Calls.Count == 0, "Descriptor rejection made an HTTP request");
    }

    private static async Task KnownReadLengths()
    {
        byte[] bytes = [3, 5, 7, 9]; var descriptor = new ImmutableObjectDescriptor("/known/object", bytes.Length, Sha(bytes));
        foreach (var (delta, error) in new[] { (-1, "TruncatedDownload"), (1, "LengthMismatch") })
        {
            using var server = new FakeBaidu { DownloadLengthDelta = delta }; server.Files[descriptor.Path] = bytes;
            await using var client = Client(server);
            await using var stream = await ((ICloudKnownObjectReader)client).OpenReadKnownAsync(descriptor);
            await Error(() => stream.CopyToAsync(Stream.Null), error);
            Assert(Calls(server, "list") == 0, "Failed known read attempted metadata lookup");
        }
        using var mismatch = new FakeBaidu(); mismatch.Files[descriptor.Path] = bytes;
        mismatch.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath == "/content"
            ? new(HttpStatusCode.OK) { Content = new ByteArrayContent([3]) } : null);
        await using var wrongLength = Client(mismatch);
        await Error(async () => { await using var _ = await ((ICloudKnownObjectReader)wrongLength).OpenReadKnownAsync(descriptor); }, "LengthMismatch");
        mismatch.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath == "/content"
            ? new(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes) } : null);
        await Error(async () => { await using var _ = await ((ICloudKnownObjectReader)wrongLength).OpenReadKnownAsync(descriptor); }, "UnexpectedDownloadStatus");
    }
}
