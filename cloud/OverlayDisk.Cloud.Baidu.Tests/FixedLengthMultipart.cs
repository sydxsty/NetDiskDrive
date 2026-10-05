using System.Net;
using System.Security.Cryptography;
using OverlayDisk.Cloud.Contracts;

internal static partial class Program
{
    private static async Task FixedLengthMultipartRetry()
    {
        byte[] input = new byte[CloudObjectGeometry.DefaultSize]; new Random(40331211).NextBytes(input);
        byte[] expected = input.ToArray(); string hash = Sha(expected);
        using var prepared = await PreparedUpload.CreateAsync(new("/fixed-length.obj", input.Length, hash), new MemoryStream(input, false));
        Array.Fill(input, (byte)0xFE); // The caller's original array cannot alter the prepared owner's bytes.
        using var server = new FakeBaidu(); int attempts = 0;
        server.Override = async (request, cancellation) =>
        {
            if (request.RequestUri!.AbsolutePath != "/rest/2.0/pcs/superfile2") return null;
            attempts++;
            var body = request.Content as MultipartFormDataContent ?? throw new Exception("The upload was not multipart");
            // Inspect the OUTER multipart before serializing. Setting only the nested
            // part's Content-Length does not make .NET calculate this total.
            long? declared = body.Headers.ContentLength;
            Assert(declared is not null && declared > expected.Length, "Outer multipart length is unknown; a real HTTP/1.1 handler would use chunked encoding");
            using var serialized = new MemoryStream();
            await body.CopyToAsync(serialized, cancellation);
            Assert(serialized.Length == declared, "Outer Content-Length differs from the complete serialized multipart body");
            byte[] part = await body.Single().ReadAsByteArrayAsync(cancellation);
            Assert(part.SequenceEqual(expected), "Prepared data changed while serializing or replaying an HTTP attempt");
            // The first real serialization is rejected; the next HTTP request must
            // reuse the owned bytes through a fresh bounded stream and exact length.
            return attempts == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null;
        };
        await using var client = Client(server);
        var receipt = await client.PutPreparedAsync(prepared);
        Assert(attempts == 2 && server.UploadedParts == 1 && receipt.Length == expected.Length && server.Files["/fixed-length.obj"].SequenceEqual(expected),
            "Length-delimited retry did not commit exactly the original 4 MiB object");
        await using var remaining = prepared.OpenRead();
        Assert(Convert.ToHexString(await SHA256.HashDataAsync(remaining)).Equals(hash, StringComparison.OrdinalIgnoreCase),
            "Retry or HttpContent disposal mutated the prepared owner's buffer");
    }
}
