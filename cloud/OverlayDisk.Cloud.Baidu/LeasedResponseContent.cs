using System.Net;

namespace OverlayDisk.Cloud.Baidu;

/// <summary>The shared concurrency lease belongs to the complete response body, not just its headers.</summary>
internal sealed class LeasedResponseContent : HttpContent
{
    private readonly HttpContent inner;
    private IDisposable? lease;
    private CancellationTokenRegistration requestCancellation, clientCancellation;
    private int disposed;
    internal LeasedResponseContent(HttpContent inner, IDisposable lease, CancellationToken requestToken, CancellationToken clientToken)
    {
        this.inner = inner; this.lease = lease;
        foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        requestCancellation = requestToken.Register(static value => ((LeasedResponseContent)value!).Dispose(), this);
        if (Volatile.Read(ref disposed) != 0) requestCancellation.Dispose();
        clientCancellation = clientToken.Register(static value => ((LeasedResponseContent)value!).Dispose(), this);
        if (Volatile.Read(ref disposed) != 0) clientCancellation.Dispose();
    }
    protected override bool TryComputeLength(out long length)
    { length = inner.Headers.ContentLength ?? 0; return inner.Headers.ContentLength.HasValue; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => inner.CopyToAsync(stream, context);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => inner.CopyToAsync(stream, context, cancellationToken);
    protected override Task<Stream> CreateContentReadStreamAsync() => inner.ReadAsStreamAsync();
    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => inner.ReadAsStreamAsync(cancellationToken);
    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref disposed, 1) != 0) return;
        requestCancellation.Dispose(); clientCancellation.Dispose();
        try { if (disposing) inner.Dispose(); }
        finally { if (disposing) Interlocked.Exchange(ref lease, null)?.Dispose(); base.Dispose(disposing); }
    }
}
