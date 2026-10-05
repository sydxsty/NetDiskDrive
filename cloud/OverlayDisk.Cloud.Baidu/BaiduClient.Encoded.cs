using OverlayDisk.Cloud.Contracts;

namespace OverlayDisk.Cloud.Baidu;

public sealed partial class BaiduClient
{
    public async Task<CloudObjectInfo?> TryGetEncodedReceiptAsync(CanonicalObjectDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ObjectTransport.ValidateDescriptor(descriptor); string path = PathValue(descriptor.Path);
        var gate = PathGate(path); await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = await MetadataAsync(cancellationToken).ConfigureAwait(false);
            if (cache is null) return null;
            var proof = await cache.LookupAsync(path, cancellationToken).ConfigureAwait(false);
            if (proof.Item?.Canonical is not { } saved) return null;
            if (saved.Path != descriptor.Path || saved.Length != descriptor.Length || saved.Codec != descriptor.Codec ||
                !saved.Sha256.Equals(descriptor.Sha256, StringComparison.OrdinalIgnoreCase) || proof.Item.Info.IsDirectory)
                throw new CloudObjectConflictException(path);
            return proof.Item.Info with { ReusedExisting = true };
        }
        finally { gate.Release(); }
    }
    public async Task<CloudObjectInfo> PutEncodedAsync(PreparedObjectUpload upload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload); ObjectTransport.ValidateDescriptor(upload.Canonical);
        var wire = upload.Wire; string path = PathValue(upload.Canonical.Path);
        if (wire.Descriptor.Path != path || wire.Length > options.MaximumUploadLength || wire.Length > ObjectTransport.MaxWireLength(upload.Canonical.Length))
            throw new ArgumentException("Encoded object descriptor mismatch.", nameof(upload));
        await using var body = wire.OpenRead();
        return await PutVerifiedAsync(path, body, wire.Length, wire.Sha256, wire.PartMd5, cancellationToken, upload.Canonical).ConfigureAwait(false);
    }
}
