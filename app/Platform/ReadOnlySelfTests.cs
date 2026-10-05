using System.Security.Cryptography;
using System.Text.Json;
using OverlayDisk.Worker;

namespace OverlayDisk;

internal static class ReadOnlySelfTests
{
    private static void Check(bool value, string message) { if (!value) throw new IOException("Read-only fixture: " + message); }
    private static async Task Reject(Func<Task> action, string message)
    {
        try { await action(); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
        throw new IOException("Read-only fixture accepted: " + message);
    }
    private static void Prepare(string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("测试目录必须为空。");
        Directory.CreateDirectory(output);
    }
    internal static async Task<int> RunAsync(string output)
    {
        Prepare(output); var checks = DiskIdentityRewriterSelfTests.Run().ToList();
        await QuotaLoopAsync(); checks.Add("Quota maintenance respects online/source/limit gates and joins an in-flight bounded step before close.");
        string sourcePath = Path.Combine(output, "source.odv4"), targetPath = Path.Combine(output, "readonly.odv4");
        byte[] data = new byte[4096]; new Random(600).NextBytes(data); LazyHydrationSelfTests.Archive archive;
        CoreDisk.Create(sourcePath, 64UL << 20, null);
        using (var source = new CoreDisk(sourcePath, null)) { source.Write(0, data, data.Length); source.Flush(); archive = LazyHydrationSelfTests.Export(source); }
        int fetched = 0;
        using (var target = CoreDisk.BeginLazyRestore(targetPath, archive.Root, null, archive.Backing))
        {
            target.SetObjectProvider((request, ct) => { ct.ThrowIfCancellationRequested(); fetched++; return Task.FromResult(archive.Objects[request.ObjectId]); });
            LazyHydrationSelfTests.FinishMetadata(target, archive); target.SetReadOnly(true);
            byte[] actual = new byte[4096]; target.Read(0, actual, actual.Length); Check(actual.SequenceEqual(data) && fetched == 1, "RO hydration did not persist correct data");
            await Reject(() => Task.Run(() => target.Write(0, data, data.Length)), "managed write");
            await Reject(() => Task.Run(() => target.Trim(0, 4096)), "managed TRIM");
            await Reject(() => Task.Run(() => target.Submit(2, 0, 512, IntPtr.Zero, 0, 900)), "async write submission");
            var snapshot = target.Control(new { cmd = "snapshot.create", name = "RO management fixture" });
            string snapshotId = snapshot.GetProperty("snapshot").GetProperty("id").GetString()!;
            target.Control(new { cmd = "snapshot.delete", id = snapshotId }); target.Flush();
            target.Read(0, actual, actual.Length); Check(actual.SequenceEqual(data) && fetched == 1, "read cache or snapshot management modified guest data");
            VerifyPublishedPhysicalMaintenance(target);
            target.SetReadOnly(false); data[17] ^= 0xFF; target.Write(0, data, data.Length); target.Flush(); target.Read(0, actual, actual.Length);
            Check(actual.SequenceEqual(data), "switch back to RW failed");
        }
        checks.Add("Native-backed RO core allows authenticated hydration, snapshots and both physical compaction modes, rejects guest write/TRIM, and switches back to RW.");
        checks.Add("After mock-ack publication, normal and deep compaction preserve data generation, changed pages and pending objects; the next prepare stays up to date.");
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks, networkUsed = false, mountedWindowsVolumes = false }));
        Console.WriteLine("READONLY_SMOKE_OK"); return 0;
    }
    internal static async Task<int> RunMountAsync(string output, bool large = false)
    {
        Prepare(output); void Log(string text) => File.AppendAllText(Path.Combine(output, "progress.log"), DateTimeOffset.Now.ToString("O") + " " + text + Environment.NewLine);
        using var controller = new DiskController(Path.Combine(output, "settings"));
        if (!controller.IsAdministrator || !controller.DriverAvailable) throw new IOException("专用只读挂载测试需要管理员及驱动。");
        char letter = Enumerable.Range('D', 23).Select(value => (char)value).Reverse().First(value => !DriveInfo.GetDrives().Any(d => d.Name[0] == value));
        string path = Path.Combine(output, "owned.odv4"), file = Path.Combine(letter + @":\", "owned-readonly-fixture.bin");
        ulong capacity = large ? 4UL << 40 : 256UL << 20;
        byte[] bytes = new byte[65536]; new Random(601).NextBytes(bytes);
        try
        {
            Log("creating and formatting owned disk in RW, then remounting requested RO");
            await controller.CreateAsync(new("RO-Fixture", path, capacity, letter, false, true), null);
            var entry = controller.Disks.Single(); Check(entry.ReadOnly && controller.TryGetCore(entry.Id)!.IsReadOnly, "created mount is not RO");
            await Reject(() => File.WriteAllBytesAsync(file, bytes), "Windows file creation on RO disk");
            await Reject(async () => { await DiskProvisioner.PrepareNewAsync(Guid.Parse(entry.Id), capacity, letter, entry.Name); }, "formatting a RO mount");
            await Reject(async () => { await controller.ReclaimFileSystemAsync(entry); }, "manual TRIM on RO mount");
            await Reject(() => controller.MountAsync(entry, null, false), "changing mode while mounted");
            await controller.UnmountAllAsync();
            Log("mounting RW to write fixture contents");
            await controller.MountAsync(entry, null, false); await File.WriteAllBytesAsync(file, bytes); await controller.FlushFileSystemAsync(entry); await controller.UnmountAllAsync();
            await controller.UpdateSettingsAsync(entry, entry.Name, letter, true);
            using (var catalog = new DiskController(Path.Combine(output, "settings"))) Check(catalog.Disks.Single().ReadOnly, "RO preference did not persist");
            Log("mounting saved RO preference and verifying reads and write rejection");
            await controller.MountAsync(entry, null);
            var core = controller.TryGetCore(entry.Id)!;
            ulong readGeneration = core.GetInfo().GetProperty("data_generation").GetUInt64();
            ulong readChangedPages = core.Control(new { cmd = "blocks.summary" }).GetProperty("changed_pages").GetUInt64();
            Check(SHA256.HashData(await File.ReadAllBytesAsync(file)).SequenceEqual(SHA256.HashData(bytes)), "RO read hash");
            byte[] table = new byte[512]; core.Read(512, table, table.Length);
            if (large) Check(table.AsSpan(0, 8).SequenceEqual("EFI PART"u8), "large disk did not use GPT");
            await Reject(() => File.WriteAllBytesAsync(file, [1, 2, 3]), "Windows overwrite on RO disk");
            await Reject(() => Task.Run(() => core.Write(0, new byte[512], 512)), "direct guest-sector write on RO core");
            await controller.FlushFileSystemAsync(entry);
            Check(SHA256.HashData(await File.ReadAllBytesAsync(file)).SequenceEqual(SHA256.HashData(bytes)), "failed write changed file");
            Check(core.GetInfo().GetProperty("data_generation").GetUInt64() == readGeneration
                && core.Control(new { cmd = "blocks.summary" }).GetProperty("changed_pages").GetUInt64() == readChangedPages,
                "read-only NTFS read or rejected write changed guest pages");
            if (!large)
            {
                Log("checking read-only physical maintenance against a mock-ack publication baseline");
                VerifyPublishedPhysicalMaintenance(core);
                Check(SHA256.HashData(await File.ReadAllBytesAsync(file)).SequenceEqual(SHA256.HashData(bytes)), "physical maintenance changed mounted file content");
            }
            await controller.UnmountAllAsync();
            Log("switching to RW, changing bytes and verifying after a full reopen");
            await controller.MountAsync(entry, null, false); bytes[12345] ^= 0x5A; await File.WriteAllBytesAsync(file, bytes);
            await controller.FlushFileSystemAsync(entry); await controller.UnmountAllAsync(); await controller.MountAsync(entry, null);
            Check(SHA256.HashData(await File.ReadAllBytesAsync(file)).SequenceEqual(SHA256.HashData(bytes)), "RW reopen hash");
            await controller.UnmountAllAsync();
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, capacity, gpt = large, entry.Id, physicalPublicationBaselineChecked = !large,
                tests = new[] { "owned new disk only", "create RO uses temporary RW format", "driver file writes rejected", "guest write/TRIM/format guards", "mode persists and mounted switch rejected", "read SHA unchanged", "RW write and reopen SHA", "safe unmount" } }));
            Log("all owned RO/RW assertions passed"); Console.WriteLine("READONLY_MOUNT_SMOKE_OK"); return 0;
        }
        finally { await controller.UnmountAllAsync(); Log("owned disks safely closed"); }
    }
    private static void VerifyPublishedPhysicalMaintenance(CoreDisk core)
    {
        Check(core.IsReadOnly, "physical maintenance fixture must remain read-only");
        core.Control(new { cmd = "cloud.bind", backend_id = "physical-fixture", account_id = "fixture", remote_root = "/OverlayDisk/" + core.Id, device_id = Guid.NewGuid().ToString(), enabled = true });
        var prepared = core.Control(new { cmd = "cloud.prepare" });
        for (int step = 0; step < 512 && prepared.GetProperty("job").GetProperty("phase").GetString() != "ready"; step++)
            prepared = core.Control(new { cmd = "cloud.prepare", job_id = prepared.GetProperty("job").GetProperty("id").GetString(), max_pages = 256, max_objects = 4 });
        var job = prepared.GetProperty("job"); Check(job.GetProperty("phase").GetString() == "ready", "bounded mock publication preparation");
        string id = job.GetProperty("id").GetString()!; ulong cursor = 0;
        for (int page = 0; page < 128; page++)
        {
            var objects = core.Control(new { cmd = "cloud.list", job_id = id, cursor, limit = 128 });
            foreach (var item in objects.GetProperty("items").EnumerateArray())
            {
                string objectId = item.GetProperty("id").GetString()!;
                var bytes = core.ReadExport(id, objectId);
                Check(Convert.ToHexString(SHA256.HashData(bytes)).Equals(item.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "mock publication object hash");
                core.Control(new { cmd = "cloud.receipt", job_id = id, object_id = objectId, sha256 = item.GetProperty("sha256").GetString(), length = bytes.Length, receipt = "fixture-ack" });
            }
            if (objects.GetProperty("next_cursor").ValueKind == JsonValueKind.Null) break;
            ulong next = objects.GetProperty("next_cursor").GetUInt64(); Check(next > cursor && page != 127, "bounded publication object pages"); cursor = next;
        }
        core.Control(new { cmd = "cloud.commit", job_id = id, root_object_id = job.GetProperty("root_object_id").GetString(), root_sha256 = job.GetProperty("root_sha256").GetString(), receipt = "fixture-publication-ack" });
        var before = core.Control(new { cmd = "blocks.summary" }); ulong generation = core.GetInfo().GetProperty("data_generation").GetUInt64();
        Check(before.GetProperty("pending_objects").GetUInt64() == 0, "mock publication baseline remains pending");
        foreach (string mode in new[] { "normal", "deep" })
        {
            var progress = core.Control(new { cmd = "compact.start", mode });
            for (int step = 0; step < 512 && progress.GetProperty("state").GetString() is not ("done" or "idle"); step++)
                progress = core.Control(new { cmd = "compact.step", max_objects = 4 });
            Check(progress.GetProperty("state").GetString() is "done" or "idle", "bounded " + mode + " maintenance");
            var after = core.Control(new { cmd = "blocks.summary" });
            Check(core.GetInfo().GetProperty("data_generation").GetUInt64() == generation
                && after.GetProperty("changed_pages").GetUInt64() == before.GetProperty("changed_pages").GetUInt64()
                && after.GetProperty("pending_objects").GetUInt64() == before.GetProperty("pending_objects").GetUInt64(), mode + " physical maintenance created pending guest changes");
            var next = core.Control(new { cmd = "cloud.prepare" });
            Check(next.GetProperty("job").ValueKind == JsonValueKind.Null && next.GetProperty("up_to_date").GetBoolean(), mode + " physical maintenance requires another upload");
        }
    }
    private static async Task QuotaLoopAsync()
    {
        JsonElement State(bool online = true, bool source = true, ulong max = 1, bool origin = false) => JsonSerializer.SerializeToElement(new { max_bytes = max, online, source_ready = source, origin_ready = origin, over_limit_bytes = 1, evicted_bytes = 0, more_work = true });
        Check(DiskCacheQuotaLoop.Eligible(State()) && DiskCacheQuotaLoop.Eligible(State(source: false, origin: true))
            && !DiskCacheQuotaLoop.Eligible(State(false)) && !DiskCacheQuotaLoop.Eligible(State(source: false)) && !DiskCacheQuotaLoop.Eligible(State(max: 0)), "quota gates");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
        var loop = new DiskCacheQuotaLoop(() => State(), () => { entered.TrySetResult(); release.Wait(); return State(false); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var stopping = loop.StopAsync(); Check(!stopping.IsCompleted, "close failed to join active maintenance");
        release.Set(); await stopping.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
