namespace OverlayDisk.Cloud.Contracts;

public sealed record CloudObjectInfo(
    string Path,
    long Length,
    bool IsDirectory,
    string? RemoteId = null,
    string? ProviderChecksum = null,
    DateTimeOffset? LastModified = null)
{
    /// <summary>Put found or recovered an already stored object, rather than confirming a new create.</summary>
    public bool ReusedExisting { get; init; }
}

public sealed record CloudAccountInfo(
    string AccountId,
    string DisplayName,
    int? MembershipLevel = null,
    long? CapacityBytes = null,
    long? UsedBytes = null);

/// <summary>A finite byte range; Offset is inclusive and Length is positive.</summary>
public readonly record struct CloudByteRange(long Offset, long Length);

public interface ICloudAccountSession
{
    /// <summary>Validate the current session with the provider; never returns credentials.</summary>
    Task<CloudAccountInfo> ValidateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Paths are absolute provider paths using '/'. List enumerates every direct child.
/// No atomic rename, transaction, history or cross-device lock is implied.
/// </summary>
public interface ICloudObjectStore : ICloudAccountSession, IAsyncDisposable
{
    string ProviderId { get; }
    Task<CloudObjectInfo?> HeadAsync(string path, CancellationToken cancellationToken = default);
    IAsyncEnumerable<CloudObjectInfo> ListAsync(string directory, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Store exactly Length bytes at a new path. Sha256 is the lowercase/uppercase
    /// hexadecimal SHA-256 of the complete content. Existing identical content is
    /// accepted; different content is a conflict and must never be overwritten.
    /// The caller retains ownership of Content. It is consumed from its current position.
    /// </summary>
    Task<CloudObjectInfo> PutImmutableAsync(
        string path, Stream content, long length, string sha256,
        CancellationToken cancellationToken = default);

    /// <summary>Caller must dispose the returned stream. A supplied range must be honored exactly.</summary>
    Task<Stream> OpenReadAsync(
        string path, CloudByteRange? range = null,
        CancellationToken cancellationToken = default);

    /// <summary>Idempotent deletion; succeeds only after absence is confirmed.</summary>
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Optional bounded deletion capability. Success confirms absence of every supplied path.
/// An interrupted operation may have removed only a subset; callers retain a durable retry list.</summary>
public interface ICloudBatchDeleteStore
{
    Task DeleteManyAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
}

public class CloudProviderException : IOException
{
    public CloudProviderException(string code, string message, bool isTransient = false, bool authenticationRequired = false)
        : base(message)
    {
        Code = code;
        IsTransient = isTransient;
        AuthenticationRequired = authenticationRequired;
    }

    public string Code { get; }
    public bool IsTransient { get; }
    public bool AuthenticationRequired { get; }
}

public sealed class CloudObjectConflictException(string path)
    : CloudProviderException("ObjectConflict", $"The cloud path already contains different data: {path}");

public sealed class CloudObjectNotFoundException(string path)
    : CloudProviderException("ObjectNotFound", $"The cloud object does not exist: {path}");
