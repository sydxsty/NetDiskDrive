using System.Runtime.CompilerServices;
using System.Text.Json;
using OverlayDisk.Cloud.Contracts;
using OverlayDisk.Cloud.Sync;

internal static class OriginalRestoreTests
{
    private const string Volume = "382813ac-0bb9-4b4d-9c5b-d42030f6fe46";
    private const string Writer = "4bfd87a9-fc40-4c12-a1bd-ab9827b6d565";
    private const string RootObject = "e8a963a3-8e48-4f63-a8c6-f98e116dabde";
    private const string Account = "original-test-account";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Root => CloudRepository.RootPath(Volume);
    private static RemoteCommit Commit(ulong generation = 7) => new(4, Volume, Writer, generation, "Original disk",
        64UL * 1024 * 1024, true, RootObject, new string('a', 64), DateTimeOffset.UnixEpoch);
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("original restore attaches the recorded writer with metadata reads only and zero-API no-op", AttachAndNoop),
        ("original restore rejects account owner root and writer identity mismatches", Identity),
        ("original restore rechecks latest and refuses changed or divergent commits", LatestChanged),
        ("original restore safely abandons absent-container intents without cloud mutation", ProvenCache),
        ("original restore drops unprovable deletion proof and checks the native published root", UnprovenCache),
        ("original restore cannot return a binding after its baseline cache save fails", SaveFailure)
    ];
    private static void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static async Task Reject(Func<Task> action)
    { try { await action(); } catch (IOException) { return; } throw new Exception("Unsafe original restore was accepted"); }

    private sealed class Store : ICloudObjectStore
    {
        public string ProviderId => "original-restore-test";
        internal string AccountId = Account;
        internal readonly Dictionary<string, byte[]> Metadata = new(StringComparer.Ordinal);
        internal readonly List<string> Calls = [];
        internal Store() { Owner(); Add(Commit()); }
        internal void Owner(string account = Account, string volume = Volume, string writer = Writer, int version = 4)
            => Metadata[Root + "/owner.json"] = JsonSerializer.SerializeToUtf8Bytes(new { version, accountId = account, volumeId = volume, writerId = writer }, Json);
        internal void Add(RemoteCommit commit) => Metadata[CloudRepository.CommitPath(Root, commit)] = JsonSerializer.SerializeToUtf8Bytes(commit, Json);
        public Task<CloudAccountInfo> ValidateAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Calls.Add("account"); return Task.FromResult(new CloudAccountInfo(AccountId, "fixture")); }
        public Task<CloudObjectInfo?> HeadAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls.Add("head:" + path);
            Assert(path == Root + "/commits", "Original attach probed objects or an unrelated directory");
            return Task.FromResult<CloudObjectInfo?>(new(path, 0, true));
        }
        public async IAsyncEnumerable<CloudObjectInfo> ListAsync(string directory, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add("list:" + directory); Assert(directory == Root + "/commits", "Original attach enumerated the object inventory");
            foreach (var (path, bytes) in Metadata.Where(pair => pair.Key.StartsWith(directory + "/", StringComparison.Ordinal)).OrderBy(pair => pair.Key, StringComparer.Ordinal))
            { cancellationToken.ThrowIfCancellationRequested(); yield return new(path, bytes.Length, false); }
            await Task.CompletedTask;
        }
        public Task<Stream> OpenReadAsync(string path, CloudByteRange? range = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls.Add("read:" + path);
            Assert(path == Root + "/owner.json" || path.StartsWith(Root + "/commits/", StringComparison.Ordinal), "Original attach downloaded an existing payload");
            if (!Metadata.TryGetValue(path, out var bytes)) throw new CloudObjectNotFoundException(path);
            return Task.FromResult<Stream>(new MemoryStream(bytes, false));
        }
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => throw new Exception("Original attach tried mkdir");
        public Task<CloudObjectInfo> PutImmutableAsync(string path, Stream content, long length, string sha256, CancellationToken cancellationToken = default)
            => throw new Exception("Original attach tried PUT/owner replacement");
        public Task DeleteAsync(string path, CancellationToken cancellationToken = default) => throw new Exception("Original attach tried automatic cleanup");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RestoredVolume(RemoteCommit commit) : ICloudVolume
    {
        public string Id => Volume;
        public int ObjectSizeBytes => commit.ObjectSizeBytes;
        public Task<JsonElement> ControlAsync(object request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); string command = JsonSerializer.SerializeToElement(request).GetProperty("cmd").GetString()!;
            Assert(command is "cloud.pause" or "cloud.status", "Initial resumed sync prepared/scanned/uploaded objects");
            object status = new { object_size = ObjectSizeBytes, data_generation = commit.Generation, published_generation = commit.Generation, pending_generation = commit.Generation,
                local_dirty = false, job = (object?)null, published_commit = new { root_object_id = commit.RootObjectId, root_sha256 = commit.RootSha256, generation = commit.Generation } };
            return Task.FromResult(JsonSerializer.SerializeToElement(command == "cloud.pause" ? new { status } : status));
        }
        public Task<byte[]> ReadObjectAsync(string jobId, string objectId, CancellationToken cancellationToken) => throw new Exception("Initial resumed sync opened a local payload");
    }
    private static CloudBinding Binding(Store store) => new(store.ProviderId, Account, Root, Writer);
    private static CloudCacheScope Scope(Store store) => CloudCacheScope.From(Binding(store));
    private static CloudSyncCacheState Baseline(Store store, RemoteCommit commit) => new()
    {
        Scope = Scope(store), OwnerConfirmed = true, LatestKnown = true, Latest = commit,
        LatestPath = CloudRepository.CommitPath(Root, commit), PublishedCommitPath = CloudRepository.CommitPath(Root, commit), ClosureGeneration = commit.Generation
    };
    private static async Task Save(ICloudSyncCache cache, Store store, CloudSyncCacheState state)
    { await using var lease = await cache.AcquireAsync(Scope(store)); await cache.SaveAsync(Scope(store), state); }
    private static async Task<CloudSyncCacheState> Load(ICloudSyncCache cache, Store store)
    { await using var lease = await cache.AcquireAsync(Scope(store)); return await cache.LoadAsync(Scope(store)) ?? throw new Exception("Attach baseline missing"); }

    private static async Task AttachAndNoop()
    {
        await using var store = new Store(); var cache = new MemoryCloudSyncCache();
        var repository = new CloudRepository(store, cache, Account);
        var binding = await repository.PrepareOriginalRestoreAsync(Volume, Commit());
        Assert(binding == Binding(store), "Attach changed the original volume/writer/account binding");
        Assert(store.Calls.Count(call => call.StartsWith("read:", StringComparison.Ordinal)) == 2, "Attach did not read exactly owner and latest commit");
        var state = await Load(cache, store);
        Assert(state.OwnerConfirmed && state.LatestKnown && state.Latest == Commit() && state.ClosureGeneration == Commit().Generation,
            "Attach did not retain its confirmed publication baseline");
        Assert(!state.ConfirmedFolders.Contains(Root + "/objects") && !state.ConfirmedFolders.Contains(Root + "/readers"), "Attach invented unverified directories");
        store.Calls.Clear();
        var reopenedRepository = new CloudRepository(store, cache, Account);
        var result = await new SyncCoordinator(reopenedRepository).RunAsync(new RestoredVolume(Commit()), binding, "Original", Commit().CapacityBytes, true, 1, null, default);
        Assert(result.Commit == Commit() && !result.HasPendingChanges && store.Calls.Count == 0, "First unchanged original sync issued a cloud API or changed identity");
    }

    private static async Task Identity()
    {
        foreach (Action<Store> change in new Action<Store>[] {
            s => s.AccountId = "other-account", s => s.Owner(account: "other-account"), s => s.Owner(volume: Writer),
            s => s.Owner(writer: Volume), s => s.Owner(version: 3), s => s.Metadata.Remove(Root + "/owner.json"),
            s => s.Metadata[Root + "/owner.json"] = "not-json"u8.ToArray() })
        {
            await using var store = new Store(); change(store); var cache = new MemoryCloudSyncCache();
            await Reject(() => new CloudRepository(store, cache, Account).PrepareOriginalRestoreAsync(Volume, Commit()));
            await using var lease = await cache.AcquireAsync(Scope(store)); Assert(await cache.LoadAsync(Scope(store)) is null, "Rejected identity acquired a cache proof");
        }
        await using var invalid = new Store(); var repo = new CloudRepository(invalid, new MemoryCloudSyncCache(), Account);
        await Reject(() => repo.PrepareOriginalRestoreAsync(Writer, Commit()));
        await Reject(() => repo.PrepareOriginalRestoreAsync(Volume, Commit() with { RootSha256 = "invalid" }));
        Assert(invalid.Calls.Count == 0, "Invalid requested volume/descriptor reached cloud APIs");
        await Reject(() => repo.PrepareOriginalRestoreAsync(Volume, Commit() with { CapacityBytes = CloudRepository.MaximumCapacityBytes + 512 }));
        Assert(invalid.Calls.Count == 0, "An over-8-TiB descriptor reached cloud APIs");
        await using var large = new Store(); var largest = Commit() with { CapacityBytes = CloudRepository.MaximumCapacityBytes }; large.Add(largest);
        var largeBinding = await new CloudRepository(large, validatedAccountId: Account).PrepareOriginalRestoreAsync(Volume, largest);
        Assert(largeBinding.RemoteRoot == Root && largest.CapacityBytes == 8UL * 1024 * 1024 * 1024 * 1024, "An exact 8-TiB descriptor was not accepted");
    }

    private static async Task LatestChanged()
    {
        await using var store = new Store(); var cache = new MemoryCloudSyncCache(); var repository = new CloudRepository(store, cache, Account);
        await repository.PrepareOriginalRestoreAsync(Volume, Commit());
        var unfinished = await Load(cache, store);
        unfinished.Publication = Intent(Commit(8) with { RootObjectId = Writer }, acknowledged: false);
        await Save(cache, store, unfinished);
        var later = Commit(8) with { RootObjectId = Writer, RootSha256 = new string('b', 64) }; store.Add(later);
        await Reject(() => repository.PrepareOriginalRestoreAsync(Volume, Commit()));
        var unchanged = await Load(cache, store);
        Assert(unchanged.Latest == Commit() && unchanged.Publication is not null, "Failed final recheck changed the baseline or abandoned an intent before verification");
        await using var changedBody = new Store(); changedBody.Add(Commit() with { Name = "Modified descriptor" });
        await Reject(() => new CloudRepository(changedBody).PrepareOriginalRestoreAsync(Volume, Commit()));
        await using var divergent = new Store(); divergent.Add(Commit() with { RootObjectId = Writer });
        await Reject(() => new CloudRepository(divergent).PrepareOriginalRestoreAsync(Volume, Commit()));
    }

    private static async Task ProvenCache()
    {
        await using var store = new Store(); var cache = new MemoryCloudSyncCache(); var state = Baseline(store, Commit());
        string oldPath = CloudRepository.CommitPath(Root, Commit(6)), currentPath = CloudRepository.CommitPath(Root, Commit());
        state.PendingDeleteObjects.UnionWith(["old-data-object", RootObject]); state.PendingDeleteCommits.UnionWith([oldPath, currentPath]);
        await Save(cache, store, state);
        await new CloudRepository(store, cache, Account).PrepareOriginalRestoreAsync(Volume, Commit());
        var retained = await Load(cache, store);
        Assert(retained.PendingDeleteObjects.SetEquals(["old-data-object"]) && retained.PendingDeleteCommits.SetEquals([oldPath]), "Same-baseline safe candidates were lost or current root became deletable");
        retained.Publication = Intent(Commit(8) with { RootObjectId = Writer }, acknowledged: false);
        await Save(cache, store, retained);
        await new CloudRepository(store, cache, Account).PrepareOriginalRestoreAsync(Volume, Commit());
        var abandoned = await Load(cache, store);
        Assert(abandoned.Publication is null && abandoned.PendingDeleteObjects.Count == 0 && abandoned.PendingDeleteCommits.SetEquals([oldPath]) &&
            abandoned.Latest == Commit() && abandoned.ClosureGeneration == Commit().Generation,
            "Deleted-container unconfirmed intent blocked recovery or invented publication/deletion proof");

        // A completed remote commit whose local finalization was lost is adopted
        // solely because owner/latest were re-read and match it now.
        foreach (bool acknowledged in new[] { false, true })
        {
            var prior = Baseline(store, Commit(6)); prior.PendingDeleteObjects.Add("unproven-old-object");
            prior.Publication = Intent(Commit(), acknowledged); await Save(cache, store, prior);
            await new CloudRepository(store, cache, Account).PrepareOriginalRestoreAsync(Volume, Commit());
            var adopted = await Load(cache, store);
            Assert(adopted.Publication is null && adopted.Latest == Commit() && adopted.PendingDeleteObjects.Count == 0,
                "Freshly confirmed latest intent was not adopted conservatively");
        }
        foreach (var confirmed in new[] { Commit(8) with { RootObjectId = Writer }, Commit() with { RootObjectId = Writer } })
        {
            var conflicted = Baseline(store, Commit()); conflicted.Publication = Intent(confirmed, acknowledged: true); await Save(cache, store, conflicted);
            await Reject(() => new CloudRepository(store, cache, Account).PrepareOriginalRestoreAsync(Volume, Commit()));
            Assert((await Load(cache, store)).Publication?.Commit == confirmed, "An acknowledged later/divergent publication was silently discarded");
        }
    }

    private static CloudPublicationIntent Intent(RemoteCommit commit, bool acknowledged) => new()
    {
        Commit = commit, CommitPath = CloudRepository.CommitPath(Root, commit), DeltaId = "deleted-container-delta",
        Closure = new HashSet<string>([commit.RootObjectId]), Removed = new HashSet<string>(["unproven-intent-removed"]), RemoteVerified = acknowledged
    };

    private static async Task UnprovenCache()
    {
        await using var store = new Store(); var cache = new MemoryCloudSyncCache(); var state = Baseline(store, Commit(6));
        state.PendingDeleteObjects.Add("possibly-reintroduced-object");
        string oldPath = CloudRepository.CommitPath(Root, Commit(5)), futurePath = CloudRepository.CommitPath(Root, Commit(9));
        state.PendingDeleteCommits.UnionWith([oldPath, futurePath]); await Save(cache, store, state);
        var repository = new CloudRepository(store, cache, Account); var binding = await repository.PrepareOriginalRestoreAsync(Volume, Commit());
        var baseline = await Load(cache, store);
        Assert(baseline.PendingDeleteObjects.Count == 0 && baseline.PendingDeleteCommits.SetEquals([oldPath]), "Unproven obsolete-object/future-commit candidate remained eligible for GC");
        store.Calls.Clear();
        await Reject(() => new SyncCoordinator(repository).RunAsync(new RestoredVolume(Commit() with { RootSha256 = new string('b', 64) }), binding,
            "Original", Commit().CapacityBytes, true, 1, null, default));
        Assert(store.Calls.Count == 0, "Wrong native root triggered a cloud mutation instead of refusing no-op");
        await Save(cache, store, Baseline(store, Commit(8)));
        await Reject(() => repository.PrepareOriginalRestoreAsync(Volume, Commit()));
        Assert((await Load(cache, store)).Latest!.Generation == 8, "Attach rolled back a newer known publication");
    }

    private sealed class RefusingSaveCache : ICloudSyncCache
    {
        private readonly MemoryCloudSyncCache inner = new();
        public ValueTask<IAsyncDisposable> AcquireAsync(CloudCacheScope scope, CancellationToken cancellationToken = default) => inner.AcquireAsync(scope, cancellationToken);
        public Task<CloudSyncCacheState?> LoadAsync(CloudCacheScope scope, CancellationToken cancellationToken = default) => inner.LoadAsync(scope, cancellationToken);
        public Task SaveAsync(CloudCacheScope scope, CloudSyncCacheState state, CancellationToken cancellationToken = default) => throw new IOException("Injected baseline persistence failure");
    }
    private static async Task SaveFailure()
    {
        await using var store = new Store(); var cache = new RefusingSaveCache();
        await Reject(() => new CloudRepository(store, cache, Account).PrepareOriginalRestoreAsync(Volume, Commit()));
        await using var lease = await cache.AcquireAsync(Scope(store)); Assert(await cache.LoadAsync(Scope(store)) is null, "Failed cache write granted an original writer baseline");
    }
}
