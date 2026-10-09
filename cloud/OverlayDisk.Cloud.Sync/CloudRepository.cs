using System.Security.Cryptography;
using System.Text.Json;
using OverlayDisk.Cloud.Contracts;

namespace OverlayDisk.Cloud.Sync;

public sealed partial class CloudRepository(ICloudObjectStore store, ICloudSyncCache? cache = null, string? validatedAccountId = null)
{
    public const string BasePath = "/OverlayDisk";
    // Explicit 4 MiB fixture/default constant; production never infers a volume's geometry from it.
    public const int ObjectLength = CloudObjectGeometry.DefaultSize;
    public const ulong MaximumCapacityBytes = 8UL * 1024 * 1024 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public ICloudObjectStore Store => store;
    internal ICloudSyncCache Cache { get; } = cache ?? new MemoryCloudSyncCache();
    private string? verifiedAccountId = validatedAccountId;
    internal CloudCacheScope Scope(CloudBinding binding, string volumeId)
    {
        ValidateRoot(binding.RemoteRoot);
        if (!IsRootForVolume(binding.RemoteRoot, volumeId) || !Guid.TryParse(volumeId, out _) || !Guid.TryParse(binding.DeviceId, out _)
            || binding.ProviderId != store.ProviderId || string.IsNullOrWhiteSpace(binding.AccountId)) throw new IOException("云端目录、后端或磁盘身份不一致。");
        if (verifiedAccountId is not null && verifiedAccountId != binding.AccountId) throw new IOException("当前登录账户与磁盘的同步账户不一致。");
        return CloudCacheScope.From(binding);
    }
    internal async Task<CloudSyncCacheState> LoadCacheAsync(CloudCacheScope scope, CancellationToken ct)
    {
        var state = await Cache.LoadAsync(scope, ct);
        try
        {
            if (Cache is IValidatedCloudSyncCache) state?.ValidateHeader(scope);
            else state?.Validate(scope);
        }
        catch (IOException) { state = null; }
        return state ?? CloudSyncCacheState.Empty(scope);
    }
    internal Task SaveCacheAsync(CloudSyncCacheState state, CancellationToken ct) => Cache.SaveAsync(state.Scope, state, ct);
    internal async Task EnsureFolderAsync(CloudSyncCacheState state, string path, CancellationToken ct)
    {
        if (state.ConfirmedFolders.Contains(path)) return;
        await store.CreateDirectoryAsync(path, ct); state.ConfirmedFolders.Add(path);
        await SaveCacheAsync(state, ct);
    }
    public static string RootPath(string volumeId) => BasePath + "/" + Component(volumeId);
    public static bool IsRootForVolume(string root, string volumeId)
        => Guid.TryParse(volumeId, out _) && root == RootPath(volumeId);
    public static string Component(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 2 || value.Length > 128 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw new IOException("云端对象标识无效。");
        return value;
    }
    public static string ObjectPath(string root, string id) => root + "/objects/" + Component(id)[..2] + "/" + id + ".obj";
    public static string CommitPath(string root, RemoteCommit commit) => root + "/commits/" + commit.Generation.ToString("D20") + "-" + Component(commit.RootObjectId) + ".json";
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static void ValidateRoot(string root)
    {
        if (string.IsNullOrEmpty(root) || !IsRootForVolume(root, root[(root.LastIndexOf('/') + 1)..]))
            throw new IOException("云同步目录不属于本程序磁盘目录。");
    }
    public async Task<bool> HasPendingWorkAsync(CloudBinding binding, string volumeId, CancellationToken ct = default)
    {
        var scope = Scope(binding, volumeId);
        await using var lease = await Cache.AcquireAsync(scope, ct);
        var state = await LoadCacheAsync(scope, ct);
        return state.Publication is not null; // Garbage collection is exclusively a user action.
    }
    public async Task<CloudCleanupEstimate> CleanupEstimateAsync(CloudBinding binding, string volumeId, CancellationToken ct = default)
    {
        var scope=Scope(binding,volumeId);await using var lease=await Cache.AcquireAsync(scope,ct);
        var state=await LoadCacheAsync(scope,ct);
        int objectSize = state.Latest?.ObjectSizeBytes ?? state.Publication?.Commit.ObjectSizeBytes ?? CloudObjectGeometry.DefaultSize;
        return new(state.LatestKnown,state.PendingDeleteObjects.Count,state.PendingDeleteCommits.Count,(long)state.PendingDeleteObjects.Count*objectSize);
    }
    public async Task EnsureWriterAsync(CloudBinding binding, string volumeId, CancellationToken ct)
    {
        var scope = Scope(binding, volumeId);
        await using var lease = await Cache.AcquireAsync(scope, ct);
        var state = await LoadCacheAsync(scope, ct);
        await EnsureWriterAsync(binding, volumeId, state, ct);
    }
    internal async Task EnsureWriterAsync(CloudBinding binding, string volumeId, CloudSyncCacheState state, CancellationToken ct)
    {
        Scope(binding, volumeId);
        verifiedAccountId ??= (await store.ValidateAsync(ct)).AccountId;
        if (verifiedAccountId != binding.AccountId) throw new IOException("当前登录账户与磁盘的同步账户不一致。");
        if (state.OwnerConfirmed) return;
        string ownerPath = binding.RemoteRoot + "/owner.json";
        Owner? owner;
        try { owner = JsonSerializer.Deserialize<Owner>(await ReadBytesAsync(ownerPath, 16384, ct), Json) ?? throw new IOException("云端写入者记录无效。"); }
        catch (CloudObjectNotFoundException) { owner = null; }
        if (owner is null)
        {
            await EnsureFolderAsync(state, BasePath, ct);
            await EnsureFolderAsync(state, binding.RemoteRoot, ct);
            await foreach (var child in store.ListAsync(binding.RemoteRoot, ct))
                if (child.Path != ownerPath) throw new IOException("云端目录已有内容但缺少写入者记录，已停止绑定。");
            owner = new Owner(4, binding.AccountId, volumeId, binding.DeviceId);
            await PutJsonAsync(ownerPath, owner, ct);
            state.LatestKnown = true; state.Latest = null; state.LatestPath = null;
        }
        if (owner.Version != 4 || owner.AccountId != binding.AccountId || owner.VolumeId != volumeId || owner.WriterId != binding.DeviceId)
            throw new IOException("这块云端磁盘由其他设备写入。可以恢复为独立副本。");
        state.ConfirmedFolders.Add(BasePath); state.ConfirmedFolders.Add(binding.RemoteRoot);
        foreach (var folder in new[] { "/objects", "/commits", "/readers" }) await EnsureFolderAsync(state, binding.RemoteRoot + folder, ct);
        state.OwnerConfirmed = true; await SaveCacheAsync(state, ct);
    }
    private sealed record Owner(int Version, string AccountId, string VolumeId, string WriterId);
    public async Task PutJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        await store.PutImmutableAsync(path, new MemoryStream(bytes, false), bytes.Length, Hash(bytes), ct);
    }
    public async Task<byte[]> ReadBytesAsync(string path, int maximum, CancellationToken ct)
    {
        await using var stream = await store.OpenReadAsync(path, null, ct);
        return await ReadBoundedAsync(stream, maximum, ct);
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken ct)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[Math.Min(maximum + 1, 64 * 1024)];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, ct); if (count == 0) break;
            if (bytes.Length + count > maximum) throw new IOException("云端对象超出允许大小。");
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }
    public async Task<byte[]> ReadObjectAsync(string root, string id, string expectedHash, int objectSizeBytes, CancellationToken ct)
    {
        ValidateRoot(root);
        CloudObjectGeometry.Validate(objectSizeBytes);
        var descriptor = ObjectDescriptor(root, id, objectSizeBytes, expectedHash);
        ObjectTransport.ValidateDescriptor(descriptor);
        int maximum = ObjectTransport.MaxWireLength(objectSizeBytes);
        await using var stream = store is ICloudBoundedObjectReader bounded
            ? await bounded.OpenReadBoundedAsync(descriptor.Path, maximum, ct)
            : await store.OpenReadAsync(descriptor.Path, null, ct);
        var wire = await ReadBoundedAsync(stream, maximum, ct);
        return ObjectTransport.Decode(wire, descriptor, EncryptionContext(root));
    }
    internal static (ulong Generation, string RootId) ValidateCommitPath(string root, string path)
    {
        string prefix = root + "/commits/";
        if (string.IsNullOrEmpty(path) || !path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(".json", StringComparison.Ordinal)) throw new IOException("云端版本路径无效。");
        string name = path[prefix.Length..^5];
        if (name.Length < 23 || name[20] != '-' || !name[..20].All(char.IsAsciiDigit) || !ulong.TryParse(name[..20], out var generation)) throw new IOException("云端版本名称无效。");
        return (generation, Component(name[21..]));
    }
    internal static void ValidateCommit(string root, string path, RemoteCommit commit)
    {
        ValidateRoot(root); ValidateCommitPath(root, path);
        if (commit is null) throw new IOException("云端提交描述无效。");
        if (commit.FormatVersion != 4 || commit.RootSlot != 0 || !ObjectTransport.IsSupportedCodec(commit.TransportCodec) || !CloudObjectGeometry.IsSupported(commit.ObjectSizeBytes) || commit.CapacityBytes < 64UL * 1024 * 1024 || commit.CapacityBytes > MaximumCapacityBytes || commit.CapacityBytes % 512 != 0
            || commit.RootSha256 is not { Length: 64 } || !commit.RootSha256.All(Uri.IsHexDigit) || !Guid.TryParse(commit.VolumeId, out _) || !Guid.TryParse(commit.WriterId, out _)) throw new IOException("云端提交描述不受支持。");
        ValidateCommitEncryption(commit);
        Component(commit.RootObjectId);
        if (!IsRootForVolume(root, commit.VolumeId) || CommitPath(root, commit) != path) throw new IOException("云端版本路径与身份不一致。");
    }
    internal async Task<RemoteCommit> ReadCommitAsync(string root, string path, CancellationToken ct)
    {
        var bytes = await ReadBytesAsync(path, 65536, ct);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("云端提交描述必须是 JSON 对象。");
        if (!document.RootElement.TryGetProperty("objectSizeBytes", out _) || !document.RootElement.TryGetProperty("transportCodec", out var codec))
            throw new UnsupportedCloudFormatException();
        if (codec.ValueKind != JsonValueKind.String) throw new IOException("云端提交描述的压缩格式字段损坏。");
        if (!ObjectTransport.IsSupportedCodec(codec.GetString())) throw new UnsupportedCloudFormatException();
        var commit = JsonSerializer.Deserialize<RemoteCommit>(bytes, Json) ?? throw new IOException("云端提交描述无效。");
        ValidateCommit(root, path, commit); return commit;
    }
    // This method is deliberately fresh: a restoring reader must recheck the
    // actual current generation after creating its durable reader pin.
    public async Task<(RemoteCommit Commit, string Path)?> LatestAsync(string root, CancellationToken ct)
    {
        ValidateRoot(root);
        if (await store.HeadAsync(root + "/commits", ct) is null) return null;
        (ulong Generation, string RootId, string Path)? newest = null;
        await foreach (var item in store.ListAsync(root + "/commits", ct))
        {
            if (item.IsDirectory || !item.Path.EndsWith(".json", StringComparison.Ordinal)) continue;
            var key = ValidateCommitPath(root, item.Path);
            if (newest is null || key.Generation > newest.Value.Generation) newest = (key.Generation, key.RootId, item.Path);
            else if (key.Generation == newest.Value.Generation && key.RootId != newest.Value.RootId) throw new IOException("云端存在冲突写入版本，已停止自动选择。");
        }
        if (newest is not { } selected) return null;
        // Reconciliation downloads at most one descriptor, never every historic commit.
        return (await ReadCommitAsync(root, selected.Path, ct), selected.Path);
    }
    public async Task<IReadOnlyList<RemoteDisk>> ListDisksAsync(CancellationToken ct)
    {
        var disks = new List<RemoteDisk>(); if (await store.HeadAsync(BasePath, ct) is null) { await store.CreateDirectoryAsync(BasePath, ct); return disks; }
        await foreach (var directory in store.ListAsync(BasePath, ct))
        {
            if (!directory.IsDirectory || !IsRootForVolume(directory.Path, directory.Path[(directory.Path.LastIndexOf('/') + 1)..])) continue;
            (RemoteCommit Commit, string Path)? latest;
            try { latest = await LatestAsync(directory.Path, ct); }
            catch (UnsupportedCloudFormatException) { continue; }
            if (latest is not { } found) continue;
            var c = found.Commit; disks.Add(new(c.VolumeId, c.Name, c.CapacityBytes, c.Encrypted, c.UpdatedUtc, directory.Path, c));
        }
        return disks;
    }
    public async Task<bool> HasReadersAsync(string root, CancellationToken ct)
    {
        ValidateRoot(root);
        try { await foreach (var _ in store.ListAsync(root + "/readers", ct)) return true; }
        catch (CloudObjectNotFoundException) { return false; }
        return false;
    }
    public async Task<string> PinReaderAsync(string root, RemoteCommit commit, string readerId, CancellationToken ct)
    {
        AuthenticateCommit(root, commit);
        if (!IsRootForVolume(root, commit.VolumeId) || commit.RootSha256 is not { Length: 64 } || !commit.RootSha256.All(Uri.IsHexDigit)) throw new IOException("恢复引用与目录身份不一致。");
        Component(commit.RootObjectId);
        string path = root + "/readers/" + Component(readerId) + ".json";
        await store.CreateDirectoryAsync(root + "/readers", ct);
        // Persistent pins survive interruption. Release only on successful completion or explicit cancellation.
        await PutJsonAsync(path, new { formatVersion = 4, rootObjectId = commit.RootObjectId, rootSha256 = commit.RootSha256, generation = commit.Generation }, ct);
        var current = await LatestAsync(root, ct);
        if (current?.Commit != commit)
        { await store.DeleteAsync(path, ct); throw new IOException("云端版本在开始恢复时发生变化，请刷新后重试。"); }
        return path;
    }
}

public sealed class UnsupportedCloudFormatException() : IOException("云端版本不是当前云端对象格式；本版本不读取或迁移旧格式。");
