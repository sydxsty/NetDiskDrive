using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace OverlayDisk;

internal static class SelfTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static byte[] Pattern(int seed, int size) { var bytes = new byte[size]; new Random(seed).NextBytes(bytes); return bytes; }
    private static void PrepareOutput(string directory)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("测试目录必须是新的或空目录。");
        Directory.CreateDirectory(directory);
    }

    public static int RunCore(string output)
    {
        PrepareOutput(output);
        var passed = new List<string>();
        passed.AddRange(PlatformSelfTests.RunAsync().GetAwaiter().GetResult());
        passed.AddRange(Services.LazySourcePolicySelfTests.Run());
        foreach (bool encrypted in new[] { false })
        {
            string containerDirectory = Path.Combine(output, encrypted ? "encrypted" : "plain");
            Directory.CreateDirectory(containerDirectory);
            string path = Path.Combine(containerDirectory, "disk.odv4");
            string? password = encrypted ? "OverlayDisk-Windows-smoke-测试" : null;
            CoreDisk.Create(path, 64UL * 1024 * 1024, password);
            Guid id;
            using (var disk = new CoreDisk(path, password))
            {
                id = disk.Id;
                Check(disk.Encrypted == encrypted, "加密模式不一致");
                var zeros = new byte[8192];
                disk.Read(0, zeros, zeros.Length);
                Check(zeros.All(x => x == 0), "未分配范围必须读零");
                Parallel.For(0, 12, thread =>
                {
                    for (int page = 0; page < 8; page++)
                    {
                        ulong offset = 4096UL * (ulong)(thread * 8 + page);
                        byte[] data = Pattern(thread * 8 + page, 4096);
                        disk.Write(offset, data, data.Length);
                        byte[] got = new byte[data.Length]; disk.Read(offset, got, got.Length);
                        Check(got.SequenceEqual(data), "并发页内容不一致");
                    }
                });
                Parallel.For(0, 8, sector => disk.Write(2UL * 1024 * 1024 + (ulong)sector * 512, Pattern(sector + 500, 512), 512));
                var overlapping = new byte[4096]; disk.Read(2UL * 1024 * 1024, overlapping, overlapping.Length);
                for (int sector = 0; sector < 8; sector++) Check(overlapping.AsSpan(sector * 512, 512).SequenceEqual(Pattern(sector + 500, 512)), "同页部分更新丢失");
                var cross = Pattern(700, 8192); disk.Write(4UL * 1024 * 1024 - 512, cross, cross.Length);
                disk.Flush();
                bool locked = false;
                try { using var duplicate = new CoreDisk(path, password); }
                catch (IOException) { locked = true; }
                Check(locked, "重复打开必须被排他锁拒绝");
                bool bounds = false;
                try { disk.Read(disk.Capacity - 512, new byte[1024], 1024); }
                catch (IOException) { bounds = true; }
                Check(bounds, "越界请求必须报错");
            }
            {
                bool rejected = false;
                try { using var wrong = new CoreDisk(path, "wrong-password"); }
                catch (IOException) { rejected = true; }
                Check(rejected, "本地格式不应接受密码参数");
            }
            using (var disk = new CoreDisk(path, password))
            {
                Check(disk.Id == id, "重新打开后磁盘标识变化");
                for (int page = 0; page < 96; page++)
                {
                    var data = new byte[4096]; disk.Read((ulong)page * 4096, data, data.Length);
                    Check(data.SequenceEqual(Pattern(page, 4096)), "重开后数据不一致");
                }
                var cross = new byte[8192]; disk.Read(4UL * 1024 * 1024 - 512, cross, cross.Length);
                Check(cross.SequenceEqual(Pattern(700, 8192)), "跨边界写入不一致");
                disk.Trim(0, 4096); disk.Flush(); disk.Compact();
                byte[] got = new byte[4096]; disk.Read(0, got, got.Length);
                Check(got.All(x => x == 0), "TRIM/整理后应读零");
                disk.Read(4096, got, got.Length);
                Check(got.SequenceEqual(Pattern(1, 4096)), "整理破坏其他页");
                File.WriteAllText(Path.Combine(output, encrypted ? "encrypted-info.json" : "plain-info.json"), disk.GetInfo().GetRawText());
            }
            Check(File.Exists(path), "容器不是单个文件");
            Check(!Directory.Exists(path), "V2 不应创建分片目录");
            Check(Directory.EnumerateFileSystemEntries(containerDirectory).Single() == path, "容器产生了持久化侧文件");
            string copied = Path.Combine(output, encrypted ? "encrypted-copy.odv4" : "plain-copy.odv4");
            File.Copy(path, copied);
            using (var copy = new CoreDisk(copied, password))
            {
                var got = new byte[4096]; copy.Read(4096, got, got.Length);
                Check(got.SequenceEqual(Pattern(1, 4096)), "复制单个文件不能完整恢复磁盘");
            }
            passed.Add((encrypted ? "encrypted" : "plain") + ": concurrent/partial/cross-boundary/durable/lock/bounds/trim/compact");
        }
        RunAsyncCore(Path.Combine(output, "async.odv4"));
        passed.Add("async ABI: copied input, ordered overlaps, immutable read completion, FUA and drain");
        using (var manager = new DiskController(Path.Combine(output, "recovery-settings")))
        {
            manager.ImportAsync(Path.Combine(output, "plain", "disk.odv4")).GetAwaiter().GetResult();
            var recovered = manager.Disks.Single();
            Check(recovered.Name == "disk" && !recovered.Initialized, "未能从单文件恢复容器配置");
            manager.UpdateSettingsAsync(recovered, "恢复测试", recovered.DriveLetter).GetAwaiter().GetResult();
        }
        using (var restored = new DiskController(Path.Combine(output, "recovery-settings")))
            Check(restored.Disks.Single().Name == "恢复测试", "设置没有持久化");
        passed.Add("controller: import self-contained volume and persist settings");
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, tests = passed }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("CORE_SMOKE_OK " + output);
        return 0;
    }

    private static void RunAsyncCore(string path)
    {
        CoreDisk.Create(path, 64UL * 1024 * 1024, null);
        var first = Pattern(1010, 1024 * 1024);
        var second = Pattern(2020, 1024 * 1024);
        var fua = Pattern(3030, 4096);
        using (var disk = new CoreDisk(path, null))
        {
            void Write(ulong token, ulong offset, byte[] source, bool force = false)
            {
                var transient = source.ToArray();
                var pinned = GCHandle.Alloc(transient, GCHandleType.Pinned);
                try { disk.Submit(2, offset, (uint)transient.Length, pinned.AddrOfPinnedObject(), force ? 1u : 0u, token); }
                finally { pinned.Free(); }
                Array.Fill(transient, (byte)0xDD); // Submit must have taken ownership before returning.
            }
            Write(1, 0, first);
            disk.Submit(1, 0, (uint)first.Length, IntPtr.Zero, 0, 2);
            Write(3, 0, second);
            disk.Submit(1, 0, (uint)second.Length, IntPtr.Zero, 0, 4);
            disk.Submit(3, 0, 0, IntPtr.Zero, 0, 5);
            Write(6, 2UL * 1024 * 1024, fua, true);
            disk.Submit(1, 2UL * 1024 * 1024, (uint)fua.Length, IntPtr.Zero, 1, 7);
            var seen = new HashSet<ulong>();
            var deadline = Stopwatch.StartNew();
            while (seen.Count < 7)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(30)) throw new IOException("异步请求未按时完成。");
                if (!disk.TryNextCompletion(out var completion, 100)) continue;
                try
                {
                    Check(seen.Add(completion.Token), "同一请求重复完成");
                    Check(completion.Status == 0, Marshal.PtrToStringUTF8(completion.Error) ?? "异步操作失败");
                    byte[]? expected = completion.Token switch { 2 => first, 4 => second, 7 => fua, _ => null };
                    if (expected != null)
                    {
                        Check(completion.Length == expected.Length && completion.Data != IntPtr.Zero, "读取完成缓冲无效");
                        byte[] actual = new byte[completion.Length];
                        Marshal.Copy(completion.Data, actual, 0, actual.Length);
                        Check(actual.SequenceEqual(expected), "异步重叠操作未保持版本顺序");
                    }
                }
                finally { disk.ReleaseCompletion(completion.Token); }
            }
            disk.Drain();
        }
        // No extra Flush here: the FUA completion must already make the prefix durable.
        using var reopened = new CoreDisk(path, null);
        var verify = new byte[second.Length]; reopened.Read(0, verify, verify.Length);
        Check(verify.SequenceEqual(second), "FUA 之前的完整写前缀未持久化");
        verify = new byte[fua.Length]; reopened.Read(2UL * 1024 * 1024, verify, verify.Length);
        Check(verify.SequenceEqual(fua), "FUA 写入未持久化");
    }

    public static int RunPipe(string output)
    {
        PrepareOutput(output);
        string diskPath = Path.Combine(output, "disk.odv4");
        CoreDisk.Create(diskPath, 256UL * 1024 * 1024, null);
        using var disk = new CoreDisk(diskPath, null);
        using var host = new WinSpdDiskHost(disk);
        string pipe = @"\\.\pipe\OverlayDiskTest-" + Environment.ProcessId;
        host.Start(pipe);
        File.WriteAllText(Path.Combine(output, "ready.json"), JsonSerializer.Serialize(new { pipe, pid = Environment.ProcessId }));
        Console.WriteLine("PIPE_READY " + pipe);
        // A scoped stop file lets the external test runner stop the pipe without exposing a command endpoint.
        while (!File.Exists(Path.Combine(output, "stop"))) Thread.Sleep(100);
        disk.Flush();
        return 0;
    }

    public static async Task<int> RunMountAsync(string output)
    {
        PrepareOutput(output);
        void Progress(string message) => File.AppendAllText(Path.Combine(output, "progress.log"), DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
        Progress("mount smoke started");
        using var controller = new DiskController(Path.Combine(output, "settings"));
        if (!controller.IsAdministrator || !controller.DriverAvailable) throw new IOException("真实挂载测试需要管理员权限和已安装的 WinSpd 驱动。");
        char letter = Enumerable.Range('D', 23).Select(i => (char)i).Reverse().First(c => !DriveInfo.GetDrives().Any(d => d.Name[0] == c));
        var request = new CreateDiskRequest("OD-FunctionTest", Path.Combine(output, "disk.odv4"), 256UL * 1024 * 1024, letter, false);
        const string? password = null;
        DiskEntry? entry = null;
        try
        {
            Progress("create and format owned NTFS volume started");
            await controller.CreateAsync(request, password);
            entry = controller.Disks.Single();
            var drive = new DriveInfo(letter + @":\");
            Check(drive.IsReady && drive.DriveFormat == "NTFS", "创建后未得到可用 NTFS 卷");
            Progress("owned NTFS volume ready; writing test files");
            string dataPath = Path.Combine(drive.RootDirectory.FullName, "data.bin");
            byte[] expected = Pattern(800, 8 * 1024 * 1024);
            await using (var stream = new FileStream(dataPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            { await stream.WriteAsync(expected); stream.Flush(true); }
            using (var map = MemoryMappedFile.CreateFromFile(dataPath, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite))
            using (var view = map.CreateViewAccessor()) { view.Write(65530, (byte)0x91); view.Flush(); expected[65530] = 0x91; }
            await Parallel.ForEachAsync(Enumerable.Range(0, 16), async (i, _) =>
            {
                string file = Path.Combine(drive.RootDirectory.FullName, $"thread-{i}.bin");
                await File.WriteAllBytesAsync(file, Pattern(i + 900, 65536));
            });
            string program = Path.Combine(drive.RootDirectory.FullName, "whoami.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), program);
            using (var process = Process.Start(new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            { await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync(); Check(process.ExitCode == 0, "无法从虚拟盘执行程序"); }
            string replacement = Path.Combine(drive.RootDirectory.FullName, "replacement.bin");
            string replacementTemp = replacement + ".tmp";
            byte[] replacementBytes = Pattern(1800, 128 * 1024);
            await File.WriteAllBytesAsync(replacement, Pattern(1801, 4096));
            await using (var stream = new FileStream(replacementTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await stream.WriteAsync(replacementBytes); stream.Flush(true); }
            File.Replace(replacementTemp, replacement, null);
            await controller.FlushFileSystemAsync(entry);
            var liveCore = controller.TryGetCore(entry.Id)!;
            Progress("test files saved; starting read-only video reference verification");
            var readOnlyVideo = await VerifyReadOnlyVideoAsync(controller, entry, drive.RootDirectory.FullName, liveCore, Progress);
            Progress("read-only video references and incremental upload set verified");
            await controller.FlushFileSystemAsync(entry);
            string snapshotId = liveCore.Control(new { cmd = "snapshot.create", name = "NTFS 功能快照" }).GetProperty("snapshot").GetProperty("id").GetString()!;
            Progress("snapshot captured; online compact and concurrent write started");
            liveCore.Control(new { cmd = "compact.start" });
            liveCore.Control(new { cmd = "compact.pause", paused = true });
            Check(liveCore.Control(new { cmd = "compact.status" }).GetProperty("state").GetString() == "paused", "后台整理未暂停");
            byte[] concurrentBytes = Pattern(3300, 512 * 1024);
            var concurrentWrite = File.WriteAllBytesAsync(Path.Combine(drive.RootDirectory.FullName, "during-compact.bin"), concurrentBytes);
            liveCore.Control(new { cmd = "compact.pause", paused = false });
            await Task.Run(() =>
            {
                for (int step = 0; step < 20000; step++)
                    if (liveCore.Control(new { cmd = "compact.step", max_pages = 128 }).GetProperty("state").GetString() is "done" or "idle") return;
                throw new IOException("后台整理未在预期步骤内完成。");
            });
            await concurrentWrite;
            Check((await File.ReadAllBytesAsync(Path.Combine(drive.RootDirectory.FullName, "during-compact.bin"))).SequenceEqual(concurrentBytes), "在线整理破坏并发文件写入");
            string snapshotCopy = Path.Combine(output, "ntfs-snapshot-copy.odv4");
            Progress("snapshot restore to owned target started");
            using (var copy = CoreDisk.BeginSnapshotRestore(liveCore, snapshotId, snapshotCopy, password))
            {
                bool finished = false;
                for (int step = 0; step < 20000; step++)
                {
                    string? phase = copy.Control(new { cmd = "snapshot.restore_step", max_pages = 256 }).GetProperty("phase").GetString();
                    if (phase is "ready" or "complete")
                    {
                        copy.Control(new { cmd = "restore.finish" });
                        finished = true; break;
                    }
                    Check(phase is not ("needs_source" or "paused"), "NTFS 快照恢复未保持可继续状态");
                }
                Check(finished, "NTFS 快照恢复未在预期步骤内完成");
                byte[] mbr = new byte[512]; copy.Read(0, mbr, mbr.Length);
                copy.Id.ToByteArray().AsSpan(0, 4).CopyTo(mbr.AsSpan(440, 4)); copy.Write(0, mbr, mbr.Length); copy.Flush();
            }
            using (var restored = new DiskController(Path.Combine(output, "snapshot-import-settings")))
            {
                try
                {
                    Progress("mounting snapshot copy beside source");
                    await restored.ImportAsync(snapshotCopy);
                    var restoredEntry = restored.Disks.Single();
                    await restored.MountAsync(restoredEntry, password);
                    string restoredRoot = restoredEntry.DriveLetter + @":\";
                    Check((await File.ReadAllBytesAsync(Path.Combine(restoredRoot, "data.bin"))).SequenceEqual(expected), "已挂载 NTFS 快照恢复后内容不一致");
                    Check((await File.ReadAllBytesAsync(Path.Combine(restoredRoot, "replacement.bin"))).SequenceEqual(replacementBytes), "快照未包含文件系统缓存的已保存内容");
                    Check(!File.Exists(Path.Combine(restoredRoot, "during-compact.bin")), "快照混入了捕获之后的文件");
                }
                finally { await restored.UnmountAllAsync(); }
            }
            Progress("snapshot copy content verified and safely unmounted");
            // The file and volume exist only inside this test's newly created container.
            // Deleting it changes the NTFS bitmap; only the explicit action may trim it.
            string reclaimPath = Path.Combine(drive.RootDirectory.FullName, "manual-reclaim-disposable.bin");
            await using (var reclaimFile = new FileStream(reclaimPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await reclaimFile.WriteAsync(Pattern(4500, 4 * 1024 * 1024)); reclaimFile.Flush(true); }
            File.Delete(reclaimPath);
            await controller.FlushFileSystemAsync(entry);
            Progress("busy reclaim and busy unmount refusal checks started");
            using (var held = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool reclaimRejected = false;
                try { await controller.ReclaimFileSystemAsync(entry); }
                catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode is 5 or 32 or 33)
                { reclaimRejected = true; }
                Check(reclaimRejected && entry.Mounted, "文件被占用时必须拒绝手动回收并保留在线磁盘");
                Check(held.ReadByte() == expected[0], "手动回收被拒绝后原文件句柄不可用");
                held.Position = 0;
                bool rejected = false;
                try { await controller.UnmountAsync(entry); }
                catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { rejected = true; }
                Check(rejected && entry.Mounted, "文件被占用时必须拒绝卸载并保留在线磁盘");
                Check(held.ReadByte() == expected[0], "卸载失败后原文件句柄不可用");
            }
            Progress("busy operations correctly rejected; manual NTFS reclaim started");
            var reclaimed = await controller.ReclaimFileSystemAsync(entry);
            Check(reclaimed.ExaminedClusters != 0 && reclaimed.FileSystemFreeBytes >= 4 * 1024 * 1024, "手动回收未扫描 NTFS 空闲簇");
            Check(!File.Exists(reclaimPath), "回收后已删除的测试文件重新出现");
            Check(SHA256.HashData(await File.ReadAllBytesAsync(dataPath)).SequenceEqual(SHA256.HashData(expected)), "手动回收改变了在用大文件");
            Check(SHA256.HashData(await File.ReadAllBytesAsync(replacement)).SequenceEqual(SHA256.HashData(replacementBytes)), "手动回收改变了在用替换文件");
            Check((await File.ReadAllBytesAsync(Path.Combine(drive.RootDirectory.FullName, "during-compact.bin"))).SequenceEqual(concurrentBytes), "手动回收改变了整理期间写入的文件");
            Check(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(drive.RootDirectory.FullName, "read-only-video-fixture.mp4")))
                .SequenceEqual(SHA256.HashData(Pattern(5901, 16 * 1024 * 1024))), "手动回收改变了只读视频测试文件");
            Progress("manual reclaim preserved live file hashes; remount started");
            await controller.UnmountAsync(entry);
            await controller.MountAsync(entry, password);
            Progress("remounted source; verifying persisted files");
            Check((await File.ReadAllBytesAsync(dataPath)).SequenceEqual(expected), "卸载再挂载后内容不一致");
            Check((await File.ReadAllBytesAsync(replacement)).SequenceEqual(replacementBytes), "原子替换/WriteThrough 内容未持久化");
            for (int i = 0; i < 16; i++) Check((await File.ReadAllBytesAsync(Path.Combine(drive.RootDirectory.FullName, $"thread-{i}.bin"))).SequenceEqual(Pattern(i + 900, 65536)), "并发文件内容不一致");
            await controller.UnmountAllAsync();
            string copiedContainer = Path.Combine(output, "imported-copy.odv4");
            File.Copy(entry.ContainerPath, copiedContainer);
            Progress("source safely closed; mounting copied container with fresh catalog");
            using (var imported = new DiskController(Path.Combine(output, "fresh-import-settings")))
            {
                try
                {
                    await imported.ImportAsync(copiedContainer);
                    var copiedEntry = imported.Disks.Single();
                    await imported.UpdateSettingsAsync(copiedEntry, "导入验证", letter);
                    await imported.MountAsync(copiedEntry, password);
                    Check((await File.ReadAllBytesAsync(dataPath)).SequenceEqual(expected), "导入单文件后 NTFS 内容改变");
                    Check((await File.ReadAllBytesAsync(replacement)).SequenceEqual(replacementBytes), "导入错误地格式化已有文件系统");
                }
                finally { await imported.UnmountAllAsync(); }
            }
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, entry.Id, driveLetter = letter, tests = new[] { "automatic NTFS", "concurrent files", "mmap", "execute", "atomic replace and WriteThrough", "read-only video retains exact page references and creates no video-data uploads", "filesystem flush before snapshot", "online compact pause/resume with concurrent write", "NTFS snapshot copy mounted beside source", "busy reclaim rejected without removal", "manual reclaim after owned file deletion preserves live file hashes", "busy unmount rejected without removal", "remount persistence", "single-file copy and fresh-catalog NTFS import" }, readOnlyVideo, reclaim = reclaimed, sha256 = Convert.ToHexString(SHA256.HashData(expected)) }));
            Progress("all mount smoke assertions passed");
            Console.WriteLine("MOUNT_SMOKE_OK " + output);
            return 0;
        }
        finally { Progress("final safe close started"); await controller.UnmountAllAsync(); Progress("final safe close completed"); }
    }

    private sealed record FileExtent(ulong Vcn, ulong Lcn, ulong Clusters);
    private sealed record FilePageReference(string ObjectId, ulong Slot, string Digest);
    private sealed record PreparedVersion(JsonElement Job, IReadOnlyList<JsonElement> Objects);

    private static async Task<object> VerifyReadOnlyVideoAsync(DiskController controller, DiskEntry entry, string root, CoreDisk core, Action<string> progress)
    {
        // Synthetic media-shaped data tests the application's byte-read path, not a video decoder.
        byte[] expected = Pattern(5901, 16 * 1024 * 1024);
        string path = Path.Combine(root, "read-only-video-fixture.mp4");
        await using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { await writer.WriteAsync(expected); writer.Flush(true); }
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        using var volume = File.OpenHandle(@"\\.\" + entry.DriveLetter + ":", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] partition = FileSystemQuery(volume, 0x00070048, [], 256);
        Check(partition.Length >= 32 && BitConverter.ToUInt32(partition, 0) == 0, "测试卷不是预期的 MBR 分区");
        ulong partitionOffset = BitConverter.ToUInt64(partition, 8), partitionLength = BitConverter.ToUInt64(partition, 16);
        Check(partitionOffset == 1024 * 1024 && partitionLength <= core.Capacity - partitionOffset, "测试卷分区范围不一致");
        byte[] ntfs = FileSystemQuery(volume, 0x00090064, [], 128);
        Check(ntfs.Length >= 96 && BitConverter.ToUInt32(ntfs, 40) == 512 && BitConverter.ToUInt32(ntfs, 44) == 4096, "测试卷不是 512 字节扇区 / 4 KiB 簇 NTFS");
        ulong clusters = BitConverter.ToUInt64(ntfs, 16);
        Check(clusters <= partitionLength / 4096, "NTFS 簇越出分区");
        var extents = RetrievalPointers(file.SafeFileHandle, (ulong)expected.Length / 4096, clusters);
        await controller.FlushFileSystemAsync(entry);
        core.Control(new { cmd = "cloud.bind", backend_id = "self-test-no-network", account_id = "isolated-fixture", remote_root = "fixture://" + core.Id, device_id = "mount-smoke-fixture", enabled = true });
        var baseline = PrepareVersion(core) ?? throw new IOException("首次本地模拟同步未形成基线。");
        progress($"read-only baseline prepared: {baseline.Objects.Count} objects; local export hash verification started");
        CompleteLocalFixtureVersion(core, baseline);
        var before = ReadFileReferences(core, extents, partitionOffset, expected);
        var videoObjectIds = before.Values.Select(value => value.ObjectId).ToHashSet(StringComparer.Ordinal);
        Check(videoObjectIds.IsSubsetOf(baseline.Objects.Select(value => value.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal)), "视频数据对象未包含在固定基线中");
        progress($"read-only baseline fixed: {before.Count} file pages in {videoObjectIds.Count} objects; sequential and seek reads started");
        Check((await SHA256.HashDataAsync(file)).SequenceEqual(SHA256.HashData(expected)), "视频样式文件顺序读取内容不一致");
        byte[] chunk = new byte[8192];
        for (int i = 0; i < 17; i++)
        {
            int offset = (i * 7919 * 512) % (expected.Length - chunk.Length);
            file.Position = offset; await file.ReadExactlyAsync(chunk);
            Check(chunk.AsSpan().SequenceEqual(expected.AsSpan(offset, chunk.Length)), "视频样式文件跳转读取内容不一致");
        }
        await controller.FlushFileSystemAsync(entry); // Preserve real NTFS access-time/log writes.
        Check(RetrievalPointers(file.SafeFileHandle, (ulong)expected.Length / 4096, clusters).SequenceEqual(extents), "只读期间文件物理簇发生移动，无法将变化归因于读取");
        var after = ReadFileReferences(core, extents, partitionOffset, expected);
        Check(before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var value) && value == pair.Value), "只读文件的原始对象、槽位或摘要发生变化");
        var increment = PrepareVersion(core);
        var additions = increment?.Objects ?? Array.Empty<JsonElement>();
        Check(additions.All(value => !videoObjectIds.Contains(value.GetProperty("id").GetString()!)), "只读操作使视频数据对象进入新的上传集合");
        // Classification is advisory. Keep every non-video change in the fixture commit,
        // including unclassified NTFS writes; never filter an actual filesystem write away.
        var types = ReadObjectTypes(core);
        var otherChanges = additions.GroupBy(value => types.GetValueOrDefault(value.GetProperty("id").GetString()!, "unclassified"))
            .ToDictionary(group => group.Key, group => group.Count());
        if (increment is not null) CompleteLocalFixtureVersion(core, increment);
        progress($"read-only delta verified: 0 video data objects; {additions.Count} other objects preserved in local-only fixture commit");
        return new { bytesRead = expected.Length + 17 * chunk.Length, filePages = before.Count, fileExtents = extents.Count, videoObjects = videoObjectIds.Count,
            changedVideoPages = 0, newVideoUploadObjects = 0, otherChangedObjects = additions.Count, advisoryOtherContentTypes = otherChanges,
            otherChangedLogicalPages = increment?.Job.GetProperty("changed_pages").GetUInt64() ?? 0,
            baselineGeneration = baseline.Job.GetProperty("generation").GetUInt64(), nextGeneration = increment?.Job.GetProperty("generation").GetUInt64(),
            networkUsed = false, lastAccessPolicyChanged = false, sha256 = Convert.ToHexString(SHA256.HashData(expected)) };
    }

    private static List<FileExtent> RetrievalPointers(SafeFileHandle file, ulong fileClusters, ulong volumeClusters)
    {
        var extents = new List<FileExtent>(); ulong cursor = 0;
        for (int page = 0; cursor < fileClusters && page < 256; page++)
        {
            byte[] input = BitConverter.GetBytes(cursor), output = new byte[16 + 256 * 16];
            bool success = DeviceIoControl(file, 0x00090073, input, 8, output, (uint)output.Length, out uint returned, IntPtr.Zero);
            int error = success ? 0 : Marshal.GetLastWin32Error();
            if (!success && error != 234) throw new System.ComponentModel.Win32Exception(error, "无法读取自建测试文件的实际 NTFS 簇位置。");
            Check(returned >= 16 && returned <= output.Length, "文件簇映射响应被截断");
            uint count = BitConverter.ToUInt32(output, 0); long first = BitConverter.ToInt64(output, 8);
            Check(count is > 0 and <= 256 && 16UL + count * 16UL <= returned && first >= 0 && (ulong)first <= cursor, "文件簇映射分页头无效");
            ulong previous = (ulong)first, nextCursor = cursor;
            for (int i = 0; i < count; i++)
            {
                long next = BitConverter.ToInt64(output, 16 + i * 16), lcn = BitConverter.ToInt64(output, 24 + i * 16);
                Check(next > 0 && (ulong)next > previous && lcn >= 0, "测试文件出现空洞、压缩或无效簇范围");
                ulong begin = Math.Max(previous, cursor), end = Math.Min((ulong)next, fileClusters);
                if (end > begin)
                {
                    ulong physical = checked((ulong)lcn + begin - previous), length = end - begin;
                    Check(physical < volumeClusters && length <= volumeClusters - physical && begin == nextCursor, "文件簇范围越界或不连续");
                    extents.Add(new(begin, physical, length)); nextCursor = end;
                }
                previous = (ulong)next;
            }
            Check(nextCursor > cursor, "文件簇映射游标没有前进"); cursor = nextCursor;
        }
        Check(cursor == fileClusters, "文件簇映射未在有限分页内完成");
        return extents;
    }

    private static Dictionary<ulong, FilePageReference> ReadFileReferences(CoreDisk core, IReadOnlyList<FileExtent> extents, ulong partitionOffset, byte[] expected)
    {
        var references = new Dictionary<ulong, FilePageReference>();
        foreach (var extent in extents)
            for (ulong done = 0; done < extent.Clusters;)
            {
                ulong count = Math.Min(128, extent.Clusters - done), first = partitionOffset / 4096 + extent.Lcn + done;
                var items = core.Control(new { cmd = "debug.pages", start_page = first, limit = count }).GetProperty("items");
                Check(items.GetArrayLength() == (int)count, "精确页映射返回数量不一致");
                for (int i = 0; i < items.GetArrayLength(); i++)
                {
                    var item = items[i]; ulong logical = first + (ulong)i, filePage = extent.Vcn + done + (ulong)i;
                    Check(item.GetProperty("page").GetUInt64() == logical && item.GetProperty("object_id").ValueKind == JsonValueKind.String, "视频数据页没有有效持久映射");
                    string digest = item.GetProperty("digest").GetString()!;
                    Check(digest.Equals(Convert.ToHexString(SHA256.HashData(expected.AsSpan(checked((int)(filePage * 4096)), 4096))), StringComparison.OrdinalIgnoreCase), "实际 LCN 对应页与文件内容摘要不匹配");
                    Check(references.TryAdd(logical, new(item.GetProperty("object_id").GetString()!, item.GetProperty("slot").GetUInt64(), digest)), "文件页映射出现重复 LCN");
                }
                done += count;
            }
        Check(references.Count == expected.Length / 4096, "文件页映射不完整");
        return references;
    }

    private static PreparedVersion? PrepareVersion(CoreDisk core)
    {
        JsonElement result = core.Control(new { cmd = "cloud.prepare" });
        for (int step = 0; step < 20000; step++)
        {
            if (!result.TryGetProperty("job", out var job) || job.ValueKind == JsonValueKind.Null) return null;
            string id = job.GetProperty("id").GetString()!;
            if (job.GetProperty("phase").GetString() == "ready")
            {
                var objects = new List<JsonElement>(); ulong cursor = 0;
                for (int page = 0; page < 2048; page++)
                {
                    var list = core.Control(new { cmd = "cloud.delta", job_id = id, side = "add", cursor, limit = 128 });
                    objects.AddRange(list.GetProperty("items").EnumerateArray().Select(value => value.Clone()));
                    if (list.GetProperty("next_cursor").ValueKind == JsonValueKind.Null) return new(job.Clone(), objects);
                    ulong next = list.GetProperty("next_cursor").GetUInt64(); Check(next > cursor, "同步对象分页未前进"); cursor = next;
                }
                throw new IOException("测试同步对象列表超过有界范围。");
            }
            result = core.Control(new { cmd = "cloud.prepare", job_id = id, max_pages = 512, max_objects = 4 });
        }
        throw new IOException("测试同步准备未在有限步骤内完成。");
    }

    private static void CompleteLocalFixtureVersion(CoreDisk core, PreparedVersion version)
    {
        string jobId = version.Job.GetProperty("id").GetString()!;
        foreach (var batch in version.Objects.Chunk(128))
        {
            foreach (var item in batch)
            {
                byte[] raw = core.ReadExport(jobId, item.GetProperty("id").GetString()!);
                Check(Convert.ToHexString(SHA256.HashData(raw)).Equals(item.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "本地模拟导出对象哈希不一致");
            }
            core.Control(new { cmd = "cloud.receipt", job_id = jobId, records = batch.Select(item => new { object_id = item.GetProperty("id").GetString(), sha256 = item.GetProperty("sha256").GetString(), length = item.GetProperty("length").GetUInt64() }).ToArray() });
        }
        core.Control(new { cmd = "cloud.commit", job_id = jobId, root_object_id = version.Job.GetProperty("root_object_id").GetString(), root_sha256 = version.Job.GetProperty("root_sha256").GetString(), receipt = "self-test:verified-local-export-only" });
    }

    private static Dictionary<string, string> ReadObjectTypes(CoreDisk core)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal); ulong start = 0;
        for (int page = 0; page < 2048; page++)
        {
            var reply = core.Control(new { cmd = "blocks.query", start, limit = 128 });
            foreach (var item in reply.GetProperty("items").EnumerateArray())
                if (item.TryGetProperty("object_id", out var id) && id.ValueKind == JsonValueKind.String)
                    result[id.GetString()!] = item.GetProperty("content_type").GetString()!;
            start += 128;
            if (start >= reply.GetProperty("total_count").GetUInt64()) return result;
        }
        throw new IOException("测试对象分类超过有界范围。");
    }

    private static byte[] FileSystemQuery(SafeFileHandle handle, uint code, byte[] input, int size)
    {
        byte[] output = new byte[size];
        if (!DeviceIoControl(handle, code, input, (uint)input.Length, output, (uint)output.Length, out uint returned, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "测试文件系统查询失败。");
        Check(returned <= output.Length, "测试文件系统查询长度无效");
        return output[..checked((int)returned)];
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize, [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
}
