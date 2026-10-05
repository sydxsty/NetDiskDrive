using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OverlayDisk.Cloud.Contracts;
using OverlayDisk.Cloud.Sync;

internal static class CloudSyncCacheJournalTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string VolumeId = "917b02ad-35db-4b52-965a-5e8b45f07817";
    private const string WriterId = "dffb9bb9-22de-4e86-973e-9c76e641cc9d";
    private static readonly CloudCacheScope Scope = new("fake-cache-store", "fake-account", WriterId, CloudRepository.RootPath(VolumeId));
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("sync cache tracks set changes and preserves JSON array shape", SetShape),
        ("warm sync cache reads no payload and appends only changed identities", WarmAndDelta),
        ("scope lease rolls back unsaved scalars sets and publication intent", UnsavedRollback),
        ("torn corrupt reordered and wrong-epoch journal grants no GC proof", Corruption),
        ("failed journal append preserves only the earlier durable proof", FailedAppend),
        ("sync cache checkpoints amortize metadata changes", CheckpointBatch),
        ("scope substitution and invalid delta paths cannot acquire a proof", ScopeAndInvalidDelta),
        ("sync cache survives durable and torn-journal process kills", ProcessCrash)
    ];
    private static void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static RemoteCommit Commit(ulong generation = 1, string rootId = "root-current") =>
        new(4, VolumeId, WriterId, generation, "Cache test", 64UL << 20, true, rootId, new string('a', 64), DateTimeOffset.UnixEpoch);
    private static CloudSyncCacheState State(int pending = 0)
    {
        var commit = Commit();
        var state = new CloudSyncCacheState { Scope = Scope, OwnerConfirmed = true, LatestKnown = true, Latest = commit,
            LatestPath = CloudRepository.CommitPath(Scope.RemoteRoot, commit), ClosureGeneration = 1,
            PublishedCommitPath = CloudRepository.CommitPath(Scope.RemoteRoot, commit) };
        state.ConfirmedFolders.UnionWith([CloudRepository.BasePath, Scope.RemoteRoot, Scope.RemoteRoot + "/objects", Scope.RemoteRoot + "/commits"]);
        for (int i = 0; i < pending; i++) state.PendingDeleteObjects.Add("old-" + i.ToString("D8"));
        return state;
    }
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OverlayDisk-sync-journal-" + Guid.NewGuid().ToString("N"));
        public FileCloudSyncCache Cache() => new(Path);
        public string Checkpoint => Directory.GetFiles(Path, "*.cache").Single();
        public string Journal => Checkpoint + ".wal";
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
    private sealed class NoNetworkStore : ICloudObjectStore
    {
        public string ProviderId => Scope.ProviderId;
        private static Exception Network() => new Exception("Warm cleanup estimate attempted a network operation");
        public Task<CloudAccountInfo> ValidateAsync(CancellationToken cancellationToken = default) => throw Network();
        public Task<CloudObjectInfo?> HeadAsync(string path, CancellationToken cancellationToken = default) => throw Network();
        public IAsyncEnumerable<CloudObjectInfo> ListAsync(string directory, CancellationToken cancellationToken = default) => throw Network();
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => throw Network();
        public Task<CloudObjectInfo> PutImmutableAsync(string path, Stream content, long length, string sha256, CancellationToken cancellationToken = default) => throw Network();
        public Task<Stream> OpenReadAsync(string path, CloudByteRange? range = null, CancellationToken cancellationToken = default) => throw Network();
        public Task DeleteAsync(string path, CancellationToken cancellationToken = default) => throw Network();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static Task SetShape()
    {
        CacheStringSet set = new HashSet<string>(["aA", "aa"], StringComparer.Ordinal);
        set.ExceptWith(["aA"]); set.UnionWith(["bb", "cc"]); set.IntersectWith(["aa", "bb"]);
        set.SymmetricExceptWith(["bb", "dd"]);
        Assert(set.SetEquals(["aa", "dd"]), "ISet operations lost identity or case sensitivity");
        string json = JsonSerializer.Serialize(set, Json);
        Assert(json.StartsWith('[') && !json.Contains("added"), "Collection serialized tracker internals");
        var restored = JsonSerializer.Deserialize<CacheStringSet>(json, Json)!;
        Assert(restored.SetEquals(set), "Array JSON did not deserialize through ISet");
        return Task.CompletedTask;
    }

    private static async Task WarmAndDelta()
    {
        using var folder = new Folder(); var cache = folder.Cache();
        await using (await cache.AcquireAsync(Scope))
        {
            var state = State(30_000); await cache.SaveAsync(Scope, state);
            var checkpoint = await File.ReadAllBytesAsync(folder.Checkpoint);
            var before = cache.GetDiagnostics(); long walBefore = new FileInfo(folder.Journal).Length;
            state.PendingDeleteObjects.Remove("old-00000017"); state.PendingDeleteObjects.Add("new-candidate");
            await cache.SaveAsync(Scope, state);
            Assert((await File.ReadAllBytesAsync(folder.Checkpoint)).SequenceEqual(checkpoint), "Small change rewrote the full checkpoint");
            Assert(new FileInfo(folder.Journal).Length - walBefore < 8192, "Small delta serialized the old pending catalog");
            var after = cache.GetDiagnostics();
            Assert(after.CheckpointWrites == 1 && after.JournalWrites == 1 && after.BytesRead == before.BytesRead, "Save reread/checkpointed unchanged content");
            await cache.SaveAsync(Scope, state);
            Assert(cache.GetDiagnostics().JournalWrites == 1, "Unchanged save appended another record");
        }
        var freshInstance = folder.Cache(); var initial = freshInstance.GetDiagnostics();
        await using var store = new NoNetworkStore();
        var repository = new CloudRepository(store, freshInstance, Scope.AccountId);
        var binding = new CloudBinding(Scope.ProviderId, Scope.AccountId, Scope.RemoteRoot, Scope.WriterId);
        for (int i = 0; i < 5; i++)
        {
            var estimate = await repository.CleanupEstimateAsync(binding, VolumeId);
            Assert(estimate.Known && estimate.Objects == 30_000 && estimate.Commits == 0 && estimate.Bytes == 30_000L * CloudRepository.ObjectLength,
                "Repository warm cleanup estimate differs from the durable proof");
        }
        var warm = freshInstance.GetDiagnostics();
        Assert(warm.BytesRead == initial.BytesRead && warm.WarmHits == 5, "Warm no-op reread or cloned the cache payload");
    }

    private static async Task UnsavedRollback()
    {
        using var folder = new Folder(); var cache = folder.Cache();
        await using (await cache.AcquireAsync(Scope)) { await cache.SaveAsync(Scope, State(3)); }
        await using (await cache.AcquireAsync(Scope))
        {
            var state = (await cache.LoadAsync(Scope))!;
            state.OwnerConfirmed = false; state.PendingDeleteObjects.Clear(); state.PendingDeleteObjects.Add("uncommitted-delete");
            var commit = Commit(2, "future-root");
            state.Latest = commit; state.LatestPath = CloudRepository.CommitPath(Scope.RemoteRoot, commit);
            state.Publication = new() { Commit = commit, CommitPath = state.LatestPath, DeltaId = "uncommitted-delta",
                Closure = new HashSet<string>(["future-root"]), Removed = new HashSet<string>(["old-00000000"]), RemoteVerified = true };
        }
        await using (await cache.AcquireAsync(Scope))
        {
            var state = (await cache.LoadAsync(Scope))!;
            Assert(state.OwnerConfirmed && state.Latest!.Generation == 1 && state.Publication is null && state.PendingDeleteObjects.Count == 3 &&
                !state.PendingDeleteObjects.Contains("uncommitted-delete"), "Unsaved changes became a warm durable proof");
            var commit = Commit(2, "future-root");
            state.Publication = new() { Commit = commit, CommitPath = CloudRepository.CommitPath(Scope.RemoteRoot, commit), DeltaId = "durable-delta",
                Closure = new HashSet<string>(["future-root"]), Removed = new HashSet<string>(["old-00000000"]) };
            await cache.SaveAsync(Scope, state);
            state.Publication.RemoteVerified = true; state.Publication.Closure.Add("not-saved");
            state.Publication.Removed.Clear();
        }
        await using (await cache.AcquireAsync(Scope))
        {
            var state = (await cache.LoadAsync(Scope))!;
            Assert(state.Publication is { RemoteVerified: false } && state.Publication.Closure.SetEquals(["future-root"]) &&
                state.Publication.Removed.SetEquals(["old-00000000"]), "Nested publication mutations escaped rollback");
        }
    }

    private static async Task Corruption()
    {
        foreach (string mode in new[] { "torn", "checksum", "sequence", "epoch", "missing", "checkpoint" })
        {
            using var folder = new Folder(); var cache = folder.Cache();
            await using (await cache.AcquireAsync(Scope))
            {
                var state = State(4); await cache.SaveAsync(Scope, state);
                state.PendingDeleteObjects.Add("appended-candidate"); await cache.SaveAsync(Scope, state);
            }
            string path = mode == "checkpoint" ? folder.Checkpoint : folder.Journal;
            if (mode == "missing") File.Delete(path);
            else
            {
                byte[] bytes = await File.ReadAllBytesAsync(path);
                if (mode == "torn") bytes = bytes[..^9];
                else if (mode is "checksum" or "checkpoint") bytes[^1] ^= 1;
                else if (mode == "epoch") { bytes[8] ^= 1; SHA256.HashData(bytes.AsSpan(0, 24)).CopyTo(bytes, 24); }
                else
                {
                    int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(56, 4));
                    var node = JsonNode.Parse(bytes.AsSpan(60, length))!; node["sequence"] = 99;
                    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(node, Json);
                    var changed = new byte[56 + 4 + payload.Length + 32]; bytes.AsSpan(0, 56).CopyTo(changed);
                    BinaryPrimitives.WriteInt32LittleEndian(changed.AsSpan(56), payload.Length); payload.CopyTo(changed, 60);
                    SHA256.HashData(payload).CopyTo(changed, 60 + payload.Length); bytes = changed;
                }
                await File.WriteAllBytesAsync(path, bytes); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
            }
            await using var lease = await cache.AcquireAsync(Scope);
            Assert(await cache.LoadAsync(Scope) is null, "Corrupt cache granted an earlier-prefix GC proof: " + mode);
        }
    }

    private static async Task FailedAppend()
    {
        using var folder = new Folder(); var cache = folder.Cache();
        await using (await cache.AcquireAsync(Scope)) { await cache.SaveAsync(Scope, State(1)); }
        await using (await cache.AcquireAsync(Scope))
        {
            var state = (await cache.LoadAsync(Scope))!;
            state.PendingDeleteObjects.Add("not-durable");
            using var locked = new FileStream(folder.Journal, FileMode.Open, FileAccess.Read, FileShare.None);
            try { await cache.SaveAsync(Scope, state); throw new Exception("A denied append succeeded"); }
            catch (IOException) { }
        }
        await using (await cache.AcquireAsync(Scope))
        {
            var state = (await cache.LoadAsync(Scope))!;
            Assert(state.PendingDeleteObjects.SetEquals(["old-00000000"]), "Failed append retained caller mutations as a proof");
        }
    }

    private static async Task CheckpointBatch()
    {
        using var folder = new Folder(); var cache = folder.Cache();
        await using (await cache.AcquireAsync(Scope))
        {
            var state = State(300); await cache.SaveAsync(Scope, state);
            for (int i = 0; i < 320; i++) { state.OwnerConfirmed = !state.OwnerConfirmed; await cache.SaveAsync(Scope, state); }
            var diagnostics = cache.GetDiagnostics();
            Assert(diagnostics.CheckpointWrites is >= 2 and <= 3 && diagnostics.JournalWrites == 320, "Checkpointing was not amortized by live metadata size");
            Assert(new FileInfo(folder.Journal).Length < 128 * 1024, "Checkpoint did not bound subsequent journal replay");
        }
        // Force a real read without corruption, to exercise checkpoint + new epoch replay.
        File.SetLastWriteTimeUtc(folder.Checkpoint, DateTime.UtcNow.AddSeconds(1));
        await using var lease = await cache.AcquireAsync(Scope);
        var loaded = await cache.LoadAsync(Scope);
        Assert(loaded is { OwnerConfirmed: true } && loaded.PendingDeleteObjects.Count == 300, "Checkpoint generation switch lost a committed state");
    }

    private static async Task ScopeAndInvalidDelta()
    {
        using var folder = new Folder(); var cache = folder.Cache();
        await using (await cache.AcquireAsync(Scope)) { await cache.SaveAsync(Scope, State(2)); }
        var other = Scope with { AccountId = "different-account" };
        string otherPath = Path.Combine(folder.Path, CloudRepository.Hash(JsonSerializer.SerializeToUtf8Bytes(other, Json)) + ".cache");
        string original = folder.Checkpoint;
        File.Copy(original, otherPath); File.Copy(original + ".wal", otherPath + ".wal");
        await using (await cache.AcquireAsync(other)) { Assert(await cache.LoadAsync(other) is null, "Copied proof crossed account scope"); }
        await using (await cache.AcquireAsync(Scope))
        {
            var state = (await cache.LoadAsync(Scope))!;
            state.PendingDeleteObjects.Add("../not-an-object");
            try { await cache.SaveAsync(Scope, state); throw new Exception("Invalid delta path persisted"); } catch (IOException) { }
        }
        await using (await cache.AcquireAsync(Scope))
        {
            var state = (await cache.LoadAsync(Scope))!;
            Assert(state.PendingDeleteObjects.Count == 2, "Rejected delta contaminated subsequent loads");
        }
    }

    internal static async Task<int?> TryRunHelperAsync(string[] args)
    {
        if (args is ["--sync-cache-journal-tests"])
        {
            int failed = 0;
            foreach (var (name, run) in All)
                try { await run(); Console.WriteLine("PASS " + name); } catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
            Console.WriteLine($"Sync journal tests: {All.Length - failed} passed, {failed} failed; no cloud APIs.");
            return failed == 0 ? 0 : 1;
        }
        if (args.Length != 3 || args[0] != "--sync-cache-journal-child") return null;
        var cache = new FileCloudSyncCache(args[1]);
        await using var lease = await cache.AcquireAsync(Scope);
        if (args[2] == "durable")
        {
            var state = await cache.LoadAsync(Scope) ?? throw new IOException("Child cache missing");
            state.PendingDeleteObjects.Add("child-committed"); await cache.SaveAsync(Scope, state);
        }
        else
        {
            string path = Directory.GetFiles(args[1], "*.cache").Single() + ".wal";
            using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            var torn = new byte[20]; BinaryPrimitives.WriteInt32LittleEndian(torn, 1000); file.Write(torn); file.Flush(true);
        }
        Console.WriteLine("sync-journal-crash-point"); Console.Out.Flush();
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 2;
    }

    private static async Task ProcessCrash()
    {
        foreach (string mode in new[] { "durable", "torn" })
        {
            using var folder = new Folder(); var cache = folder.Cache();
            await using (await cache.AcquireAsync(Scope)) { await cache.SaveAsync(Scope, State(1)); }
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--sync-cache-journal-child"); start.ArgumentList.Add(folder.Path); start.ArgumentList.Add(mode);
            using var child = Process.Start(start) ?? throw new IOException("Could not start journal child");
            try
            {
                Assert(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) == "sync-journal-crash-point", "Child did not reach journal crash point");
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync();
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
            await using var lease = await cache.AcquireAsync(Scope); var loaded = await cache.LoadAsync(Scope);
            if (mode == "durable") Assert(loaded?.PendingDeleteObjects.SetEquals(["old-00000000", "child-committed"]) == true, "Killed process lost a flushed receipt");
            else Assert(loaded is null, "Killed process left a torn journal which granted an old proof");
        }
    }
}
