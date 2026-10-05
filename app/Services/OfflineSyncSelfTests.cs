using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OverlayDisk.Cloud.Contracts;
using OverlayDisk.Cloud.Sync;

namespace OverlayDisk.Services;

internal static class OfflineSyncSelfTests
{
    internal static async Task<int> RunAsync(string output, uint objectSizeBytes = 4 * 1024 * 1024)
    {
        if (Directory.Exists(output)) throw new IOException("请使用新的离线同步测试目录。");
        Directory.CreateDirectory(output);
        string source = Path.Combine(output, "source.odv4"), target = Path.Combine(output, "target.odv4"), cachePath = Path.Combine(output, "cache");
        const string password = "isolated-cache-test-password";
        CoreDisk.Create(source, 64UL * 1024 * 1024, password, objectSizeBytes);
        var store = new MemoryStore(); var checks = new List<string>(); var events = new List<SyncLogEntry>();
        CloudBinding binding; SyncResult first, second;
        CloudRepository Repo() => new(store, new FileCloudSyncCache(cachePath), "offline-account");
        async Task<SyncResult> Run(CoreDisk disk) => await new SyncCoordinator(Repo()).RunAsync(new Volume(disk), binding, "cache fixture", disk.Capacity, true, 2, null, default, new Events(events));
        using (var disk = new CoreDisk(source, password))
        {
            binding = new(store.ProviderId, "offline-account", CloudRepository.RootPath(disk.Id.ToString()), Guid.NewGuid().ToString());
            disk.Control(new { cmd = "cloud.bind", backend_id = binding.ProviderId, account_id = binding.AccountId, remote_root = binding.RemoteRoot, device_id = binding.DeviceId, enabled = true });
            var data = new byte[8192]; new Random(711).NextBytes(data);
            disk.Write(512, data, data.Length); disk.Write(8UL * 1024 * 1024, data, data.Length); disk.Flush();
            first = await Run(disk);
            Check(Diagnostic(disk, "upload_read_bytes") > 0, "诊断未统计首次同步的真实对象读取");
        }
        using (var disk = new CoreDisk(source, password))
        {
            await CheckReadAndIdenticalWriteAsync(disk, store, Run, first, checks, 0, 1024 * 1024);
            byte[] changed = new byte[4096]; new Random(719).NextBytes(changed);
            // Replace both occupied leaf regions so the old metadata pack really
            // becomes obsolete. A V4 root pack can otherwise retain live nodes.
            disk.Write(4096, changed, changed.Length);
            disk.Write(8UL * 1024 * 1024, changed, changed.Length); disk.Flush();
            second = await Run(disk);
            Check(second.Commit.Generation > first.Commit.Generation, "新写入没有发布新版本");
            Check(store.Files.ContainsKey(CloudRepository.ObjectPath(binding.RemoteRoot, first.Commit.RootObjectId)), "同步不应自动删除旧根对象");
            checks.Add("ordinary sync retains old remote objects until explicit collection");
            await new SyncCoordinator(Repo()).CleanupAsync(new Volume(disk), binding, new Events(events), default);
            Check(!store.Files.ContainsKey(CloudRepository.ObjectPath(binding.RemoteRoot, first.Commit.RootObjectId)), "旧根对象未按手动差集清理");
            Check(!store.Calls.Any(c => c.StartsWith("list:" + binding.RemoteRoot + "/objects", StringComparison.Ordinal)), "同步执行了全量对象扫描");
            Check(events.Any(e => e.Action == "upload.confirmed" && e.ObjectId == second.Commit.RootObjectId) && events.Any(e => e.Action == "commit.confirmed" && e.Generation == second.Commit.Generation), "块上传或版本提交没有日志");
            checks.Add("changed native generation publishes, logs exact objects and deletes only known old closure without object-directory scans");

            string unknown = CloudRepository.ObjectPath(binding.RemoteRoot, Guid.NewGuid().ToString()); store.Files[unknown] = new byte[objectSizeBytes];
            foreach (string file in Directory.EnumerateFiles(cachePath)) File.Delete(file);
            store.Calls.Clear(); await Run(disk);
            Check(store.Files.ContainsKey(unknown), "丢失缓存后删除了未知对象");
            Check(!store.Calls.Any(c => c.StartsWith("list:" + binding.RemoteRoot + "/objects", StringComparison.Ordinal)), "缓存重建扫描了对象目录");
            checks.Add("cache loss reconciles native published metadata and preserves unknown objects");

            second = await CheckConcurrentSyncAsync(disk, store, Run, checks);
            await CheckReadAndIdenticalWriteAsync(disk, store, Run, second, checks, 16UL * 1024 * 1024, 1024 * 1024);

            var repository = Repo(); byte[] root = await repository.ReadObjectAsync(binding.RemoteRoot, second.Commit.RootObjectId, second.Commit.RootSha256, (int)objectSizeBytes, default);
            using var restored = CoreDisk.BeginRestore(target, root, password);
            for (int step = 0; ; step++)
            {
                Check(step < 4096, "原生恢复未能在有界步骤内完成");
                var status = restored.Control(new { cmd = "restore.status", cursor = 0, limit = 128 });
                if (status.GetProperty("phase").GetString() is "ready" or "complete") break;
                if (status.GetProperty("phase").GetString() == "building") { restored.Control(new { cmd = "restore.step", max_pages = 128 }); continue; }
                foreach (var item in status.GetProperty("needed").EnumerateArray())
                {
                    var obj = SyncCoordinator.ParseObject(item);
                    restored.AcceptRestoreObject(obj.Id, await repository.ReadObjectAsync(binding.RemoteRoot, obj.Id, obj.Sha256, (int)objectSizeBytes, default));
                }
            }
            restored.Control(new { cmd = "restore.finish" }); restored.Flush();
            byte[] expected = new byte[1024 * 1024], actual = new byte[expected.Length];
            for (ulong offset = 0; offset < disk.Capacity; offset += (ulong)expected.Length)
            {
                disk.Read(offset, expected, expected.Length); restored.Read(offset, actual, actual.Length);
                Check(expected.AsSpan().SequenceEqual(actual), "差集清理后恢复内容不一致");
            }
            checks.Add("latest closure remains fully restorable after manual delta cleanup and a concurrent same-page write; all 64 MiB match the real native source");
        }
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, realNetworkRequests = 0, checks }));
        Console.WriteLine("OFFLINE_SYNC_SMOKE_OK"); return 0;
    }
    private static ulong Diagnostic(CoreDisk disk, string field)
    {
        var diagnostics = disk.Control(new { cmd = "sync.diagnostics" });
        return diagnostics.TryGetProperty(field, out var counter) ? counter.GetUInt64() : 0;
    }
    private static async Task CheckReadAndIdenticalWriteAsync(CoreDisk disk, MemoryStore store,
        Func<CoreDisk, Task<SyncResult>> run, SyncResult expectedCommit, List<string> checks, ulong readOffset, int readLength)
    {
        ulong generation = disk.GetInfo().GetProperty("data_generation").GetUInt64();
        ulong exported = Diagnostic(disk, "upload_read_bytes");
        var read = new byte[readLength]; disk.Read(readOffset, read, read.Length);
        var page = new byte[4096]; disk.Read(4096, page, page.Length);
        disk.Write(4096, page, page.Length); disk.Flush();
        var status = disk.Control(new { cmd = "cloud.status" });
        Check(disk.GetInfo().GetProperty("data_generation").GetUInt64() == generation && !status.GetProperty("local_dirty").GetBoolean(),
            "纯读取或相同完整页写入产生了新脏页");
        store.Calls.Clear(); var unchanged = await run(disk);
        Check(unchanged.Commit == expectedCommit.Commit && store.Calls.IsEmpty, "无变化同步仍请求了云端");
        Check(Diagnostic(disk, "upload_read_bytes") == exported, "无变化同步重新读取了上传对象");
        checks.Add("pure native reads and identical full-page writes preserve generation; unchanged sync makes zero provider calls and zero export reads");
    }
    private static async Task<SyncResult> CheckConcurrentSyncAsync(CoreDisk disk, MemoryStore store,
        Func<CoreDisk, Task<SyncResult>> run, List<string> checks)
    {
        byte[] oldPage = new byte[4096], newerPage = new byte[4096], video = new byte[1024 * 1024];
        new Random(727).NextBytes(oldPage); new Random(733).NextBytes(newerPage); new Random(739).NextBytes(video);
        disk.Write(4096, oldPage, oldPage.Length);
        disk.Write(16UL * 1024 * 1024, video, video.Length); disk.Flush();
        ulong frozenGeneration = disk.GetInfo().GetProperty("data_generation").GetUInt64();
        var pause = store.PauseNextObjectPut();
        Task<SyncResult> syncing = Task.Run(() => run(disk));
        Task foreground = Task.CompletedTask;
        string? snapshotId = null;
        Exception? foregroundError = null;
        try
        {
            Task first = await Task.WhenAny(pause.Reached.Task, syncing).WaitAsync(TimeSpan.FromSeconds(60));
            if (first == syncing) { await syncing; throw new IOException("同步没有进入预定的真实对象上传暂停点。"); }
            string uploadedPath = await pause.Reached.Task;
            Check(store.Files.ContainsKey(uploadedPath) && !syncing.IsCompleted, "对象上传暂停点不在真实 Put 完成与回执之间");
            foreground = Task.Run(() =>
            {
                var actual = new byte[video.Length]; disk.Read(16UL * 1024 * 1024, actual, actual.Length);
                Check(actual.AsSpan().SequenceEqual(video), "上传期间读取视频内容不一致");
                disk.Write(4096, newerPage, newerPage.Length);
                var readback = new byte[newerPage.Length]; disk.Read(4096, readback, readback.Length);
                Check(readback.AsSpan().SequenceEqual(newerPage), "上传期间新写入没有立即可读");
                disk.Flush();
                var summary = disk.Control(new { cmd = "debug.summary" });
                var blocks = disk.Control(new { cmd = "debug.query", start = 0, limit = 32 });
                Check(summary.GetProperty("total_blocks").GetUInt64() > 0 && blocks.GetProperty("items").GetArrayLength() > 0,
                    "上传期间块查询结果无效");
                disk.Control(new { cmd = "snapshot.list", cursor = 0, limit = 32 });
                snapshotId = disk.Control(new { cmd = "snapshot.create", name = "created during a paused upload" })
                    .GetProperty("snapshot").GetProperty("id").GetString()!;
                var snapshots = disk.Control(new { cmd = "snapshot.list", cursor = 0, limit = 32 });
                Check(snapshots.GetProperty("items").EnumerateArray().Any(s => s.GetProperty("id").GetString() == snapshotId),
                    "上传期间创建的快照未出现在列表");
            });
            await foreground.WaitAsync(TimeSpan.FromSeconds(20));
            Check(!syncing.IsCompleted, "前台操作没有与暂停中的上传实际重叠");
        }
        catch (Exception error) { foregroundError = error; }
        finally { store.ReleasePause(pause); }

        // Release the provider first, then join every operation before the caller
        // can dispose its native handle, including failure/timeout paths.
        try { await Task.WhenAll(syncing, foreground).WaitAsync(TimeSpan.FromSeconds(90)); }
        catch (Exception error) { throw new IOException("隔离同步任务释放暂停点后未能正常结束。", foregroundError ?? error); }
        if (foregroundError is not null) throw new IOException("暂停上传时的原生并发操作未能有界完成。", foregroundError);
        SyncResult pinned = await syncing;
        var after = disk.Control(new { cmd = "cloud.status" });
        Check(pinned.Commit.Generation == frozenGeneration && pinned.HasPendingChanges
            && after.GetProperty("published_generation").GetUInt64() == frozenGeneration
            && after.GetProperty("data_generation").GetUInt64() > frozenGeneration
            && after.GetProperty("local_dirty").GetBoolean(), "旧同步任务提交清除了同页的新修改");
        checks.Add("while one actual object Put is paused, native reads/writes/flush, block queries and snapshot create/list finish within a bounded wait");
        checks.Add("committing the pinned upload generation preserves a later durable change to the same logical page");
        var latest = await run(disk);
        Check(latest.Commit.Generation > pinned.Commit.Generation && !latest.HasPendingChanges, "新任务没有发布上传期间产生的修改");
        if (snapshotId is not null) disk.Control(new { cmd = "snapshot.delete", id = snapshotId });
        return latest;
    }
    private static void Check(bool value, string message) { if (!value) throw new IOException(message); }
    private sealed class Events(List<SyncLogEntry> entries) : IProgress<SyncLogEntry>
    { public void Report(SyncLogEntry value) { lock (entries) entries.Add(value); } }
    private sealed class Volume(CoreDisk disk) : ICloudVolume
    {
        public string Id => disk.Id.ToString();
        public int ObjectSizeBytes => checked((int)disk.ObjectSizeBytes);
        public Task<JsonElement> ControlAsync(object request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(disk.Control(request)); }
        public Task<byte[]> ReadObjectAsync(string jobId, string objectId, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(disk.ReadExport(jobId, objectId)); }
    }
    private sealed class MemoryStore : ICloudObjectStore, ICloudBatchDeleteStore
    {
        public string ProviderId => "offline";
        public ConcurrentDictionary<string, byte[]> Files { get; } = new();
        private readonly ConcurrentDictionary<string, byte> folders = new();
        private ObjectPutPause? nextObjectPut;
        public ConcurrentQueue<string> Calls { get; } = new();
        public ObjectPutPause PauseNextObjectPut()
        {
            var pause = new ObjectPutPause();
            if (Interlocked.CompareExchange(ref nextObjectPut, pause, null) is not null) throw new IOException("上传暂停点已设置。");
            return pause;
        }
        public void ReleasePause(ObjectPutPause pause)
        {
            Interlocked.CompareExchange(ref nextObjectPut, null, pause); pause.Resume();
        }
        public Task<CloudAccountInfo> ValidateAsync(CancellationToken ct = default) { Calls.Enqueue("validate"); return Task.FromResult(new CloudAccountInfo("offline-account", "fixture")); }
        public Task<CloudObjectInfo?> HeadAsync(string path, CancellationToken ct = default)
        { Calls.Enqueue("head:" + path); return Task.FromResult(Files.TryGetValue(path, out var b) ? new CloudObjectInfo(path, b.Length, false) : folders.ContainsKey(path) ? new CloudObjectInfo(path, 0, true) : null); }
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        { Calls.Enqueue("mkdir:" + path); for (string part = path; part.Length != 0; part = part[..part.LastIndexOf('/')]) folders[part] = 0; return Task.CompletedTask; }
        public async IAsyncEnumerable<CloudObjectInfo> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls.Enqueue("list:" + path); await Task.CompletedTask;
            foreach (var pair in Files) if (Parent(pair.Key) == path) yield return new(pair.Key, pair.Value.Length, false);
            foreach (string folder in folders.Keys) if (Parent(folder) == path) yield return new(folder, 0, true);
        }
        public async Task<CloudObjectInfo> PutImmutableAsync(string path, Stream content, long length, string sha256, CancellationToken ct = default)
        {
            Calls.Enqueue("put:" + path); using var m = new MemoryStream(); await content.CopyToAsync(m, ct); var bytes = m.ToArray();
            if (bytes.Length != length || CloudRepository.Hash(bytes) != sha256) throw new IOException("Fixture hash mismatch");
            bool added = Files.TryAdd(path, bytes);
            if (!added && !Files[path].AsSpan().SequenceEqual(bytes)) throw new CloudObjectConflictException(path);
            var pause = added && path.Contains("/objects/", StringComparison.Ordinal)
                ? Interlocked.Exchange(ref nextObjectPut, null) : null;
            if (pause is not null) { pause.Reached.TrySetResult(path); await pause.WaitAsync(ct); }
            return new(path, length, false) { ReusedExisting = !added };
        }
        public Task<Stream> OpenReadAsync(string path, CloudByteRange? range = null, CancellationToken ct = default)
        { Calls.Enqueue("read:" + path); if (!Files.TryGetValue(path, out var bytes)) throw new CloudObjectNotFoundException(path); return Task.FromResult<Stream>(new MemoryStream(bytes, false)); }
        public Task DeleteAsync(string path, CancellationToken ct = default) { Calls.Enqueue("delete:" + path); Files.TryRemove(path, out _); return Task.CompletedTask; }
        public async Task DeleteManyAsync(IReadOnlyList<string> paths, CancellationToken ct = default) { foreach (string path in paths) await DeleteAsync(path, ct); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private static string Parent(string path) { int end = path.LastIndexOf('/'); return end <= 0 ? "/" : path[..end]; }
    }
    private sealed class ObjectPutPause
    {
        public TaskCompletionSource<string> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitAsync(CancellationToken ct) => resume.Task.WaitAsync(ct);
        public void Resume() => resume.TrySetResult(true);
    }
}
