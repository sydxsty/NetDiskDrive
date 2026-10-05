using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using OverlayDisk.Cloud.Sync;
using OverlayDisk.Services;
using OverlayDisk.Worker;

namespace OverlayDisk;

internal static class RestoreModeSelfTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonElement E(object? value) => value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, Json);
    private static void Check(bool value, string message) { if (!value) throw new IOException("Restore-mode fixture: " + message); }
    private static async Task Reject(Func<Task> action, string message)
    {
        bool failed = false;
        try { await action(); } catch (IOException) { failed = true; }
        Check(failed, message);
    }
    private static void PrepareOutput(string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("测试目录必须是新的或空目录。");
        Directory.CreateDirectory(output);
    }
    internal static async Task<int> RunAsync(string output)
    {
        PrepareOutput(output); var checks = new List<string>();
        await PreflightAsync(Path.Combine(output, "guards"));
        checks.Add("Original-mode preflight rejects stale catalogs, in-process identities and external mounted identities before target creation; copy mode remains available.");
        Guid sourceId; ulong generation;
        byte[] mbr = new byte[4096], boot = new byte[4096];
        new Random(431).NextBytes(mbr); new Random(433).NextBytes(boot);
        Array.Clear(mbr, 446, 64); mbr[450] = 7; mbr[510] = 0x55; mbr[511] = 0xAA;
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(454, 4), 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(458, 4), 63 * 1024 * 1024 / 512);
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(440, 4), 0x13572468);
        BinaryPrimitives.WriteUInt64LittleEndian(boot.AsSpan(72, 8), 0x1122334455667788);
        // These are raw fixture sectors, not a filesystem mounted on Windows.
        boot[510] = 0; boot[511] = 0;
        string sourcePath = Path.Combine(output, "source.odv4"); CoreDisk.Create(sourcePath, 64UL * 1024 * 1024, null);
        LazyHydrationSelfTests.Archive archive;
        using (var source = new CoreDisk(sourcePath, null))
        {
            sourceId = source.Id; source.Write(0, mbr, mbr.Length); source.Write(1024 * 1024, boot, boot.Length); source.Flush();
            archive = LazyHydrationSelfTests.Export(source);
            generation = source.Control(new { cmd = "cloud.status" }).GetProperty("job").GetProperty("generation").GetUInt64();
        }
        string remoteRoot = CloudRepository.RootPath(sourceId.ToString()), writer = Guid.NewGuid().ToString();
        string rootId = archive.Backing.GetProperty("root_object_id").GetString()!, rootHash = archive.Backing.GetProperty("root_sha256").GetString()!;
        var binding = new { backend_id = "restore-mode-fixture", account_id = "fixture", remote_root = remoteRoot, device_id = writer, enabled = true };
        var commit = new RemoteCommit(4, sourceId.ToString(), writer, generation, "mode fixture", 64UL * 1024 * 1024, false, rootId, rootHash, DateTimeOffset.UnixEpoch);
        var publication = new { commit, binding };
        var backing = new { provider_id = "restore-mode-fixture", account_id = "fixture", source_volume_id = sourceId.ToString(), remote_root = remoteRoot,
            root_object_id = rootId, root_sha256 = rootHash, reader_pin = remoteRoot + "/readers/fixture.json" };
        string originalPath = Path.Combine(output, "original.odv4"), copyPath = Path.Combine(output, "copy.odv4");
        string workerSettings = Path.Combine(output, "worker-settings");
        var worker = new WorkerApplicationService(workerSettings, (_, _) => Task.FromResult(false));
        int fetches = 0;
        Task<byte[]> Provider(LazyObjectRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref fetches);
            Check(archive.Hashes[request.ObjectId].Equals(request.Sha256, StringComparison.OrdinalIgnoreCase), "unrecognized immutable object");
            return Task.FromResult(archive.Objects[request.ObjectId].ToArray());
        }
        worker.SetObjectProvider(Provider);
        JsonElement Args(string mode, string path, bool resume = false) => E(new { name = Path.GetFileNameWithoutExtension(path), path, mode, sourceVolumeId = sourceId,
            lazy = mode == "original", resume, backing = mode == "original" ? (object)backing : null, publication = mode == "original" ? (object)publication : null });
        try
        {
            var original = await BeginAsync(worker, Args("original", originalPath), archive.Root);
            Check(original == sourceId.ToString(), "original restore created a new container identity");
            string simultaneousPath = Path.Combine(output, "simultaneous-original.odv4");
            await Reject(async () => { await BeginAsync(worker, Args("original", simultaneousPath), archive.Root); }, "active original restore allowed a second container with the same identity");
            Check(!File.Exists(simultaneousPath), "active restore duplicate created a target before rejecting it");
            // Force only the isolated catalog save to fail after native restore.finish.
            // Keep the desired name equal to the filename so a rename cannot accidentally
            // hide a phantom in-memory entry by causing an unrelated successful save.
            string blockedCatalogTemp = Path.Combine(workerSettings, "disks.json.tmp");
            Directory.CreateDirectory(blockedCatalogTemp);
            bool catalogSaveFailed = false;
            try { await FinishAsync(worker, original, archive, metadataOnly: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { catalogSaveFailed = true; }
            finally { Directory.Delete(blockedCatalogTemp); }
            Check(catalogSaveFailed, "catalog fault did not reject finish");
            Check(E(await worker.InvokeAsync("state", E(new { }), default)).GetProperty("disks").GetArrayLength() == 0,
                "failed catalog save published a phantom in-memory disk");
            Check(!File.Exists(Path.Combine(workerSettings, "disks.json")), "failed catalog write unexpectedly published a catalog");
            using (var complete = new CoreDisk(originalPath, null))
                Check(complete.Control(new { cmd = "restore.status" }).GetProperty("phase").GetString() == "complete", "fault happened before the intended durable native finish");
            await BeginAsync(worker, Args("original", originalPath, true), archive.Root);
            var progress = await FinishAsync(worker, original, archive, metadataOnly: true);
            using (var reloadedCatalog = new DiskController(workerSettings))
                Check(reloadedCatalog.Disks.Count == 1 && reloadedCatalog.Disks[0].Id == original
                    && reloadedCatalog.Disks[0].Name == Path.GetFileNameWithoutExtension(originalPath)
                    && reloadedCatalog.Disks[0].ContainerPath == Path.GetFullPath(originalPath), "finish retry did not persist the exact target catalog entry");
            checks.Add("A catalog-write fault after durable native finish leaves no phantom entry; retry persists the completed original even when its name already matches the filename.");
            Check(fetches == 0, "original finish read or rewrote the MBR instead of preserving it");
            using (var disk = new CoreDisk(originalPath, null))
            {
                disk.SetObjectProvider(Provider); byte[] actual = new byte[4096]; disk.Read(0, actual, actual.Length);
                Check(actual.SequenceEqual(mbr), "original mode changed MBR bytes or disk signature");
                disk.Read(1024 * 1024, actual, actual.Length); Check(actual.SequenceEqual(boot), "original mode changed boot-sector/volume-serial bytes");
                var cloud = disk.Control(new { cmd = "cloud.status" });
                Check(cloud.GetProperty("published_generation").GetUInt64() == generation && cloud.GetProperty("binding").GetProperty("device_id").GetString() == writer,
                    "original mode lost the published baseline or original writer");
                var next = disk.Control(new { cmd = "cloud.prepare" });
                Check(next.TryGetProperty("up_to_date", out var unchanged) && unchanged.GetBoolean(), "unchanged original disk needs a new upload");
            }
            checks.Add("Original lazy restore retains the source UUID and all MBR/boot bytes; finish fetches no MBR data and unchanged sync reuses the original published baseline.");

            // The native finish and catalog import succeeded, but a GUI completion record
            // could have been lost. The exact closed target must remain retryable.
            await BeginAsync(worker, Args("original", originalPath, true), archive.Root);
            await worker.InvokeAsync("restore.finish", E(new { id = original }), default);
            await worker.InvokeAsync("disks.unlock", E(new { id = original }), default);
            await Reject(async () => { await BeginAsync(worker, Args("original", originalPath, true), archive.Root); }, "resume accepted an unlocked catalog target");
            await worker.InvokeAsync("disks.close", E(new { id = original }), default);
            await Reject(async () => { await BeginAsync(worker, Args("copy", originalPath, true), archive.Root); }, "resume silently changed original into copy");
            await Reject(async () => { await BeginAsync(worker, E(new { name = "original fixture", path = originalPath, mode = "original", sourceVolumeId = sourceId,
                lazy = true, resume = true, backing, publication = new { commit = commit with { Generation = generation + 1 }, binding } }), archive.Root); }, "resume changed the published generation");
            byte[] corruptRoot = archive.Root.ToArray(); corruptRoot[^1] ^= 1;
            await Reject(async () => { await BeginAsync(worker, Args("original", originalPath, true), corruptRoot); }, "resume accepted a different fixed root");
            await Reject(async () => { await worker.InvokeAsync("restore.preflight", E(new { mode = "original", sourceVolumeId = sourceId, path = Path.Combine(output, "duplicate.odv4") }), default); }, "new original bypassed completed catalog duplicate");
            Check(!File.Exists(Path.Combine(output, "duplicate.odv4")), "duplicate preflight created a target");
            checks.Add("Completed original import can finish idempotently after a lost GUI acknowledgment; changing mode/root or starting another original is rejected.");

            var copy = await BeginAsync(worker, Args("copy", copyPath), archive.Root);
            Check(Guid.Parse(copy) != sourceId, "copy restore retained source identity");
            await FinishAsync(worker, copy, archive, metadataOnly: false);
            using (var disk = new CoreDisk(copyPath, null))
            {
                byte[] actual = new byte[4096]; disk.Read(0, actual, actual.Length);
                byte[] expected = mbr.ToArray(); disk.Id.ToByteArray().AsSpan(0, 4).CopyTo(expected.AsSpan(440, 4));
                Check(actual.SequenceEqual(expected), "copy mode did not change only its MBR signature");
                disk.Read(1024 * 1024, actual, actual.Length); Check(actual.SequenceEqual(boot), "copy mode modified unrelated boot-sector bytes");
                Check(!disk.Control(new { cmd = "cloud.status" }).GetProperty("bound").GetBoolean(), "copy inherited original cloud writer binding");
            }
            checks.Add("Copy mode has a new UUID and MBR signature, preserves other sector bytes and does not inherit writer ownership.");
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks, sourceId, copyId = copy,
                originalId = original, originalFinishDownloads = 0, progress, networkUsed = false, mountedWindowsVolumes = false }, Json));
            Console.WriteLine("RESTORE_MODE_SMOKE_OK"); return 0;
        }
        finally { await worker.ShutdownAsync(default); }
    }

    private static async Task PreflightAsync(string output)
    {
        Directory.CreateDirectory(output); Guid id = Guid.NewGuid();
        string target = Path.Combine(output, "new.odv4"), catalog = Path.Combine(output, "stale-settings"); Directory.CreateDirectory(catalog);
        var stale = new DiskEntry { Id = id.ToString(), Name = "missing source", ContainerPath = Path.Combine(output, "missing.odv4"), CapacityBytes = 64UL * 1024 * 1024 };
        File.WriteAllText(Path.Combine(catalog, "disks.json"), JsonSerializer.Serialize(new[] { stale }));
        int staleProbeCalls = 0;
        var worker = new WorkerApplicationService(catalog, (_, _) => { staleProbeCalls++; return Task.FromResult(false); });
        try
        {
            await Reject(async () => { await worker.InvokeAsync("restore.preflight", E(new { mode = "original", sourceVolumeId = id, path = target }), default); }, "stale catalog was silently discarded");
            Check(staleProbeCalls == 0 && !File.Exists(target), "stale duplicate reached the driver probe or created a target");
            await worker.InvokeAsync("restore.preflight", E(new { mode = "copy", sourceVolumeId = id, path = target }), default);
        }
        finally { await worker.ShutdownAsync(default); }
        int externalCalls = 0;
        var external = new WorkerApplicationService(Path.Combine(output, "empty-settings"), (source, _) => { Check(source == id, "driver probe received another identity"); externalCalls++; return Task.FromResult(true); });
        try
        {
            await Reject(async () => { await external.InvokeAsync("restore.preflight", E(new { mode = "original", sourceVolumeId = id, path = target }), default); }, "external mounted identity was ignored");
            Check(externalCalls == 1, "mounted identity was not checked exactly once");
            await ActiveDisks.Gate.WaitAsync(); try { ActiveDisks.Items.Add(id, new(64UL * 1024 * 1024)); } finally { ActiveDisks.Gate.Release(); }
            try
            {
                await Reject(async () => { await external.InvokeAsync("restore.preflight", E(new { mode = "original", sourceVolumeId = id, path = target }), default); }, "in-process registered identity was ignored");
                Check(externalCalls == 1, "in-process duplicate did not stop before the external probe");
            }
            finally { await ActiveDisks.Gate.WaitAsync(); try { ActiveDisks.Items.Remove(id); } finally { ActiveDisks.Gate.Release(); } }
            File.WriteAllBytes(target, [1]);
            await Reject(async () => { await external.InvokeAsync("restore.preflight", E(new { mode = "copy", sourceVolumeId = id, path = target }), default); }, "existing target was accepted");
            Check(File.ReadAllBytes(target).SequenceEqual(new byte[] { 1 }), "preflight changed the existing target");
        }
        finally { await external.ShutdownAsync(default); }
        var status = E(new { kind = "cloud", mode = "original", source_volume_id = id });
        Check(WorkerApplicationService.ValidateCloudRestoreIdentity(id, status, "original", id) == id, "original identity rejected");
        await Reject(() => Task.Run(() => WorkerApplicationService.ValidateCloudRestoreIdentity(Guid.NewGuid(), status, "original", id)), "original target ID mismatch accepted");
        await Reject(() => Task.Run(() => WorkerApplicationService.ValidateCloudRestoreIdentity(id, status, "copy", id)), "persisted mode mismatch accepted");
    }
    private static async Task<string> BeginAsync(WorkerApplicationService worker, JsonElement args, byte[] root)
        => E((await worker.InvokeBulkAsync("restore.begin", args, root, default)).Data).GetProperty("id").GetString()!;
    private static async Task<JsonElement> FinishAsync(WorkerApplicationService worker, string id, LazyHydrationSelfTests.Archive archive, bool metadataOnly)
    {
        for (int step = 0; step < 4096; step++)
        {
            var status = E(await worker.InvokeAsync("restore.status", E(new { id, cursor = 0, limit = 128 }), default));
            if (status.GetProperty("phase").GetString() is "ready" or "complete")
            {
                await worker.InvokeAsync("restore.finish", E(new { id }), default); return status;
            }
            foreach (var item in status.GetProperty("needed").EnumerateArray())
            {
                if (metadataOnly) Check(item.GetProperty("kind").GetString() == "metadata", "lazy original restore requested eager data");
                string objectId = item.GetProperty("id").GetString()!;
                await worker.InvokeBulkAsync("restore.accept", E(new { id, object_id = objectId }), archive.Objects[objectId], default);
            }
            await worker.InvokeAsync("restore.step", E(new { id, max_nodes = 256, max_objects = 4 }), default);
        }
        throw new IOException("Restore mode fixture exceeded bounded checkpoint steps.");
    }
}
