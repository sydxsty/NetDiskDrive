namespace OverlayDisk.Cloud.Contracts;

/// <summary>
/// Optional, fresh directory enumeration for immutable version discovery. File children are
/// yielded in descending ordinal filename order; directory children may be grouped separately.
/// Implementations fetch pages on demand and must not use cached negative directory proofs.
/// Stopping early proves nothing about unreturned siblings. Returned metadata may be cached as
/// positive observations only, never as a complete directory listing.
/// </summary>
public interface ICloudDescendingDirectoryReader
{
    IAsyncEnumerable<CloudObjectInfo> ListByNameDescendingAsync(
        string directory, CancellationToken cancellationToken = default);
}
