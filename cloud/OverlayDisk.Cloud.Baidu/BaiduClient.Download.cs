using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OverlayDisk.Cloud.Contracts;

namespace OverlayDisk.Cloud.Baidu;

public sealed partial class BaiduClient
{
    public async Task<Stream> OpenReadBoundedAsync(string path, int maximumLength, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this); path = PathValue(path);
        if (maximumLength <= 0 || maximumLength > ObjectTransport.MaximumWireLength) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        return await RetryAsync(async () =>
        {
            var link = await LocateDownloadAsync(path, cancellationToken).ConfigureAwait(false);
            var response = await SendAsync(HttpMethod.Get, link, null, "download", cancellationToken, native: true).ConfigureAwait(false);
            try
            {
                if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
                    throw new CloudProviderException("UnexpectedHtml", "Baidu returned an error page instead of compressed object data.", authenticationRequired: true);
                if (response.StatusCode != HttpStatusCode.OK) throw new CloudProviderException("UnexpectedDownloadStatus", "Baidu did not return a complete object response.");
                long? length = response.Content.Headers.ContentLength;
                if (length is <= 0 || length > maximumLength) throw new CloudProviderException("LengthMismatch", "Compressed download exceeds its bounded length.");
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return (Stream)new VerifiedLengthStream(stream, response, length ?? maximumLength, options.RequestTimeout, cancellationToken, exactLength: length.HasValue);
            }
            catch { response.Dispose(); throw; }
        }, cancellationToken).ConfigureAwait(false);
    }
    public Task<Stream> OpenReadKnownAsync(ImmutableObjectDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        PreparedUpload.ValidateDescriptor(descriptor);
        string path = PathValue(descriptor.Path);
        cancellationToken.ThrowIfCancellationRequested();
        // Expected hash belongs to the caller's authenticated manifest. This path
        // avoids Head/LIST; the existing stream still enforces exact byte length.
        return OpenReadKnownAsync(new CloudObjectInfo(path, descriptor.Length, false), null, cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string path, CloudByteRange? range = null, CancellationToken cancellationToken = default)
    {
        path = PathValue(path);
        var item = await HeadAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new CloudObjectNotFoundException(path);
        return await OpenReadKnownAsync(item, range, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Stream> OpenReadKnownAsync(CloudObjectInfo item, CloudByteRange? range, CancellationToken cancellationToken)
    {
        if (item.IsDirectory) throw new CloudProviderException("IsDirectory", "A directory cannot be downloaded as an object.");
        if (range is { } requested && (requested.Offset < 0 || requested.Length <= 0 ||
            requested.Offset > item.Length || requested.Length > item.Length - requested.Offset))
            throw new ArgumentOutOfRangeException(nameof(range));
        return await RetryAsync(async () =>
        {
            var link = await LocateDownloadAsync(item.Path, cancellationToken).ConfigureAwait(false);
            var response = await SendAsync(HttpMethod.Get, link, null, "download", cancellationToken, range, native: true).ConfigureAwait(false);
            try
            {
                if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
                    throw new CloudProviderException("UnexpectedHtml", "Baidu returned a login or error page instead of object data.", authenticationRequired: true);
                var expected = range?.Length ?? item.Length;
                if (range is { } bytes)
                {
                    var received = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent || received is null || received.Unit != "bytes" ||
                        received.From != bytes.Offset || received.To != bytes.Offset + bytes.Length - 1 || received.Length != item.Length)
                        throw new CloudProviderException("RangeNotHonored", "Baidu did not return the exact requested byte range.");
                }
                else if (response.StatusCode != HttpStatusCode.OK)
                    throw new CloudProviderException("UnexpectedDownloadStatus", "Baidu did not return a complete object response.");
                if (response.Content.Headers.ContentLength is { } actual && actual != expected)
                    throw new CloudProviderException("LengthMismatch", "The download length differs from the requested object.");
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return (Stream)new VerifiedLengthStream(stream, response, expected, options.RequestTimeout, cancellationToken);
            }
            catch { response.Dispose(); throw; }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Uri> LocateDownloadAsync(string path, CancellationToken cancellationToken)
    {
        var identity = account ?? await ValidateAsync(cancellationToken).ConfigureAwait(false);
        var bduss = RequireBduss();
        var first = HashHex(SHA1.HashData(Encoding.UTF8.GetBytes(bduss)));
        var device = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(bduss))) + "|0";
        var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = HashHex(SHA1.HashData(Encoding.UTF8.GetBytes(first + identity.AccountId +
            "ebrcUYiuxaZv2XGu7KIYKxUrqfnOfpDF" + time + device)));
        using var document = await JsonAsync(HttpMethod.Get, Url(Pcs, "rest/2.0/pcs/file",
            ("method", "locatedownload"), ("app_id", "250528"), ("path", path), ("ver", "4.0"),
            ("clienttype", "17"), ("channel", "0"), ("ant", "1"), ("check_blue", "1"), ("es", "1"), ("esl", "1"),
            ("apn_id", "1_0"), ("freeisp", "0"), ("queryfree", "0"), ("use", "0"),
            ("time", time), ("rand", signature), ("devuid", device), ("cuid", device)),
            null, "locate-download", cancellationToken, requireErrno: false, native: true).ConfigureAwait(false);
        var root = Data(document.RootElement);
        if (root.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
            foreach (var entry in urls.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || Num(entry, "encrypt") is > 0) continue;
                if (TrySecureEndpoint(Text(entry, "url"), out var uri)) return uri;
            }
        throw new CloudProviderException("MissingDownloadLink", "Baidu did not provide a supported download URL.");
    }
}

/// <summary>Owns the response and checks early EOF/extra bytes. No signed URLs escape through exceptions.</summary>
internal sealed class VerifiedLengthStream(Stream inner, HttpResponseMessage response, long length,
    TimeSpan readTimeout, CancellationToken requestCancellation, bool exactLength = true) : Stream
{
    private long position;
    private bool ended;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => exactLength ? length : throw new NotSupportedException();
    public override long Position { get => position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override int Read(Span<byte> buffer)
    {
        var temporary = new byte[buffer.Length];
        var count = Read(temporary, 0, temporary.Length);
        temporary.AsSpan(0, count).CopyTo(buffer);
        return count;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0 || ended) return 0;
        cancellationToken.ThrowIfCancellationRequested(); requestCancellation.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, requestCancellation);
        timeout.CancelAfter(readTimeout);
        try
        {
            if (position == length)
            {
                var extra = new byte[1];
                if (await inner.ReadAsync(extra, timeout.Token).ConfigureAwait(false) != 0)
                    throw new CloudProviderException("LengthMismatch", "Baidu returned more bytes than the declared object length.");
                ended = true;
                response.Dispose(); // Release the global slot on a verified EOF, even before caller disposal.
                return 0;
            }
            var count = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, length - position)], timeout.Token).ConfigureAwait(false);
            if (count == 0)
            {
                if (exactLength) throw new CloudProviderException("TruncatedDownload", "Baidu closed the object stream before all bytes arrived.", isTransient: true);
                ended = true; response.Dispose(); return 0;
            }
            position += count;
            return count;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !requestCancellation.IsCancellationRequested)
        { response.Dispose(); throw new CloudProviderException("DownloadTimeout", "The Baidu object stream timed out.", isTransient: true); }
        catch (HttpRequestException) { response.Dispose(); throw new CloudProviderException("DownloadInterrupted", "The Baidu object stream was interrupted.", isTransient: true); }
        catch (IOException error) when (error is not CloudProviderException)
        { response.Dispose(); throw new CloudProviderException("DownloadInterrupted", "The Baidu object stream was interrupted.", isTransient: true); }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || requestCancellation.IsCancellationRequested)
        { response.Dispose(); throw new OperationCanceledException(cancellationToken.IsCancellationRequested ? cancellationToken : requestCancellation); }
        catch { response.Dispose(); throw; }
    }
    protected override void Dispose(bool disposing)
    { try { if (disposing) inner.Dispose(); } finally { if (disposing) response.Dispose(); base.Dispose(disposing); } }
    public override async ValueTask DisposeAsync()
    { try { await inner.DisposeAsync().ConfigureAwait(false); } finally { response.Dispose(); GC.SuppressFinalize(this); } }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
