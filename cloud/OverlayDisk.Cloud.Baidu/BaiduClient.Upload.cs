using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using OverlayDisk.Cloud.Contracts;

namespace OverlayDisk.Cloud.Baidu;

public sealed partial class BaiduClient
{
    private const int UploadPartSize = 4 * 1024 * 1024;

    public async Task<CloudObjectInfo?> TryGetConfirmedReceiptAsync(ImmutableObjectDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        PreparedUpload.ValidateDescriptor(descriptor);
        var path = PathValue(descriptor.Path);
        if (path == "/") throw new ArgumentException("The account root is not a file.", nameof(descriptor));
        var gate = PathGate(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // This is intentionally local: a miss neither reads the source nor
            // sends HEAD/LIST/download. Account validation can select the scope.
            var cache = await MetadataAsync(cancellationToken).ConfigureAwait(false);
            return cache is null ? null : Confirmed(await cache.LookupAsync(path, cancellationToken).ConfigureAwait(false), path, descriptor.Length, descriptor.Sha256);
        }
        finally { gate.Release(); }
    }

    public async Task<CloudObjectInfo> PutPreparedAsync(PreparedUpload upload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var path = PathValue(upload.Descriptor.Path);
        if (path == "/") throw new ArgumentException("The account root is not a file.", nameof(upload));
        if (upload.Length > options.MaximumUploadLength) throw new ArgumentOutOfRangeException(nameof(upload));
        // Hold one reader for the complete operation, including all retries.
        // Disposing the public owner cannot clear or mutate these in-flight bytes.
        await using var content = upload.OpenRead();
        return await PutVerifiedAsync(path, content, upload.Length, upload.Sha256, upload.PartMd5, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CloudObjectInfo> PutImmutableAsync(string path, Stream content, long length, string sha256,
        CancellationToken cancellationToken = default)
    {
        path = PathValue(path);
        if (path == "/") throw new ArgumentException("The account root is not a file.", nameof(path));
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead) throw new ArgumentException("Upload content must be readable.", nameof(content));
        if (length <= 0 || length > options.MaximumUploadLength) throw new ArgumentOutOfRangeException(nameof(length));
        if (sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw new ArgumentException("A complete SHA-256 hash is required.", nameof(sha256));
        if (length <= PreparedUpload.MaximumLength)
        {
            using var prepared = await PreparedUpload.CreateAsync(new(path, length, sha256), content, cancellationToken).ConfigureAwait(false);
            return await PutPreparedAsync(prepared, cancellationToken).ConfigureAwait(false);
        }
        // The general API also accepts larger finite objects. Its bounded spool
        // remains self-verifying; the prepared capability is limited to the 16 MiB object compression bound.
        await using var spool = new FileStream(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OverlayDisk-upload-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        var hashes = new List<string>();
        using var full = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[UploadPartSize];
        for (long remaining = length; remaining > 0;)
        {
            var count = (int)Math.Min(buffer.Length, remaining);
            await ReadExactlyUploadAsync(content, buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            full.AppendData(buffer, 0, count);
            hashes.Add(HashHex(MD5.HashData(buffer.AsSpan(0, count))));
            await spool.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            remaining -= count;
        }
        if (!string.Equals(HashHex(full.GetHashAndReset()), sha256, StringComparison.OrdinalIgnoreCase))
            throw new CloudProviderException("InputHashMismatch", "The upload content does not match its expected SHA-256 hash.");
        return await PutVerifiedAsync(path, spool, length, sha256, hashes, cancellationToken).ConfigureAwait(false);
    }

    private static CloudObjectInfo? Confirmed(CloudCacheLookup proof, string path, long length, string sha256)
    {
        if (proof.Item?.Sha256 is not { } knownHash) return null;
        if (proof.Item.Info.IsDirectory || proof.Item.Info.Length != length || !knownHash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new CloudObjectConflictException(path);
        return proof.Item.Info with { ReusedExisting = true };
    }

    private async Task<CloudObjectInfo> PutVerifiedAsync(string path, Stream spool, long length, string sha256,
        IReadOnlyList<string> hashes, CancellationToken cancellationToken, CanonicalObjectDescriptor? canonical = null)
    {
        var gate = PathGate(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = await MetadataAsync(cancellationToken).ConfigureAwait(false);
            if (cache is not null)
            {
                var proof = await cache.LookupAsync(path, cancellationToken).ConfigureAwait(false);
                if (Confirmed(proof, path, length, sha256) is { } receipt)
                {
                    if (canonical is not null) await cache.RememberVerifiedAsync(new(receipt with { ReusedExisting = false }, sha256.ToLowerInvariant(), canonical), cancellationToken).ConfigureAwait(false);
                    return receipt;
                }
            }
            var existing = await HeadAsync(path, cancellationToken).ConfigureAwait(false);
            if (existing is not null) return await VerifyExistingAsync(existing, length, sha256, cancellationToken, canonical: canonical).ConfigureAwait(false);
            await InitializeWebSessionAsync(cancellationToken).ConfigureAwait(false);
            await using var mutation = cache is null ? null : await cache.BeginMutationAsync([path], false, cancellationToken).ConfigureAwait(false);
            var blockList = JsonSerializer.Serialize(hashes);
            CloudObjectInfo? recoveryObject = null;
            var confirmed = await RetryAsync(async () =>
            {
                if (recoveryObject is not null) return await VerifyExistingAsync(recoveryObject, length, sha256, cancellationToken, canonical: canonical).ConfigureAwait(false);
                try
                {
                    using var precreate = await JsonAsync(HttpMethod.Post, WebUrl("api/precreate"), Form(
                        ("path", path), ("size", length.ToString(CultureInfo.InvariantCulture)), ("isdir", "0"),
                        ("autoinit", "1"), ("rtype", "0"), ("is_revision", "0"), ("block_list", blockList)),
                        "precreate", cancellationToken, native: true, retry: false).ConfigureAwait(false);
                    var pre = Data(precreate.RootElement);
                    var returnType = Num(pre, "return_type");
                    if (returnType == 2) return AcknowledgeUpload(precreate.RootElement, path, length, reusedExisting: true);
                    if (returnType != 1) throw new CloudProviderException("UnknownPrecreateResult", "Baidu returned an unsupported pre-upload state.");
                    var uploadId = Required(pre, "uploadid");
                    if (!pre.TryGetProperty("block_list", out var missing) || missing.ValueKind != JsonValueKind.Array)
                        throw new CloudProviderException("MissingUploadParts", "Baidu did not return the required upload part list.");
                    var indices = missing.EnumerateArray().Select(Number).ToArray();
                    if (indices.Length == 0) indices = [0L];
                    if (indices.Any(i => i is null || i < 0 || i >= hashes.Count) || indices.Distinct().Count() != indices.Length)
                        throw new CloudProviderException("InvalidUploadParts", "Baidu returned an invalid upload part list.");
                    var server = await LocateUploadAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var index in indices)
                    {
                        var part = checked((int)index!.Value);
                        var count = (int)Math.Min(UploadPartSize, length - (long)part * UploadPartSize);
                        Func<HttpContent?> body = () =>
                        {
                            var multipart = new MultipartFormDataContent();
                            var bytes = new StreamContent(new UploadPartStream(spool, (long)part * UploadPartSize, count));
                            bytes.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                            bytes.Headers.ContentLength = count;
                            multipart.Add(bytes, "file", "blob");
                            return multipart;
                        };
                        using var uploaded = await JsonAsync(HttpMethod.Post, Url(server, "rest/2.0/pcs/superfile2",
                            ("method", "upload"), ("app_id", "250528"), ("type", "tmpfile"), ("path", path),
                            ("uploadid", uploadId), ("partseq", part.ToString(CultureInfo.InvariantCulture))),
                            body, "upload-part", cancellationToken, requireErrno: false, native: true, retry: true).ConfigureAwait(false);
                        bool hasPartMd5 = false;
                        foreach (var partReply in new[] { uploaded.RootElement, Data(uploaded.RootElement) })
                        {
                            if (!partReply.TryGetProperty("md5", out var partMd5)) continue;
                            hasPartMd5 = true;
                            if (partMd5.ValueKind != JsonValueKind.String || !string.Equals(partMd5.GetString(), hashes[part], StringComparison.OrdinalIgnoreCase))
                            {
                                lock (cacheState) { uploadEndpoint = null; endpointValidUntil = default; }
                                throw new CloudProviderException("UploadChecksumMismatch", "Baidu returned a different uploaded part MD5 hash.");
                            }
                        }
                        if (!hasPartMd5) EnsureSuccess(uploaded.RootElement, required: true);
                    }
                    using var created = await JsonAsync(HttpMethod.Post, WebUrl("api/create", ("a", "commit")), Form(
                        ("path", path), ("size", length.ToString(CultureInfo.InvariantCulture)), ("isdir", "0"),
                        ("uploadid", uploadId), ("rtype", "0"), ("is_revision", "0"), ("block_list", blockList)),
                        "upload-commit", cancellationToken, native: true, retry: false).ConfigureAwait(false);
                    // An explicit successful create is the provider's completion receipt.
                    // Final MD5 may be transformed and optional fields may be omitted;
                    // neither condition causes the newly uploaded payload to be downloaded.
                    return AcknowledgeUpload(created.RootElement, path, length);
                }
                catch (CloudProviderException error) when (error.IsTransient || error.Code == "Baidu:-8")
                {
                    // Recover a lost final response by checking the same immutable path;
                    // never switch names or use rtype=3 overwrite during retry.
                    var afterFailure = await HeadCoreAsync(path, true, cancellationToken).ConfigureAwait(false);
                    if (afterFailure is not null)
                    {
                        recoveryObject = afterFailure;
                        return await VerifyExistingAsync(afterFailure, length, sha256, cancellationToken, canonical: canonical).ConfigureAwait(false);
                    }
                    throw;
                }
            }, cancellationToken).ConfigureAwait(false);
            if (mutation is not null) await mutation.CompleteAsync([new(confirmed with { ReusedExisting = false }, sha256.ToLowerInvariant(), canonical)]).ConfigureAwait(false);
            return confirmed;
        }
        finally { gate.Release(); }
    }

    private static CloudObjectInfo AcknowledgeUpload(JsonElement root, string path, long length, bool reusedExisting = false)
    {
        string? remoteId = null, checksum = null;
        void Check(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new CloudProviderException("MalformedResponse", "Baidu returned invalid upload metadata.");
            if (value.TryGetProperty("path", out var actualPath) && (actualPath.ValueKind != JsonValueKind.String || actualPath.GetString() != path) ||
                value.TryGetProperty("size", out var actualLength) && Number(actualLength) != length ||
                value.TryGetProperty("isdir", out var isDirectory) && Number(isDirectory) != 0)
                throw new CloudProviderException("CreatedMetadataMismatch", "Baidu's upload acknowledgment does not describe the requested object.");
            if (value.TryGetProperty("fs_id", out _))
            {
                var text = Text(value, "fs_id");
                if (!ulong.TryParse(text, out var number) || number == 0)
                    throw new CloudProviderException("MalformedResponse", "Baidu returned an invalid upload identity.");
                remoteId = text;
            }
            if (value.TryGetProperty("md5", out var md5))
            {
                if (md5.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw new CloudProviderException("MalformedResponse", "Baidu returned an invalid upload checksum field.");
                checksum = md5.ValueKind == JsonValueKind.String ? md5.GetString() : null;
            }
            // The checksum is provider metadata, not a second content check. In
            // particular, even a 32-character transformed MD5 is not compared.
        }
        void CheckInfo(JsonElement value)
        {
            Check(value);
            if (value.TryGetProperty("info", out var info) && info.ValueKind != JsonValueKind.Null) Check(info);
        }
        CheckInfo(root);
        if (root.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null) CheckInfo(data);
        return new(path, length, false, remoteId, checksum) { ReusedExisting = reusedExisting };
    }

    // Each HTTP attempt gets a seekable bounded view; disposing HttpContent leaves
    // the stable owner open. MultipartContent computes its total length from
    // StreamContent.TryComputeLength, not the part's Content-Length header.
    // A non-seekable view causes chunked HTTP transfer, which PCS rejects with 31211/403.
    // No part-sized copy or additional hash is needed.
    private sealed class UploadPartStream(Stream source, long offset, int length) : Stream
    {
        private int position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => position;
            set
            {
                if (value < 0 || value > length) throw new ArgumentOutOfRangeException(nameof(value));
                position = (int)value;
            }
        }
        public override int Read(byte[] buffer, int start, int count) => Read(buffer.AsSpan(start, count));
        public override int Read(Span<byte> buffer)
        {
            int count = Math.Min(buffer.Length, length - position);
            source.Position = offset + position;
            int read = source.Read(buffer[..count]); position += read; return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = Math.Min(buffer.Length, length - position);
            source.Position = offset + position;
            int read = await source.ReadAsync(buffer[..count], cancellationToken).ConfigureAwait(false); position += read; return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int start, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(start, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)
        {
            long start = origin switch
            {
                SeekOrigin.Begin => 0, SeekOrigin.Current => position, SeekOrigin.End => length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            Position = checked(start + offset);
            return position;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private async Task<CloudObjectInfo> VerifyExistingAsync(CloudObjectInfo item, long length, string sha256, CancellationToken cancellationToken, bool reusedExisting = true, CanonicalObjectDescriptor? canonical = null)
    {
        if (item.IsDirectory || item.Length != length) throw new CloudObjectConflictException(item.Path);
        await using var stream = await OpenReadKnownAsync(item, null, cancellationToken).ConfigureAwait(false);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(HashHex(actual), sha256, StringComparison.OrdinalIgnoreCase)) throw new CloudObjectConflictException(item.Path);
        if (await MetadataAsync(cancellationToken).ConfigureAwait(false) is { } cache)
            await cache.RememberVerifiedAsync(new(item with { ReusedExisting = false }, sha256.ToLowerInvariant(), canonical), cancellationToken).ConfigureAwait(false);
        return item with { ReusedExisting = reusedExisting };
    }

    private async Task<Uri> LocateUploadAsync(CancellationToken cancellationToken)
    {
        await endpointGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        lock (cacheState)
            if (uploadEndpoint is not null && endpointValidUntil > options.TimeProvider.GetUtcNow()) return uploadEndpoint;
        using var document = await JsonAsync(HttpMethod.Get, Url(Pcs, "rest/2.0/pcs/file",
            ("method", "locateupload"), ("upload_version", "2.0"), ("app_id", "250528")),
            null, "locate-upload", cancellationToken, requireErrno: false, native: true).ConfigureAwait(false);
        var root = Data(document.RootElement);
        var candidates = new List<string>();
        if (Text(root, "host") is { Length: > 0 } host) candidates.Add(host.Contains("://", StringComparison.Ordinal) ? host : "https://" + host);
        foreach (var name in new[] { "servers", "bak_servers" })
            if (root.TryGetProperty(name, out var servers) && servers.ValueKind == JsonValueKind.Array)
                foreach (var item in servers.EnumerateArray()) if (Text(item, "server") is { } server) candidates.Add(server);
        foreach (var candidate in candidates)
            if (TrySecureEndpoint(candidate, out var uri) && uri.AbsolutePath == "/" &&
                string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo))
            {
                var seconds = Num(root, "expire");
                var ttl = seconds is > 0 ? TimeSpan.FromSeconds(Math.Min(seconds.Value, options.UploadEndpointTtl.TotalSeconds)) : options.UploadEndpointTtl;
                lock (cacheState) { uploadEndpoint = uri; endpointValidUntil = options.TimeProvider.GetUtcNow() + ttl; }
                return uri;
            }
        throw new CloudProviderException("MissingUploadServer", "Baidu did not provide a supported upload endpoint.");
        }
        finally { endpointGate.Release(); }
    }

    private static async Task ReadExactlyUploadAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try { await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false); }
        catch (EndOfStreamException) { throw new CloudProviderException("InputLengthMismatch", "The upload stream ended before its declared length."); }
    }
}
