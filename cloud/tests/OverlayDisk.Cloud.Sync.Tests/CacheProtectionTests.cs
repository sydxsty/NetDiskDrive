using System.Runtime.CompilerServices;
using System.Text.Json;
using OverlayDisk.Cloud.Contracts;
using OverlayDisk.Cloud.Sync;

internal static class CacheProtectionTests
{
    private const string VolumeId = "370343ee-b1f9-4558-895f-3dc66555c4aa", WriterId = "a2f8d626-34f3-40b8-8b3c-40ccdb203f63";
    private const string RootId = "1e6d3324-e8cb-4bd0-9296-b41a969f693e", Account = "cache-account";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Root => CloudRepository.RootPath(VolumeId);
    private static RemoteCommit Commit => new(4, VolumeId, WriterId, 7, "Cache", 64UL << 20, true, RootId, new string('a', 64), DateTimeOffset.UnixEpoch);
    private static string ObjectId(int n) => "00000000-0000-0000-0000-" + n.ToString("x12");
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("cache pin requires acknowledged publication and failed pin cannot enable eviction", EnableBoundary),
        ("cache cleanup protects snapshot references and advances past protected batches", ProtectedBatch),
        ("external reader pins still block cleanup beside the owned cache pin", ExternalReader),
        ("malformed native protection and mismatched or missing pins fail closed", InvalidProtection),
        ("interrupted cache cleanup preserves deletion candidates for idempotent retry", DeleteFailure),
        ("explicit deleted-volume cleanup removes only its verified cache pin", ReleasePin)
    ];
    private static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static async Task Reject(Func<Task> work)
    { try { await work(); } catch (IOException) { return; } throw new Exception("Unsafe cache operation succeeded"); }

    private sealed class Fixture
    {
        internal readonly Store Store = new();
        internal readonly MemoryCloudSyncCache Cache = new();
        internal readonly LocalVolume Volume = new();
        internal CloudBinding Binding => new(Store.ProviderId, Account, Root, WriterId);
        internal CloudCacheScope Scope => CloudCacheScope.From(Binding);
        internal CloudRepository Repository => new(Store, Cache, Account);
        internal string Pin => CloudRepository.CachePinPath(Root, VolumeId);
        internal async Task Seed(int objects = 0)
        {
            var state = new CloudSyncCacheState { Scope = Scope, OwnerConfirmed = true, LatestKnown = true, Latest = Commit,
                LatestPath = CloudRepository.CommitPath(Root, Commit), PublishedCommitPath = CloudRepository.CommitPath(Root, Commit), ClosureGeneration = Commit.Generation };
            state.ConfirmedFolders.UnionWith([CloudRepository.BasePath, Root, Root + "/commits", Root + "/readers"]);
            for (int i = 1; i <= objects; i++) { string id = ObjectId(i); state.PendingDeleteObjects.Add(id); Store.Files[CloudRepository.ObjectPath(Root, id)] = [1]; }
            await Save(state);
        }
        internal async Task Save(CloudSyncCacheState state)
        { await using var lease = await Cache.AcquireAsync(Scope); await Cache.SaveAsync(Scope, state); }
        internal async Task<CloudSyncCacheState> Load()
        { await using var lease = await Cache.AcquireAsync(Scope); return (await Cache.LoadAsync(Scope))!; }
        internal async Task Enable()
        {
            string pin = await Repository.EnsureCachePinAsync(Binding, VolumeId);
            // Represents native cache.bind only after the confirmed remote pin.
            Volume.Pin = new { reader_pin = pin, backend_id = Binding.ProviderId, account_id = Account, remote_root = Root, device_id = WriterId, volume_id = VolumeId };
        }
        internal Task<CloudCleanupResult> Cleanup() => new SyncCoordinator(Repository).CleanupAsync(Volume, Binding, null, default);
    }

    private sealed class LocalVolume : ICloudVolume
    {
        public string Id => VolumeId;
        public int ObjectSizeBytes => CloudObjectGeometry.DefaultSize;
        internal object? Pin;
        internal readonly HashSet<string> Protected = new(StringComparer.Ordinal);
        internal readonly List<string[]> Batches = [];
        internal Func<string[], object>? Malformed;
        public Task<JsonElement> ControlAsync(object request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); var r = JsonSerializer.SerializeToElement(request); object result;
            switch (r.GetProperty("cmd").GetString())
            {
                case "cloud.status": case "cloud.pause":
                    result = new { object_size = ObjectSizeBytes, data_generation = Commit.Generation, published_generation = Commit.Generation, pending_generation = Commit.Generation,
                        local_dirty = false, job = (object?)null, published_commit = new { root_object_id = RootId, root_sha256 = Commit.RootSha256, generation = Commit.Generation } }; break;
                case "cloud.gc_candidates":
                    var ids = r.GetProperty("object_ids").EnumerateArray().Select(x => x.GetString()!).ToArray();
                    Assert(ids.Length <= 64, "GC native query exceeded its bounded candidate batch"); Batches.Add(ids);
                    result = Malformed?.Invoke(ids) ?? new { allowed = ids.Where(id => !Protected.Contains(id)).ToArray(), @protected = ids.Where(Protected.Contains).ToArray(), cache_pin = Pin }; break;
                default: throw new Exception("Cache cleanup/no-op invoked unrelated native mutation or object scan");
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(result));
        }
        public Task<byte[]> ReadObjectAsync(string jobId, string objectId, CancellationToken cancellationToken) => throw new Exception("Cache maintenance read upload payload");
    }

    private sealed class Store : ICloudObjectStore
    {
        public string ProviderId => "cache-protection-test";
        internal readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
        internal readonly List<string> Calls = [];
        internal bool FailPut;
        internal int FailDeleteAt, DeleteCalls;
        public Task<CloudAccountInfo> ValidateAsync(CancellationToken cancellationToken = default)
        { Calls.Add("account"); return Task.FromResult(new CloudAccountInfo(Account, "Fixture")); }
        public Task<CloudObjectInfo?> HeadAsync(string path, CancellationToken cancellationToken = default)
            => throw new Exception("Unexpected inventory probe");
        public async IAsyncEnumerable<CloudObjectInfo> ListAsync(string directory, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add("list:" + directory); Assert(directory == Root + "/readers", "Cloud object inventory was enumerated");
            foreach (var path in Files.Keys.Where(p => p.StartsWith(directory + "/", StringComparison.Ordinal)).ToArray())
            { cancellationToken.ThrowIfCancellationRequested(); yield return new(path, Files[path].Length, false); }
            await Task.CompletedTask;
        }
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        { Calls.Add("mkdir:" + path); Assert(path == Root + "/readers", "Pin created another directory"); return Task.CompletedTask; }
        public async Task<CloudObjectInfo> PutImmutableAsync(string path, Stream content, long length, string sha256, CancellationToken cancellationToken = default)
        {
            Calls.Add("put:" + path); if (FailPut) throw new IOException("Injected pin upload failure");
            Assert(path == CloudRepository.CachePinPath(Root, VolumeId), "Cache enable uploaded a data object");
            using var bytes = new MemoryStream(); await content.CopyToAsync(bytes, cancellationToken); byte[] actual = bytes.ToArray();
            Assert(actual.Length == length && CloudRepository.Hash(actual) == sha256, "Pin bytes changed");
            if (Files.TryGetValue(path, out var prior) && !actual.SequenceEqual(prior)) throw new CloudObjectConflictException(path);
            Files[path] = actual; return new(path, length, false);
        }
        public Task<Stream> OpenReadAsync(string path, CloudByteRange? range = null, CancellationToken cancellationToken = default)
        {
            Calls.Add("read:" + path); Assert(path == CloudRepository.CachePinPath(Root, VolumeId), "Cache maintenance read an object payload");
            if (!Files.TryGetValue(path, out var value)) throw new CloudObjectNotFoundException(path);
            return Task.FromResult<Stream>(new MemoryStream(value, false));
        }
        public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            Calls.Add("delete:" + path); DeleteCalls++;
            if (FailDeleteAt != 0 && DeleteCalls == FailDeleteAt) throw new IOException("Injected deletion failure");
            Files.Remove(path); return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task EnableBoundary()
    {
        var f = new Fixture(); await Reject(() => f.Repository.EnsureCachePinAsync(f.Binding, VolumeId));
        Assert(f.Store.Calls.Count == 0 && f.Volume.Pin is null, "No-publication state enabled eviction");
        await f.Seed(); var state = await f.Load();
        state.Publication = new() { Commit = Commit, CommitPath = state.LatestPath!, DeltaId = "pending", Closure = new HashSet<string>([RootId]) }; await f.Save(state);
        await Reject(() => f.Repository.EnsureCachePinAsync(f.Binding, VolumeId)); Assert(f.Store.Calls.Count == 0, "Unfinished publication created a protection pin");
        state.Publication = null; await f.Save(state); f.Store.FailPut = true;
        await Reject(f.Enable); Assert(f.Volume.Pin is null, "Failed remote pin acknowledgment enabled native eviction");
        f.Store.FailPut = false; await f.Enable(); var pin = f.Store.Files[f.Pin].ToArray();
        state = await f.Load(); var later = Commit with { Generation = 8 }; state.Latest = later; state.LatestPath = CloudRepository.CommitPath(Root, later);
        state.PublishedCommitPath = state.LatestPath; state.ClosureGeneration = 8; await f.Save(state);
        await f.Enable(); Assert(f.Store.Files[f.Pin].SequenceEqual(pin), "Pin contents/path changed across publication generations");
    }

    private static async Task ProtectedBatch()
    {
        var f = new Fixture(); await f.Seed(150); await f.Enable();
        foreach (var id in Enumerable.Range(1, 70).Select(ObjectId)) f.Volume.Protected.Add(id);
        var state = await f.Load(); string oldCommit = CloudRepository.CommitPath(Root, Commit with { Generation = 6 }); state.PendingDeleteCommits.Add(oldCommit); f.Store.Files[oldCommit] = [2]; await f.Save(state);
        f.Store.Calls.Clear(); var result = await f.Cleanup();
        Assert(result.Pending && result.RemainingObjects == 70 && result.RemainingCommits == 0 && f.Volume.Batches.Count == 3,
            "Protected first batch blocked progress or lost retained references");
        Assert(f.Store.Calls.Count(c => c == "read:" + f.Pin) == 1 && f.Store.DeleteCalls == 81 && f.Store.Files.ContainsKey(f.Pin), "Own pin caused full GC blocking or was itself collected");
        Assert(Enumerable.Range(1, 70).All(i => f.Store.Files.ContainsKey(CloudRepository.ObjectPath(Root, ObjectId(i)))), "Snapshot/current reference was deleted");
        f.Store.Calls.Clear(); await new SyncCoordinator(f.Repository).RunAsync(f.Volume, f.Binding, "Cache", Commit.CapacityBytes, true, 1, null, default);
        Assert(f.Store.Calls.Count == 0, "No-op sync refreshed the cache pin or ran automatic cleanup");
        var onlyCommit = new Fixture(); await onlyCommit.Seed(); await onlyCommit.Enable();
        state = await onlyCommit.Load(); state.PendingDeleteCommits.Add(oldCommit); onlyCommit.Store.Files[oldCommit] = [2]; await onlyCommit.Save(state);
        Assert(!(await onlyCommit.Cleanup()).Pending && onlyCommit.Volume.Batches.Single().Length == 0 && onlyCommit.Store.Files.ContainsKey(onlyCommit.Pin),
            "Commit-only cleanup skipped native pin protection or deleted the cache pin");
    }
    private static async Task ExternalReader()
    {
        var f = new Fixture(); await f.Seed(2); await f.Enable(); f.Store.Files[Root + "/readers/" + Guid.NewGuid() + ".json"] = [1];
        var result = await f.Cleanup(); Assert(result.Pending && f.Store.DeleteCalls == 0, "Foreign/source reader pin was incorrectly ignored");
    }
    private static async Task InvalidProtection()
    {
        foreach (var kind in new[] { "missing-verdict", "foreign-id", "overlap", "wrong-scope", "bad-pin", "missing-pin" })
        {
            var f = new Fixture(); await f.Seed(1); await f.Enable();
            switch (kind)
            {
                case "missing-verdict": f.Volume.Malformed = _ => new { allowed = Array.Empty<string>(), @protected = Array.Empty<string>(), cache_pin = f.Volume.Pin }; break;
                case "foreign-id": f.Volume.Malformed = _ => new { allowed = new[] { ObjectId(999) }, @protected = Array.Empty<string>(), cache_pin = f.Volume.Pin }; break;
                case "overlap": f.Volume.Malformed = ids => new { allowed = ids, @protected = ids, cache_pin = f.Volume.Pin }; break;
                case "wrong-scope": f.Volume.Pin = new { reader_pin = f.Pin, backend_id = f.Binding.ProviderId, account_id = "other", remote_root = Root, device_id = WriterId, volume_id = VolumeId }; break;
                case "bad-pin": f.Store.Files[f.Pin] = "{\"kind\":\"another-reader\"}"u8.ToArray(); break;
                case "missing-pin": f.Store.Files.Remove(f.Pin); break;
            }
            var result = await f.Cleanup(); Assert(result.Pending && f.Store.DeleteCalls == 0 && (await f.Load()).PendingDeleteObjects.Count == 1, "Invalid protection granted deletion: " + kind);
        }
    }
    private static async Task DeleteFailure()
    {
        var f = new Fixture(); await f.Seed(70); await f.Enable(); f.Store.FailDeleteAt = 5;
        Assert((await f.Cleanup()).Pending && (await f.Load()).PendingDeleteObjects.Count == 70, "Partial failed batch forgot uncertain deletions");
        f.Store.FailDeleteAt = 0; var resumed = await f.Cleanup();
        Assert(!resumed.Pending && resumed.RemainingObjects == 0 && f.Store.Files.ContainsKey(f.Pin), "Idempotent retry did not finish safely");
    }
    private static async Task ReleasePin()
    {
        var f = new Fixture(); await f.Seed(1); await f.Enable(); string external = Root + "/readers/" + Guid.NewGuid() + ".json"; f.Store.Files[external] = [1];
        f.Store.Calls.Clear(); await f.Repository.ReleaseDeletedVolumeCachePinAsync(f.Binding, VolumeId);
        Assert(f.Store.Calls.Count(c => c.StartsWith("delete:", StringComparison.Ordinal)) == 1 && !f.Store.Files.ContainsKey(f.Pin) && f.Store.Files.ContainsKey(external) && f.Store.Files.ContainsKey(CloudRepository.ObjectPath(Root, ObjectId(1))), "Pin release touched other references/data");
        int removed = f.Store.DeleteCalls; await f.Repository.ReleaseDeletedVolumeCachePinAsync(f.Binding, VolumeId); Assert(f.Store.DeleteCalls == removed, "Missing pin release was not idempotent");
        f.Store.Files[f.Pin] = "{\"kind\":\"foreign\"}"u8.ToArray(); await Reject(() => f.Repository.ReleaseDeletedVolumeCachePinAsync(f.Binding, VolumeId));
        Assert(f.Store.Files.ContainsKey(f.Pin), "Mismatched pin was deleted");
    }
}
