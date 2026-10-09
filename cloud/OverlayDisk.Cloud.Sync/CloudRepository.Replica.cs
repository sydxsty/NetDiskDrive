using System.Text.Json;
using OverlayDisk.Cloud.Contracts;

namespace OverlayDisk.Cloud.Sync;

public sealed partial class CloudRepository
{
    /// <summary>
    /// Discover the newest immutable reader version without enumerating object directories.
    /// A previously validated, durable source descriptor may be reused when its path is still
    /// newest. This is deliberately separate from the writer's reconciliation/cache protocol.
    /// </summary>
    public async Task<(RemoteCommit Commit, string Path)?> LatestForReplicaAsync(
        string root, RemoteCommit? known = null, CancellationToken ct = default)
    {
        ValidateRoot(root);
        if (known is not null) ValidateCommit(root, CommitPath(root, known), known);
        bool descending = store is ICloudDescendingDirectoryReader;
        var listing = store is ICloudDescendingDirectoryReader ordered
            ? ordered.ListByNameDescendingAsync(root + "/commits", ct)
            : store.ListAsync(root + "/commits", ct);
        (ulong Generation, string RootId, CloudObjectInfo Info)? newest = null;
        bool conflict = false;
        string? previousFile = null;
        try
        {
            await foreach (var item in listing.WithCancellation(ct).ConfigureAwait(false))
            {
                if (item.IsDirectory) continue;
                if (descending && previousFile is not null && StringComparer.Ordinal.Compare(previousFile, item.Path) <= 0)
                    throw new IOException("云端版本列表顺序异常，已停止选择快照。");
                previousFile = item.Path;
                if (!item.Path.EndsWith(".json", StringComparison.Ordinal)) continue;
                var key = ValidateCommitPath(root, item.Path);
                if (newest is null || key.Generation > newest.Value.Generation)
                { newest = (key.Generation, key.RootId, item); conflict = false; }
                else if (key.Generation == newest.Value.Generation && key.RootId != newest.Value.RootId)
                {
                    if (descending) throw new IOException("云端存在冲突写入版本，已停止自动选择。");
                    conflict = true;
                }
                else if (descending && key.Generation < newest.Value.Generation) break;
            }
        }
        catch (CloudObjectNotFoundException) { return null; }
        catch (CloudProviderException error) when (error.Code is "Baidu:-9" or "Baidu:31066") { return null; }
        if (newest is not { } selected) return null;
        if (conflict) throw new IOException("云端存在冲突写入版本，已停止自动选择。");
        if (known is not null && CommitPath(root, known) == selected.Info.Path) return (known, selected.Info.Path);
        var commit = await ReadReplicaCommitAsync(root, selected.Info, ct).ConfigureAwait(false);
        return (commit, selected.Info.Path);
    }

    private async Task<RemoteCommit> ReadReplicaCommitAsync(string root, CloudObjectInfo info, CancellationToken ct)
    {
        const int maximum = 65536;
        if (info.Length is <= 0 or > maximum) throw new IOException("云端版本描述大小无效。");
        // Baidu can locate a known path directly; neither a HEAD nor an ascending full listing
        // is needed just to download the descriptor found in the fresh descending page.
        await using var stream = store is ICloudBoundedObjectReader bounded
            ? await bounded.OpenReadBoundedAsync(info.Path, maximum, ct).ConfigureAwait(false)
            : await store.OpenReadAsync(info.Path, null, ct).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(stream, maximum, ct).ConfigureAwait(false);
        if (bytes.LongLength != info.Length) throw new IOException("云端版本描述长度与目录记录不一致。");
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("云端提交描述必须是 JSON 对象。");
        if (!document.RootElement.TryGetProperty("objectSizeBytes", out _) || !document.RootElement.TryGetProperty("transportCodec", out var codec))
            throw new UnsupportedCloudFormatException();
        if (codec.ValueKind != JsonValueKind.String) throw new IOException("云端提交描述的压缩格式字段损坏。");
        if (!ObjectTransport.IsSupportedCodec(codec.GetString())) throw new UnsupportedCloudFormatException();
        var commit = JsonSerializer.Deserialize<RemoteCommit>(bytes, Json) ?? throw new IOException("云端提交描述无效。");
        ValidateCommit(root, info.Path, commit);
        return commit;
    }

    public async Task<(byte[] Canonical, long WireBytes)> ReadObjectForReplicaAsync(
        string root, string id, string expectedHash, int objectSizeBytes, CancellationToken ct = default)
    {
        ValidateRoot(root);
        CloudObjectGeometry.Validate(objectSizeBytes);
        var descriptor = ObjectDescriptor(root, id, objectSizeBytes, expectedHash);
        ObjectTransport.ValidateDescriptor(descriptor);
        int maximum = ObjectTransport.MaxWireLength(objectSizeBytes);
        await using var stream = store is ICloudBoundedObjectReader bounded
            ? await bounded.OpenReadBoundedAsync(descriptor.Path, maximum, ct).ConfigureAwait(false)
            : await store.OpenReadAsync(descriptor.Path, null, ct).ConfigureAwait(false);
        var wire = await ReadBoundedAsync(stream, maximum, ct).ConfigureAwait(false);
        return (ObjectTransport.Decode(wire, descriptor, EncryptionContext(root)), wire.LongLength);
    }

    /// <summary>Create durable reader protection and recheck the source generation before adopting it.</summary>
    public Task<string> PinReplicaReaderAsync(string root, RemoteCommit commit, string readerId, CancellationToken ct = default)
        => EnsureReplicaReaderAsync(root, commit, readerId, false, ct);

    /// <summary>
    /// Resume a previously confirmed source without forcing it to follow a newer publication.
    /// previouslyConfirmed requires durable caller evidence that pin creation AND its newest
    /// check completed; a preallocated reader path alone is insufficient. Missing pins are
    /// always checked against newest again, even when the caller had confirmed them earlier.
    /// </summary>
    public async Task<string> EnsureReplicaReaderAsync(string root, RemoteCommit commit, string readerId,
        bool previouslyConfirmed, CancellationToken ct = default)
    {
        AuthenticateCommit(root, commit);
        string path = root + "/readers/" + Component(readerId) + ".json";
        await store.CreateDirectoryAsync(root + "/readers", ct).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 4, rootObjectId = commit.RootObjectId, rootSha256 = commit.RootSha256, generation = commit.Generation }, Json);
        using var body = new MemoryStream(payload, false);
        var pin = await store.PutImmutableAsync(path, body, payload.Length, Hash(payload), ct).ConfigureAwait(false);
        if (previouslyConfirmed && pin.ReusedExisting) return path;
        var current = await LatestForReplicaAsync(root, commit, ct).ConfigureAwait(false);
        if (current?.Commit != commit)
        {
            await store.DeleteAsync(path, ct).ConfigureAwait(false);
            throw new IOException("云端版本在开始恢复时发生变化，请刷新后重试。");
        }
        return path;
    }

    public async Task<IReadOnlyList<RemoteDisk>> ListDisksForReplicaAsync(CancellationToken ct = default)
    {
        var disks = new List<RemoteDisk>();
        // Explicit listing is fresh; do not let a stale account-root absence proof hide disks.
        var listing = store is ICloudDescendingDirectoryReader ordered
            ? ordered.ListByNameDescendingAsync(BasePath, ct) : store.ListAsync(BasePath, ct);
        await using var directories = listing.GetAsyncEnumerator(ct);
        while (true)
        {
            bool hasNext;
            try { hasNext = await directories.MoveNextAsync().ConfigureAwait(false); }
            catch (CloudObjectNotFoundException) { await store.CreateDirectoryAsync(BasePath, ct).ConfigureAwait(false); break; }
            catch (CloudProviderException error) when (error.Code is "Baidu:-9" or "Baidu:31066") { await store.CreateDirectoryAsync(BasePath, ct).ConfigureAwait(false); break; }
            if (!hasNext) break;
            var directory = directories.Current;
            if (!directory.IsDirectory || !IsRootForVolume(directory.Path, directory.Path[(directory.Path.LastIndexOf('/') + 1)..])) continue;
            (RemoteCommit Commit, string Path)? latest;
            try { latest = await LatestForReplicaAsync(directory.Path, ct: ct).ConfigureAwait(false); }
            catch (UnsupportedCloudFormatException) { continue; }
            if (latest is not { } found) continue;
            var c = found.Commit;
            disks.Add(new(c.VolumeId, c.Name, c.CapacityBytes, c.Encrypted, c.UpdatedUtc, directory.Path, c));
        }
        return disks;
    }
}
