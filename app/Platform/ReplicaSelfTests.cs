using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using OverlayDisk.Cloud.Sync;
using OverlayDisk.Services;
using OverlayDisk.Worker;

namespace OverlayDisk;

/// <summary>Isolated reader fixtures; all remote objects are in memory and no cloud account is used.</summary>
internal static class ReplicaSelfTests
{
    private const string Password = "replica-isolated-fixture-password";
    private const ulong MiB = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonElement E(object? value) => value is JsonElement e ? e : JsonSerializer.SerializeToElement(value, Json);
    private static void Check(bool value, string message) { if (!value) throw new IOException("Replica fixture: " + message); }
    private static async Task Reject(Func<Task> run, string message)
    {
        try { await run(); } catch (IOException) { return; }
        throw new IOException("Replica fixture accepted: " + message);
    }
    private static byte[] Pattern(int seed, int bytes = 4096) { var value = new byte[bytes]; new Random(seed).NextBytes(value); return value; }
    private static void PrepareOutput(string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("请使用新的空测试目录。");
        Directory.CreateDirectory(output);
    }
    private sealed record Version(LazyHydrationSelfTests.Archive Archive, RemoteCommit Commit, JsonElement Backing);
    private static Version Export(CoreDisk core, ConcurrentDictionary<string, byte[]> objects)
    {
        var archive = LazyHydrationSelfTests.Export(core);
        foreach (var pair in archive.Objects) objects[pair.Key] = pair.Value;
        var job = core.Control(new { cmd = "cloud.status" }).GetProperty("job");
        string jobId = job.GetProperty("id").GetString()!, rootId = archive.Backing.GetProperty("root_object_id").GetString()!;
        string hash = archive.Hashes[rootId], remoteRoot = CloudRepository.RootPath(core.Id.ToString());
        var commit = new RemoteCommit(4, core.Id.ToString(), core.Id.ToString(), job.GetProperty("generation").GetUInt64(), "replica fixture",
            core.Capacity, core.Encrypted, rootId, hash, DateTimeOffset.UnixEpoch) { ObjectSizeBytes = checked((int)core.ObjectSizeBytes) };
        foreach (var pair in archive.Objects)
            core.Control(new { cmd = "cloud.receipt", job_id = jobId, object_id = pair.Key, sha256 = archive.Hashes[pair.Key], length = pair.Value.Length, receipt = "fixture-ack" });
        core.Control(new { cmd = "cloud.commit", job_id = jobId, root_object_id = rootId, root_sha256 = hash, receipt = "fixture-publication" });
        var backing = E(new { provider_id = "replica-fixture", account_id = "fixture", source_volume_id = core.Id.ToString(), remote_root = remoteRoot,
            root_object_id = rootId, root_sha256 = hash, reader_pin = remoteRoot + "/readers/" + Guid.NewGuid() + ".json" });
        return new(archive, commit, backing);
    }
    private static async Task<string> ImportAsync(WorkerApplicationService worker, string path, Version version, bool original = false, bool resume = false, bool finish = true)
    {
        var begin = await worker.InvokeBulkAsync("restore.begin", E(new { path, name = "Replica fixture", password = Password, lazy = true, resume,
            mode = original ? "original" : "copy", sourceVolumeId = version.Commit.VolumeId, objectSizeBytes = version.Commit.ObjectSizeBytes,
            commit = version.Commit, backing = version.Backing, publication = original ? (object)new { commit = version.Commit,
                binding = new { backend_id = "replica-fixture", account_id = "fixture", remote_root = CloudRepository.RootPath(version.Commit.VolumeId),
                    device_id = version.Commit.WriterId, enabled = true } } : null }), version.Archive.Root, default);
        string id = E(begin.Data).GetProperty("id").GetString()!;
        for (int step = 0; step < 64; step++)
        {
            var status = E(await worker.InvokeAsync("restore.status", E(new { id, cursor = 0, limit = 128 }), default));
            if (status.GetProperty("phase").GetString() is "ready" or "complete")
            {
                if (finish) await worker.InvokeAsync("restore.finish", E(new { id }), default).WaitAsync(TimeSpan.FromSeconds(45));
                return id;
            }
            foreach (var item in status.GetProperty("needed").EnumerateArray())
            {
                Check(item.GetProperty("kind").GetString() == "metadata", "import requested an eager data object");
                string oid = item.GetProperty("id").GetString()!;
                await worker.InvokeBulkAsync("restore.accept", E(new { id, object_id = oid }), version.Archive.Objects[oid], default);
            }
            await worker.InvokeAsync("restore.step", E(new { id, max_nodes = 1, max_objects = 1 }), default);
        }
        throw new IOException("Lazy import exceeded bounded bootstrap work.");
    }
    internal static async Task<int> RunGptAsync(string output)
    {
        PrepareOutput(output);
        const ulong capacity = 4UL << 40, dataOffset = 1UL << 40, farOffset = 3UL << 40;
        ulong lastLba = capacity / 512 - 1;
        void Progress(string message) => File.AppendAllText(Path.Combine(output, "progress.log"), DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
        string sourcePath = Path.Combine(output, "source-gpt.odv4"), copyPath = Path.Combine(output, "copy-gpt.odv4"), resumedPath = Path.Combine(output, "resumed-gpt.odv4");
        var objects = new ConcurrentDictionary<string, byte[]>();
        byte[] before = Pattern(3101), after = Pattern(3102), unchanged = Pattern(3103);
        Version first, latest; Guid sourceId; byte[] firstEntries, latestEntries;
        (byte[] Primary, byte[] Backup, byte[] Entries) Layout(Guid diskId, ulong dataEnd)
        {
            byte[] mbr = new byte[512], entries = new byte[16384];
            diskId.ToByteArray().AsSpan(0, 4).CopyTo(mbr.AsSpan(440, 4));
            mbr[450] = 0xEE; mbr[510] = 0x55; mbr[511] = 0xAA;
            BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(454), 1); BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(458), uint.MaxValue);
            void Partition(int index, Guid type, ulong start, ulong end)
            {
                var row = entries.AsSpan(index * 128, 128); type.TryWriteBytes(row); Guid.NewGuid().TryWriteBytes(row[16..]);
                BinaryPrimitives.WriteUInt64LittleEndian(row[32..], start); BinaryPrimitives.WriteUInt64LittleEndian(row[40..], end);
            }
            Partition(0, DiskIdentityRewriter.Reserved, 2048, 34815); Partition(1, DiskIdentityRewriter.BasicData, 34816, dataEnd);
            byte[] Header(ulong at, ulong other, ulong table)
            {
                byte[] h = new byte[512]; "EFI PART"u8.CopyTo(h);
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 0x10000); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), 92);
                BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(24), at); BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(32), other);
                BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(40), 34); BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(48), lastLba - 33);
                diskId.TryWriteBytes(h.AsSpan(56, 16)); BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(72), table);
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80), 128); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(84), 128);
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), DiskIdentityRewriter.Crc32(entries));
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), DiskIdentityRewriter.Crc32(h.AsSpan(0, 92))); return h;
            }
            byte[] primary = new byte[17408], backup = new byte[16896];
            mbr.CopyTo(primary, 0); Header(1, lastLba, 2).CopyTo(primary, 512); entries.CopyTo(primary, 1024);
            entries.CopyTo(backup, 0); Header(lastLba, 1, lastLba - 32).CopyTo(backup, 16384); return (primary, backup, entries);
        }
        Progress("Create actual encrypted 4 TiB virtual container with bounded GPT and data writes");
        CoreDisk.Create(sourcePath, capacity, Password, 8U << 20);
        using (var source = new CoreDisk(sourcePath, Password))
        {
            sourceId = source.Id; var layout = Layout(sourceId, lastLba - 33); firstEntries = layout.Entries;
            source.Write(0, layout.Primary, layout.Primary.Length); source.Write(capacity - (ulong)layout.Backup.Length, layout.Backup, layout.Backup.Length);
            source.Write(dataOffset, before, before.Length); source.Write(farOffset, unchanged, unchanged.Length); source.Flush(); first = Export(source, objects);
            layout = Layout(sourceId, lastLba - 2049); latestEntries = layout.Entries;
            source.Write(0, layout.Primary, layout.Primary.Length); source.Write(capacity - (ulong)layout.Backup.Length, layout.Backup, layout.Backup.Length);
            source.Write(dataOffset, after, after.Length); source.Flush(); latest = Export(source, objects);
        }
        Check(new FileInfo(sourcePath).Length < 512L * 1024 * 1024, "sparse GPT fixture expanded with virtual capacity");
        int failIdentityFetch = 0, fetched = 0;
        Task<byte[]> Provider(LazyObjectRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref failIdentityFetch, 0) == 1) throw new IOException("injected identity hydration interruption after native finish");
            Interlocked.Increment(ref fetched); return Task.FromResult(objects[request.ObjectId].ToArray());
        }
        void Verify(string path, string expectedId, byte[] expectedData, byte[] expectedEntries)
        {
            using var disk = new CoreDisk(path, Password); disk.SetObjectProvider(Provider);
            Check(disk.Capacity == capacity && disk.Id.ToString() == expectedId && disk.Id != sourceId, "GPT copy identity or capacity changed");
            byte[] Read(ulong offset, int length) { var bytes = new byte[length]; disk.ReadForManagement(offset, bytes, length); return bytes; }
            byte[]? previousEntries = null;
            foreach (ulong sector in new[] { 1UL, lastLba })
            {
                byte[] header = Read(sector * 512, 512); uint crc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
                Check(new Guid(header.AsSpan(56, 16)) == disk.Id && header.AsSpan(0, 8).SequenceEqual("EFI PART"u8), "GPT mirror retained source disk GUID");
                header.AsSpan(16, 4).Clear(); Check(DiskIdentityRewriter.Crc32(header.AsSpan(0, 92)) == crc, "GPT header CRC");
                byte[] entries = Read(BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72)) * 512, 16384);
                Check(DiskIdentityRewriter.Crc32(entries) == BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(88)), "GPT partition-array CRC");
                if (previousEntries != null) Check(entries.SequenceEqual(previousEntries), "GPT primary and backup disagree");
                for (int row = 0; row < 2; row++)
                {
                    var actual = entries.AsSpan(row * 128, 128); var expected = expectedEntries.AsSpan(row * 128, 128);
                    Check(!actual.Slice(16, 16).SequenceEqual(expected.Slice(16, 16)) && actual[..16].SequenceEqual(expected[..16])
                        && actual[32..].SequenceEqual(expected[32..]), "copy partition GUIDs or published ranges");
                }
                previousEntries = entries;
            }
            Check(Read(dataOffset, expectedData.Length).SequenceEqual(expectedData) && Read(farOffset, unchanged.Length).SequenceEqual(unchanged), "GPT data-range bytes changed");
            var replica = disk.Control(new { cmd = "replica.status" });
            Check(replica.GetProperty("identity_ready").GetBoolean() && !replica.GetProperty("local_changes").GetBoolean(), "GPT identity repair marked content dirty or remained pending");
            Check(!disk.Control(new { cmd = "cloud.status" }).GetProperty("bound").GetBoolean(), "GPT copy enabled cloud uploads");
        }
        var worker = new WorkerApplicationService(Path.Combine(output, "worker-settings"), (_, _) => Task.FromResult(false)); worker.SetObjectProvider(Provider);
        async Task<JsonElement> Call(string method, object args) => E(await worker.InvokeAsync(method, E(args), default));
        try
        {
            Progress("Restore GPT copy through worker finish and native identity publication");
            string id = await ImportAsync(worker, copyPath, first);
            await Call("disks.unlock", new { id, password = Password });
            Check((await Call("replica.status", new { id })).GetProperty("identity_ready").GetBoolean(), "worker unlock did not prepare GPT identity");
            await Call("snapshots.create", new { id, name = "GPT before remote update" }); await Call("disks.close", new { id });
            Verify(copyPath, id, before, firstEntries); Verify(copyPath, id, before, firstEntries);
            Progress("Load next source version while unmounted; verify changed GPT layout and data after reopening");
            await Call("disks.unlock", new { id, password = Password }); var staged = await StageAsync(worker, id, latest);
            var applied = await Call("replica.apply", Confirmation(id, staged)); Check(applied.GetProperty("identityReady").GetBoolean(), "updated GPT identity remained pending");
            Check((await Call("snapshots.list", new { id })).GetProperty("items").GetArrayLength() == 1, "GPT root replacement dropped manual snapshot");
            await Call("disks.close", new { id }); Verify(copyPath, id, after, latestEntries);
            await Call("disks.unlock", new { id, password = Password }); await Call("disks.close", new { id }); Verify(copyPath, id, after, latestEntries);
            Progress("Interrupt identity hydration after durable finish, then continue the same container");
            string resumedId = await ImportAsync(worker, resumedPath, first, finish: false); failIdentityFetch = 1;
            await Reject(async () => { await Call("restore.finish", new { id = resumedId }); }, "identity hydration fault did not interrupt finish");
            Check((await Call("restore.status", new { id = resumedId })).GetProperty("phase").GetString() == "complete", "injected failure occurred before durable native finish");
            await Call("restore.cancel", new { id = resumedId });
            using (var incomplete = new CoreDisk(resumedPath, Password))
                Check(!incomplete.Control(new { cmd = "replica.status" }).GetProperty("identity_ready").GetBoolean(), "interrupted identity was marked complete");
            string continuedId = await ImportAsync(worker, resumedPath, first, resume: true); Check(continuedId == resumedId, "finish retry created a different disk");
            await Call("disks.unlock", new { id = continuedId, password = Password }); await Call("disks.close", new { id = continuedId });
            Verify(resumedPath, continuedId, before, firstEntries);
            Progress("All GPT copy and recovery checks passed without mounting or cloud access");
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, virtualCapacityBytes = capacity, objectSizeBytes = 8U << 20,
                networkUsed = false, mountedWindowsVolumes = false, fetched, checks = new[] { "Actual encrypted 4 TiB virtual container and complete 34304-byte GPT identity overlay",
                    "Worker finish, unlock, close and reopen preserve both header/array CRCs, distinct disk/partition GUIDs and data bytes",
                    "Latest cloud root switches changed GPT layout and data while preserving snapshot and disabled upload",
                    "Identity failure after durable native finish resumes the same container without reporting an incomplete overlay ready" } }, Json));
            Console.WriteLine("REPLICA_GPT_SMOKE_OK"); return 0;
        }
        finally { await worker.ShutdownAsync(default); }
    }
    private static object Confirmation(string id, JsonElement status, bool discard = false)
    {
        var candidate = status.GetProperty("candidate");
        return new { id, token = candidate.GetProperty("token").GetString(), expectedRevision = candidate.GetProperty("expected_revision").GetUInt64(), discardLocalChanges = discard };
    }
    private static async Task<JsonElement> StageAsync(WorkerApplicationService worker, string id, Version version)
    {
        await worker.InvokeAsync("replica.prepare", E(new { id }), default);
        return E((await worker.InvokeBulkAsync("replica.stage", E(new { id, options = new { commit = version.Commit, backing = version.Backing } }), version.Archive.Root, default)).Data);
    }
    internal static async Task<int> RunAsync(string output)
    {
        PrepareOutput(output); var checks = new List<string>();
        string sourcePath = Path.Combine(output, "source.odv4"), targetPath = Path.Combine(output, "copy.odv4");
        var objects = new ConcurrentDictionary<string, byte[]>();
        byte[] before = Pattern(803), after = Pattern(804), local = Pattern(805);
        Version first, latest;
        CoreDisk.Create(sourcePath, 64 * MiB, Password);
        using (var source = new CoreDisk(sourcePath, Password))
        {
            byte[] mbr = new byte[4096]; mbr[450] = 7; mbr[510] = 0x55; mbr[511] = 0xAA;
            BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(454), 2048);
            BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(458), 63 * 1024 * 1024 / 512);
            source.Write(0, mbr, mbr.Length); source.Write(MiB, before, before.Length); source.Write(48 * MiB, Pattern(806), 4096); source.Flush();
            first = Export(source, objects);
            source.Write(MiB, after, after.Length); source.Flush(); latest = Export(source, objects);
        }
        var worker = new WorkerApplicationService(Path.Combine(output, "worker-settings"), (_, _) => Task.FromResult(false));
        int fetched = 0, sourceQueries = 0;
        worker.SetObjectProvider(async (request, ct) =>
        {
            // This lookup reenters the worker while restore.finish or native stage is
            // holding its command gate. It must never queue behind that mutation.
            var source = E(await worker.InvokeAsync("cache.source", E(new { id = request.DiskId, object_id = request.ObjectId }), ct));
            Check(source.GetProperty("sha256").GetString()!.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase), "hydration source mismatch");
            Interlocked.Increment(ref sourceQueries); Interlocked.Increment(ref fetched); return objects[request.ObjectId].ToArray();
        });
        async Task<JsonElement> Call(string method, object args) => E(await worker.InvokeAsync(method, E(args), default));
        string id = "";
        try
        {
            id = await ImportAsync(worker, targetPath, first);
            await Call("disks.unlock", new { id, password = Password });
            var initial = await Call("replica.status", new { id });
            Check(!initial.GetProperty("local_changes").GetBoolean(), "copy identity overlay became a local data change");
            Check(!((await Call("cloud.status", new { id })).GetProperty("bound").GetBoolean()), "copy enabled uploading");
            Check(sourceQueries > 0, "finish did not exercise a reentrant identity hydration lookup");
            checks.Add("Lazy copy finish hydrates only partition identity as needed, reenters cache.source without deadlock, and leaves upload disabled and local delta clean.");
            await Call("snapshots.create", new { id, name = "before replica update" });
            await ActiveDisks.Gate.WaitAsync(); try { ActiveDisks.Items.Add(Guid.Parse(id), new(64 * MiB)); } finally { ActiveDisks.Gate.Release(); }
            try { await Reject(async () => { await Call("replica.prepare", new { id }); }, "mounted native identity bypassed the prepare guard"); }
            finally { await ActiveDisks.Gate.WaitAsync(); try { ActiveDisks.Items.Remove(Guid.Parse(id)); } finally { ActiveDisks.Gate.Release(); } }
            await Call("disks.settings", new { id, name = "Replica fixture", driveLetter = "Z", readOnly = true });
            var staged = await StageAsync(worker, id, latest);
            await Reject(async () => { await Call("replica.cancel", new { id, token = "stale-cancel" }); }, "stale cancellation removed another prepared candidate");
            await Call("replica.cancel", new { id, token = staged.GetProperty("candidate").GetProperty("token").GetString() });
            await Call("replica.prepare", new { id }); int reusedFetches = fetched;
            staged = await Call("replica.stageCached", new { id, options = new { commit = latest.Commit, backing = latest.Backing } });
            Check(staged.GetProperty("cached").GetBoolean() && fetched == reusedFetches, "retry downloaded an already verified candidate root");
            await Reject(async () => { await Call("disks.mount", new { id }); }, "reservation allowed a mount");
            await Reject(async () => { await Call("snapshots.create", new { id, name = "conflict" }); }, "reservation allowed conflicting snapshot mutation");
            await Reject(async () => { await Call("replica.apply", new { id, token = "stale", expectedRevision = 0, discardLocalChanges = true }); }, "stale confirmation applied a root");
            await Call("replica.apply", Confirmation(id, staged));
            Check(!(await Call("replica.status", new { id })).GetProperty("local_changes").GetBoolean(), "read-only apply became dirty");
            Check((await Call("snapshots.list", new { id })).GetProperty("items").GetArrayLength() == 1, "root switch lost the manual snapshot");
            checks.Add("Mounted prepare is rejected; a pending candidate prevents remount and conflicting changes; stale confirmation is rejected and read-only apply preserves the manual snapshot.");
            await Call("disks.close", new { id });
            using (var copy = new CoreDisk(targetPath, Password))
            {
                copy.SetObjectProvider((r, _) => Task.FromResult(objects[r.ObjectId].ToArray()));
                byte[] actual = new byte[4096]; copy.Read(MiB, actual, actual.Length); Check(actual.SequenceEqual(after), "latest root returned stale content");
                copy.Write(MiB, local, local.Length); copy.Flush();
            }
            await Call("disks.unlock", new { id, password = Password });
            Check((await Call("replica.status", new { id })).GetProperty("local_changes").GetBoolean(), "reopened local changes were not detected");
            await Call("replica.prepare", new { id });
            int beforeReset = fetched;
            staged = await Call("replica.stageCurrent", new { id, commit = latest.Commit });
            Check(fetched == beforeReset, "same source reset downloaded cached root objects");
            await Reject(async () => { await Call("replica.apply", Confirmation(id, staged)); }, "dirty apply lacked explicit confirmation");
            await Call("replica.cancel", new { id, token = staged.GetProperty("candidate").GetProperty("token").GetString() });
            await Call("disks.close", new { id });
            using (var copy = new CoreDisk(targetPath, Password)) { byte[] actual = new byte[4096]; copy.Read(MiB, actual, actual.Length); Check(actual.SequenceEqual(local), "cancel discarded local modifications"); }
            await Call("disks.unlock", new { id, password = Password });
            await Call("replica.prepare", new { id }); staged = await Call("replica.stageCurrent", new { id, commit = latest.Commit });
            await Call("replica.apply", Confirmation(id, staged, true)); await Call("disks.close", new { id });
            using (var copy = new CoreDisk(targetPath, Password))
            {
                copy.SetObjectProvider((r, _) => Task.FromResult(objects[r.ObjectId].ToArray()));
                byte[] actual = new byte[4096]; copy.Read(MiB, actual, actual.Length); Check(actual.SequenceEqual(after), "confirmed reset retained local modifications");
                Check(!copy.Control(new { cmd = "cloud.status" }).GetProperty("bound").GetBoolean(), "reset enabled uploading");
                Check(!copy.Control(new { cmd = "replica.status" }).GetProperty("local_changes").GetBoolean(), "identity rebuilding dirtied reset");
            }
            checks.Add("Reopened RW modifications require an explicit discard acknowledgment even in RO mode; cancel preserves bytes, same-root reset reuses cached objects, confirmed reset restores the source and keeps upload disabled.");
            string originalPath = Path.Combine(output, "original.odv4");
            string originalId = await ImportAsync(worker, originalPath, first, original: true);
            Check(originalId == first.Commit.VolumeId, "original import changed source identity");
            await Call("disks.settings", new { id = originalId, name = "Original fixture", driveLetter = "Y", readOnly = true });
            await Call("disks.unlock", new { id = originalId, password = Password });
            staged = await StageAsync(worker, originalId, latest);
            await Call("replica.apply", Confirmation(originalId, staged));
            var publication = await Call("cloud.status", new { id = originalId });
            Check(publication.GetProperty("bound").GetBoolean() && publication.GetProperty("binding").GetProperty("device_id").GetString() == first.Commit.WriterId,
                "original update changed existing writer ownership");
            await Call("disks.close", new { id = originalId });
            using (var original = new CoreDisk(originalPath, Password)) { original.Write(MiB, local, local.Length); original.Flush(); }
            await Call("disks.unlock", new { id = originalId, password = Password });
            await Call("replica.prepare", new { id = originalId });
            staged = await Call("replica.stageCurrent", new { id = originalId, commit = latest.Commit });
            await Reject(async () => { await Call("replica.apply", Confirmation(originalId, staged)); }, "original dirty reset did not require confirmation");
            await Call("replica.apply", Confirmation(originalId, staged, true));
            await Call("disks.close", new { id = originalId });
            using (var original = new CoreDisk(originalPath, Password))
            {
                original.SetObjectProvider((r, _) => Task.FromResult(objects[r.ObjectId].ToArray()));
                byte[] actual = new byte[4096]; original.Read(MiB, actual, actual.Length); Check(actual.SequenceEqual(after), "original confirmed update returned stale data");
                Check(!original.Control(new { cmd = "replica.status" }).GetProperty("local_changes").GetBoolean(), "original update remained dirty");
                original.Write(MiB, local, local.Length); original.Flush();
                original.Control(new { cmd = "replica.stage_current", commit = E(latest.Commit) });
            }
            checks.Add("Original-identity lazy restore updates a clean source and explicitly resets local changes to the same source while preserving existing writer ownership.");
            await Call("disks.unlock", new { id = originalId, password = Password });
            await Call("disks.quiesce", new { id = originalId });
            await Call("disks.close", new { id = originalId });
            using (var original = new CoreDisk(originalPath, Password))
                Check(original.Control(new { cmd = "replica.status" }).GetProperty("candidate").ValueKind == JsonValueKind.Object,
                    "closing a reopened disk discarded the saved candidate");
            checks.Add("A candidate recovered after restart still blocks remount but permits safe close without discarding its durable preparation.");
            var settings = new AppSettings();
            var sourceRecord = new RestoreRecord { AccountId = "fixture", RemoteRoot = CloudRepository.RootPath(first.Commit.VolumeId), TargetPath = targetPath, Commit = first.Commit };
            string firstPin = sourceRecord.RemoteRoot + "/readers/" + Guid.NewGuid() + ".json";
            string nextPin = sourceRecord.RemoteRoot + "/readers/" + Guid.NewGuid() + ".json";
            ApplicationService.RememberReplicaPin(settings, sourceRecord, id, first.Commit, firstPin);
            ApplicationService.RememberReplicaPin(settings, sourceRecord, id, latest.Commit, nextPin);
            Check(settings.Restores.Count == 2 && settings.Restores.Values.All(r => r.PreparedReplicaOnly && r.Complete && r.ReaderPin != null),
                "superseding a failed candidate lost its confirmed reader pin or made it the current source");
            foreach (var record in settings.Restores.Values) record.SourcePinReplaced = true;
            settings.Restores.Values.First().ContainerDeleted = true;
            Check(ApplicationService.RetainReplicaSourcePins(settings, id)
                && settings.Restores.Values.All(r => r.SourcePinReplaced == r.ContainerDeleted),
                "restoring replica pin protection did not clear stale replacement flags or changed deleted-container cleanup");
            Check(WorkerApplicationService.ReplicaExpectedRevision(E(new { expectedRevision = "9007199254740993" })) == 9007199254740993UL,
                "confirmation revision lost precision");
            checks.Add("Superseded candidate pins remain in the manual-cleanup ledger, live replica pin protection repairs stale handoff flags, and confirmation revisions retain full 64-bit precision.");
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks, sourceQueries, fetched, networkUsed = false, mountedWindowsVolumes = false }, Json));
            Console.WriteLine("REPLICA_SMOKE_OK"); return 0;
        }
        finally { await worker.ShutdownAsync(default); }
    }
    internal static async Task<int> RunMountAsync(string output)
    {
        PrepareOutput(output);
        void Progress(string message) => File.AppendAllText(Path.Combine(output, "progress.log"), DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
        using var source = new DiskController(Path.Combine(output, "source-settings"));
        if (!source.IsAdministrator || !source.DriverAvailable) throw new IOException("此测试需要管理员及已安装驱动，仅使用新建测试容器。");
        var worker = new WorkerApplicationService(Path.Combine(output, "worker-settings"));
        var objects = new ConcurrentDictionary<string, byte[]>();
        worker.SetObjectProvider((request, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(objects[request.ObjectId].ToArray()); });
        async Task<JsonElement> Call(string method, object args) => E(await worker.InvokeAsync(method, E(args), default));
        char letter = Enumerable.Range('D', 23).Select(i => (char)i).Reverse().First(c => !DriveInfo.GetDrives().Any(d => d.Name[0] == c));
        string mount = letter + @":\", targetPath = Path.Combine(output, "copy.odv4");
        byte[] oldBytes = Pattern(1301, 2 * (int)MiB), newBytes = Pattern(1302, 2 * (int)MiB), unchanged = Pattern(1303, (int)MiB);
        try
        {
            Progress("Create isolated encrypted 256 MiB NTFS source");
            await source.CreateAsync(new("Replica source", Path.Combine(output, "source.odv4"), 256 * MiB, letter, true), Password);
            var entry = source.Disks.Single();
            await File.WriteAllBytesAsync(Path.Combine(mount, "video.mp4"), oldBytes); await File.WriteAllBytesAsync(Path.Combine(mount, "unchanged.bin"), unchanged);
            await source.FlushFileSystemAsync(entry); var first = Export(source.TryGetCore(entry.Id)!, objects);
            await File.WriteAllBytesAsync(Path.Combine(mount, "video.mp4"), newBytes);
            await source.FlushFileSystemAsync(entry); var latest = Export(source.TryGetCore(entry.Id)!, objects);
            await source.UnmountAllAsync();
            Progress("Import old snapshot and mount RO using lazy index/data");
            string id = await ImportAsync(worker, targetPath, first);
            await Call("disks.settings", new { id, name = "Replica fixture", driveLetter = letter.ToString(), readOnly = true });
            await Call("disks.mount", new { id, password = Password, readOnly = true });
            Check((await File.ReadAllBytesAsync(Path.Combine(mount, "video.mp4"))).SequenceEqual(oldBytes), "old mounted copy content");
            await Reject(async () => { await Call("replica.prepare", new { id }); }, "mounted real NTFS volume allowed update");
            await Call("snapshots.create", new { id, name = "old mounted snapshot" });
            await Call("disks.quiesce", new { id });
            Progress("Apply newer cloud snapshot while manually unmounted; remount RO");
            var staged = await StageAsync(worker, id, latest); await Call("replica.apply", Confirmation(id, staged));
            await Call("disks.mount", new { id, readOnly = true });
            Check((await File.ReadAllBytesAsync(Path.Combine(mount, "video.mp4"))).SequenceEqual(newBytes), "new mounted copy content");
            Check((await File.ReadAllBytesAsync(Path.Combine(mount, "unchanged.bin"))).SequenceEqual(unchanged), "unchanged mounted file");
            await Call("disks.quiesce", new { id });
            Progress("RW local edit requires discard acknowledgment before same-source reset");
            await Call("disks.mount", new { id, readOnly = false });
            await File.WriteAllTextAsync(Path.Combine(mount, "local-only.txt"), "local test update");
            await Call("disks.quiesce", new { id });
            await Call("replica.prepare", new { id }); staged = await Call("replica.stageCurrent", new { id, commit = latest.Commit });
            await Reject(async () => { await Call("replica.apply", Confirmation(id, staged)); }, "real NTFS dirty reset did not require confirmation");
            await Call("replica.apply", Confirmation(id, staged, true));
            await Call("disks.mount", new { id, readOnly = true });
            Check(!File.Exists(Path.Combine(mount, "local-only.txt")), "confirmed reset retained local-only file");
            Check((await File.ReadAllBytesAsync(Path.Combine(mount, "video.mp4"))).SequenceEqual(newBytes), "reset video hash");
            await Call("disks.quiesce", new { id });
            Check((await Call("snapshots.list", new { id })).GetProperty("items").GetArrayLength() == 1, "real NTFS switch lost manual snapshot");
            Check(!(await Call("cloud.status", new { id })).GetProperty("bound").GetBoolean(), "real mounted copy enabled cloud upload");
            Progress("All isolated NTFS files verified and safely unmounted");
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, networkUsed = false, isolatedContainersOnly = true,
                checks = new[] { "RO lazy mount and file checksum", "mounted update rejection", "manual unmount / atomic latest / manual remount", "unchanged file content", "RW change warning and explicit discard", "manual snapshot retained", "copy upload remains disabled" } }, Json));
            Console.WriteLine("REPLICA_MOUNT_SMOKE_OK"); return 0;
        }
        finally { await worker.ShutdownAsync(default); await source.UnmountAllAsync(); }
    }
}
