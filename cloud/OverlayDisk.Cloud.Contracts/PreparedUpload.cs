using System.Security.Cryptography;

namespace OverlayDisk.Cloud.Contracts;

/// <summary>The expected immutable content identity. The caller obtains Sha256 from its authenticated sealed manifest.</summary>
public sealed record ImmutableObjectDescriptor(string Path, long Length, string Sha256);

/// <summary>
/// Optional upload capability. A confirmed receipt is a local proof scoped to this store's
/// account and immutable path; a miss is not proof of remote absence. Query before opening
/// the source. Implementations may validate the account before choosing their local scope.
/// </summary>
public interface ICloudPreparedUploadStore
{
    Task<CloudObjectInfo?> TryGetConfirmedReceiptAsync(ImmutableObjectDescriptor descriptor, CancellationToken cancellationToken = default);
    Task<CloudObjectInfo> PutPreparedAsync(PreparedUpload upload, CancellationToken cancellationToken = default);
}

/// <summary>
/// An opaque, independently owned immutable buffer, created only after checking the complete
/// input against the expected SHA-256. No writable array or caller-owned memory is retained.
/// Disposal prevents new readers; existing readers retain stable bytes until they close.
/// </summary>
public sealed class PreparedUpload : IDisposable
{
    public const int MaximumLength = ObjectTransport.MaximumWireLength;
    public const int PartLength = 4 * 1024 * 1024;
    private readonly object gate = new();
    private byte[]? buffer;
    private int readers;
    private bool disposed;

    private PreparedUpload(ImmutableObjectDescriptor descriptor, byte[] buffer, string md5, string[] partMd5)
    {
        Descriptor = descriptor;
        this.buffer = buffer;
        Md5 = md5;
        PartMd5 = Array.AsReadOnly(partMd5);
    }

    public ImmutableObjectDescriptor Descriptor { get; }
    public long Length => Descriptor.Length;
    public string Sha256 => Descriptor.Sha256;
    /// <summary>The complete object's MD5. Multipart uploads use PartMd5 instead.</summary>
    public string Md5 { get; }
    public IReadOnlyList<string> PartMd5 { get; }

    // Ownership is transferred by the codec, which never exposes this array.
    // Unlike CreateAsync, no expected wire hash exists before this one pass.
    internal static PreparedUpload FromOwnedBytes(string path, byte[] owned)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var part = owned.Length > PartLength ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;
        var parts = new List<string>();
        for (int position = 0; position < owned.Length;)
        {
            int count = Math.Min(64 * 1024, Math.Min(owned.Length - position, PartLength - position % PartLength));
            sha.AppendData(owned, position, count); md5.AppendData(owned, position, count); part?.AppendData(owned, position, count); position += count;
            if (part is not null && (position % PartLength == 0 || position == owned.Length)) parts.Add(Hex(part.GetHashAndReset()));
        }
        string hash = Hex(sha.GetHashAndReset()), whole = Hex(md5.GetHashAndReset()); if (part is null) parts.Add(whole);
        var descriptor = new ImmutableObjectDescriptor(path, owned.Length, hash); ValidateDescriptor(descriptor);
        return new(descriptor, owned, whole, parts.ToArray());
    }

    /// <summary>
    /// Read one finite object from the current position, without closing Content. Exactly Length
    /// bytes followed by EOF are required. SHA-256 and MD5 are updated during this single read;
    /// failure never returns a prepared object. The source's authentication is the caller's duty.
    /// </summary>
    public static async Task<PreparedUpload> CreateAsync(ImmutableObjectDescriptor descriptor, Stream content, CancellationToken cancellationToken = default)
    {
        ValidateDescriptor(descriptor);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead) throw new ArgumentException("The source must be readable.", nameof(content));
        var owned = new byte[checked((int)descriptor.Length)];
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            using var partMd5 = owned.Length > PartLength ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;
            var parts = new List<string>((owned.Length + PartLength - 1) / PartLength);
            int position = 0;
            while (position < owned.Length)
            {
                int count = await content.ReadAsync(owned.AsMemory(position, Math.Min(64 * 1024, Math.Min(owned.Length - position, PartLength - position % PartLength))), cancellationToken).ConfigureAwait(false);
                if (count == 0) throw new CloudProviderException("InputLengthMismatch", "The upload source ended before its declared length.");
                sha.AppendData(owned, position, count);
                md5.AppendData(owned, position, count);
                partMd5?.AppendData(owned, position, count);
                position += count;
                if (partMd5 is not null && (position % PartLength == 0 || position == owned.Length)) parts.Add(Hex(partMd5.GetHashAndReset()));
            }
            var extra = new byte[1];
            if (await content.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
                throw new CloudProviderException("InputLengthMismatch", "The upload source exceeds its declared length.");
            if (!CryptographicOperations.FixedTimeEquals(sha.GetHashAndReset(), Convert.FromHexString(descriptor.Sha256)))
                throw new CloudProviderException("InputHashMismatch", "The upload content does not match its expected SHA-256 hash.");
            cancellationToken.ThrowIfCancellationRequested();
            string whole = Hex(md5.GetHashAndReset());
            if (partMd5 is null) parts.Add(whole);
            return new PreparedUpload(descriptor with { Sha256 = descriptor.Sha256.ToLowerInvariant() }, owned, whole, parts.ToArray());
        }
        catch
        {
            CryptographicOperations.ZeroMemory(owned);
            throw;
        }
    }

    /// <summary>Open an independent, seekable read-only view. The underlying array cannot be obtained.</summary>
    public Stream OpenRead()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            readers++;
            return new Reader(this, buffer!);
        }
    }

    public static void ValidateDescriptor(ImmutableObjectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrEmpty(descriptor.Path) || !descriptor.Path.StartsWith('/') || descriptor.Path.Contains('\\') || descriptor.Path.Contains('\0') ||
            descriptor.Path.Split('/').Any(p => p is "." or ".."))
            throw new ArgumentException("An absolute immutable object path is required.", nameof(descriptor));
        if (descriptor.Length is <= 0 or > MaximumLength) throw new ArgumentOutOfRangeException(nameof(descriptor), "The prepared wire object exceeds its bounded transport size.");
        if (descriptor.Sha256 is null || descriptor.Sha256.Length != 64 || !descriptor.Sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("A complete SHA-256 hash is required.", nameof(descriptor));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    private void Release()
    {
        lock (gate) { readers--; ClearIfUnused(); }
    }
    private void ClearIfUnused()
    {
        if (!disposed || readers != 0 || buffer is null) return;
        CryptographicOperations.ZeroMemory(buffer); buffer = null;
    }
    public void Dispose()
    {
        lock (gate) { disposed = true; ClearIfUnused(); }
    }

    private sealed class Reader(PreparedUpload owner, byte[] bytes) : MemoryStream(bytes, 0, bytes.Length, false, false)
    {
        private int closed;
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Interlocked.Exchange(ref closed, 1) == 0) owner.Release();
        }
    }
}
