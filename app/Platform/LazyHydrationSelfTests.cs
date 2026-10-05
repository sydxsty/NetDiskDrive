using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using OverlayDisk.Cloud.Sync;
using OverlayDisk.Worker;

namespace OverlayDisk;

/// <summary>Offline object-provider fixtures. Any mounted volumes are created exclusively in output.</summary>
internal static class LazyHydrationSelfTests
{
    private const string Password = "OverlayDisk-lazy-fixture-only";
    private const int MiB = 1024 * 1024;
    private static void Check(bool value, string message) { if (!value) throw new IOException("Lazy fixture: " + message); }
    private static byte[] Pattern(int seed, int size) { var data = new byte[size]; new Random(seed).NextBytes(data); return data; }
    private static void PrepareOutput(string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("测试输出目录必须是新的或空目录。");
        Directory.CreateDirectory(output);
    }
    internal static async Task<int> RunAsync(string output)
    {
        PrepareOutput(output); var checks = (await HydrationTransportSelfTests.RunAsync()).ToList();
        string sourcePath = Path.Combine(output, "source.odv4"), targetPath = Path.Combine(output, "lazy.odv4");
        CoreDisk.Create(sourcePath, 64UL * MiB, Password);
        byte[] original = Pattern(8301, 16 * MiB), replacement = Pattern(8302, 4096), partial = Pattern(8303, 512);
        Archive archive;
        using (var source = new CoreDisk(sourcePath, Password))
        {
            for (int at = 0; at < original.Length; at += MiB) source.Write((ulong)at, original.AsSpan(at, MiB).ToArray(), MiB);
            source.Flush(); archive = Export(source);
        }
        var provider = new FixtureProvider(archive);
        Guid targetId;
        using (var target = CoreDisk.BeginLazyRestore(targetPath, archive.Root, Password, archive.Backing))
        {
            target.SetObjectProvider(provider.ReadAsync); FinishMetadata(target, archive);
            targetId = target.Id; Check(targetId != archive.SourceId, "lazy import inherited source identity");
            var initial = target.GetLazyStatus();
            Check(initial.GetProperty("missing_objects").GetUInt64() >= 3 && provider.Calls == 0, "metadata import downloaded file data");
            checks.Add("Metadata-only restore creates an independent target without downloading data objects.");

            byte[] first = new byte[4096], sameObject = new byte[4096];
            await Task.WhenAll(Task.Run(() => target.Read(0, first, first.Length)), Task.Run(() => target.Read(4096, sameObject, sameObject.Length)));
            Check(first.AsSpan().SequenceEqual(original.AsSpan(0, 4096)) && sameObject.AsSpan().SequenceEqual(original.AsSpan(4096, 4096)), "concurrent demand reads changed bytes");
            Check(provider.Calls == 1, "same native object downloaded more than once");
            target.Read(8192, first, first.Length); Check(provider.Calls == 1, "cached object fetched again");
            checks.Add("Concurrent native reads share one download and reuse its authenticated local object.");

            int fullOffset = 12 * MiB;
            Check(target.Control(new { cmd = "lazy.needs", offset = (ulong)fullOffset, length = 4096, operation = "write", limit = 2 }).GetProperty("items").GetArrayLength() == 0, "full-page overwrite requires old data");
            int beforeWrite = provider.Calls; target.Write((ulong)fullOffset, replacement, replacement.Length); target.Flush();
            Check(provider.Calls == beforeWrite, "full-page write fetched an obsolete remote page");
            replacement.CopyTo(original, fullOffset);

            int partialOffset = 8 * MiB + 512; target.Write((ulong)partialOffset, partial, partial.Length); target.Flush();
            partial.CopyTo(original, partialOffset); byte[] changed = new byte[4096]; target.Read(8UL * MiB, changed, changed.Length);
            Check(changed.AsSpan().SequenceEqual(original.AsSpan(8 * MiB, 4096)), "partial write lost untouched bytes");
            Check(provider.Calls == beforeWrite + 1, "partial write did not hydrate exactly its old object");
            checks.Add("Full 4 KiB overwrites avoid fetching old data; 512-byte writes hydrate and preserve the rest of the page.");

            var need = target.Control(new { cmd = "lazy.needs", offset = 15UL * MiB, length = 4096, operation = "read", limit = 2 }).GetProperty("items");
            Check(need.GetArrayLength() == 1, "error fixture object was unexpectedly eager-loaded");
            provider.FailId = need[0].GetProperty("id").GetString();
            bool failed = false;
            try { target.Read(15UL * MiB, first, first.Length); } catch (IOException) { failed = true; }
            Check(failed, "network failure was reported as a successful zero read");
            provider.FailId = null; target.Read(15UL * MiB, first, first.Length);
            Check(first.AsSpan().SequenceEqual(original.AsSpan(15 * MiB, 4096)), "temporary provider failure poisoned subsequent reads");
            Check(target.GetLazyStatus().GetProperty("missing_objects").GetUInt64() > 0, "small reads eagerly downloaded the whole disk");
            checks.Add("Unavailable remote data fails honestly; retry succeeds and unrelated objects remain uncached.");
        }
        int beforeReopen = provider.Calls;
        using (var reopened = new CoreDisk(targetPath, Password))
        {
            reopened.SetObjectProvider(provider.ReadAsync);
            foreach (int at in new[] { 0, 8 * MiB, 12 * MiB, 15 * MiB })
            {
                var read = new byte[4096]; reopened.Read((ulong)at, read, read.Length);
                Check(read.AsSpan().SequenceEqual(original.AsSpan(at, read.Length)), "reopened lazy cache or writes changed");
            }
            Check(provider.Calls == beforeReopen, "reopen downloaded an already persistent cached object");
        }
        checks.Add("Container reopen preserves downloaded objects, full-page overwrites and partial writes without more downloads.");
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks, targetId, providerRequests = provider.Calls, networkUsed = false }));
        Console.WriteLine("LAZY_OFFLINE_SMOKE_OK"); return 0;
    }

    internal static async Task<int> RunMountAsync(string output, bool original = false)
    {
        PrepareOutput(output);
        void Progress(string text) => File.AppendAllText(Path.Combine(output, "progress.log"), DateTimeOffset.Now.ToString("O") + " " + text + Environment.NewLine);
        using var sourceController = new DiskController(Path.Combine(output, "source-settings"));
        using var targetController = new DiskController(Path.Combine(output, "target-settings"));
        if (!sourceController.IsAdministrator || !sourceController.DriverAvailable) throw new IOException("专用 lazy 挂载测试需要管理员和已安装驱动。");
        char letter = Enumerable.Range('D', 23).Select(value => (char)value).Reverse().First(value => !DriveInfo.GetDrives().Any(d => d.Name[0] == value));
        byte[] video = Pattern(9101, 16 * MiB), untouched = Pattern(9102, 16 * MiB);
        string sourcePath = Path.Combine(output, "source.odv4"), targetPath = Path.Combine(output, "lazy.odv4");
        try
        {
            Progress("creating owned source NTFS volume");
            await sourceController.CreateAsync(new("Lazy-Source-Fixture", sourcePath, 256UL * MiB, letter, true), Password);
            var sourceEntry = sourceController.Disks.Single(); string root = letter + @":\";
            await File.WriteAllBytesAsync(Path.Combine(root, "video-fixture.mp4"), video);
            await File.WriteAllBytesAsync(Path.Combine(root, "untouched.bin"), untouched);
            await sourceController.FlushFileSystemAsync(sourceEntry);
            Progress("source saved; exporting immutable local fixture objects");
            var sourceCore = sourceController.TryGetCore(sourceEntry.Id)!;
            byte[] sourceMbr = new byte[512], sourceBoot = new byte[512];
            sourceCore.Read(0, sourceMbr, sourceMbr.Length); sourceCore.Read(1024 * 1024, sourceBoot, sourceBoot.Length);
            var archive = Export(sourceCore);
            ulong sourceGeneration = sourceCore.Control(new { cmd = "cloud.status" }).GetProperty("job").GetProperty("generation").GetUInt64();
            await sourceController.UnmountAllAsync();
            JsonElement originalOptions = default;
            if (original)
            {
                Progress("source safely closed; removing only the owned fixture source before original-identity import");
                await sourceController.DeleteAsync(sourceEntry, true, sourceEntry.Name);
                Check(!File.Exists(sourcePath) && sourceController.Disks.Count == 0, "owned source was not removed before original restore");
                Check(!await DiskProvisioner.HasMountedIdentityAsync(archive.SourceId, default), "source identity remains mounted after safe close");
                string remoteRoot = CloudRepository.RootPath(sourceEntry.Id), writer = Guid.NewGuid().ToString();
                string rootId = archive.Backing.GetProperty("root_object_id").GetString()!, rootSha = archive.Backing.GetProperty("root_sha256").GetString()!;
                var backing = new { provider_id = "offline-fixture", account_id = "fixture", source_volume_id = sourceEntry.Id,
                    remote_root = remoteRoot, root_object_id = rootId, root_sha256 = rootSha, reader_pin = remoteRoot + "/readers/fixture.json" };
                var publication = new
                {
                    commit = new RemoteCommit(4, sourceEntry.Id, writer, sourceGeneration, sourceEntry.Name, sourceEntry.CapacityBytes, sourceEntry.Encrypted, rootId, rootSha, DateTimeOffset.UnixEpoch),
                    binding = new { backend_id = "offline-fixture", account_id = "fixture", remote_root = remoteRoot, device_id = writer, enabled = true }
                };
                originalOptions = JsonSerializer.SerializeToElement(new { mode = "original", lazy = true, source_volume_id = sourceEntry.Id, backing, publication }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            var provider = new FixtureProvider(archive); ulong missingBeforeMount;
            using (var target = original ? CoreDisk.BeginRestore(targetPath, archive.Root, Password, originalOptions)
                : CoreDisk.BeginLazyRestore(targetPath, archive.Root, Password, archive.Backing))
            {
                target.SetObjectProvider(provider.ReadAsync); FinishMetadata(target, archive);
                missingBeforeMount = target.GetLazyStatus().GetProperty("missing_objects").GetUInt64();
                Check(missingBeforeMount > 0 && provider.Calls == 0, "lazy NTFS creation eagerly loaded data");
                byte[] mbr = new byte[512]; target.Read(0, mbr, mbr.Length);
                Check(mbr[510] == 0x55 && mbr[511] == 0xAA, "fixture MBR signature");
                if (original)
                {
                    Check(target.Id == archive.SourceId && mbr.SequenceEqual(sourceMbr), "original NTFS target changed UUID or MBR identity");
                    byte[] boot = new byte[512]; target.Read(1024 * 1024, boot, boot.Length);
                    Check(boot.SequenceEqual(sourceBoot), "original NTFS boot sector or volume serial changed");
                    Progress("original UUID, MBR signature and NTFS boot sector preserved");
                }
                else
                {
                    Check(target.Id != archive.SourceId, "independent NTFS target retained source UUID");
                    target.Id.ToByteArray().AsSpan(0, 4).CopyTo(mbr.AsSpan(440, 4)); target.Write(0, mbr, mbr.Length); target.Flush();
                }
            }
            targetController.CoreInitializer = core => core.SetObjectProvider(provider.ReadAsync);
            await targetController.ImportAsync(targetPath); var entry = targetController.Disks.Single();
            await targetController.UpdateSettingsAsync(entry, "Lazy-Target-Fixture", letter);
            Progress("mounting metadata-only target; NTFS reads now fetch missing objects");
            await targetController.MountAsync(entry, Password);
            var mountedStatus = targetController.TryGetCore(entry.Id)!.GetLazyStatus();
            Check(mountedStatus.GetProperty("missing_objects").GetUInt64() > 0, "mount downloaded every file object");
            string file = Path.Combine(letter + @":\", "video-fixture.mp4");
            Check(SHA256.HashData(await File.ReadAllBytesAsync(file)).SequenceEqual(SHA256.HashData(video)), "lazy mounted video hash mismatch");
            Progress("lazy file hash verified; performing partial file write");
            byte[] patch = Pattern(9103, 512);
            await using (var writable = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None))
            { writable.Position = 12345; await writable.WriteAsync(patch); writable.Flush(true); }
            patch.CopyTo(video, 12345);
            Check(SHA256.HashData(await File.ReadAllBytesAsync(file)).SequenceEqual(SHA256.HashData(video)), "lazy NTFS partial write lost bytes");
            await targetController.UnmountAllAsync();
            Progress("target safely closed; remounting cached container");
            await targetController.MountAsync(entry, Password);
            Check(SHA256.HashData(await File.ReadAllBytesAsync(file)).SequenceEqual(SHA256.HashData(video)), "lazy file hash changed after reopen");
            await targetController.UnmountAllAsync();
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, mode = original ? "original" : "copy", sourceId = archive.SourceId, entry.Id, missingBeforeMount,
                missingAfterMount = mountedStatus.GetProperty("missing_objects").GetUInt64(), providerRequests = provider.Calls, networkUsed = false,
                tests = new[] { "owned 256 MiB NTFS source", original ? "owned source removed; original UUID, MBR and NTFS boot identity retained" : "metadata-only independent target", "real mount hydrates a subset", "video hash", "partial file write", "cached container remount", "all owned disks safely unmounted" } }));
            Progress("all lazy NTFS assertions passed"); Console.WriteLine(original ? "ORIGINAL_MOUNT_SMOKE_OK" : "LAZY_MOUNT_SMOKE_OK"); return 0;
        }
        finally { await targetController.UnmountAllAsync(); await sourceController.UnmountAllAsync(); Progress("final safe cleanup completed"); }
    }

    internal sealed record Archive(Guid SourceId, byte[] Root, JsonElement Backing, IReadOnlyDictionary<string, byte[]> Objects, IReadOnlyDictionary<string, string> Hashes);
    private sealed class FixtureProvider(Archive archive)
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        internal string? FailId;
        internal Task<byte[]> ReadAsync(LazyObjectRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); request.Validate(); Interlocked.Increment(ref calls);
            if (request.ObjectId == Volatile.Read(ref FailId)) throw new IOException("Simulated unavailable immutable cloud object.");
            Check(archive.Hashes.TryGetValue(request.ObjectId, out var hash) && hash.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase), "unrecognized object request");
            return Task.FromResult(archive.Objects[request.ObjectId].ToArray());
        }
    }
    internal static Archive Export(CoreDisk source)
    {
        var prepared = source.Control(new { cmd = "cloud.prepare" });
        for (int steps = 0; steps < 20000; steps++)
        {
            var job = prepared.GetProperty("job"); string id = job.GetProperty("id").GetString()!;
            if (job.GetProperty("phase").GetString() == "ready")
            {
                var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal); var hashes = new Dictionary<string, string>(StringComparer.Ordinal); ulong cursor = 0;
                for (int pages = 0; pages < 2048; pages++)
                {
                    var list = source.Control(new { cmd = "cloud.list", job_id = id, cursor, limit = 128 });
                    foreach (var item in list.GetProperty("items").EnumerateArray())
                    {
                        string objectId = item.GetProperty("id").GetString()!, hash = item.GetProperty("sha256").GetString()!;
                        byte[] raw = source.ReadExport(id, objectId); Check(Convert.ToHexString(SHA256.HashData(raw)).Equals(hash, StringComparison.OrdinalIgnoreCase), "fixture export SHA");
                        objects.Add(objectId, raw); hashes.Add(objectId, hash);
                    }
                    if (list.GetProperty("next_cursor").ValueKind == JsonValueKind.Null)
                    {
                        string rootId = job.GetProperty("root_object_id").GetString()!, remoteRoot = "/OverlayDisk-fixture/" + source.Id;
                        var backing = JsonSerializer.SerializeToElement(new { provider_id = "offline-fixture", account_id = "fixture", source_volume_id = source.Id.ToString(), remote_root = remoteRoot,
                            root_object_id = rootId, root_sha256 = hashes[rootId], reader_pin = remoteRoot + "/readers/fixture.json" });
                        return new(source.Id, objects[rootId], backing, objects, hashes);
                    }
                    ulong next = list.GetProperty("next_cursor").GetUInt64(); Check(next > cursor, "object pagination did not advance"); cursor = next;
                }
                throw new IOException("Fixture object pagination exceeded limit.");
            }
            prepared = source.Control(new { cmd = "cloud.prepare", job_id = id, max_pages = 512, max_objects = 4 });
        }
        throw new IOException("Fixture export preparation exceeded limit.");
    }
    internal static void FinishMetadata(CoreDisk target, Archive archive)
    {
        for (int step = 0; step < 20000; step++)
        {
            var status = target.Control(new { cmd = "restore.status", cursor = 0, limit = 128 });
            if (status.GetProperty("phase").GetString() is "ready" or "complete") { target.Control(new { cmd = "restore.finish" }); return; }
            foreach (var item in status.GetProperty("needed").EnumerateArray())
            {
                Check(item.GetProperty("kind").GetString() == "metadata", "lazy restore requested eager file data");
                target.AcceptRestoreObject(item.GetProperty("id").GetString()!, archive.Objects[item.GetProperty("id").GetString()!]);
            }
            target.Control(new { cmd = "restore.step", max_pages = 256 });
        }
        throw new IOException("Fixture metadata restore exceeded limit.");
    }
}
