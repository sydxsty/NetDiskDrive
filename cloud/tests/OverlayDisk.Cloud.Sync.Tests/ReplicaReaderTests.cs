using System.Runtime.CompilerServices;
using System.Text.Json;
using OverlayDisk.Cloud.Contracts;
using OverlayDisk.Cloud.Sync;

internal static class ReplicaReaderTests
{
    private const string Volume = "cc75bd90-79b6-4909-8e3a-137ba16fb73b";
    private const string Writer = "9d3c6fe0-f56f-4b1c-a5ad-cc0b8fe87a31";
    private static readonly string Root = CloudRepository.RootPath(Volume);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("replica: unchanged newest path reads one page and zero descriptor/object payloads", Unchanged),
        ("replica: jumping many generations reads only the newest descriptor", LatestDirect),
        ("replica: same-generation conflicts spanning pages reject before downloading", ConflictAcrossPages),
        ("replica: fresh enumeration bypasses cached absence and missing directory is empty", FreshDiscovery),
        ("replica: disk discovery is fresh and does not hide a vanished commit payload", DiskDiscovery),
        ("replica: generic fallback remains fresh and ignores obsolete generation conflicts", GenericFallback),
        ("replica: durable reader pin rechecks latest and removes only a raced candidate pin", PinRace),
        ("replica: confirmed existing pins resume old sources while recreated pins recheck newest", ResumePin),
        ("replica: compressed object bytes are counted in the one verified read", WireBytes),
        ("replica: malformed ordering descriptor length and identity reject", InvalidMetadata),
        ("replica: cancellation cannot select or pin a new source", Cancellation)
    ];
    private static RemoteCommit Commit(ulong generation, string? objectId = null) => new(4, Volume, Writer, generation,
        "Replica", 64UL * 1024 * 1024, false, objectId ?? $"00000000-0000-0000-0000-{generation:D12}", new string('a', 64), DateTimeOffset.UnixEpoch);
    private static void Assert(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static async Task Reject(Func<Task> action)
    { try { await action(); } catch (IOException) { return; } throw new Exception("Unsafe reader metadata was accepted"); }

    private sealed class Store : ICloudObjectStore, ICloudDescendingDirectoryReader, ICloudBoundedObjectReader
    {
        public string ProviderId => "replica-test";
        internal readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
        internal readonly List<string> Calls = [];
        internal int PageSize = 16, Pages, Yields;
        internal bool Missing, Unordered, AllowObjectRead, DiscoverDisks, MissingDescriptor;
        internal Action? AfterPut;
        internal long LengthDelta;
        internal void Add(RemoteCommit commit) => Files[CloudRepository.CommitPath(Root, commit)] = JsonSerializer.SerializeToUtf8Bytes(commit, Json);
        internal void Reset() { Calls.Clear(); Pages = Yields = 0; }
        public Task<CloudAccountInfo> ValidateAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Calls.Add("account"); return Task.FromResult(new CloudAccountInfo("reader-account", "fixture")); }
        public Task<CloudObjectInfo?> HeadAsync(string path, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Calls.Add("head:" + path); return Task.FromResult<CloudObjectInfo?>(null); }
        public IAsyncEnumerable<CloudObjectInfo> ListAsync(string directory, CancellationToken ct = default) => Enumerate(directory, false, ct);
        public IAsyncEnumerable<CloudObjectInfo> ListByNameDescendingAsync(string directory, CancellationToken ct = default) => Enumerate(directory, true, ct);
        private async IAsyncEnumerable<CloudObjectInfo> Enumerate(string directory, bool descending, [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls.Add((descending ? "descending:" : "list:") + directory);
            if (DiscoverDisks && directory == CloudRepository.BasePath)
            {
                if (Missing) throw new CloudObjectNotFoundException(directory);
                yield return new(Root, 0, true); yield return new(CloudRepository.BasePath + "/v4", 0, true);
                yield break;
            }
            Assert(directory == Root + "/commits", "Version discovery scanned object directories");
            if (Missing) throw new CloudObjectNotFoundException(directory);
            var sorted = Files.Where(pair => pair.Key.StartsWith(directory + "/", StringComparison.Ordinal)).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            if (descending && !Unordered) Array.Reverse(sorted);
            for (int i = 0; i < sorted.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (i % PageSize == 0) Pages++;
                Yields++;
                yield return new(sorted[i].Key, sorted[i].Value.LongLength + LengthDelta, false);
            }
            await Task.CompletedTask;
        }
        public Task<Stream> OpenReadAsync(string path, CloudByteRange? range = null, CancellationToken ct = default)
        { Calls.Add("open:" + path); return Read(path, ct); }
        public Task<Stream> OpenReadBoundedAsync(string path, int maximumLength, CancellationToken ct = default)
        { Calls.Add("bounded:" + path); Assert(maximumLength == (path.EndsWith(".obj", StringComparison.Ordinal) ? ObjectTransport.MaxWireLength(CloudObjectGeometry.DefaultSize) : 65536), "Read is not bounded"); return Read(path, ct); }
        private Task<Stream> Read(string path, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert(path.Contains("/commits/", StringComparison.Ordinal) || AllowObjectRead && path.EndsWith(".obj", StringComparison.Ordinal), "Version check downloaded an object payload");
            if (MissingDescriptor || !Files.TryGetValue(path, out var bytes)) throw new CloudObjectNotFoundException(path);
            return Task.FromResult<Stream>(new MemoryStream(bytes, false));
        }
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Calls.Add("mkdir:" + path); return Task.CompletedTask; }
        public async Task<CloudObjectInfo> PutImmutableAsync(string path, Stream content, long length, string sha256, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Assert(path.StartsWith(Root + "/readers/", StringComparison.Ordinal), "Replica wrote outside reader protection");
            using var bytes = new MemoryStream(); await content.CopyToAsync(bytes, ct);
            Assert(bytes.Length == length && CloudRepository.Hash(bytes.ToArray()) == sha256, "Pin bytes mismatch");
            bool reused = Files.TryGetValue(path, out var previous);
            if (reused && !previous!.SequenceEqual(bytes.ToArray())) throw new CloudObjectConflictException(path);
            Calls.Add("put:" + path); Files[path] = bytes.ToArray(); AfterPut?.Invoke(); return new(path, length, false) { ReusedExisting = reused };
        }
        public Task DeleteAsync(string path, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Calls.Add("delete:" + path); Files.Remove(path); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task Unchanged()
    {
        await using var store = new Store(); for (ulong generation = 1; generation <= 2000; generation++) store.Add(Commit(generation));
        var known = Commit(2000); var latest = await new CloudRepository(store).LatestForReplicaAsync(Root, known);
        Assert(ReferenceEquals(latest?.Commit, known), "Known descriptor was re-created or downloaded");
        Assert(store.Pages == 1 && store.Yields == 2, "Reader enumerated immutable history");
        Assert(store.Calls.SequenceEqual(["descending:" + Root + "/commits"]), "Unchanged check made HEAD/GET/mutation calls");
    }
    private static async Task LatestDirect()
    {
        await using var store = new Store(); for (ulong generation = 1; generation <= 100; generation++) store.Add(Commit(generation));
        var latest = await new CloudRepository(store).LatestForReplicaAsync(Root, Commit(1));
        Assert(latest?.Commit == Commit(100) && store.Pages == 1, "Reader did not directly select newest version");
        Assert(store.Calls.SequenceEqual(["descending:" + Root + "/commits", "bounded:" + CloudRepository.CommitPath(Root, Commit(100))]), "Jump read historic descriptors or made metadata probes");
    }
    private static async Task ConflictAcrossPages()
    {
        await using var store = new Store { PageSize = 1 }; var current = Commit(8); store.Add(current);
        store.Add(Commit(8, "ffffffff-ffff-ffff-ffff-ffffffffffff")); store.Add(Commit(7));
        await Reject(async () => _ = await new CloudRepository(store).LatestForReplicaAsync(Root, current));
        Assert(store.Pages == 2 && store.Calls.All(c => c.StartsWith("descending:", StringComparison.Ordinal)), "Conflict crossed a page without being checked before GET");
    }
    private static async Task FreshDiscovery()
    {
        await using var store = new Store(); store.Add(Commit(1)); var repository = new CloudRepository(store);
        Assert((await repository.LatestForReplicaAsync(Root))?.Commit.Generation == 1, "Cached negative HEAD hid the current commit");
        store.Add(Commit(2)); store.Reset();
        Assert((await repository.LatestForReplicaAsync(Root, Commit(1)))?.Commit.Generation == 2, "Manual check reused stale remote version");
        store.Missing = true;
        Assert(await repository.LatestForReplicaAsync(Root, Commit(2)) is null, "Missing directory produced a fabricated known version");
        Assert(store.Calls.All(c => !c.StartsWith("head:", StringComparison.Ordinal)), "Reader consulted stale absence proof");
    }
    private sealed class GenericStore(Store inner) : ICloudObjectStore
    {
        public string ProviderId => inner.ProviderId;
        public Task<CloudAccountInfo> ValidateAsync(CancellationToken ct = default) => inner.ValidateAsync(ct);
        public Task<CloudObjectInfo?> HeadAsync(string path, CancellationToken ct = default) => inner.HeadAsync(path, ct);
        public IAsyncEnumerable<CloudObjectInfo> ListAsync(string path, CancellationToken ct = default) => inner.ListAsync(path, ct);
        public Task<Stream> OpenReadAsync(string path, CloudByteRange? range = null, CancellationToken ct = default) => inner.OpenReadAsync(path, range, ct);
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => inner.CreateDirectoryAsync(path, ct);
        public Task<CloudObjectInfo> PutImmutableAsync(string path, Stream content, long length, string sha256, CancellationToken ct = default) => inner.PutImmutableAsync(path, content, length, sha256, ct);
        public Task DeleteAsync(string path, CancellationToken ct = default) => inner.DeleteAsync(path, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static async Task DiskDiscovery()
    {
        await using var store = new Store { DiscoverDisks = true }; store.Add(Commit(7)); store.Add(Commit(8));
        var repository = new CloudRepository(store); var disks = await repository.ListDisksForReplicaAsync();
        Assert(disks.Count == 1 && disks[0].Commit == Commit(8) && store.Calls.All(c => !c.StartsWith("head:", StringComparison.Ordinal)), "Discovery trusted stale absence or enumerated version folders");
        store.MissingDescriptor = true;
        await Reject(async () => _ = await repository.ListDisksForReplicaAsync());
        store.MissingDescriptor = false;
        store.Files[CloudRepository.CommitPath(Root, Commit(8))] = JsonSerializer.SerializeToUtf8Bytes(Commit(8) with { TransportCodec = "unsupported" }, Json);
        Assert((await repository.ListDisksForReplicaAsync()).Count == 0, "Unsupported disk was offered for import");
        store.Missing = true;
        Assert((await repository.ListDisksForReplicaAsync()).Count == 0, "Missing account directory was not empty");
    }
    private static async Task GenericFallback()
    {
        await using var store = new Store(); store.Add(Commit(1)); store.Add(Commit(1, "ffffffff-ffff-ffff-ffff-ffffffffffff")); store.Add(Commit(9));
        var latest = await new CloudRepository(new GenericStore(store)).LatestForReplicaAsync(Root, Commit(9));
        Assert(latest?.Commit == Commit(9) && store.Yields == 3 && store.Calls.Count == 1, "Generic fallback falsely rejected historic conflicts or read known payloads");
        store.Add(Commit(9, "ffffffff-ffff-ffff-ffff-ffffffffffff"));
        await Reject(async () => _ = await new CloudRepository(new GenericStore(store)).LatestForReplicaAsync(Root, Commit(9)));
    }
    private static async Task PinRace()
    {
        await using var store = new Store(); var current = Commit(8); store.Add(current); var repository = new CloudRepository(store);
        string oldPin = await repository.PinReplicaReaderAsync(Root, current, "old-reader");
        Assert(store.Calls.All(c => !c.StartsWith("bounded:", StringComparison.Ordinal)), "Unchanged pin recheck downloaded descriptor again");
        store.AfterPut = () => store.Add(Commit(9)); store.Reset();
        await Reject(() => repository.PinReplicaReaderAsync(Root, current, "new-reader"));
        Assert(store.Files.ContainsKey(oldPin) && !store.Files.ContainsKey(Root + "/readers/new-reader.json"), "Raced pin cleanup removed old source protection or retained wrong candidate");
    }
    private static async Task InvalidMetadata()
    {
        await using var store = new Store { Unordered = true }; store.Add(Commit(1)); store.Add(Commit(2));
        await Reject(async () => _ = await new CloudRepository(store).LatestForReplicaAsync(Root));
        store.Unordered = false; store.LengthDelta = 1;
        await Reject(async () => _ = await new CloudRepository(store).LatestForReplicaAsync(Root));
        store.LengthDelta = 0;
        store.Files[CloudRepository.CommitPath(Root, Commit(2))] = JsonSerializer.SerializeToUtf8Bytes(Commit(2) with { VolumeId = Guid.NewGuid().ToString() }, Json);
        await Reject(async () => _ = await new CloudRepository(store).LatestForReplicaAsync(Root));
        await Reject(async () => _ = await new CloudRepository(store).LatestForReplicaAsync(Root, Commit(2) with { VolumeId = Guid.NewGuid().ToString() }));
    }
    private static async Task ResumePin()
    {
        await using var store = new Store(); var commit = Commit(8); store.Add(commit); var repository = new CloudRepository(store);
        string path = await repository.EnsureReplicaReaderAsync(Root, commit, "durable-reader", false);
        store.Add(Commit(9)); store.Reset();
        Assert(await repository.EnsureReplicaReaderAsync(Root, commit, "durable-reader", true) == path && store.Calls.All(c => !c.StartsWith("descending:", StringComparison.Ordinal)),
            "A confirmed protected snapshot could not resume after newer publication");
        store.Files.Remove(path); store.Reset();
        await Reject(() => repository.EnsureReplicaReaderAsync(Root, commit, "durable-reader", true));
        Assert(!store.Files.ContainsKey(path) && store.Calls.Any(c => c.StartsWith("descending:", StringComparison.Ordinal)), "Recreated old pin bypassed newest check");
    }
    private static async Task WireBytes()
    {
        await using var store = new Store { AllowObjectRead = true }; var data = new byte[CloudObjectGeometry.DefaultSize]; data[4096] = 3;
        string id = "002f40f0-6d83-4f8c-8ce1-1a71ad8c8062", path = CloudRepository.ObjectPath(Root, id), hash = CloudRepository.Hash(data);
        using (var prepared = await PreparedObjectUpload.CreateAsync(new(path, data.Length, hash), new MemoryStream(data, false)))
        {
            using var input = prepared.Wire.OpenRead(); using var bytes = new MemoryStream(); await input.CopyToAsync(bytes); store.Files[path] = bytes.ToArray();
        }
        var result = await new CloudRepository(store).ReadObjectForReplicaAsync(Root, id, hash, data.Length);
        Assert(result.Canonical.SequenceEqual(data) && result.WireBytes == store.Files[path].Length && result.WireBytes < data.Length,
            "Replica read reported logical size or decoded incorrect bytes");
        Assert(store.Calls.SequenceEqual(["bounded:" + path]), "Traffic accounting downloaded the payload more than once");
        await Reject(async () => _ = await new CloudRepository(store).ReadObjectForReplicaAsync(Root, id, new string('b', 64), data.Length));
    }
    private static async Task Cancellation()
    {
        await using var store = new Store(); store.Add(Commit(1)); using var stop = new CancellationTokenSource(); stop.Cancel();
        try { _ = await new CloudRepository(store).LatestForReplicaAsync(Root, Commit(1), stop.Token); throw new Exception("Cancelled latest succeeded"); }
        catch (OperationCanceledException) { }
        try { _ = await new CloudRepository(store).PinReplicaReaderAsync(Root, Commit(1), "reader", stop.Token); throw new Exception("Cancelled pin succeeded"); }
        catch (OperationCanceledException) { }
        Assert(store.Calls.Count == 0, "Cancelled source discovery mutated remote state");
    }
}
