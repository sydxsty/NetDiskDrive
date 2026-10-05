using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OverlayDisk.Cloud.Contracts;
using OverlayDisk.Cloud.Sync;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static async Task Reject(Func<Task> action, string reason)
    {
        try { await action(); }
        catch (IOException) { return; }
        throw new Exception(reason);
    }
    public static async Task<int> Main(string[] args)
    {
        if (await CloudSyncCacheJournalTests.TryRunHelperAsync(args) is int helperCode) return helperCode;
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("publish order and collection of obsolete closure", PublishOrder),
            ("failed upload keeps old closure and local pin; receipts resume", FailureAndResume),
            ("root upload failure cannot publish or release pin", RootUploadFailure),
            ("commit upload failure cannot release pin or collect", CommitUploadFailure),
            ("successful fresh uploads never read back owner objects or commit", UploadAcknowledgmentOnly),
            ("persistent reader pin defers garbage collection", ReaderPin),
            ("invalid cloud roots reject before mutation", PathBoundary),
            ("new disks use unversioned root and ignore version folders", UnversionedRoot),
            ("known immutable downloads bypass probes and still verify SHA", KnownImmutableRead),
            ("mixed cloud formats hide only unsupported legacy roots and preserve real errors", MixedFormatDiscovery),
            ("divergent latest generation is rejected", Divergence),
            ("new local generation during upload remains pending", NewGeneration),
            ("unchanged manual sync reuses published root", NoChanges),
            ("new writes during no-op cloud check remain pending", NoChangesWithNewWrite),
            ("account or owner mismatch cannot upload", WrongOwner),
            ("bad object hash cannot reach cloud commit", LocalHash),
            ("cancellation before publish retains old version", Cancellation),
            ("persistent warm restart without changes issues zero cloud APIs", PersistentNoChanges),
            ("cache loss and corruption reconcile metadata without object scans", CacheLossAndCorruption),
            ("native commit crash resumes durable publication intent", NativeCommitCrash),
            ("cache write failure prevents unjournaled garbage collection", CacheWriteFailure),
            ("delete interruption and cache-save crash resume idempotently", DeletionCrash),
            ("reader pins are freshly checked for each bounded delete batch", BoundedBatchReaders),
            ("new generation reuses folder cache and preserves shared objects", CachedNewGeneration),
            ("persistent cache scope rejects account provider and writer confusion", CacheScope),
            ("per-object logs distinguish started confirmed and failed uploads", ObjectLogs),
            ("lost commit acknowledgment recovers the same immutable publication", PublicationAcknowledgmentLost),
            ("prepared receipt is checked before opening local payload", PreparedReceiptBeforeRead),
            ("incremental sync never enumerates published closure", NoClosureEnumeration),
            ("sync never deletes; explicit cleanup uses retained delta", ManualCollectionOnly)
        };
        tests = tests.Concat(CloudSyncCacheJournalTests.All).Concat(OriginalRestoreTests.All).Concat(CacheProtectionTests.All).Concat(PreparationProgressTests.All).Concat(ObjectGeometryTests.All).ToArray();
        if (args.Length != 0)
        {
            if (args.Length != 2 || args[0] != "--filter" || args[1] is not ("upload-ack" or "original-restore" or "cache-policy" or "preparing" or "object-size" or "object-transport"))
            { Console.Error.WriteLine("Supported selection: --filter upload-ack|original-restore|cache-policy|preparing|object-size|object-transport"); return 2; }
            var selected = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(PublishOrder), nameof(FailureAndResume), nameof(RootUploadFailure), nameof(CommitUploadFailure),
                nameof(UploadAcknowledgmentOnly), nameof(NativeCommitCrash), nameof(PublicationAcknowledgmentLost), nameof(PersistentNoChanges)
            };
            if (args[1] == "original-restore") tests = OriginalRestoreTests.All;
            else if (args[1] == "preparing") tests = PreparationProgressTests.All;
            else if (args[1] == "object-transport") tests = ObjectGeometryTests.All.Concat(PreparationProgressTests.All).Concat(tests.Where(test => new[] { nameof(PreparedReceiptBeforeRead), nameof(KnownImmutableRead), nameof(MixedFormatDiscovery), nameof(PublishOrder), nameof(FailureAndResume), nameof(RootUploadFailure), nameof(CommitUploadFailure), nameof(UploadAcknowledgmentOnly), nameof(NativeCommitCrash), nameof(PublicationAcknowledgmentLost), nameof(PersistentNoChanges), nameof(NoChanges), nameof(LocalHash), nameof(ManualCollectionOnly) }.Contains(test.Run.Method.Name))).ToArray();
            else if (args[1] == "object-size") tests = ObjectGeometryTests.All.Concat(PreparationProgressTests.All).ToArray();
            else if (args[1] == "cache-policy") tests = CacheProtectionTests.All.Concat([OriginalRestoreTests.All[1]])
                .Concat(tests.Where(test => new[] { nameof(ManualCollectionOnly), nameof(BoundedBatchReaders), nameof(DeletionCrash), nameof(CachedNewGeneration) }.Contains(test.Run.Method.Name))).ToArray();
            else
            {
                tests = tests.Where(test => selected.Contains(test.Run.Method.Name)).ToArray();
                Assert(tests.Length == selected.Count, "Upload-ack filter omitted a selected scenario");
            }
            Console.WriteLine("Filter " + args[1] + ": " + tests.Length + " directly affected mock scenarios.");
        }
        var failed = 0;
        foreach (var (name, run) in tests)
        {
            try { await run(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.GetType().Name + " " + error.Message); }
        }
        Console.WriteLine($"Sync tests: {tests.Length - failed} passed, {failed} failed; mock store and volume, no external account.");
        return failed == 0 ? 0 : 1;
    }

    private static async Task KnownImmutableRead()
    {
        var f = new Fixture(); var data = new byte[CloudRepository.ObjectLength]; data[4096] = 37;
        string id = Guid.NewGuid().ToString(), path = f.Path(id), hash = CloudRepository.Hash(data);
        f.Store.SeedCanonical(path, data); f.Events.Clear();
        var read = await f.Repository.ReadObjectAsync(f.Binding.RemoteRoot, id, hash, CloudObjectGeometry.DefaultSize, default);
        Assert(read.SequenceEqual(data) && f.Events.Contains("bounded-read:" + path), "Repository ignored known immutable read capability");
        Assert(!f.Events.Any(e => e.StartsWith("head:") || e.StartsWith("list:")), "Known immutable download probed metadata");
        f.Store.AlterRead = (_, bytes) => { var corrupt = bytes.ToArray(); corrupt[11] ^= 1; return corrupt; };
        await Reject(() => f.Repository.ReadObjectAsync(f.Binding.RemoteRoot, id, hash, CloudObjectGeometry.DefaultSize, default), "Known read bypassed caller SHA verification");
    }

    private static async Task MixedFormatDiscovery()
    {
        var f = new Fixture(); f.SeedOld(); await f.Run();
        string oldId = Guid.NewGuid().ToString(), oldRoot = CloudRepository.RootPath(oldId);
        var oldCommit = f.Commit(Guid.NewGuid().ToString(), new string('A', 64), 1) with { VolumeId = oldId };
        string oldPath = CloudRepository.CommitPath(oldRoot, oldCommit);
        var legacy = JsonSerializer.SerializeToNode(oldCommit, Json)!; legacy.AsObject().Remove("transportCodec"); legacy.AsObject().Remove("objectSizeBytes");
        f.Store.Seed(oldPath, JsonSerializer.SerializeToUtf8Bytes(legacy, Json)); f.Events.Clear();
        var disks = await f.Repository.ListDisksAsync(default);
        Assert(disks.Count == 1 && disks[0].Id == f.Volume.Id, "One legacy UUID hid a current compressed volume");
        Assert(!f.Events.Any(e => e.StartsWith("put:") || e.StartsWith("delete:") || e.Contains("/objects/")), "Browsing migrated/mutated/read legacy payloads");
        await Reject(() => f.Repository.LatestAsync(oldRoot, default), "Explicit legacy import was accepted");
        f.Store.Seed(oldPath, JsonSerializer.SerializeToUtf8Bytes(oldCommit with { RootSha256 = "invalid" }, Json));
        await Reject(() => f.Repository.ListDisksAsync(default), "Corrupt current-format metadata was silently hidden as unsupported");
        var malformed = JsonSerializer.SerializeToNode(oldCommit, Json)!; malformed["transportCodec"] = 123;
        f.Store.Seed(oldPath, JsonSerializer.SerializeToUtf8Bytes(malformed, Json));
        await Reject(() => f.Repository.ListDisksAsync(default), "Malformed current codec type was silently hidden as unsupported");
        f.Store.Files.TryRemove(oldPath, out _); f.Store.AlterRead = (_, _) => throw new IOException("Injected read failure");
        await Reject(() => f.Repository.ListDisksAsync(default), "An actual cloud read error was hidden");
    }

    private static async Task UnversionedRoot()
    {
        var f = new Fixture();
        Assert(f.Binding.RemoteRoot == "/OverlayDisk/" + f.Volume.Id, "New disk still contains a version directory");
        await f.Repository.EnsureWriterAsync(f.Binding, f.Volume.Id, default);
        // This model starts at local published generation 1; seed its matching remote predecessor.
        f.SeedOld();
        f.Repository = new(f.Store, new MemoryCloudSyncCache());
        await f.Run();
        Assert(!f.Events.Any(e => e.Contains("/OverlayDisk/v4", StringComparison.OrdinalIgnoreCase)), "Fresh synchronization touched a version folder");
        var commit = f.Commit(f.Volume.RootId, f.Volume.RootHash, f.Volume.PublishedGeneration);
        foreach (var name in new[] { "v4", "V4" })
        {
            string oldRoot = "/OverlayDisk/" + name + "/" + f.Volume.Id;
            f.Store.Seed(CloudRepository.CommitPath(oldRoot, commit), JsonSerializer.SerializeToUtf8Bytes(commit, Json));
            await Reject(() => f.Repository.LatestAsync(oldRoot, default), "Versioned directory was accepted");
        }
        f.Events.Clear();
        var listed = await f.Repository.ListDisksAsync(default);
        Assert(listed.Count == 1 && listed[0].RemoteRoot == f.Binding.RemoteRoot, "Versioned folder was traversed or duplicated the disk");
        Assert(!f.Events.Any(e => e.Contains("/OverlayDisk/v4", StringComparison.OrdinalIgnoreCase)), "Cloud disk discovery traversed a version folder");
        await f.Repository.PinReaderAsync(f.Binding.RemoteRoot, listed[0].Commit, Guid.NewGuid().ToString(), default);
    }

    private static async Task ManualCollectionOnly()
    {
        var f=new Fixture();f.SeedOld();var coordinator=new SyncCoordinator(f.Repository);
        var first=await coordinator.RunAsync(f.Volume,f.Binding,"manual",Fixture.Capacity,false,1,null,default);
        Assert(first.CleanupPending&&f.Store.Files.ContainsKey(f.OldPath),"Normal sync deleted old objects");
        Assert(!f.Events.Any(e=>e.StartsWith("delete:")),"Normal sync called delete");
        Assert(!await f.Repository.HasPendingWorkAsync(f.Binding,f.Volume.Id),"Manual cleanup was admitted to automatic maintenance");
        f.Events.Clear();await coordinator.RunAsync(f.Volume,f.Binding,"manual",Fixture.Capacity,false,1,null,default);
        Assert(!f.Events.Any(CloudCall),"Unchanged sync checked or collected pending garbage");
        var estimate=await f.Repository.CleanupEstimateAsync(f.Binding,f.Volume.Id);
        Assert(estimate.Objects>0&&estimate.Bytes>0,"Manual cleanup estimate missing");
        var result=await coordinator.CleanupAsync(f.Volume,f.Binding,null,default);
        Assert(!result.Pending&&!f.Store.Files.ContainsKey(f.OldPath),"Explicit collection did not consume known obsolete objects");
    }

    private static async Task PreparedReceiptBeforeRead()
    {
        var f=new Fixture();f.SeedOld();_=f.Volume.RootHash;
        string id=f.Volume.DataIds[0];f.Store.SeedCanonical(f.Path(id),f.Volume.Objects[id]);
        await f.Run();Assert(!f.Volume.Reads.Contains(id),"Confirmed receipt still opened the source payload");
        Assert(f.Volume.Uploaded.Contains(id),"Recovered remote receipt was not durable locally");
        var progress = f.Progress.Items.Last();
        Assert(progress.ReusedBytes == CloudRepository.ObjectLength && progress.LogicalUploadedBytes == 2L * CloudRepository.ObjectLength && progress.UploadedBytes > 0 && progress.UploadedBytes < progress.LogicalUploadedBytes,
            "Reused object bytes were counted as uploaded traffic");
    }
    private static async Task NoClosureEnumeration()
    {
        var f=new Fixture();f.SeedOld();await f.Run();f.Volume.NextGeneration();f.Events.Clear();await f.Run();
        Assert(!f.Events.Contains("control:cloud.published_objects"),"Normal incremental sync enumerated full published objects");
        Assert(f.Events.Contains("control:cloud.delta"),"Incremental deletion proof was not used");
    }

    private static async Task PublishOrder()
    {
        var f = new Fixture(); f.SeedOld();
        var result = await f.Run();
        var events = f.Events.ToArray();
        int Index(string value) => Array.IndexOf(events, value);
        var rootPath = f.Path(f.Volume.RootId); var commitPath = CloudRepository.CommitPath(f.Binding.RemoteRoot, result.Commit);
        Assert(f.Volume.DataIds.All(id => Index("put:" + f.Path(id)) >= 0
            && Index("put:" + f.Path(id)) < Index("ack:" + f.Path(id))
            && Index("ack:" + f.Path(id)) < Index("put:" + rootPath)), "Root was published before data upload acknowledgments");
        Assert(Index("put:" + rootPath) < Index("ack:" + rootPath) && Index("ack:" + rootPath) < Index("put:" + commitPath), "Commit was visible before root upload acknowledgment");
        Assert(Index("put:" + commitPath) < Index("ack:" + commitPath) && Index("ack:" + commitPath) < Index("control:cloud.commit"), "Local pin released before commit upload acknowledgment");
        foreach (string path in f.Volume.Objects.Keys.Select(f.Path).Append(commitPath))
            Assert(!events.Take(Index("control:cloud.commit")).Any(e => e == "read:" + path || e == "known-read:" + path), "Successful publication downloaded a newly uploaded payload");
        var deletion = Array.FindIndex(events, e => e.StartsWith("delete:"));
        Assert(deletion > Index("control:cloud.commit"), "Garbage collection preceded local durable acknowledgment");
        Assert(!f.Volume.Pin && !result.CleanupPending, "Completed publish kept unexpected pin/cleanup state");
        Assert(f.Volume.Objects.Keys.All(id => f.Store.Files.ContainsKey(f.Path(id))), "Current closure was deleted");
        Assert(!f.Store.Files.ContainsKey(f.OldPath), "Obsolete object not collected");
        Assert(f.Progress.Items.Last().LogicalUploadedBytes == 3L * CloudRepository.ObjectLength && f.Progress.Items.Last().UploadedBytes == f.Volume.Objects.Keys.Sum(id => (long)f.Store.Files[f.Path(id)].Length) && f.Progress.Items.Last().ReusedBytes == 0,
            "Actual uploads and reuse were not separated at completion");
        Assert((await f.Repository.LatestAsync(f.Binding.RemoteRoot, default))?.Commit.RootObjectId == f.Volume.RootId, "Latest commit wrong");
    }

    private static async Task FailureAndResume()
    {
        var f = new Fixture(); f.SeedOld();
        var failing = f.Path(f.Volume.DataIds[1]);
        f.Store.BeforePut = path => { if (path == failing) throw new CloudProviderException("Injected", "Injected upload interruption."); };
        await Reject(async () => _ = await f.Run(), "Upload interruption was ignored");
        Assert(f.Volume.Pin && !f.Volume.Committed, "Failed upload released the snapshot pin");
        Assert(f.Store.Files.ContainsKey(f.OldPath) && !f.Events.Any(e => e.StartsWith("delete:")), "Failed upload deleted current remote data");
        var uploaded = f.Volume.Uploaded.ToArray(); Assert(uploaded.Length > 0, "Successful prior object lacked durable receipt");
        f.Events.Clear(); f.Store.BeforePut = null;
        _ = await f.Run();
        Assert(uploaded.All(id => !f.Events.Contains("put:" + f.Path(id))), "Resume reuploaded an object with a durable receipt");
        Assert(f.Volume.Committed && !f.Volume.Pin, "Resume did not finish old pinned job");
    }

    private static async Task RootUploadFailure()
    {
        var f = new Fixture(); f.SeedOld();
        string rootPath = f.Path(f.Volume.RootId);
        f.Store.BeforePut = path => { if (path == rootPath) throw new CloudProviderException("Injected", "Root upload failed before acknowledgment."); };
        await Reject(async () => _ = await f.Run(), "Failed root upload was published");
        Assert(f.Volume.Pin && !f.Volume.Committed && !f.Volume.Uploaded.Contains(f.Volume.RootId), "Root upload failure released pin or fabricated its receipt");
        Assert(f.Store.Files.ContainsKey(f.OldPath) && !f.Events.Any(e => e.StartsWith("delete:")), "Root upload failure collected old objects");
        Assert(!f.Events.Contains("ack:" + rootPath) && !f.Events.Contains("control:cloud.commit"), "Root upload failure advanced local commit");
        Assert(!f.Events.Any(e => e.StartsWith("put:") && e.Contains("/commits/")), "Descriptor was uploaded before root acknowledgment");
    }

    private static async Task CommitUploadFailure()
    {
        var f = new Fixture(); f.SeedOld();
        f.Store.BeforePut = path => { if (path.Contains("/commits/") && path.Contains(f.Volume.RootId)) throw new CloudProviderException("Injected", "Commit upload failed before acknowledgment."); };
        await Reject(async () => _ = await f.Run(), "Failed commit upload was accepted");
        Assert(f.Volume.Pin && !f.Volume.Committed && !f.Events.Contains("control:cloud.commit"), "Commit upload failure released pin");
        Assert(f.Volume.Uploaded.Contains(f.Volume.RootId), "Successful root upload acknowledgment was lost");
        Assert(f.Store.Files.ContainsKey(f.OldPath) && !f.Events.Any(e => e.StartsWith("delete:")), "Failed commit upload collected old data");
        Assert(!f.Events.Any(e => e.StartsWith("ack:") && e.Contains("/commits/") && e.Contains(f.Volume.RootId)), "Failed commit upload produced an acknowledgment");
    }

    private static async Task UploadAcknowledgmentOnly()
    {
        var f = new Fixture(); f.Volume.StartUnpublished();
        var newlyUploaded = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        f.Store.AfterPut = path => newlyUploaded.TryAdd(path, 0);
        f.Store.AlterRead = (path, bytes) => newlyUploaded.ContainsKey(path)
            ? throw new IOException("Successful upload was unnecessarily downloaded: " + path) : bytes;
        // This operation intentionally excludes explicit garbage collection. Its fresh
        // latest-version/reader-pin checks are unrelated to acknowledging an upload.
        var result = await new SyncCoordinator(f.Repository).RunAsync(f.Volume, f.Binding, "Test disk", Fixture.Capacity, false, 1, f.Progress, default);
        string owner = f.Binding.RemoteRoot + "/owner.json", commit = CloudRepository.CommitPath(f.Binding.RemoteRoot, result.Commit);
        var expected = f.Volume.Objects.Keys.Select(f.Path).Append(owner).Append(commit).ToArray();
        Assert(expected.All(path => newlyUploaded.ContainsKey(path) && f.Events.Contains("ack:" + path)), "Fresh publication omitted an upload acknowledgment");
        var events = f.Events.ToArray();
        foreach (string path in expected)
        {
            int put = Array.IndexOf(events, "put:" + path);
            Assert(put >= 0 && !events.Skip(put + 1).Any(e => e == "read:" + path || e == "known-read:" + path), "An acknowledged upload was read back");
        }
        Assert(f.Volume.Committed && !f.Volume.Pin && !result.CleanupPending, "Acknowledgments did not finish the fresh version");
        Assert(!f.Events.Any(e => e.StartsWith("delete:")), "Fresh publication deleted cloud data");
    }

    private static async Task ReaderPin()
    {
        var f = new Fixture(); var old = f.SeedOld();
        var pin = await f.Repository.PinReaderAsync(f.Binding.RemoteRoot, old, Guid.NewGuid().ToString(), default);
        f.Events.Clear();
        var result = await f.Run();
        Assert(result.CleanupPending && !f.Events.Any(e => e.StartsWith("delete:")), "A restore reader did not stop GC");
        Assert(f.Store.Files.ContainsKey(f.OldPath) && f.Store.Files.ContainsKey(pin), "Reader pin or its old closure was deleted");
    }

    private static async Task PathBoundary()
    {
        var f = new Fixture();
        var mismatched = f.Binding with { RemoteRoot = CloudRepository.RootPath(Guid.NewGuid().ToString()) };
        await Reject(() => f.Repository.EnsureWriterAsync(mismatched, f.Volume.Id, default), "A different volume root was bound");
        Assert(!f.Events.Any(IsMutation), "Invalid volume/root binding mutated cloud state");
        f.Events.Clear();
        var commit = f.Commit(f.Volume.RootId, f.Volume.RootHash, 2);
        await Reject(async () => _ = await f.Repository.PinReaderAsync("/somewhere-else", commit, Guid.NewGuid().ToString(), default), "Outside root accepted");
        Assert(!f.Events.Any(IsMutation), "Invalid reader root mutated cloud state before rejection");
        static bool IsMutation(string e) => e.StartsWith("put:") || e.StartsWith("mkdir:") || e.StartsWith("delete:");
    }

    private static async Task Divergence()
    {
        var f = new Fixture(); f.SeedOld();
        f.SeedCommit(f.Commit(f.Volume.RootId, f.Volume.RootHash, 7));
        var other = Guid.NewGuid().ToString(); f.SeedCommit(f.Commit(other, f.Volume.RootHash, 7));
        await Reject(async () => _ = await f.Repository.LatestAsync(f.Binding.RemoteRoot, default), "Two roots at one latest generation were silently selected");
        f.Events.Clear();
        await Reject(async () => _ = await f.Run(), "Conflicting latest versions allowed publication");
        Assert(!f.Volume.Committed && f.Store.Files.ContainsKey(f.OldPath) && !f.Events.Any(e => e.StartsWith("delete:")), "Conflict advanced publication or discarded the remote closure");
    }

    private static async Task NewGeneration()
    {
        var f = new Fixture(); f.SeedOld();
        f.Volume.AdvanceGenerationOnRead = true;
        var result = await f.Run();
        Assert(result.Commit.Generation == 2 && f.Volume.PublishedGeneration == 2 && f.Volume.CurrentGeneration == 3, "Publish accidentally included newer mutable data");
        Assert(f.Progress.Items.Last().Phase != "synced", "New local generation is misleadingly shown as fully synced");
        Assert(result.HasPendingChanges, "The scheduler was not told about new local changes");
    }

    private static async Task NoChanges()
    {
        var f = new Fixture(); f.SeedOld();
        var first = await f.Run(); f.Events.Clear();
        var second = await f.Run();
        Assert(first.Commit == second.Commit && !second.HasPendingChanges, "Unchanged volume made a different commit");
        Assert(!f.Events.Contains("control:cloud.prepare") && !f.Events.Contains("control:cloud.commit"), "No-op sync created another pinned export job");
        Assert(!f.Events.Any(e => e.StartsWith("put:")), "No-op sync uploaded objects again");
        Assert(f.Volume.Objects.Keys.All(id => f.Store.Files.ContainsKey(f.Path(id))), "No-op cleanup lost the published closure");
    }

    private static async Task NoChangesWithNewWrite()
    {
        var f = new Fixture(); f.SeedOld(); _ = await f.Run();
        var changed = false;
        int states = 0;
        f.Volume.OnControl = method => { if (method == "cloud.status" && ++states == 2) { changed = true; f.Volume.AdvanceLocal(); } };
        var result = await f.Run();
        Assert(changed && result.HasPendingChanges && f.Progress.Items.Last().Phase == "pending", "No-op remote check hid a newer local write");
    }

    private static async Task WrongOwner()
    {
        var f = new Fixture(); f.SeedOld(writer: Guid.NewGuid().ToString());
        await Reject(async () => _ = await f.Run(), "Another writer's owner record was accepted");
        Assert(!f.Events.Any(e => e.StartsWith("put:") || e.StartsWith("delete:")), "Writer mismatch mutated cloud objects");
        var binding = f.Binding with { AccountId = "another-account" }; f.Events.Clear();
        await Reject(() => f.Repository.EnsureWriterAsync(binding, f.Volume.Id, default), "Account mismatch accepted");
        Assert(!f.Events.Any(e => e.StartsWith("mkdir:")), "Account mismatch reached directory writes");
    }

    private static async Task LocalHash()
    {
        var f = new Fixture(); f.SeedOld(); f.Volume.CorruptLocalRead = true;
        await Reject(async () => _ = await f.Run(), "Bad local export hash was accepted");
        Assert(f.Volume.Pin && !f.Volume.Committed && !f.Events.Any(e => e.StartsWith("delete:")), "Bad local export advanced commit state");
    }

    private static async Task Cancellation()
    {
        var f = new Fixture(); f.SeedOld(); using var stop = new CancellationTokenSource();
        f.Store.AfterPut = path => { if (path.EndsWith(".obj")) stop.Cancel(); };
        try { _ = await f.Run(stop.Token); throw new Exception("Cancelled upload completed"); }
        catch (OperationCanceledException) { }
        Assert(f.Volume.Pin && !f.Volume.Committed && f.Store.Files.ContainsKey(f.OldPath), "Cancellation discarded the recoverable version");
        Assert(!f.Events.Any(e => e.StartsWith("delete:")), "Cancellation ran destructive cleanup");
    }

    private static bool CloudCall(string entry) => !entry.StartsWith("control:", StringComparison.Ordinal);
    private static void NoObjectScan(Fixture f) => Assert(!f.Events.Any(e=>e.StartsWith("list:"+f.Binding.RemoteRoot+"/objects",StringComparison.Ordinal)),"Routine sync enumerated remote object directories");
    private sealed class TempCache : IDisposable
    {
        public string DirectoryPath { get; }=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"overlaydisk-sync-cache-"+Guid.NewGuid().ToString("N"));
        public FileCloudSyncCache Open()=>new(DirectoryPath);
        public void Dispose(){if(Directory.Exists(DirectoryPath))Directory.Delete(DirectoryPath,true);}
    }
    private sealed class FaultCache(ICloudSyncCache inner) : ICloudSyncCache
    {
        public Action<CloudSyncCacheState>? BeforeSave;
        public ValueTask<IAsyncDisposable> AcquireAsync(CloudCacheScope scope,CancellationToken ct=default)=>inner.AcquireAsync(scope,ct);
        public Task<CloudSyncCacheState?> LoadAsync(CloudCacheScope scope,CancellationToken ct=default)=>inner.LoadAsync(scope,ct);
        public Task SaveAsync(CloudCacheScope scope,CloudSyncCacheState state,CancellationToken ct=default){BeforeSave?.Invoke(state);return inner.SaveAsync(scope,state,ct);}
    }
    private sealed class LogCapture : IProgress<SyncLogEntry>
    {public ConcurrentQueue<SyncLogEntry> Items{get;}=new();public void Report(SyncLogEntry entry)=>Items.Enqueue(entry);}
    private static async Task PersistentNoChanges()
    {
        using var temp=new TempCache();var f=new Fixture(temp.Open());f.SeedOld();var first=await f.Run();f.Events.Clear();
        var warm=await f.Run();Assert(!f.Events.Any(CloudCall),"Warm no-change sync called the cloud provider");
        f.Repository=new(f.Store,temp.Open(),"test-account");f.Events.Clear();var restart=await f.Run();
        Assert(!f.Events.Any(CloudCall),"Trusted restart made a cloud API call");Assert(first.Commit==warm.Commit&&first.Commit==restart.Commit&&!restart.CleanupPending,"No-change cache lost its confirmed commit");
        Assert(!await f.Repository.HasPendingWorkAsync(f.Binding,f.Volume.Id),"Clean cache incorrectly requests maintenance");
    }
    private static async Task CacheLossAndCorruption()
    {
        foreach(bool corrupt in new[]{false,true})
        {
            using var temp=new TempCache();var f=new Fixture(temp.Open());f.SeedOld();await f.Run();string unknown=f.Path(Guid.NewGuid().ToString());f.Store.Seed(unknown,[42]);
            string path=Directory.GetFiles(temp.DirectoryPath,"*.cache").Single();if(corrupt){byte[] bytes=File.ReadAllBytes(path);bytes[^4]^=1;File.WriteAllBytes(path,bytes);}else File.Delete(path);
            f.Repository=new(f.Store,temp.Open(),"test-account");f.Events.Clear();var result=await f.Run();NoObjectScan(f);
            Assert(f.Store.Files.ContainsKey(unknown)&&!f.Events.Contains("delete:"+unknown),"Lost cache inferred garbage from unknown remote objects");
            Assert(f.Events.Count(e=>e.StartsWith("read:")&&e.Contains("/commits/"))==1,"Reconcile read more than one commit descriptor");
            Assert(!result.HasPendingChanges&&!result.CleanupPending,"Metadata reconciliation changed the committed state");
            f.Repository=new(f.Store,temp.Open(),"test-account");f.Events.Clear();await f.Run();Assert(!f.Events.Any(CloudCall),"Reconciled cache was not reusable after restart");
        }
    }
    private static async Task NativeCommitCrash()
    {
        using var temp=new TempCache();var f=new Fixture(temp.Open());f.SeedOld();f.Volume.ThrowAfterNativeCommitOnce=true;
        await Reject(async()=>_=await f.Run(),"Injected post-commit crash was ignored");Assert(f.Volume.Committed&&f.Store.Files.ContainsKey(f.OldPath),"Crash incorrectly collected old data");
        f.Repository=new(f.Store,temp.Open(),"test-account");Assert(await f.Repository.HasPendingWorkAsync(f.Binding,f.Volume.Id),"Unfinished publication intent was not discoverable");f.Events.Clear();
        var coordinator = new SyncCoordinator(f.Repository);
        var resumed = await coordinator.RunAsync(f.Volume, f.Binding, "Test disk", Fixture.Capacity, false, 1, f.Progress, default);
        Assert(resumed.CleanupPending && f.Store.Files.ContainsKey(f.OldPath), "Crash recovery discarded the manual cleanup plan");
        Assert(!f.Events.Any(CloudCall), "Durably acknowledged publication was uploaded or read back after native commit crash");
        var cleanup = await coordinator.CleanupAsync(f.Volume, f.Binding, null, default);
        Assert(!cleanup.Pending && !f.Store.Files.ContainsKey(f.OldPath), "Explicit cleanup did not finish recovered publication"); NoObjectScan(f);
    }
    private static async Task CacheWriteFailure()
    {
        using var temp=new TempCache();var failing=new FaultCache(temp.Open());var f=new Fixture(failing);f.SeedOld();
        failing.BeforeSave=state=>{if(state.Latest?.Generation==2&&state.Publication is null)throw new IOException("Injected cache write failure");};
        await Reject(async()=>_=await f.Run(),"Failed cache commit was ignored");Assert(!f.Events.Any(e=>e.StartsWith("delete:")),"Remote cleanup ran without a durable plan");
        f.Repository=new(f.Store,temp.Open(),"test-account");f.Events.Clear();await f.Run();Assert(!f.Store.Files.ContainsKey(f.OldPath),"Durable earlier intent did not recover cleanup after cache write failure");NoObjectScan(f);
    }
    private static async Task DeletionCrash()
    {
        foreach(bool failedSave in new[]{false,true})
        {
            using var temp=new TempCache();var faults=new FaultCache(temp.Open());var f=new Fixture(faults);f.SeedOld();
            bool injected=false;
            if(failedSave)faults.BeforeSave=state=>{if(!injected&&state.Latest?.Generation==2&&state.Publication is null&&state.PendingDeleteObjects.Count==0&&state.PendingDeleteCommits.Count==0){injected=true;throw new IOException("Crash before persisting delete receipts");}};
            else f.Store.AfterDelete=path=>{if(!injected){injected=true;throw new IOException("Crash after remote delete");}};
            var first=await f.Run();Assert(first.CleanupPending&&injected,"Deletion interruption was not retained");
            f.Store.AfterDelete=null;f.Repository=new(f.Store,temp.Open(),"test-account");Assert((await f.Repository.CleanupEstimateAsync(f.Binding,f.Volume.Id)).Objects+(await f.Repository.CleanupEstimateAsync(f.Binding,f.Volume.Id)).Commits>0,"Delete plan vanished across restart");f.Events.Clear();var resumed=await f.Run();
            Assert(!resumed.CleanupPending&&!f.Store.Files.ContainsKey(f.OldPath),"Pending deletes did not resume idempotently");Assert(f.Volume.Objects.Keys.All(id=>f.Store.Files.ContainsKey(f.Path(id))),"Delete recovery removed live objects");NoObjectScan(f);
        }
    }
    private sealed class BatchStore(ICloudObjectStore inner) : ICloudObjectStore,ICloudBatchDeleteStore
    {
        public List<int> BatchSizes{get;}=[];public Action? AfterBatch;
        public string ProviderId=>inner.ProviderId;
        public Task<CloudAccountInfo> ValidateAsync(CancellationToken ct=default)=>inner.ValidateAsync(ct);
        public Task<CloudObjectInfo?> HeadAsync(string path,CancellationToken ct=default)=>inner.HeadAsync(path,ct);
        public IAsyncEnumerable<CloudObjectInfo> ListAsync(string path,CancellationToken ct=default)=>inner.ListAsync(path,ct);
        public Task CreateDirectoryAsync(string path,CancellationToken ct=default)=>inner.CreateDirectoryAsync(path,ct);
        public Task<CloudObjectInfo> PutImmutableAsync(string path,Stream content,long length,string sha256,CancellationToken ct=default)=>inner.PutImmutableAsync(path,content,length,sha256,ct);
        public Task<Stream> OpenReadAsync(string path,CloudByteRange? range=null,CancellationToken ct=default)=>inner.OpenReadAsync(path,range,ct);
        public Task DeleteAsync(string path,CancellationToken ct=default)=>inner.DeleteAsync(path,ct);
        public async Task DeleteManyAsync(IReadOnlyList<string> paths,CancellationToken ct=default){BatchSizes.Add(paths.Count);foreach(string path in paths)await inner.DeleteAsync(path,ct);AfterBatch?.Invoke();}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private static async Task BoundedBatchReaders()
    {
        using var temp=new TempCache();var f=new Fixture(temp.Open());f.SeedOld();var bytes=new byte[CloudRepository.ObjectLength];bytes[0]=12;string hash=CloudRepository.Hash(bytes);
        var prior=f.Volume.PublishedObjects.ToList();for(int i=0;i<130;i++){string id=Guid.NewGuid().ToString();f.Store.Seed(f.Path(id),bytes);prior.Add(new(id,"data",bytes.Length,hash,true));}f.Volume.PublishedObjects=prior.ToArray();
        string pin=f.Binding.RemoteRoot+"/readers/"+Guid.NewGuid()+".json";var batching=new BatchStore(f.Store);batching.AfterBatch=()=>f.Store.Seed(pin,[1]);f.Repository=new(batching,temp.Open(),"test-account");
        var first=await f.Run();Assert(first.CleanupPending&&batching.BatchSizes.SequenceEqual(new[]{64}),"Fresh reader check did not stop the next deletion batch");
        Assert(f.Store.Files.ContainsKey(pin),"Cleanup removed a reader pin");f.Store.Files.TryRemove(pin,out _);batching.AfterBatch=null;f.Repository=new(batching,temp.Open(),"test-account");f.Events.Clear();var second=await f.Run();
        Assert(!second.CleanupPending&&batching.BatchSizes.All(n=>n<=64),"Bounded cleanup failed after the reader released");
        Assert(f.Events.Count(e=>e=="list:"+f.Binding.RemoteRoot+"/readers")==2,"Reader presence was cached or checked per object instead of per batch");NoObjectScan(f);
    }
    private static async Task CachedNewGeneration()
    {
        using var temp=new TempCache();var f=new Fixture(temp.Open());f.SeedOld();var first=await f.Run();string retained=f.Volume.DataIds[0];string retired=f.Volume.DataIds[1];f.Volume.NextGeneration();
        f.Repository=new(f.Store,temp.Open(),"test-account");f.Events.Clear();var second=await f.Run();
        Assert(second.Commit.Generation==first.Commit.Generation+1&&!second.CleanupPending,"New generation was not published");
        Assert(f.Store.Files.ContainsKey(f.Path(retained))&&!f.Store.Files.ContainsKey(f.Path(retired)),"Known difference deleted shared data or retained obsolete data");
        Assert(!f.Events.Contains("mkdir:"+f.Path(retained)[..f.Path(retained).LastIndexOf('/')])&&!f.Events.Contains("put:"+f.Path(retained)),"Uploaded object recreated its folder or uploaded again");
        Assert(!f.Events.Any(e=>e.StartsWith("list:")&&e.Contains("/commits"))&&!f.Events.Any(e=>e.StartsWith("head:")),"Warm changed sync re-enumerated latest or used HEAD");NoObjectScan(f);
    }
    private static async Task CacheScope()
    {
        using var temp=new TempCache();var f=new Fixture(temp.Open());f.SeedOld();await f.Run();
        foreach(var binding in new[]{f.Binding with{AccountId="other-account"},f.Binding with{ProviderId="other-provider"},f.Binding with{DeviceId=Guid.NewGuid().ToString()}})
        {
            f.Repository=new(f.Store,temp.Open(),"test-account");f.Events.Clear();await Reject(()=>f.Repository.EnsureWriterAsync(binding,f.Volume.Id,default),"Different identity inherited a trusted cache");
            Assert(!f.Events.Any(e=>e.StartsWith("put:")||e.StartsWith("delete:")||e.StartsWith("mkdir:")),"Scope mismatch mutated cloud data");
        }
    }
    private static async Task ObjectLogs()
    {
        var f=new Fixture();f.SeedOld();var log=new LogCapture();await new SyncCoordinator(f.Repository).RunAsync(f.Volume,f.Binding,"logs",Fixture.Capacity,false,1,f.Progress,default,log);
        foreach(string id in f.Volume.Objects.Keys){Assert(log.Items.Any(e=>e.Action=="upload.started"&&e.ObjectId==id&&e.Bytes==CloudRepository.ObjectLength),"Missing per-object upload start");Assert(log.Items.Any(e=>e.Action=="upload.confirmed"&&e.ObjectId==id&&e.TimestampUtc!=default),"Missing per-object confirmation timestamp");}
        Assert(!log.Items.Any(e=>e.Action=="cleanup.deleted"),"Sync automatically deleted remote objects");
        await new SyncCoordinator(f.Repository).CleanupAsync(f.Volume,f.Binding,log,default);
        Assert(log.Items.Count(e=>e.Action=="commit.confirmed")==1&&log.Items.Any(e=>e.Action=="cleanup.deleted"&&e.ObjectId==f.OldId),"Missing commit or explicit cleanup evidence");
        var recovered=new Fixture();recovered.SeedOld();_=recovered.Volume.RootHash;string reusedId=recovered.Volume.DataIds[0];recovered.Store.SeedCanonical(recovered.Path(reusedId),recovered.Volume.Objects[reusedId]);var reuseLog=new LogCapture();
        await new SyncCoordinator(recovered.Repository).RunAsync(recovered.Volume,recovered.Binding,"logs",Fixture.Capacity,false,1,null,default,reuseLog);
        Assert(reuseLog.Items.Any(e=>e.Action=="upload.reused"&&e.ObjectId==reusedId)&&!reuseLog.Items.Any(e=>e.Action=="upload.confirmed"&&e.ObjectId==reusedId),"A recovered remote receipt was falsely logged as a new upload");
        var failed=new Fixture();failed.SeedOld();failed.Store.BeforePut=_=>throw new IOException("Injected upload error");var errors=new LogCapture();await Reject(()=>new SyncCoordinator(failed.Repository).RunAsync(failed.Volume,failed.Binding,"logs",Fixture.Capacity,false,1,null,default,errors),"Upload failure ignored");Assert(errors.Items.Any(e=>e.Action=="upload.failed"&&e.ObjectId is not null&&e.Level=="error"),"Failed object has no structured log");
    }

    private static async Task DeleteJournalRecovery()
    {
        using var temp=new TempCache();var f=new Fixture(temp.Open());f.SeedOld();var bytes=new byte[CloudRepository.ObjectLength];string hash=CloudRepository.Hash(bytes);
        var prior=f.Volume.PublishedObjects.ToList();for(int i=0;i<70;i++){string id=Guid.NewGuid().ToString();f.Store.Seed(f.Path(id),bytes);prior.Add(new(id,"data",bytes.Length,hash,true));}f.Volume.PublishedObjects=prior.ToArray();
        string pin=f.Binding.RemoteRoot+"/readers/"+Guid.NewGuid()+".json";var batching=new BatchStore(f.Store);batching.AfterBatch=()=>f.Store.Seed(pin,[1]);f.Repository=new(batching,temp.Open(),"test-account");
        var first=await f.Run();Assert(first.CleanupPending,"Expected a checkpointed partial cleanup");string journal=Directory.GetFiles(temp.DirectoryPath,"*.deletes").Single();byte[] committed=File.ReadAllBytes(journal);Assert(committed.Length>100,"Confirmed batch lacked a durable deletion receipt");
        File.WriteAllBytes(journal,committed[..(committed.Length/2)]);f.Store.Files.TryRemove(pin,out _);batching.AfterBatch=null;f.Repository=new(batching,temp.Open(),"test-account");f.Events.Clear();
        var resumed=await f.Run();Assert(!resumed.CleanupPending&&f.Volume.Objects.Keys.All(id=>f.Store.Files.ContainsKey(f.Path(id))),"Torn receipt lost live objects or blocked idempotent retry");NoObjectScan(f);
        File.WriteAllBytes(journal,committed);f.Repository=new(batching,temp.Open(),"test-account");f.Events.Clear();await f.Run();Assert(!f.Events.Any(CloudCall),"A stale receipt epoch resurrected cleanup work or invalidated the current cache");
    }
    private static async Task PublicationAcknowledgmentLost()
    {
        using var temp=new TempCache();var f=new Fixture(temp.Open());f.SeedOld();bool interrupted=false;
        f.Store.AfterPut = path =>
        {
            if (!interrupted && path.Contains("/commits/") && path.Contains(f.Volume.RootId))
            { interrupted = true; throw new IOException("Server stored commit but its upload response was lost."); }
        };
        await Reject(async()=>_=await f.Run(),"Lost upload acknowledgment was ignored");
        Assert(interrupted && f.Volume.Pin&&!f.Volume.Committed&&f.Store.Files.ContainsKey(f.OldPath),"Publication without acknowledgment released the old version");
        string descriptor=f.Store.Files.Keys.Single(path=>path.Contains("/commits/")&&path.Contains(f.Volume.RootId));byte[] original=f.Store.Files[descriptor].ToArray();
        Assert(!f.Events.Contains("ack:" + descriptor) && !f.Events.Any(e => e.StartsWith("delete:")), "Lost response fabricated an acknowledgment or collected data");
        f.Store.AfterPut=null;f.Repository=new(f.Store,temp.Open(),"test-account");f.Events.Clear();
        var coordinator = new SyncCoordinator(f.Repository);
        var result = await coordinator.RunAsync(f.Volume, f.Binding, "Test disk", Fixture.Capacity, false, 1, f.Progress, default);
        Assert(result.CleanupPending && original.SequenceEqual(f.Store.Files[descriptor]), "Retry changed immutable commit bytes or lost cleanup state");
        Assert(f.Volume.Committed && !f.Volume.Pin, "Recovered accepted commit did not finish local publication");
        Assert(f.Events.Count(e => e == "read:" + descriptor) == 1, "Uncertain publication did not perform exactly one reconciliation read");
        Assert(!f.Events.Any(e=>e.StartsWith("put:")),"Accepted publication was uploaded again after the lost response");
        Assert(!f.Events.Any(e => e.StartsWith("delete:")), "Publication recovery automatically collected objects"); NoObjectScan(f);
    }

    private static byte[] Corrupt(byte[] bytes) { var result = bytes.ToArray(); result[^1] ^= 1; return result; }
    private sealed class ProgressCapture : IProgress<TransferProgress>
    {
        public ConcurrentQueue<TransferProgress> Items { get; } = new();
        public void Report(TransferProgress value) => Items.Enqueue(value);
    }

    private sealed class Fixture
    {
        public const ulong Capacity = 64UL * 1024 * 1024;
        public ConcurrentQueue<string> Events { get; } = new();
        public MemoryStore Store { get; }
        public ModelVolume Volume { get; }
        public CloudRepository Repository { get; set; }
        public ICloudSyncCache Cache { get; }
        public CloudBinding Binding { get; }
        public ProgressCapture Progress { get; } = new();
        public string OldId { get; } = Guid.NewGuid().ToString();
        public string OldPath => Path(OldId);
        public Fixture(ICloudSyncCache? cache = null)
        {
            Cache = cache ?? new MemoryCloudSyncCache();
            Store = new(Events); Volume = new(Events); Repository = new(Store, Cache);
            Binding = new(Store.ProviderId, "test-account", CloudRepository.RootPath(Volume.Id), Guid.NewGuid().ToString());
        }
        public string Path(string id) => CloudRepository.ObjectPath(Binding.RemoteRoot, id);
        public RemoteCommit Commit(string id, string hash, ulong generation) => new(4, Volume.Id, Binding.DeviceId, generation,
            "Test disk", Capacity, false, id, hash, DateTimeOffset.UnixEpoch);
        public void SeedCommit(RemoteCommit commit) => Store.Seed(CloudRepository.CommitPath(Binding.RemoteRoot, commit), JsonSerializer.SerializeToUtf8Bytes(commit, Json));
        public RemoteCommit SeedOld(string? writer = null)
        {
            Store.Seed(Binding.RemoteRoot + "/owner.json", JsonSerializer.SerializeToUtf8Bytes(new
            { version = 4, accountId = Binding.AccountId, volumeId = Volume.Id, writerId = writer ?? Binding.DeviceId }, Json));
            var bytes = new byte[CloudRepository.ObjectLength]; bytes[0] = 71; Store.SeedCanonical(OldPath, bytes);
            var commit = Commit(OldId, CloudRepository.Hash(bytes), 1); SeedCommit(commit);
            Volume.PublishedObjects = [new(OldId,"commit",CloudRepository.ObjectLength,CloudRepository.Hash(bytes),true)];
            Store.SeedDirectory(Binding.RemoteRoot + "/readers");
            return commit;
        }
        // Existing collection scenarios explicitly exercise both user operations in sequence.
        public async Task<SyncResult> Run(CancellationToken ct = default)
        {
            var coordinator=new SyncCoordinator(Repository);
            var result=await coordinator.RunAsync(Volume,Binding,"Test disk",Capacity,false,1,Progress,ct);
            if (result.CleanupPending)
            {
                var cleanup=await coordinator.CleanupAsync(Volume,Binding,null,ct);
                result=result with {CleanupPending=cleanup.Pending};
            }
            return result;
        }
    }

    private sealed class ModelVolume(ConcurrentQueue<string> events) : ICloudVolume
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public int ObjectSizeBytes => CloudObjectGeometry.DefaultSize;
        public string JobId { get; private set; } = Guid.NewGuid().ToString();
        public string RootId { get; private set; } = Guid.NewGuid().ToString();
        public string[] DataIds { get; private set; } = [Guid.NewGuid().ToString(), Guid.NewGuid().ToString()];
        public Dictionary<string, byte[]> Objects { get; } = new();
        public HashSet<string> Uploaded { get; } = new();
        public HashSet<string> Reads { get; } = new();
        private Dictionary<string, string> Hashes { get; } = new();
        public bool Pin { get; private set; }
        public bool Committed { get; private set; }
        public ulong CurrentGeneration { get; private set; } = 2;
        public ulong PublishedGeneration { get; private set; } = 1;
        public bool AdvanceGenerationOnRead, CorruptLocalRead, ThrowAfterNativeCommitOnce;
        private ulong jobGeneration = 2;
        public Action<string>? OnControl;
        public ExportObject[] PublishedObjects { get; set; } = [];
        private readonly Dictionary<string,(string[] Added,string[] Removed)> deltas = new();
        private string publishedJobId = "";
        public void AdvanceLocal() => CurrentGeneration++;
        public void StartUnpublished()
        {
            Assert(!Pin && !Committed && PublishedObjects.Length == 0, "Fresh-volume setup used after publication");
            PublishedGeneration = 0; publishedJobId = "";
        }
        public void NextGeneration()
        {
            Initialize(); string retained=DataIds[0]; byte[] prior=Objects[retained];
            DataIds=[retained,Guid.NewGuid().ToString()]; RootId=Guid.NewGuid().ToString(); JobId=Guid.NewGuid().ToString();
            jobGeneration=PublishedGeneration+1; CurrentGeneration=jobGeneration; Pin=false; Committed=false; prepared=false;
            Objects.Clear(); Hashes.Clear(); Uploaded.Clear();
            int ordinal=1; foreach(string id in DataIds.Append(RootId)) {byte[] data=id==retained?prior:new byte[CloudRepository.ObjectLength];if(id!=retained){data[0]=(byte)(++ordinal+(int)jobGeneration);data[^1]=data[0];}Objects[id]=data;Hashes[id]=CloudRepository.Hash(data);}
            Uploaded.Add(retained);
        }
        public string RootHash { get { Initialize(); return Hashes[RootId]; } }
        private bool prepared;
        private void Initialize()
        {
            if (Objects.Count != 0) return;
            var ordinal = 0;
            foreach (var id in DataIds.Append(RootId))
            {
                var data = new byte[CloudRepository.ObjectLength]; data[0] = (byte)++ordinal; data[^1] = (byte)ordinal;
                Objects[id] = data; Hashes[id] = CloudRepository.Hash(data);
            }
        }
        private object Job(string phase) => new { object_size = ObjectSizeBytes, id = JobId, phase, root_object_id = prepared ? RootId : null,
            root_sha256 = prepared ? RootHash : null, generation = jobGeneration, estimated_bytes = 3L * CloudRepository.ObjectLength, total_objects = 3 };
        private object Status() => new { object_size = ObjectSizeBytes, data_generation = CurrentGeneration, pending_generation = CurrentGeneration,
            published_generation = PublishedGeneration, local_dirty = false, estimated_bytes = 0L, job = Pin ? Job("uploading") : null,
            published_commit = new { id=publishedJobId, delta_id=publishedJobId, root_object_id=PublishedObjects.FirstOrDefault(o=>o.Kind=="commit")?.Id, root_sha256=PublishedObjects.FirstOrDefault(o=>o.Kind=="commit")?.Sha256, generation=PublishedGeneration } };
        public Task<JsonElement> ControlAsync(object request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Initialize();
            var node = JsonSerializer.SerializeToElement(request); var cmd = node.GetProperty("cmd").GetString()!;
            events.Enqueue("control:" + cmd); OnControl?.Invoke(cmd);
            object result;
            switch (cmd)
            {
                case "cloud.pause": result = new { status = Status() }; break;
                case "cloud.transfer": result = new { ok = true }; break;
                case "cloud.gc_candidates":
                    var selected = node.GetProperty("object_ids").EnumerateArray().Select(v => v.GetString()!).ToArray();
                    result = new { allowed = selected.Where(id => PublishedObjects.All(obj => obj.Id != id)).ToArray(),
                        @protected = selected.Where(id => PublishedObjects.Any(obj => obj.Id == id)).ToArray(), cache_pin = (object?)null }; break;
                case "cloud.delta":
                    var deltaId = node.GetProperty("job_id").GetString()!;
                    var delta = deltas.GetValueOrDefault(deltaId, (Array.Empty<string>(),Array.Empty<string>()));
                    var ids = node.GetProperty("side").GetString()=="add" ? delta.Item1 : delta.Item2;
                    int cursor = node.GetProperty("cursor").GetInt32(), limit = node.GetProperty("limit").GetInt32();
                    result = new { items=ids.Skip(cursor).Take(limit).Select(id=>new {id}), next_cursor=cursor+limit<ids.Length?(int?)(cursor+limit):null }; break;
                case "cloud.prepare":
                    if (!deltas.ContainsKey(JobId)) deltas[JobId] = (Objects.Keys.ToArray(), PublishedObjects.Select(o=>o.Id).Except(Objects.Keys).ToArray());
                    Pin = true;
                    if (!prepared && !node.TryGetProperty("job_id", out _)) result = new { job = Job("preparing") };
                    else { prepared = true; result = new { job = Job("uploading") }; }
                    break;
                case "cloud.published_objects":
                    result = new { items = PublishedObjects.Select(o => new { id=o.Id,kind=o.Kind,length=o.Length,sha256=o.Sha256,uploaded=true }), next_cursor=(int?)null }; break;
                case "cloud.list":
                    result = new { items = DataIds.Append(RootId).Select(id => new { id, kind = id == RootId ? "commit" : "data",
                        length = CloudRepository.ObjectLength, sha256 = Hashes[id], uploaded = Uploaded.Contains(id) }).ToArray(), next_cursor = (int?)null };
                    break;
                case "cloud.receipt":
                    foreach (var record in node.TryGetProperty("records", out var batch) ? batch.EnumerateArray().ToArray() : new[] { node })
                    {
                        var objectId = record.GetProperty("object_id").GetString()!;
                        Assert(record.GetProperty("sha256").GetString() == Hashes[objectId], "Receipt has the wrong object hash");
                        Uploaded.Add(objectId);
                    }
                    result = new { job = Job("uploading") }; break;
                case "cloud.commit":
                    Assert(DataIds.Append(RootId).All(Uploaded.Contains), "Local commit precedes data or root upload acknowledgments");
                    Committed = true; Pin = false; PublishedGeneration = jobGeneration; publishedJobId = JobId;
                    PublishedObjects = DataIds.Append(RootId).Select(id=>new ExportObject(id,id==RootId?"commit":"data",CloudRepository.ObjectLength,Hashes[id],true)).ToArray();
                    if(ThrowAfterNativeCommitOnce){ThrowAfterNativeCommitOnce=false;throw new IOException("Injected crash after native commit");}
                    result = new { status = Status() }; break;
                case "cloud.status": result = Status(); break;
                default: throw new IOException("Unsupported model command " + cmd);
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(result));
        }
        public Task<byte[]> ReadObjectAsync(string jobId, string objectId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Initialize();
            Assert(Pin && jobId == JobId, "Read occurred without its pinned job");
            Reads.Add(objectId);
            if (AdvanceGenerationOnRead) CurrentGeneration = 3;
            return Task.FromResult(CorruptLocalRead ? Corrupt(Objects[objectId]) : Objects[objectId].ToArray());
        }
    }

    private sealed class MemoryStore(ConcurrentQueue<string> events) : ICloudObjectStore, ICloudPreparedUploadStore, ICloudKnownObjectReader, ICloudEncodedObjectStore, ICloudBoundedObjectReader
    {
        public string ProviderId => "memory-test";
        public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, bool> directories = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, CanonicalObjectDescriptor> encodedReceipts = new(StringComparer.Ordinal);
        public Action<string>? BeforePut, AfterPut, AfterDelete;
        public Func<string, byte[], byte[]>? AlterRead;
        public void Seed(string path, byte[] bytes) { SeedDirectory(Parent(path)); Files[path] = bytes; }
        public void SeedCanonical(string path, byte[] bytes)
        {
            using var upload = PreparedObjectUpload.CreateAsync(new(path, bytes.Length, CloudRepository.Hash(bytes)), new MemoryStream(bytes, false)).GetAwaiter().GetResult();
            using var view = upload.Wire.OpenRead(); using var sink = new MemoryStream(); view.CopyTo(sink);
            Seed(path, sink.ToArray()); encodedReceipts[path] = upload.Canonical;
        }
        public void SeedDirectory(string path)
        {
            directories["/"] = true;
            string current = ""; foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) { current += "/" + part; directories[current] = true; }
        }
        private static string Parent(string path) { var index = path.LastIndexOf('/'); return index <= 0 ? "/" : path[..index]; }
        private CloudObjectInfo Info(string path) => Files.TryGetValue(path, out var bytes) ? new(path, bytes.Length, false, "receipt") : new(path, 0, true);
        public Task<CloudAccountInfo> ValidateAsync(CancellationToken cancellationToken = default) { events.Enqueue("validate"); return Task.FromResult(new CloudAccountInfo("test-account", "test")); }
        public Task<CloudObjectInfo?> HeadAsync(string path, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); events.Enqueue("head:"+path); return Task.FromResult<CloudObjectInfo?>(Files.ContainsKey(path) || directories.ContainsKey(path) ? Info(path) : null); }
        public async IAsyncEnumerable<CloudObjectInfo> ListAsync(string directory, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            events.Enqueue("list:"+directory);
            foreach (var path in Files.Keys.Concat(directories.Keys).Where(p => p != "/" && Parent(p) == directory).Distinct().Order(StringComparer.Ordinal).ToArray())
            { cancellationToken.ThrowIfCancellationRequested(); yield return Info(path); }
            await Task.CompletedTask;
        }
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); events.Enqueue("mkdir:" + path); SeedDirectory(path); return Task.CompletedTask; }
        public Task<CloudObjectInfo?> TryGetConfirmedReceiptAsync(ImmutableObjectDescriptor descriptor,CancellationToken ct=default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Files.TryGetValue(descriptor.Path,out var bytes)&&bytes.Length==descriptor.Length&&CloudRepository.Hash(bytes)==descriptor.Sha256 ? Info(descriptor.Path) with {ReusedExisting=true}:null);
        }
        public Task<CloudObjectInfo?> TryGetEncodedReceiptAsync(CanonicalObjectDescriptor descriptor, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(encodedReceipts.TryGetValue(descriptor.Path, out var known) && known == descriptor && Files.ContainsKey(descriptor.Path)
                ? Info(descriptor.Path) with { ReusedExisting = true } : null);
        }
        public async Task<CloudObjectInfo> PutEncodedAsync(PreparedObjectUpload upload, CancellationToken ct = default)
        {
            var result = await PutPreparedAsync(upload.Wire, ct); encodedReceipts[upload.Canonical.Path] = upload.Canonical; return result;
        }
        public Task<Stream> OpenReadBoundedAsync(string path, int maximumLength, CancellationToken ct = default)
        { events.Enqueue("bounded-read:" + path); return OpenReadAsync(path, null, ct); }
        public async Task<CloudObjectInfo> PutPreparedAsync(PreparedUpload upload,CancellationToken ct=default)
        { using var view=upload.OpenRead();return await PutImmutableAsync(upload.Descriptor.Path,view,upload.Length,upload.Sha256,ct); }
        public async Task<CloudObjectInfo> PutImmutableAsync(string path, Stream content, long length, string sha256, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); BeforePut?.Invoke(path);
            using var stream = new MemoryStream(); await content.CopyToAsync(stream, cancellationToken); var bytes = stream.ToArray();
            Assert(bytes.Length == length && CloudRepository.Hash(bytes) == sha256, "Invalid put contract");
            bool reused = Files.TryGetValue(path, out var existing);
            if (reused && !existing!.SequenceEqual(bytes)) throw new CloudObjectConflictException(path);
            Seed(path, bytes); events.Enqueue("put:" + path); AfterPut?.Invoke(path);
            events.Enqueue("ack:" + path); return Info(path) with { ReusedExisting = reused };
        }
        public Task<Stream> OpenReadAsync(string path, CloudByteRange? range = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); events.Enqueue("read:" + path);
            if (!Files.TryGetValue(path, out var bytes)) throw new CloudObjectNotFoundException(path);
            bytes = AlterRead?.Invoke(path, bytes) ?? bytes;
            if (range is { } r) bytes = bytes[(int)r.Offset..(int)(r.Offset + r.Length)];
            return Task.FromResult<Stream>(new MemoryStream(bytes, false));
        }
        public Task<Stream> OpenReadKnownAsync(ImmutableObjectDescriptor descriptor, CancellationToken cancellationToken = default)
        { events.Enqueue("known-read:" + descriptor.Path); return OpenReadAsync(descriptor.Path, null, cancellationToken); }
        public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Files.TryRemove(path, out _); encodedReceipts.TryRemove(path, out _); events.Enqueue("delete:" + path); AfterDelete?.Invoke(path); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
