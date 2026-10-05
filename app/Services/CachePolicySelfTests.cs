using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using OverlayDisk.Cloud.Contracts;
using OverlayDisk.Cloud.Sync;
using OverlayDisk.Worker;

namespace OverlayDisk.Services;

// Real native files, real publication/cache protocols, in-memory remote objects.
// No saved application settings, external account, filesystem mount, or benchmark.
internal static class CachePolicySelfTests
{
    private const int MiB = 1024 * 1024, Chunks = 12;
    private const string Password = "isolated-cache-policy-fixture", Account = "cache-policy-fixture";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static async Task<int> RunAsync(string output)
    {
        if (Directory.Exists(output)) throw new IOException("请使用新的缓存功能测试目录。");
        Directory.CreateDirectory(output);
        string path = Path.Combine(output, "cache-source.odv4"), copyPath = Path.Combine(output, "cache-copy.odv4");
        var store = new MemoryStore(); var cache = new FileCloudSyncCache(Path.Combine(output, "sync-cache"));
        var checks = new List<string>(); var events = new List<SyncLogEntry>();
        CloudRepository Repo() => new(store, cache, Account);
        CoreDisk.Create(path, 64UL * MiB, Password);
        CloudBinding binding; SyncResult latest; string ownPin, snapshot;
        using (var disk = new CoreDisk(path, Password))
        {
            binding = Binding(disk); Bind(disk, binding);
            WritePattern(disk, 103); disk.Flush();
            latest = await Sync(disk, binding, Repo());
            var originalData = Published(disk).Where(o => o.Kind == "data").ToArray();
            Check(originalData.Length >= 2, "Fixture did not create multiple immutable data objects");
            var router = new Router(disk, Repo(), store);
            disk.SetObjectProvider(router.ReadAsync); // Ordinary disks are not yet cache-capable.
            ownPin = await Enable(disk, binding, Repo(), router);
            snapshot = disk.CreateSnapshot();
            ulong before = Status(disk).GetProperty("allocated_bytes").GetUInt64();
            Evict(disk);
            var evicted = Status(disk);
            Check(evicted.GetProperty("missing_objects").GetUInt64() > 0 && evicted.GetProperty("evicted_bytes").GetUInt64() > 0,
                "No published object was physically released");
            Check(evicted.GetProperty("allocated_bytes").GetUInt64() < before, "Reported eviction did not lower allocated storage");
            await CheckPolicyBoundary(disk, originalData[0]);
            CheckPattern(disk, 103);
            Check(router.Requests.Any(r => r.Root == binding.RemoteRoot && r.Kind == "own"), "Ordinary disk hydration did not use its own confirmed root");
            checks.Add("Real native publication and acknowledged cache pin permit physical eviction; full logical data rehydrates through SHA-checked own-source routing.");

            byte[] unsynced = Pattern(211, 0)[..4096];
            disk.Write(32UL * MiB, unsynced, unsynced.Length); disk.Flush();
            Evict(disk); int reads = router.Requests.Count;
            var actual = new byte[4096]; disk.Read(32UL * MiB, actual, actual.Length);
            Check(actual.SequenceEqual(unsynced) && router.Requests.Count == reads, "Unpublished page was evicted or changed");
            Check(disk.Control(new { cmd = "cloud.status" }).GetProperty("local_dirty").GetBoolean(), "Cache eviction cleared unsynchronized changes");
            checks.Add("Unsynchronized data stays local and remains dirty while published objects can be evicted.");

            WritePattern(disk, 307); disk.Flush(); latest = await Sync(disk, binding, Repo());
            int deletedBefore = store.Deleted.Count;
            var result = await new SyncCoordinator(Repo()).CleanupAsync(new NativeVolume(disk), binding, new Events(events), default);
            Check(result.Pending && originalData.All(o => store.Files.ContainsKey(CloudRepository.ObjectPath(binding.RemoteRoot, o.Id))),
                "Manual cloud cleanup removed objects still needed by the old local snapshot");
            Check(store.Deleted.Count > deletedBefore, "Owned cache pin blocked unrelated obsolete objects as well");
            Check(events.Any(e => e.Action == "cleanup.deferred" && e.Message.Contains("本地", StringComparison.Ordinal)), "Local-reference retention was not distinguished in cleanup diagnostics");
            var snapshotRaw = new byte[CloudRepository.ObjectLength];
            disk.ReadObject(snapshot, originalData[0].Id, 0, snapshotRaw);
            Check(CloudRepository.Hash(snapshotRaw) == originalData[0].Sha256, "Snapshot-only old object did not survive eviction/publication/cleanup");
            checks.Add("Manual cloud cleanup deletes unreferenced candidates while retaining old local-snapshot objects; the snapshot still reads its exact immutable bytes.");
            Evict(disk);
            Check(Missing(disk).Length != 0, "Reopen fixture has no current missing objects");
            disk.Control(new { cmd = "cache.configure", max_bytes = 0UL, policy = "lru" });
            disk.Flush();
        }
        using (var disk = new CoreDisk(path, Password))
        {
            var status = Status(disk);
            Check(status.GetProperty("max_bytes").GetUInt64() == 0 && status.GetProperty("source_ready").GetBoolean()
                && status.GetProperty("missing_objects").GetUInt64() > 0, "Reopen lost disabled-quota placeholders or their backing");
            var router = new Router(disk, Repo(), store); disk.SetObjectProvider(router.ReadAsync);
            CheckPattern(disk, 307);
            Check(router.Requests.Count > 0 && store.Files.ContainsKey(ownPin), "Disabled quota stopped missing-object reads or deleted its pin");
            checks.Add("After reopen with the quota disabled, persistent missing objects still hydrate and the cloud protection pin remains.");
            await CheckCopySources(copyPath, disk, latest.Commit, binding, Repo, store, checks);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, realNetworkRequests = 0, checks }, Json));
        Console.WriteLine("CACHE_POLICY_SMOKE_OK"); return 0;
    }

    private static CloudBinding Binding(CoreDisk disk) => new("baidu-private-web", Account, CloudRepository.RootPath(disk.Id.ToString()), Guid.NewGuid().ToString());
    private static void Bind(CoreDisk disk, CloudBinding b) => disk.Control(new { cmd = "cloud.bind", backend_id = b.ProviderId, account_id = b.AccountId, remote_root = b.RemoteRoot, device_id = b.DeviceId, enabled = true });
    private static Task<SyncResult> Sync(CoreDisk disk, CloudBinding b, CloudRepository repo)
        => new SyncCoordinator(repo).RunAsync(new NativeVolume(disk), b, "isolated cache fixture", disk.Capacity, true, 2, null, default);
    private static JsonElement Status(CoreDisk disk) => disk.Control(new { cmd = "cache.status" });
    private static async Task<string> Enable(CoreDisk disk, CloudBinding b, CloudRepository repo, Router router)
    {
        string pin = await repo.EnsureCachePinAsync(b, disk.Id.ToString());
        disk.Control(new { cmd = "cache.bind", backing = new { reader_pin = pin, backend_id = b.ProviderId, account_id = b.AccountId,
            remote_root = b.RemoteRoot, device_id = b.DeviceId, volume_id = disk.Id.ToString() } });
        disk.SetObjectProvider(router.ReadAsync); // Mirrors worker's reconfiguration after first bind.
        // Deliberately smaller than the GUI minimum to exercise physical eviction with a small fixture.
        disk.Control(new { cmd = "cache.configure", max_bytes = 4UL * MiB, policy = "lru" });
        disk.Control(new { cmd = "cache.online", available = true });
        return pin;
    }
    private static void Evict(CoreDisk disk)
    {
        for (int i = 0; i < 64; i++)
        {
            var status = disk.Control(new { cmd = "cache.step", max_objects = 4 });
            if (Missing(disk).Length != 0 && status.GetProperty("pending_reclaims").GetInt32() == 0) return;
            string? blocked = status.GetProperty("blocked_reason").GetString();
            if (blocked is "offline" or "source_not_ready" or "restore_incomplete" or "sync_in_progress") throw new IOException("Cache fixture unexpectedly blocked: " + blocked);
        }
        throw new IOException("Cache eviction did not reach a missing, physically reclaimed object in bounded steps");
    }
    private static byte[] Pattern(int seed, int chunk) { var data = new byte[MiB]; new Random(seed + chunk * 7919).NextBytes(data); return data; }
    private static void WritePattern(CoreDisk disk, int seed)
    { for (int chunk = 0; chunk < Chunks; chunk++) { byte[] bytes = Pattern(seed, chunk); disk.Write((ulong)chunk * MiB, bytes, bytes.Length); } }
    private static void CheckPattern(CoreDisk disk, int seed)
    { for (int chunk = 0; chunk < Chunks; chunk++) { byte[] actual = new byte[MiB]; disk.Read((ulong)chunk * MiB, actual, actual.Length); Check(actual.SequenceEqual(Pattern(seed, chunk)), "Logical readback mismatch at chunk " + chunk); } }
    private static ExportObject[] Missing(CoreDisk disk) => disk.Control(new { cmd = "lazy.needs", offset = 0UL, length = (ulong)Chunks * MiB, operation = "read", limit = 16 })
        .GetProperty("items").EnumerateArray().Select(SyncCoordinator.ParseObject).ToArray();
    private static ExportObject[] Published(CoreDisk disk)
    {
        var output = new List<ExportObject>(); ulong cursor = 0;
        for (int page = 0; page < 64; page++)
        {
            var result = disk.Control(new { cmd = "cloud.published_objects", cursor, limit = 128 });
            output.AddRange(result.GetProperty("items").EnumerateArray().Select(SyncCoordinator.ParseObject));
            if (result.GetProperty("next_cursor").ValueKind == JsonValueKind.Null) return output.ToArray();
            ulong next = result.GetProperty("next_cursor").GetUInt64(); Check(next > cursor, "Publication cursor did not advance"); cursor = next;
        }
        throw new IOException("Publication fixture exceeded bounded listing");
    }
    private static async Task CheckPolicyBoundary(CoreDisk disk, ExportObject obj)
    {
        var source = disk.Control(new { cmd = "cache.source", object_id = obj.Id });
        var request = new LazyObjectRequest(disk.Id.ToString(), obj.Id, obj.Sha256);
        CacheSourcePolicy.ValidateRequest(source, request, Account);
        foreach (var pair in new[] { ("object_id", Guid.NewGuid().ToString()), ("sha256", new string('f', 64)), ("source", "unknown") })
        {
            var changed = JsonNode.Parse(source.GetRawText())!; changed[pair.Item1] = pair.Item2;
            await Reject(() => CacheSourcePolicy.ValidateRequest(JsonSerializer.SerializeToElement(changed), request, Account));
        }
        foreach (var pair in new[] { ("backend_id", "other"), ("account_id", "other"), ("remote_root", "/OverlayDisk/v4/" + disk.Id), ("volume_id", Guid.NewGuid().ToString()) })
        {
            var changed = JsonNode.Parse(source.GetRawText())!; changed["backing"]![pair.Item1] = pair.Item2;
            await Reject(() => CacheSourcePolicy.ValidateRequest(JsonSerializer.SerializeToElement(changed), request, Account));
        }
        await Reject(() => CacheSourcePolicy.ValidateRequest(source, request, "different-account"));
        await Reject(() => CacheSourcePolicy.ValidateRequest(source, request with { DiskId = Guid.NewGuid().ToString() }, Account));
    }
    private static Task Reject(Action action)
    { try { action(); } catch (IOException) { return Task.CompletedTask; } throw new IOException("Unsafe cache source passed validation"); }
    private static void Check(bool condition, string message) { if (!condition) throw new IOException(message); }

    private static async Task CheckCopySources(string path, CoreDisk source, RemoteCommit commit, CloudBinding original,
        Func<CloudRepository> repo, MemoryStore store, List<string> checks)
    {
        var originObject = Published(source).First(o => o.Kind == "data");
        string readerPin = await repo().PinReaderAsync(original.RemoteRoot, commit, Guid.NewGuid().ToString(), default);
        byte[] root = await repo().ReadObjectAsync(original.RemoteRoot, commit.RootObjectId, commit.RootSha256, commit.ObjectSizeBytes, default);
        var backing = JsonSerializer.SerializeToElement(new { provider_id = original.ProviderId, account_id = Account, source_volume_id = source.Id.ToString(),
            remote_root = original.RemoteRoot, root_object_id = commit.RootObjectId, root_sha256 = commit.RootSha256, reader_pin = readerPin });
        using var copy = CoreDisk.BeginLazyRestore(path, root, Password, backing);
        Check(copy.Id != source.Id, "Independent copy reused original identity");
        var router = new Router(copy, repo(), store); copy.SetObjectProvider(router.ReadAsync);
        for (int step = 0; ; step++)
        {
            Check(step < 2048, "Lazy metadata restore exceeded bound");
            var state = copy.Control(new { cmd = "restore.status", cursor = 0, limit = 128 });
            if (state.GetProperty("phase").GetString() is "ready" or "complete") { copy.Control(new { cmd = "restore.finish" }); break; }
            foreach (var item in state.GetProperty("needed").EnumerateArray())
            {
                var obj = SyncCoordinator.ParseObject(item); Check(obj.Kind == "metadata", "Lazy restore eagerly requested data");
                copy.AcceptRestoreObject(obj.Id, await repo().ReadObjectAsync(original.RemoteRoot, obj.Id, obj.Sha256, commit.ObjectSizeBytes, default));
            }
            copy.Control(new { cmd = "restore.step", max_nodes = 256, max_objects = 4 });
        }
        string snapshot = copy.CreateSnapshot();
        WritePattern(copy, 401); var page = Pattern(409, 0)[..4096]; copy.Write(32UL * MiB, page, page.Length); copy.Flush();
        CloudBinding own = Binding(copy); Bind(copy, own); await Sync(copy, own, repo());
        await Enable(copy, own, repo(), router);
        var origin = copy.Control(new { cmd = "cache.source", object_id = originObject.Id });
        Check(origin.GetProperty("source").GetString() == "origin", "Snapshot-only imported object lost its original source");
        string routed = CacheSourcePolicy.ValidateRequest(origin, new(copy.Id.ToString(), originObject.Id, originObject.Sha256), Account);
        Check(routed == original.RemoteRoot, "Imported snapshot routes to the copy's unrelated cloud root");
        byte[] bytes = new byte[CloudRepository.ObjectLength]; copy.ReadObject(snapshot, originObject.Id, 0, bytes);
        Check(CloudRepository.Hash(bytes) == originObject.Sha256 && router.Requests.Any(r => r.Kind == "origin" && r.Root == original.RemoteRoot), "Original snapshot hydration did not use the verified old root");
        Evict(copy); CheckPattern(copy, 401);
        Check(router.Requests.Any(r => r.Kind == "own" && r.Root == own.RemoteRoot) && store.Files.ContainsKey(readerPin), "Copy's new objects failed own-root hydration or old-source pin was discarded");
        checks.Add("A synchronized independent copy routes snapshot-only imported objects to origin and its new published objects to own, with both source pins retained.");
    }
    private sealed class NativeVolume(CoreDisk disk) : ICloudVolume
    {
        public string Id => disk.Id.ToString();
        public int ObjectSizeBytes => checked((int)disk.ObjectSizeBytes);
        public Task<JsonElement> ControlAsync(object request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(disk.Control(request)); }
        public Task<byte[]> ReadObjectAsync(string job, string id, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(disk.ReadExport(job, id)); }
    }
    private sealed class Router(CoreDisk disk, CloudRepository repo, MemoryStore store)
    {
        internal ConcurrentQueue<(string Root, string Kind, string Id)> Requests { get; } = new();
        internal async Task<byte[]> ReadAsync(LazyObjectRequest request, CancellationToken ct)
        {
            var source = disk.Control(new { cmd = "cache.source", object_id = request.ObjectId });
            string root = CacheSourcePolicy.ValidateRequest(source, request, Account);
            Check(store.Files.ContainsKey(source.GetProperty("backing").GetProperty("reader_pin").GetString()!), "Hydration used an unacknowledged/missing reader pin");
            Requests.Enqueue((root, source.GetProperty("source").GetString()!, request.ObjectId));
            return await repo.ReadObjectAsync(root, request.ObjectId, request.Sha256, request.Length, ct);
        }
    }
    private sealed class Events(List<SyncLogEntry> events) : IProgress<SyncLogEntry>
    { public void Report(SyncLogEntry value) { lock (events) events.Add(value); } }
    private sealed class MemoryStore : ICloudObjectStore, ICloudBatchDeleteStore
    {
        public string ProviderId => "baidu-private-web";
        internal ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        internal ConcurrentQueue<string> Deleted { get; } = new();
        private readonly ConcurrentDictionary<string, byte> folders = new(StringComparer.Ordinal);
        public Task<CloudAccountInfo> ValidateAsync(CancellationToken ct = default) => Task.FromResult(new CloudAccountInfo(Account, "offline fixture"));
        public Task<CloudObjectInfo?> HeadAsync(string path, CancellationToken ct = default)
            => Task.FromResult(Files.TryGetValue(path, out var bytes) ? new CloudObjectInfo(path, bytes.Length, false) : folders.ContainsKey(path) ? new CloudObjectInfo(path, 0, true) : null);
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        { for (string at = path; at.Length != 0; at = at[..at.LastIndexOf('/')]) folders[at] = 0; return Task.CompletedTask; }
        public async IAsyncEnumerable<CloudObjectInfo> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Check(!path.Contains("/objects", StringComparison.Ordinal), "Integration invoked cloud object-directory inventory");
            foreach (var file in Files.Where(p => Parent(p.Key) == path)) { ct.ThrowIfCancellationRequested(); yield return new(file.Key, file.Value.Length, false); }
            foreach (string folder in folders.Keys.Where(p => Parent(p) == path)) yield return new(folder, 0, true);
            await Task.CompletedTask;
        }
        public async Task<CloudObjectInfo> PutImmutableAsync(string path, Stream content, long length, string sha256, CancellationToken ct = default)
        {
            using var bytes = new MemoryStream(); await content.CopyToAsync(bytes, ct); byte[] payload = bytes.ToArray();
            Check(payload.Length == length && CloudRepository.Hash(payload) == sha256, "Provider received wrong payload/hash");
            bool added = Files.TryAdd(path, payload);
            if (!added && !Files[path].SequenceEqual(payload)) throw new CloudObjectConflictException(path);
            return new(path, length, false) { ReusedExisting = !added };
        }
        public Task<Stream> OpenReadAsync(string path, CloudByteRange? range = null, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); if (!Files.TryGetValue(path, out var bytes)) throw new CloudObjectNotFoundException(path); return Task.FromResult<Stream>(new MemoryStream(bytes, false)); }
        public Task DeleteAsync(string path, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); Files.TryRemove(path, out _); Deleted.Enqueue(path); return Task.CompletedTask; }
        public async Task DeleteManyAsync(IReadOnlyList<string> paths, CancellationToken ct = default) { foreach (var path in paths) await DeleteAsync(path, ct); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private static string Parent(string path) => path[..path.LastIndexOf('/')];
    }
}
