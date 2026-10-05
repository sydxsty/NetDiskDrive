namespace OverlayDisk.Cloud.Contracts;

/// <summary>
/// Optional full-object read with a caller-known immutable descriptor. Implementations may
/// skip metadata lookup, but must preserve HTTP success and exact declared stream length.
/// The caller owns the returned stream, must read through EOF and verify its SHA-256 before
/// accepting the bytes. A well-formed descriptor alone is not proof of authenticated content.
/// This capability accepts finite objects between 1 byte and PreparedUpload.MaximumLength; it does not imply Range.
/// </summary>
public interface ICloudKnownObjectReader
{
    Task<Stream> OpenReadKnownAsync(ImmutableObjectDescriptor descriptor, CancellationToken cancellationToken = default);
}
