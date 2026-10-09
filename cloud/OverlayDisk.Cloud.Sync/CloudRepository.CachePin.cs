using System.Text.Json;
using OverlayDisk.Cloud.Contracts;

namespace OverlayDisk.Cloud.Sync;

public sealed partial class CloudRepository
{
    private sealed record CachePinPayload(int FormatVersion, string Kind, string ProviderId, string AccountId, string VolumeId, string WriterId);
    private static CachePinPayload CachePinContent(CloudBinding binding, string volumeId)
        => new(4, "local-cache", binding.ProviderId, binding.AccountId, volumeId, binding.DeviceId);

    public static string CachePinPath(string root, string volumeId)
    {
        if (!IsRootForVolume(root, volumeId)) throw new IOException("本地缓存保护引用与云盘身份不一致。");
        return root + "/readers/" + Component(volumeId) + ".json";
    }

    /// <summary>
    /// Establish one immutable, generation-independent protection pin after a successful publication.
    /// Only after this returns may native persist the matching remote source and enable cache eviction.
    /// Native retains the acknowledged pin across restart; do not call this once per evicted object.
    /// Enabling a limit, receiving individual upload receipts, or a failed publication is insufficient.
    /// </summary>
    public async Task<string> EnsureCachePinAsync(CloudBinding binding, string volumeId, CancellationToken ct = default)
    {
        var scope = Scope(binding, volumeId);
        await using var lease = await Cache.AcquireAsync(scope, ct);
        var state = await LoadCacheAsync(scope, ct);
        if (!state.OwnerConfirmed || !state.LatestKnown || state.Latest is not { } commit || state.Publication is not null ||
            state.ClosureGeneration != commit.Generation || state.PublishedCommitPath != state.LatestPath || commit.WriterId != binding.DeviceId)
            throw new IOException("请先完成并确认一次云同步，再启用已同步数据的本地缓存回收。");
        AuthenticateCommit(binding.RemoteRoot, commit);
        await EnsureWriterAsync(binding, volumeId, state, ct);
        await EnsureFolderAsync(state, binding.RemoteRoot + "/readers", ct);
        string path = CachePinPath(binding.RemoteRoot, volumeId);
        await PutJsonAsync(path, CachePinContent(binding, volumeId), ct);
        return path;
    }

    /// <summary>Validates the small remote pin before cleanup may exclude it from the external-reader check.</summary>
    internal async Task<bool> VerifyCachePinAsync(CloudBinding binding, string volumeId, string path, CancellationToken ct)
    {
        Scope(binding, volumeId);
        if (path != CachePinPath(binding.RemoteRoot, volumeId)) throw new IOException("本地缓存保护引用路径不匹配。");
        byte[] bytes;
        try { bytes = await ReadBytesAsync(path, 16384, ct); }
        catch (CloudObjectNotFoundException) { return false; }
        try
        {
            var actual = JsonSerializer.Deserialize<CachePinPayload>(bytes, Json);
            if (actual != CachePinContent(binding, volumeId)) throw new IOException("云端缓存保护引用与本地来源身份不一致。");
        }
        catch (JsonException error) { throw new IOException("云端缓存保护引用格式无效。", error); }
        return true;
    }

    /// <summary>
    /// Explicit cleanup after the application's worker has closed and actually deleted this local container.
    /// Turning off a cache limit is not sufficient: current data or snapshots may still have missing objects.
    /// This removes only the verified small pin, never any object/commit; ordinary sync never calls it.
    /// </summary>
    public async Task ReleaseDeletedVolumeCachePinAsync(CloudBinding binding, string volumeId, CancellationToken ct = default)
    {
        var scope = Scope(binding, volumeId);
        await using var lease = await Cache.AcquireAsync(scope, ct);
        var account = await Store.ValidateAsync(ct);
        if (account.AccountId != binding.AccountId) throw new IOException("请登录此本地缓存所属的网盘账户。");
        string path = CachePinPath(binding.RemoteRoot, volumeId);
        if (await VerifyCachePinAsync(binding, volumeId, path, ct)) await Store.DeleteAsync(path, ct);
    }

    internal async Task<bool> HasReadersOtherThanAsync(string root, string? verifiedOwnPin, CancellationToken ct)
    {
        ValidateRoot(root);
        if (verifiedOwnPin is not null && verifiedOwnPin != CachePinPath(root, root[(root.LastIndexOf('/') + 1)..]))
            throw new IOException("不能忽略不属于此卷的读取引用。");
        try
        {
            await foreach (var item in Store.ListAsync(root + "/readers", ct))
                if (item.IsDirectory || item.Path != verifiedOwnPin) return true;
        }
        catch (CloudObjectNotFoundException) { return false; }
        return false;
    }
}
