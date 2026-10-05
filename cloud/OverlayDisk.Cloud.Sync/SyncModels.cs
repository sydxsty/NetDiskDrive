using System.Text.Json;
using System.Text.Json.Serialization;
using OverlayDisk.Cloud.Contracts;
namespace OverlayDisk.Cloud.Sync;

public interface ICloudVolume
{
    string Id { get; }
    int ObjectSizeBytes { get; }
    Task<JsonElement> ControlAsync(object request, CancellationToken cancellationToken);
    Task<byte[]> ReadObjectAsync(string jobId, string objectId, CancellationToken cancellationToken);
}
public sealed record CloudBinding(string ProviderId, string AccountId, string RemoteRoot, string DeviceId);
public sealed record RemoteCommit(int FormatVersion, string VolumeId, string WriterId, ulong Generation,
    string Name, ulong CapacityBytes, bool Encrypted, string RootObjectId, string RootSha256, DateTimeOffset UpdatedUtc)
{
    public int RootSlot { get; init; } = 0;
    [JsonRequired] public int ObjectSizeBytes { get; init; } = CloudObjectGeometry.DefaultSize;
    [JsonRequired] public string TransportCodec { get; init; } = ObjectTransport.Codec;
}
public sealed record RemoteDisk(string Id, string Name, ulong CapacityBytes, bool Encrypted, DateTimeOffset UpdatedUtc, string RemoteRoot, RemoteCommit Commit)
{ public int ObjectSizeBytes => Commit.ObjectSizeBytes; }
public sealed record TransferProgress(string Phase, string Message, long CompletedBytes, long TotalBytes,
    long PendingBytes, bool Estimated = false, string? Error = null)
{
    public long UploadedBytes { get; init; }
    public long ReusedBytes { get; init; }
    public long LogicalUploadedBytes { get; init; }
}
public sealed record SyncResult(RemoteCommit Commit, string CommitPath, bool CleanupPending, bool HasPendingChanges = false);
public sealed record ExportObject(string Id, string Kind, long Length, string Sha256, bool Uploaded);
public sealed record SyncLogEntry(DateTimeOffset TimestampUtc, string Action, string Message,
    string Level = "info", string? ObjectId = null, string? ObjectKind = null, long? Bytes = null, ulong? Generation = null)
{ public long? WireBytes { get; init; } }

public sealed record CloudCleanupResult(bool Pending, int RemainingObjects, int RemainingCommits, long RemainingBytes);
public sealed record CloudCleanupEstimate(bool Known, int Objects, int Commits, long Bytes);
