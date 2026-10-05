using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using OverlayDisk.Cloud.Contracts;
using OverlayDisk.Cloud.Sync;

internal static class ObjectGeometryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("object geometry preserves 4/8/16 MiB publication receipt byte counts and no-op reuse", Publication),
        ("object geometry rejects unsupported or conflicting native and export sizes before upload", Mismatches),
        ("object geometry bounds complete root reads and checks SHA without metadata probes", BoundedReads),
        ("object geometry requires current codec and explicit size in commits and reader pins", RequiredCommitFields)
    ];
    private static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    private static async Task Reject(Func<Task> work)
    { try { await work(); } catch (IOException) { return; } catch (ArgumentException) { return; } throw new Exception("Invalid geometry was accepted"); }
    private sealed class Fixture(int size)
    {
        internal readonly Volume Volume = new(size);
        internal readonly Store Store = new();
        internal readonly MemoryCloudSyncCache Cache = new();
        internal CloudBinding Binding => new(Store.ProviderId, "geometry-account", CloudRepository.RootPath(Volume.Id), Writer);
        private readonly string Writer = Guid.NewGuid().ToString();
        internal CloudRepository Repository => new(Store, Cache, Binding.AccountId);
        internal CloudCacheScope Scope => CloudCacheScope.From(Binding);
        internal async Task Seed(RemoteCommit? prior = null)
        {
            var state = new CloudSyncCacheState { Scope = Scope, OwnerConfirmed = true, LatestKnown = true,
                Latest = prior, LatestPath = prior is null ? null : CloudRepository.CommitPath(Binding.RemoteRoot, prior),
                ClosureGeneration = prior?.Generation ?? 0, PublishedCommitPath = prior is null ? null : CloudRepository.CommitPath(Binding.RemoteRoot, prior) };
            state.ConfirmedFolders.UnionWith([CloudRepository.BasePath, Binding.RemoteRoot, Binding.RemoteRoot + "/commits", Binding.RemoteRoot + "/readers",
                Binding.RemoteRoot + "/objects/" + Volume.DataId[..2], Binding.RemoteRoot + "/objects/" + Volume.RootId[..2]]);
            await using var lease = await Cache.AcquireAsync(Scope); await Cache.SaveAsync(Scope, state);
        }
        internal Task<SyncResult> Run(Progress? progress = null) => new SyncCoordinator(Repository).RunAsync(Volume, Binding, "geometry", 64UL << 20, true, 2, progress, default);
        internal RemoteCommit Commit(int objectSize) => new(4, Volume.Id, Writer, 1, "geometry", 64UL << 20, true, Volume.RootId, Volume.RootHash, DateTimeOffset.UnixEpoch) { ObjectSizeBytes = objectSize };
    }
    private sealed class Progress : IProgress<TransferProgress>
    { internal List<TransferProgress> Items { get; } = []; public void Report(TransferProgress value) { lock (Items) Items.Add(value); } }
    private sealed class Volume : ICloudVolume
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public int ObjectSizeBytes { get; set; }
        internal int? NativeSize, JobSize;
        internal long? WrongExportLength;
        internal readonly string DataId = Guid.NewGuid().ToString(), RootId = Guid.NewGuid().ToString(), JobId = Guid.NewGuid().ToString();
        internal readonly byte[] Data, Root;
        internal readonly string DataHash, RootHash;
        internal int Reads, Controls;
        internal bool Committed;
        internal readonly List<long> ReceiptLengths = [];
        internal Volume(int size)
        {
            ObjectSizeBytes = size; NativeSize = size; JobSize = size;
            Data = new byte[size]; Root = new byte[size]; Data[17] = 83; Data[^1] = 31; Root[17] = 97; Root[^1] = 41;
            DataHash = CloudRepository.Hash(Data); RootHash = CloudRepository.Hash(Root);
        }
        private object Job() => new { id = JobId, phase = "ready", object_size = JobSize, generation = 1UL, root_object_id = RootId, root_sha256 = RootHash,
            total_objects = 2, uploaded_objects = 0, estimated_bytes = (long)ObjectSizeBytes * 2 };
        public Task<JsonElement> ControlAsync(object request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Controls++; var r = JsonSerializer.SerializeToElement(request); object result;
            switch (r.GetProperty("cmd").GetString())
            {
                case "cloud.status": result = new { object_size = NativeSize, data_generation = 1UL, published_generation = Committed ? 1UL : 0UL,
                    local_dirty = !Committed, job = (object?)null, published_commit = Committed ? new { generation = 1UL, root_object_id = RootId, root_sha256 = RootHash } : null }; break;
                case "cloud.pause": case "cloud.transfer": result = new { ok = true }; break;
                case "cloud.prepare": result = new { job = Job() }; break;
                case "cloud.list": result = new { items = new[] { new { id = DataId, kind = "data", length = WrongExportLength ?? Data.Length, sha256 = DataHash, uploaded = false }, new { id = RootId, kind = "index", length = (long)Root.Length, sha256 = RootHash, uploaded = false } }, next_cursor = (long?)null }; break;
                case "cloud.receipt":
                    if (r.TryGetProperty("records", out var records)) foreach (var item in records.EnumerateArray()) ReceiptLengths.Add(item.GetProperty("length").GetInt64());
                    else ReceiptLengths.Add(r.GetProperty("length").GetInt64());
                    result = new { job = Job() }; break;
                case "cloud.delta": result = new { items = Array.Empty<object>(), next_cursor = (long?)null }; break;
                case "cloud.commit": Committed = true; result = new { status = new { object_size = NativeSize, published_generation = 1UL, data_generation = 1UL, local_dirty = false, job = (object?)null } }; break;
                case "cloud.gc_candidates": result = new { allowed = r.GetProperty("object_ids").EnumerateArray().Select(x => x.GetString()!).ToArray(), @protected = Array.Empty<string>(), cache_pin = (object?)null }; break;
                default: throw new Exception("Unexpected native request");
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(result));
        }
        public Task<byte[]> ReadObjectAsync(string jobId, string objectId, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Check(jobId == JobId, "Wrong native job"); Reads++; return Task.FromResult(objectId == DataId ? Data.ToArray() : objectId == RootId ? Root.ToArray() : throw new Exception("Unknown object")); }
    }
    private sealed class Store : ICloudObjectStore, ICloudBoundedObjectReader
    {
        public string ProviderId => "geometry-fixture";
        internal Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        internal List<string> Calls { get; } = [];
        internal long LastKnownLength;
        public Task<CloudAccountInfo> ValidateAsync(CancellationToken ct = default) { Calls.Add("account"); return Task.FromResult(new CloudAccountInfo("geometry-account", "Fixture")); }
        public Task<CloudObjectInfo?> HeadAsync(string path, CancellationToken ct = default)
        { Calls.Add("head:" + path); return Task.FromResult<CloudObjectInfo?>(path.EndsWith("/commits", StringComparison.Ordinal) ? new(path, 0, true) : Files.TryGetValue(path, out var bytes) ? new(path, bytes.Length, false) : null); }
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) { Calls.Add("mkdir:" + path); return Task.CompletedTask; }
        public async IAsyncEnumerable<CloudObjectInfo> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls.Add("list:" + path); Check(!path.Contains("/objects", StringComparison.Ordinal), "Object-directory inventory occurred");
            foreach (var item in Files.Where(p => p.Key[..p.Key.LastIndexOf('/')] == path).ToArray()) { ct.ThrowIfCancellationRequested(); yield return new(item.Key, item.Value.Length, false); }
            await Task.CompletedTask;
        }
        public async Task<CloudObjectInfo> PutImmutableAsync(string path, Stream input, long length, string sha256, CancellationToken ct = default)
        {
            using var bytes = new MemoryStream(); await input.CopyToAsync(bytes, ct); var content = bytes.ToArray();
            Check(content.Length == length && CloudRepository.Hash(content) == sha256, "Wrong publication bytes");
            if (Files.TryGetValue(path, out var prior) && !prior.SequenceEqual(content)) throw new CloudObjectConflictException(path);
            Files[path] = content; Calls.Add("put:" + path); return new(path, length, false);
        }
        public Task<Stream> OpenReadAsync(string path, CloudByteRange? range = null, CancellationToken ct = default)
        { Calls.Add("read:" + path); if (!Files.TryGetValue(path, out var bytes)) throw new CloudObjectNotFoundException(path); return Task.FromResult<Stream>(new MemoryStream(bytes, false)); }
        public Task<Stream> OpenReadBoundedAsync(string path, int maximumLength, CancellationToken ct = default)
        { LastKnownLength = maximumLength; Calls.Add("bounded:" + path); if (!Files.TryGetValue(path, out var bytes)) throw new CloudObjectNotFoundException(path); return Task.FromResult<Stream>(new MemoryStream(bytes, false)); }
        public Task DeleteAsync(string path, CancellationToken ct = default) { Calls.Add("delete:" + path); Files.Remove(path); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static async Task Publication()
    {
        foreach (int size in new[] { 4, 8, 16 }.Select(n => n * 1024 * 1024))
        {
            var f = new Fixture(size); await f.Seed(); var progress = new Progress(); var done = await f.Run(progress);
            Check(done.Commit.ObjectSizeBytes == size && f.Volume.ReceiptLengths.SequenceEqual(new long[] { size, size }) && f.Volume.Reads == 2, "Publication used default-sized roots/receipts");
            var stored = JsonSerializer.Deserialize<RemoteCommit>(f.Store.Files[done.CommitPath], Json)!;
            long wireBytes = f.Store.Files.Where(p => p.Key.EndsWith(".obj", StringComparison.Ordinal)).Sum(p => (long)p.Value.Length);
            Check(stored == done.Commit && progress.Items.Last().UploadedBytes == wireBytes && progress.Items.Last().LogicalUploadedBytes == 2L * size && progress.Items.Last().TotalBytes == 2L * size && wireBytes < 2L * size, "Compressed and canonical progress bytes were confused");
            using (var wire = JsonDocument.Parse(f.Store.Files[done.CommitPath]))
                Check(wire.RootElement.GetProperty("objectSizeBytes").GetInt32() == size && wire.RootElement.GetProperty("transportCodec").GetString() == ObjectTransport.Codec, "Required transport fields absent");
            f.Store.Calls.Clear(); var same = await f.Run(); Check(same.Commit == done.Commit && f.Store.Calls.Count == 0 && f.Volume.Reads == 2, "No-op large-volume sync performed I/O");
            string obsolete = Guid.NewGuid().ToString();
            await using (var lease = await f.Cache.AcquireAsync(f.Scope)) { var state = (await f.Cache.LoadAsync(f.Scope))!; state.PendingDeleteObjects.Add(obsolete); await f.Cache.SaveAsync(f.Scope, state); }
            Check((await f.Repository.CleanupEstimateAsync(f.Binding, f.Volume.Id)).Bytes == size, "Cleanup estimate used 4 MiB for a larger volume");
            var result = await new SyncCoordinator(f.Repository).CleanupAsync(f.Volume, f.Binding, null, default);
            Check(!result.Pending && result.RemainingBytes == 0, "Per-volume cleanup failed");
        }
    }
    private static async Task Mismatches()
    {
        foreach (var kind in new[] { "unsupported", "native", "missing-native", "job", "export", "cached" })
        {
            var f = new Fixture(8 * 1024 * 1024); await f.Seed();
            switch (kind)
            {
                case "unsupported": f.Volume.ObjectSizeBytes = 6 * 1024 * 1024; break;
                case "native": f.Volume.NativeSize = 4 * 1024 * 1024; break;
                case "missing-native": f.Volume.NativeSize = null; break;
                case "job": f.Volume.JobSize = 16 * 1024 * 1024; break;
                case "export": f.Volume.WrongExportLength = 4 * 1024 * 1024; break;
                case "cached": await f.Seed(f.Commit(4 * 1024 * 1024)); break;
            }
            await Reject(async () => { await f.Run(); });
            Check(f.Volume.Reads == 0 && f.Store.Calls.Count == 0, "Mismatched geometry reached payload/network: " + kind);
        }
    }
    private static async Task BoundedReads()
    {
        foreach (int size in new[] { 4, 8, 16 }.Select(n => n * 1024 * 1024))
        {
            var f = new Fixture(size); string path = CloudRepository.ObjectPath(f.Binding.RemoteRoot, f.Volume.RootId);
            using var upload = await PreparedObjectUpload.CreateAsync(new(path, size, f.Volume.RootHash), new MemoryStream(f.Volume.Root, false));
            using var memory = new MemoryStream(); using (var stream = upload.Wire.OpenRead()) await stream.CopyToAsync(memory); byte[] encoded = memory.ToArray();
            f.Store.Files[path] = encoded;
            var actual = await f.Repository.ReadObjectAsync(f.Binding.RemoteRoot, f.Volume.RootId, f.Volume.RootHash, size, default);
            Check(actual.SequenceEqual(f.Volume.Root) && f.Store.LastKnownLength == ObjectTransport.MaxWireLength(size) && f.Store.Calls.Count == 1, "Bounded wire root read lost its canonical identity");
            foreach (int change in new[] { -1, 0, 1 })
            {
                var wrong = new byte[encoded.Length + change]; encoded.AsSpan(0, Math.Min(encoded.Length, wrong.Length)).CopyTo(wrong); if (change == 0) wrong[^1] ^= 1;
                f.Store.Files[path] = wrong; await Reject(() => f.Repository.ReadObjectAsync(f.Binding.RemoteRoot, f.Volume.RootId, f.Volume.RootHash, size, default));
            }
            f.Store.Files[path] = f.Volume.Root;
            await Reject(() => f.Repository.ReadObjectAsync(f.Binding.RemoteRoot, f.Volume.RootId, f.Volume.RootHash, size, default));
            f.Store.Calls.Clear(); await Reject(() => f.Repository.ReadObjectAsync(f.Binding.RemoteRoot, f.Volume.RootId, f.Volume.RootHash, 6 * 1024 * 1024, default));
            Check(f.Store.Calls.Count == 0, "Unsupported root size reached provider");
        }
    }
    private static async Task RequiredCommitFields()
    {
        var f = new Fixture(4 * 1024 * 1024);
        string path = CloudRepository.CommitPath(f.Binding.RemoteRoot, f.Commit(4 * 1024 * 1024));
        foreach (string field in new[] { "objectSizeBytes", "transportCodec" })
        {
            var invalid = JsonSerializer.SerializeToNode(f.Commit(4 * 1024 * 1024), Json)!; invalid.AsObject().Remove(field);
            f.Store.Files[path] = JsonSerializer.SerializeToUtf8Bytes(invalid, Json);
            await Reject(async () => { await f.Repository.LatestAsync(f.Binding.RemoteRoot, default); });
        }
        f.Store.Files[path] = JsonSerializer.SerializeToUtf8Bytes(f.Commit(4 * 1024 * 1024) with { TransportCodec = "raw" }, Json);
        await Reject(async () => { await f.Repository.LatestAsync(f.Binding.RemoteRoot, default); });
        foreach (int size in new[] { 4, 8, 16, 6, 32 }.Select(n => n * 1024 * 1024))
        {
            var commit = f.Commit(size); f.Store.Files[path] = JsonSerializer.SerializeToUtf8Bytes(commit, Json);
            if (CloudObjectGeometry.IsSupported(size))
            {
                Check((await f.Repository.LatestAsync(f.Binding.RemoteRoot, default))!.Value.Commit.ObjectSizeBytes == size, "Large commit geometry was lost");
                string pin = await f.Repository.PinReaderAsync(f.Binding.RemoteRoot, commit, Guid.NewGuid().ToString(), default); Check(f.Store.Files.ContainsKey(pin), "Large commit could not pin a reader");
            }
            else await Reject(async () => { await f.Repository.LatestAsync(f.Binding.RemoteRoot, default); });
        }
    }
}
