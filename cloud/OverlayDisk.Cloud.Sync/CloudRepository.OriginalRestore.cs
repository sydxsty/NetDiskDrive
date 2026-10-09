using System.Text.Json;

namespace OverlayDisk.Cloud.Sync;

public sealed partial class CloudRepository
{
    /// <summary>
    /// Reattach the original volume/writer identity after the user explicitly confirms that
    /// no other local copy or cloud writer remains. This performs metadata reads and a local
    /// cache save only: it does not acquire a remote lease, replace owner.json, scan objects,
    /// or grant permission to mount an unauthenticated/incomplete local restore.
    /// Call again with the same expected commit immediately before enabling the restored writer.
    /// The native published root/generation must match that commit before normal sync starts.
    /// The caller must also exclude active local jobs/duplicate containers: an unconfirmed
    /// intent left by a deleted container can be abandoned after fresh cloud verification.
    /// </summary>
    public Task<CloudBinding> PrepareOriginalRestoreAsync(string volumeId, RemoteCommit expectedCommit, CancellationToken ct = default)
        => PrepareOriginalRestoreCoreAsync(volumeId, expectedCommit, false, ct);

    /// <summary>Reader import variant with fresh newest-first discovery and known descriptor reuse.</summary>
    public Task<CloudBinding> PrepareOriginalRestoreForReplicaAsync(string volumeId, RemoteCommit expectedCommit, CancellationToken ct = default)
        => PrepareOriginalRestoreCoreAsync(volumeId, expectedCommit, true, ct);

    private async Task<CloudBinding> PrepareOriginalRestoreCoreAsync(string volumeId, RemoteCommit expectedCommit, bool replicaReader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expectedCommit);
        string root = RootPath(volumeId), expectedPath = CommitPath(root, expectedCommit);
        AuthenticateCommit(root, expectedCommit);
        if (expectedCommit.VolumeId != volumeId) throw new IOException("恢复目标身份与原云盘不一致。");

        var account = await Store.ValidateAsync(ct);
        if (string.IsNullOrWhiteSpace(account.AccountId) || verifiedAccountId is not null && verifiedAccountId != account.AccountId)
            throw new IOException("当前登录账户与恢复任务的账户不一致。");
        verifiedAccountId = account.AccountId;
        Owner owner;
        try
        {
            owner = JsonSerializer.Deserialize<Owner>(await ReadBytesAsync(root + "/owner.json", 16384, ct), Json)
                ?? throw new IOException("原云盘缺少有效的写入者记录。");
        }
        catch (JsonException error) { throw new IOException("原云盘写入者记录格式无效。", error); }
        if (owner.Version != 4 || owner.AccountId != account.AccountId || owner.VolumeId != volumeId ||
            !Guid.TryParse(owner.WriterId, out _) || owner.WriterId != expectedCommit.WriterId)
            throw new IOException("原云盘的账户、磁盘或写入者身份与所选版本不一致。");

        // Preserve the verified remote writer identity per volume. A new installation's
        // global device identity is intentionally neither compared nor changed here.
        var binding = new CloudBinding(Store.ProviderId, account.AccountId, root, owner.WriterId);
        var scope = Scope(binding, volumeId);
        await using var lease = await Cache.AcquireAsync(scope, ct);
        var state = await LoadCacheAsync(scope, ct);
        var latest = replicaReader ? await LatestForReplicaAsync(root, expectedCommit, ct) : await LatestAsync(root, ct);
        if (latest is not { } current || current.Commit != expectedCommit || current.Path != expectedPath)
            throw new IOException("云端最新版本已变化，请重新选择版本；尚未接回原云盘写入身份。");
        if (state.Latest is { } previous && (previous.Generation > expectedCommit.Generation ||
            previous.Generation == expectedCommit.Generation && previous != expectedCommit) ||
            state.ClosureGeneration is ulong retained && retained > expectedCommit.Generation)
            throw new IOException("本机已确认的发布记录与当前云端版本冲突，不能回退原盘写入基线。");

        var abandoned = state.Publication;
        if (abandoned is { RemoteVerified: true } && abandoned.Commit != expectedCommit && abandoned.Commit.Generation >= expectedCommit.Generation)
            throw new IOException("本机曾确认另一云端发布成功，但它与当前最新版本矛盾；不能将已确认发布当作未发布记录丢弃。");

        bool sameBaseline = abandoned is null && state.LatestKnown && state.Latest == expectedCommit && state.LatestPath == expectedPath &&
            state.ClosureGeneration == expectedCommit.Generation && state.PublishedCommitPath == expectedPath;
        if (!sameBaseline)
        {
            // A later publication may have reintroduced an old object. Without its
            // complete intervening deltas, retaining object-deletion proof is unsafe.
            // Forget the proof locally; leave every remote object untouched.
            state.PendingDeleteObjects.Clear();
            state.PublishedClosure.Clear();
        }
        // The original container/job is explicitly absent. Adopt only the fresh
        // cloud latest, never the abandoned intent's generation or deletion delta.
        // Its uploaded objects remain on the provider, without new GC permission.
        state.Publication = null;
        state.PendingDeleteObjects.Remove(expectedCommit.RootObjectId);
        foreach (string path in state.PendingDeleteCommits.Where(path => ValidateCommitPath(root, path).Generation >= expectedCommit.Generation).ToArray())
            state.PendingDeleteCommits.Remove(path);
        state.OwnerConfirmed = true; state.LatestKnown = true;
        state.Latest = expectedCommit; state.LatestPath = expectedPath;
        state.ClosureGeneration = expectedCommit.Generation; state.PublishedCommitPath = expectedPath;
        // These ancestors are proven by the successfully read owner/commit paths.
        // No object shard or reader directory is invented as a confirmed directory.
        state.ConfirmedFolders.UnionWith([BasePath, root, root + "/commits"]);
        await SaveCacheAsync(state, ct);
        return binding;
    }
}
