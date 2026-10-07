using System.Collections.Concurrent;
using System.Text.Json;
using System.Security.Cryptography;
using OverlayDisk.Worker;
using OverlayDisk.Cloud.Contracts;

namespace OverlayDisk.Services;

public sealed partial class WorkerApplicationService : IWorkerDispatcher, IWorkerBulkDispatcher, IWorkerObjectProviderDispatcher
{
    private readonly DiskController controller;
    private readonly Func<Guid, CancellationToken, Task<bool>> mountedIdentityProbe;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> originalRestoreGates = new();
    private readonly ConcurrentDictionary<string, DiskCacheQuotaLoop> cacheLoops = new();
    private readonly ConcurrentDictionary<string, string> cacheErrors = new();
    private readonly object cacheLoopGate = new();
    private Func<LazyObjectRequest, CancellationToken, Task<byte[]>>? objectProvider;
    private PrefetchSettings prefetch = new();
    private readonly object prefetchGate = new();
    private readonly HashSet<CoreDisk> prefetchCores = new();
    private readonly ConcurrentDictionary<string, CoreDisk> openingCores = new();
    private long prefetchRevision = 1;
    public void SetObjectProvider(Func<LazyObjectRequest, CancellationToken, Task<byte[]>>? provider)
    {
        Volatile.Write(ref objectProvider, provider);
        if (provider is null)
            foreach (var disk in controller.Disks)
                try { controller.TryGetCore(disk.Id)?.Control(new { cmd = "cache.online", available = false }); }
                catch (ObjectDisposedException) { }
                catch (IOException error) { cacheErrors[disk.Id] = error.Message; }
    }
    private void StartCacheLoop(CoreDisk core)
    {
        string id = core.Id.ToString();
        lock (cacheLoopGate)
            if (!cacheLoops.TryGetValue(id, out var previous) || previous.IsCompleted) cacheLoops[id] = new DiskCacheQuotaLoop(() => core.Control(new { cmd = "cache.status" }),
                () => core.Control(new { cmd = "cache.step", max_objects = 4 }), () => Volatile.Read(ref objectProvider) is not null);
    }
    private async Task StopCacheLoopAsync(string id)
    {
        if (!cacheLoops.TryGetValue(id, out var loop)) return;
        await loop.StopAsync();
        lock (cacheLoopGate)
            if (cacheLoops.TryGetValue(id, out var current) && ReferenceEquals(current, loop)) cacheLoops.TryRemove(id, out _);
    }
    private void ConfigureCore(CoreDisk core)
    {
        string id = core.Id.ToString();
        core.BeforeDispose = () =>
        {
            lock (prefetchGate) prefetchCores.Remove(core);
            StopCacheLoopAsync(id).GetAwaiter().GetResult();
        };
        PrefetchSettings choice; long revision;
        lock (prefetchGate) { prefetchCores.Add(core); choice = prefetch; revision = prefetchRevision; }
        core.ConfigurePrefetch(choice, revision);
        core.SetObjectProvider((request, ct) =>
            (Volatile.Read(ref objectProvider) ?? throw new IOException("主界面连接已断开，未缓存的云端内容暂时不可读取。"))(request, ct));
        var info = core.GetInfo();
        if (!info.TryGetProperty("restore_incomplete", out var incomplete) || !incomplete.GetBoolean())
        {
            openingCores[id] = core;
            try { EnsureReplicaIdentity(core); }
            finally { openingCores.TryRemove(id, out _); }
            StartCacheLoop(core);
        }
    }
    private readonly string localRestoreCatalog;
    private readonly ConcurrentDictionary<string, LocalRestoreRecord> localRestores = new();
    private readonly object restoreCatalogGate = new();
    private sealed class LocalRestoreRecord
    {
        public string Id { get; set; } = "";
        public string TargetId { get; set; } = "";
        public string SourceId { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string SourceSnapshotId { get; set; } = "";
        public string TargetPath { get; set; } = "";
        public string Name { get; set; } = "";
        public bool TargetEncrypted { get; set; }
        public bool SourceEncrypted { get; set; }
        public bool Complete { get; set; }
    }
    public WorkerApplicationService(string? settingsDirectory = null) : this(settingsDirectory, null) { }
    internal WorkerApplicationService(string? settingsDirectory, Func<Guid, CancellationToken, Task<bool>>? mountedIdentityProbe)
    {
        this.mountedIdentityProbe = mountedIdentityProbe ?? DiskProvisioner.HasMountedIdentityAsync;
        string directory = settingsDirectory ?? SettingsStorage.DirectoryPath;
        controller = new DiskController(directory);
        controller.CoreInitializer = ConfigureCore;
        localRestoreCatalog = Path.Combine(directory, "snapshot-restores.json");
        if (!File.Exists(localRestoreCatalog)) return;
        var records = JsonSerializer.Deserialize<List<LocalRestoreRecord>>(File.ReadAllText(localRestoreCatalog), Json)
            ?? throw new IOException("本地快照恢复任务文件无效，请保留后检查。");
        foreach (var record in records)
        {
            if (!Guid.TryParse(record.Id, out _) || !Guid.TryParse(record.SourceId, out _) || !Path.IsPathFullyQualified(record.TargetPath))
                throw new IOException("本地快照恢复任务身份无效，请保留后检查。");
            if (!localRestores.TryAdd(record.Id, record)) throw new IOException("重复恢复任务身份。");
            maintenance[record.Id] = new Maintenance { Id = record.Id, DiskId = record.SourceId, Kind = "snapshotRestore", State = record.Complete ? "done" : "paused" };
        }
    }
    private void SaveLocalRestores()
    {
        lock (restoreCatalogGate)
        {
        string temporary = localRestoreCatalog + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, localRestores.Values.ToArray(), Json); stream.Flush(true); }
        File.Move(temporary, localRestoreCatalog, true);
        }
    }
    private readonly ConcurrentDictionary<string, SemaphoreSlim> commandGates = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> cloudGates = new();
    private SemaphoreSlim CommandFor(string id) => commandGates.GetOrAdd(id, _ => new(1, 1));
    private string CommandId(JsonElement args)
    {
        string task = Text(args, "taskId");
        if (localRestores.TryGetValue(task, out var record)) return record.SourceId;
        return Text(args, "id", Text(args, "path", Text(args, "containerPath", "$catalog")));
    }
    private static bool IsQuery(string method) => method is "state" or "snapshots.list" or "blocks.list" or "blocks.summary" or "blocks.query" or "blocks.changes" or "sync.diagnostics" or "compact.status" or "cloud.status" or "cloud.delta" or "cloud.transfer" or "cloud.objects" or "cloud.commitBytes" or "cloud.gc_candidates" or "restore.status" or "lazy.status" or "cache.status" or "cache.source";
    private readonly ConcurrentDictionary<string, Maintenance> maintenance = new();
    private readonly ConcurrentDictionary<string, RestoreSession> restores = new();
    private sealed class Maintenance
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string DiskId = "";
        public string Kind = "compact";
        public string Mode = "normal";
        public ulong LogicalPagesChanged;
        public string State = "running";
        public string? Error;
        private readonly object progressGate = new();
        private JsonElement progress;
        public JsonElement Progress { get { lock (progressGate) return progress; } set { lock (progressGate) progress = value; } }
        public CancellationTokenSource Cancellation = new();
        public Task? Task;
    }
    private sealed record RestoreSession(CoreDisk Core, string Path, string Name, bool Local, string Mode = "copy", Guid SourceId = default);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Text(JsonElement a, string name, string fallback = "") => a.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : fallback;
    private static string? Secret(JsonElement a) => a.TryGetProperty("password", out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(p.GetString()) ? p.GetString() : null;
    private static int Number(JsonElement a, string n, int fallback) => a.TryGetProperty(n, out var p) && p.TryGetInt32(out var x) ? x : fallback;
    private static bool? OptionalBool(JsonElement args, string name) => args.TryGetProperty(name, out var value)
        ? value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new IOException("磁盘设置字段无效：" + name) : null;
    private DiskEntry Entry(JsonElement args) => controller.Disks.SingleOrDefault(d => d.Id == Text(args, "id")) ?? throw new IOException("找不到磁盘。");
    private CoreDisk Core(DiskEntry entry) => controller.TryGetCore(entry.Id) ?? throw new IOException("请先解锁磁盘。");
    private static object? Value(JsonElement value) => JsonSerializer.Deserialize<object>(value.GetRawText());

    public async Task<object?> InvokeAsync(string method, JsonElement args, CancellationToken cancellationToken)
    {
        if (method == "snapshots.restorePause") return await PauseLocalRestoreAsync(args, cancellationToken);
        if (IsQuery(method) || method == "replica.status") return await Task.Run(() => DispatchAsync(method, args, cancellationToken), cancellationToken);
        var commands = method.StartsWith("cloud.", StringComparison.Ordinal)
            ? cloudGates.GetOrAdd(CommandId(args), _ => new(1, 1)) : CommandFor(CommandId(args));
        await commands.WaitAsync(cancellationToken);
        try { return await DispatchAsync(method, args, cancellationToken); }
        finally { commands.Release(); }
    }
    public async Task<WorkerBulkResult> InvokeBulkAsync(string method, JsonElement args, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (method == "export.read")
        {
            if (!bytes.IsEmpty) throw new IOException("导出读取请求不应包含数据。");
            var core = Core(Entry(args));
            return new(null, await Task.Run(() => core.ReadExport(Text(args, "job_id"), Text(args, "object_id")), ct));
        }
        if (method is not ("restore.begin" or "restore.accept" or "replica.stage") || !CloudObjectGeometry.IsSupported(bytes.Length))
            throw new IOException("恢复对象长度或操作无效。");
        string id = Text(args, "id", Text(args, "taskId", Text(args, "path")));
        var commands = CommandFor(id);
        await commands.WaitAsync(ct);
        try
        {
            if (method == "restore.begin") return new(await BeginRestoreAsync(args, bytes, ct), Array.Empty<byte>());
            if (method == "replica.stage") return new(StageReplica(args, bytes), Array.Empty<byte>());
            if (!restores.TryGetValue(id, out var session)) throw new IOException("恢复任务尚未打开。");
            if (bytes.Length != session.Core.ObjectSizeBytes) throw new IOException("恢复对象大小与磁盘不一致。");
            session.Core.AcceptRestoreObject(Text(args, "object_id"), bytes.ToArray());
            return new(new { ok = true }, Array.Empty<byte>());
        }
        finally { commands.Release(); }
    }

    private async Task<object?> DispatchAsync(string method, JsonElement args, CancellationToken ct)
    {
        if (method == "state") return GetState();
        if (method == "prefetch.configure")
        {
            var choice = args.Deserialize<PrefetchSettings>(Json) ?? throw new IOException("缺少预取设置。");
            choice.Validate(); CoreDisk[] cores; long revision;
            lock (prefetchGate) { prefetch = choice; revision = ++prefetchRevision; cores = prefetchCores.ToArray(); }
            foreach (var activeCore in cores)
                try { activeCore.ConfigurePrefetch(choice, revision); }
                catch (ObjectDisposedException) { lock (prefetchGate) prefetchCores.Remove(activeCore); }
            return new { ok = true };
        }
        if (method is "cache.source" or "cache.status")
        {
            // A restore.finish callback can require this query while its mutation
            // gate is held. Never route it through that gate or a storage I/O barrier.
            string sourceId = Text(args, "id");
            var current = restores.TryGetValue(sourceId, out var restoring) ? restoring.Core
                : openingCores.TryGetValue(sourceId, out var opening) ? opening : Core(Entry(args));
            return current.Control(new { cmd = method, object_id = Text(args, "object_id") });
        }
        if (method == "restore.preflight")
        {
            string path = Path.GetFullPath(Text(args, "path")); CheckTarget(path);
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("恢复目标已存在，请另选文件名。");
            string mode = Text(args, "mode", "copy");
            if (mode is not ("copy" or "original")) throw new IOException("恢复模式无效。");
            if (mode == "original")
            {
                if (!Guid.TryParse(Text(args, "sourceVolumeId"), out var source) || source == Guid.Empty) throw new IOException("缺少有效的原磁盘身份。");
                var identity = originalRestoreGates.GetOrAdd(source, _ => new(1, 1));
                await identity.WaitAsync(ct);
                try { await EnsureOriginalIdentityAvailableAsync(source, null, ct); }
                finally { identity.Release(); }
            }
            return new { ok = true };
        }
        if (method == "snapshots.restoreResume") return await ResumeLocalRestoreAsync(args);
        if (method == "driver.install") { await controller.InstallDriverAsync(); return new { ok = true }; }
        if (method == "disks.create")
        {
            var name = Text(args, "name"); var path = Text(args, "containerPath");
            ulong capacity = args.GetProperty("capacityBytes").GetUInt64();
            char letter = Text(args, "driveLetter", "Z")[0]; bool encrypted = args.GetProperty("encrypted").GetBoolean();
            uint objectSize = args.TryGetProperty("objectSizeBytes", out var size) ? size.GetUInt32() : CloudObjectGeometry.DefaultSize;
            CloudObjectGeometry.Validate(checked((int)objectSize));
            await controller.CreateAsync(new(name, path, capacity, letter, encrypted, OptionalBool(args, "readOnly") ?? false, objectSize), Secret(args)); return GetState();
        }
        if (method == "disks.import") { await controller.ImportAsync(Text(args, "path")); return GetState(); }
        if (method is "restore.begin" or "restore.accept" or "cloud.read") throw new IOException("对象数据必须通过独立传输通道发送。");
        if (method.StartsWith("restore.", StringComparison.Ordinal)) return await RestoreActionAsync(method, args);
        var entry = Entry(args);
        if (method.StartsWith("replica.", StringComparison.Ordinal)) return await ReplicaActionAsync(method, entry, args, ct);
        if (!IsQuery(method)) RequireNoReplicaChange(entry, method is "disks.close" or "disks.quiesce");
        if (method == "disks.delete")
        {
            static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            if (localRestores.Values.Any(r => !r.Complete && (r.SourceId == entry.Id || r.TargetId == entry.Id || SamePath(r.TargetPath, entry.ContainerPath)))
                || restores.Values.Any(r => SamePath(r.Path, entry.ContainerPath))
                || maintenance.Values.Any(t => t.DiskId == entry.Id && t.Task is { IsCompleted: false }))
                throw new IOException("这块磁盘仍被后台整理或恢复任务使用，请完成相关任务后再删除。");
            bool delete = args.TryGetProperty("deleteContainer", out var permanent) && permanent.ValueKind == JsonValueKind.True;
            await controller.DeleteAsync(entry, delete, Text(args, "confirmName"));
            maintenance.TryRemove(entry.Id, out _);
            return new { deletedId = entry.Id, containerDeleted = delete, containerPath = entry.ContainerPath };
        }
        if (method == "disks.mount")
        {
            if (controller.TryGetCore(entry.Id) is { } mounting) EnsureReplicaIdentity(mounting);
            var selected = OptionalBool(args, "readOnly");
            if (selected is { } mode && mode != entry.ReadOnly)
            {
                if (entry.Mounted) throw new IOException("请先安全卸载，再切换挂载模式。");
                await PauseMaintenanceAsync(entry.Id);
            }
            await controller.MountAsync(entry, Secret(args), selected); StartCacheLoop(Core(entry)); return GetState();
        }
        if (method == "disks.unlock") { await controller.UnlockAsync(entry, Secret(args)); StartCacheLoop(Core(entry)); return GetState(); }
        if (method == "disks.flush") { await controller.FlushFileSystemAsync(entry); return new { ok = true }; }
        if (method == "disks.quiesce") { await StopCacheLoopAsync(entry.Id); await PauseMaintenanceAsync(entry.Id); await controller.QuiesceAsync(entry); return GetState(); }
        if (method == "disks.close") { await StopCacheLoopAsync(entry.Id); await PauseMaintenanceAsync(entry.Id); await controller.CloseAsync(entry); return GetState(); }
        if (method == "disks.settings")
        {
            var selected = OptionalBool(args, "readOnly");
            if (selected is { } mode && mode != entry.ReadOnly)
            {
                if (entry.Mounted) throw new IOException("请先安全卸载，再切换挂载模式。");
                await PauseMaintenanceAsync(entry.Id);
            }
            await controller.UpdateSettingsAsync(entry, Text(args, "name"), Text(args, "driveLetter", "Z")[0], selected); return GetState();
        }
        if (method == "disks.open") { controller.OpenFolder(entry); return new { ok = true }; }
        var core = Core(entry);
        switch (method)
        {
            case "cache.configure": case "cache.bind": case "cache.online": case "cache.step":
            {
                var request = args.EnumerateObject().Where(p => p.Name != "id").ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
                request["cmd"] = method;
                if (method == "cache.online" && OptionalBool(args, "available") == true) ConfigureCore(core);
                var result = core.Control(request); ConfigureCore(core); cacheErrors.TryRemove(entry.Id, out _); StartCacheLoop(core); return result;
            }
            case "snapshots.list":
            {
                var snapshots = new List<JsonElement>(); long cursor = 0; bool more = false;
                do
                {
                    var page = core.Control(new { cmd = "snapshot.list", cursor, limit = Math.Min(128, 1024 - snapshots.Count) });
                    snapshots.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.Clone()));
                    more = page.TryGetProperty("next_cursor", out var next) && next.ValueKind == JsonValueKind.Number;
                    if (more) { long nextCursor = next.GetInt64(); if (nextCursor <= cursor) throw new IOException("快照分页游标无效。"); cursor = nextCursor; }
                } while (more && snapshots.Count < 1024);
                return new { items = snapshots, truncated = more };
            }
            case "snapshots.create": await controller.FlushFileSystemAsync(entry); return core.Control(new { cmd = "snapshot.create", name = Text(args, "name") });
            case "snapshots.rename": return core.Control(new { cmd = "snapshot.rename", id = Text(args, "snapshotId"), name = Text(args, "name") });
            case "snapshots.delete": return core.Control(new { cmd = "snapshot.delete", id = Text(args, "snapshotId") });
            case "snapshots.restore": return StartLocalRestore(entry, core, args);
            case "blocks.list":
            {
                int limit = Math.Clamp(Number(args, "limit", 128), 1, 128); int start = Math.Max(0, Number(args, "start", 0));
                var result = core.Control(new { cmd = "debug.blocks", page = start / limit, page_size = limit, view = "physical" });
                return new { items = result.GetProperty("items"), total = result.GetProperty("total_count") };
            }
            case "reclaim.start": case "reclaim.resume":
            {
                core.RequireWritable();
                if (!entry.Mounted) throw new IOException("手动 TRIM 需要先挂载磁盘，并关闭盘内打开的文件。");
                if (maintenance.TryGetValue(entry.Id,out var running)&&running.Task is {IsCompleted:false}) throw new IOException("已有本地维护任务正在运行，请先暂停或完成。");
                var compact = core.Control(new { cmd = "compact.status" });
                if (Text(compact, "state") is "running" or "paused" && Text(compact, "mode") == "deep")
                    throw new IOException("已有深度整理任务尚未完成。请先继续该任务，手动回收不会自动启动深度整理。");
                var reclaim=new Maintenance {DiskId=entry.Id,Kind="reclaim"};maintenance[entry.Id]=reclaim;
                reclaim.Task=Task.Run(()=>ReclaimLoopAsync(entry,core,reclaim));return new {taskId=reclaim.Id};
            }
            case "reclaim.pause": await PauseMaintenanceAsync(entry.Id); return new {ok=true};
            case "compact.start": case "compact.resume":
            {
                string requested = Text(args, "mode", "normal");
                if (requested is not ("normal" or "deep")) throw new IOException("存储整理模式无效。");
                var saved = core.Control(new { cmd = "compact.status" });
                bool unfinished = Text(saved, "state") is "running" or "paused";
                string actualMode = unfinished ? Text(saved, "mode", "normal") : requested;
                if (method == "compact.start" && unfinished && actualMode != requested)
                    throw new IOException("已有另一种整理任务尚未完成；请继续当前任务，不能悄然切换整理模式。");
                if (maintenance.TryGetValue(entry.Id, out var previous) && previous.Task is { IsCompleted: false })
                {
                    if (previous.Kind != "compact") throw new IOException("当前磁盘已有其他维护任务运行，请先等待完成或暂停。");
                    return saved;
                }
                if (method == "compact.start") saved = core.Control(new { cmd = "compact.start", mode = requested });
                core.Control(new { cmd = "compact.pause", paused = false });
                var work = new Maintenance { DiskId = entry.Id, Mode = actualMode, Progress = saved };
                maintenance[entry.Id] = work;
                work.Task = Task.Run(() => CompactLoopAsync(core, work)); return new { taskId = work.Id };
            }
            case "compact.pause": await PauseMaintenanceAsync(entry.Id); return core.Control(new { cmd = "compact.pause", paused = true });
            case "compact.cancel":
            {
                await PauseMaintenanceAsync(entry.Id);
                var previous = core.Control(new { cmd = "compact.status" });
                var result = core.Control(new { cmd = "compact.cancel" });
                var stopped = maintenance.GetOrAdd(entry.Id, _ => new Maintenance { DiskId = entry.Id });
                stopped.Progress = previous; stopped.Mode = Text(previous, "mode", stopped.Mode);
                stopped.State = "cancelled"; stopped.Error = null;
                return result;
            }
            case "compact.status": return core.Control(new { cmd = "compact.status" });
            case "lazy.status": return core.GetLazyStatus();
            case "sync.diagnostics":
                var diagnostic = core.Control(new { cmd = "sync.diagnostics" }).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
                diagnostic["managedFlush"] = controller.FlushDiagnostics(entry.Id); return diagnostic;
            case "blocks.summary": case "blocks.query": case "blocks.changes":
                var query = args.EnumerateObject().Where(p => p.Name != "id").ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
                query["cmd"] = method; return core.Control(query);
            case "cloud.delta": case "cloud.transfer": case "cloud.bind": case "cloud.status": case "cloud.prepare": case "cloud.objects": case "cloud.receipts": case "cloud.commit": case "cloud.abandon": case "cloud.commitBytes": case "cloud.gc_candidates":
            {
                var request = args.EnumerateObject().Where(p => p.Name != "id").ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
                request["cmd"] = method switch { "cloud.objects" => "cloud.list", "cloud.receipts" => "cloud.receipt", "cloud.abandon" => "cloud.pause", "cloud.commitBytes" => "cloud.published_objects", _ => method };
                if (method == "cloud.abandon" && !request.ContainsKey("paused")) request["paused"] = true;
                return core.Control(request);
            }
            default: throw new IOException("不支持的磁盘操作。");
        }
    }
    private object GetState()
    {
        var items = controller.Disks.Select(d =>
        {
            var core = controller.TryGetCore(d.Id); JsonElement? info = null, cloud = null, compact = null, lazy = null, cache = null, replica = null;
            if (core != null)
            {
                try { info = core.GetInfo(); cloud = core.Control(new { cmd = "cloud.status" }); compact = core.Control(new { cmd = "compact.status" }); lazy = core.GetLazyStatus(); cache = core.Control(new { cmd = "cache.status" }); replica = core.Control(new { cmd = "replica.status" }); }
                catch (Exception e) { d.Status = e.Message; }
            }
            if (compact is { } saved && Text(saved, "state") is "running" or "paused" && !maintenance.ContainsKey(d.Id))
                maintenance.TryAdd(d.Id, new Maintenance { DiskId = d.Id, State = "paused", Progress = saved, Mode = Text(saved, "mode", "normal") });
            return new { d.Id, d.Name, d.ContainerPath, d.CapacityBytes, d.ObjectSizeBytes, d.DriveLetter, d.Encrypted, d.ReadOnly, d.Initialized, d.FormatVersion, d.Mounted, d.Unlocked, d.Status, info, cloud, compact, lazy, cache, replica,
                cacheError = (cacheLoops.TryGetValue(d.Id, out var cacheLoop) ? cacheLoop.Error : null) ?? cacheErrors.GetValueOrDefault(d.Id), hydration = core?.GetHydrationState() };
        }).ToArray();
        return new { connected = true, driverAvailable = controller.DriverAvailable, disks = items, tasks = maintenance.Values.Select(TaskView).ToArray() };
    }
    private object TaskView(Maintenance m)
    {
        var p = m.Progress;
        if (p.ValueKind != JsonValueKind.Object) p = JsonSerializer.SerializeToElement(new { });
        long Read(string n) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(n, out var v) && v.TryGetInt64(out var value) ? value : 0;
        localRestores.TryGetValue(m.Id, out var restore);
        bool hasTarget = restores.ContainsKey(m.Id);
        bool physical = m.Kind == "compact" || m.Kind == "reclaim" && p.TryGetProperty("processed_objects", out _);
        string mode = Text(p, "mode", m.Mode), label = mode == "deep" ? "深度整理" : "普通整理";
        long processed = Read("processed_objects"), budget = Read("total_objects") != 0 ? Read("total_objects") : Read("total_pages");
        string message = physical ? $"{label}：已处理 {processed} 个对象，回收 {Read("reclaimed_bytes")} 字节，搬移 {Read("moved_bytes")} 字节，截短 {Read("truncated_bytes")} 字节"
            : Text(p, "message", "正在后台处理");
        if (m.State == "paused") message = "已暂停，可继续 · " + message;
        else if (m.State == "done") message = "已完成 · " + message;
        else if (m.State == "cancelled") message = "已停止本次整理，已完成部分保留 · " + message;
        return new { id = m.Id, diskId = m.DiskId, kind = m.Kind, title = m.Kind == "compact" ? label : m.Kind == "reclaim" ? "手动回收空闲空间" : "恢复快照" + (restore is null ? "" : " · " + restore.Name), state = m.State,
            mode, processed_objects = processed, reclaimed_bytes = Read("reclaimed_bytes"), moved_bytes = Read("moved_bytes"), truncated_bytes = Read("truncated_bytes"), logicalPagesChanged = m.LogicalPagesChanged,
            message = m.Error ?? message, progressUnit = physical ? "objects" : m.Kind == "reclaim" ? "clusters" : "pages",
            completed = physical ? processed : Read(m.Kind == "snapshotRestore" ? "completed_pages" : "scanned_pages"), total = physical ? budget : Read("total_pages") != 0 ? Read("total_pages") : Read("expected_pages"),
            canPause = m.State == "running", canResume = m.State == "paused", error = m.Error,
            requiresPassword = restore is { TargetEncrypted: true, Complete: false } && !hasTarget,
            sourceRequiresPassword = restore is { SourceEncrypted: true, Complete: false } && !hasTarget && controller.TryGetCore(restore.SourceId) is null };

    }
    private async Task ReclaimLoopAsync(DiskEntry entry, CoreDisk core, Maintenance work)
    {
        try
        {
            work.Progress=JsonSerializer.SerializeToElement(new {state="running",phase="ntfs_lock",message="正在锁定文件系统并核对空闲簇"});
            var cleared=await controller.ReclaimFileSystemAsync(entry,(done,total)=>work.Progress=JsonSerializer.SerializeToElement(new {state="running",phase="ntfs_scan",message="正在手动回收 NTFS 空闲簇",scanned_pages=done,total_pages=total}),work.Cancellation.Token);
            work.LogicalPagesChanged = cleared.ClearedPages;
            work.Progress=JsonSerializer.SerializeToElement(new {state="running",phase="compacting",message=$"已标记 {cleared.ClearedPages} 个页可回收，开始整理容器"});
            core.Control(new {cmd="compact.start",mode="normal"});core.Control(new {cmd="compact.pause",paused=false});
            await CompactLoopAsync(core,work);
        }
        catch (OperationCanceledException) {work.State="paused";}
        catch (Exception error) {work.Error=error.Message;work.State="paused";}
    }

    private async Task CompactLoopAsync(CoreDisk core, Maintenance work)
    {
        try
        {
            while (!work.Cancellation.IsCancellationRequested)
            {
                work.Progress = core.Control(new { cmd = "compact.step", max_objects = 4 });
                string state = Text(work.Progress, "state");
                if (state is "done" or "idle") { work.State = "done"; return; }
                await Task.Delay(25, work.Cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { work.Error = error.Message; }
        work.State = "paused";
        try { core.Control(new { cmd = "compact.pause", paused = true }); } catch { }
    }
    private async Task PauseMaintenanceAsync(string diskId)
    {
        var work = maintenance.Values.Where(task => task.DiskId == diskId).ToArray();
        foreach (var task in work) task.Cancellation.Cancel();
        // Local restore's wait to finalize under commands is cancellable. Consequently a
        // source close may wait here while holding commands without forming a lock cycle.
        await Task.WhenAll(work.Where(task => task.Task is not null).Select(task => task.Task!));
        foreach (var task in work.Where(task => task.Kind == "snapshotRestore"))
        {
            if (!restores.TryGetValue(task.Id, out var session)) continue;
            session.Core.Flush(); session.Core.Dispose(); restores.TryRemove(task.Id, out _);
        }
    }
    private static void CheckTarget(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".odv4", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network) throw new IOException("请选择本地 .odv4 文件路径。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }
    private async Task<object> BeginRestoreAsync(JsonElement args, ReadOnlyMemory<byte> rootObject, CancellationToken ct)
    {
        int objectSize = args.TryGetProperty("objectSizeBytes", out var size) ? size.GetInt32() : rootObject.Length;
        CloudObjectGeometry.Validate(objectSize);
        if (rootObject.Length != objectSize) throw new IOException("云端根对象大小与所选版本不一致。");
        string path = Path.GetFullPath(Text(args, "path")); CheckTarget(path);
        string name = ValidateRestoreName(Text(args, "name", "恢复磁盘"));
        string mode = Text(args, "mode", "copy");
        if (mode is not ("copy" or "original")) throw new IOException("恢复模式只能为独立副本或接回原磁盘。");
        string sourceText = Text(args, "sourceVolumeId");
        Guid? expectedSource = Guid.TryParse(sourceText, out var sourceId) && sourceId != Guid.Empty ? sourceId : null;
        if ((mode == "original" || sourceText.Length != 0) && expectedSource is null) throw new IOException("缺少有效的原磁盘身份。");
        bool resume = args.TryGetProperty("resume", out var r) && r.GetBoolean();
        bool lazy = args.TryGetProperty("lazy", out var lazyFlag) && lazyFlag.ValueKind == JsonValueKind.True;
        if (mode == "original" && (!args.TryGetProperty("publication", out var originalPublication) || originalPublication.ValueKind != JsonValueKind.Object))
            throw new IOException("接回原磁盘需要经过确认的云端发布记录和写入身份。");
        var optionValues = new Dictionary<string, object?> { ["mode"] = mode, ["lazy"] = lazy };
        if (expectedSource is { } claimedSource) optionValues["source_volume_id"] = claimedSource.ToString();
        foreach (string key in new[] { "backing", "publication" })
            if (args.TryGetProperty(key, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) optionValues[key] = value.Clone();
        var options = JsonSerializer.SerializeToElement(optionValues);
        CoreDisk Begin(byte[] root) => CoreDisk.BeginRestore(path, root, Secret(args), options);
        var identityGate = mode == "original" ? originalRestoreGates.GetOrAdd(expectedSource!.Value, _ => new(1, 1)) : null;
        if (identityGate is not null) await identityGate.WaitAsync(ct);
        try
        {
        if (mode == "original") await EnsureOriginalIdentityAvailableAsync(expectedSource!.Value, null, ct, resume ? path : null);
        CoreDisk core;
        if (resume) core = new CoreDisk(path, Secret(args));
        else
        {
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("恢复目标已存在，请另选文件名。");
            byte[] root = rootObject.ToArray();
            core = Begin(root);
        }
        try
        {
            if (core.ObjectSizeBytes != objectSize) throw new IOException("恢复磁盘的块大小与云端版本不一致。");
            if (lazy) core.Control(new { cmd = "replica.bootstrap", commit = args.TryGetProperty("commit", out var declaredCommit) ? (object)declaredCommit.Clone() : null });
            ConfigureCore(core);
            var status = core.Control(new { cmd = "restore.status", cursor = 0, limit = 128 });
            if (Text(status, "kind") == "uninitialized")
            {
                byte[] verifiedRoot = rootObject.ToArray();
                // Native accepts only an authenticated empty restore bootstrap here. It keeps
                // the target identity and rejects normal disks or targets with accepted data.
                core.Dispose();
                core = Begin(verifiedRoot);
                if (core.ObjectSizeBytes != objectSize) throw new IOException("恢复磁盘的块大小与云端版本不一致。");
                if (lazy) core.Control(new { cmd = "replica.bootstrap", commit = args.TryGetProperty("commit", out var resumedCommit) ? (object)resumedCommit.Clone() : null });
                ConfigureCore(core);
                status = core.Control(new { cmd = "restore.status", cursor = 0, limit = 128 });
            }
            sourceId = ValidateCloudRestoreIdentity(core.Id, status, mode, expectedSource);
            if (core.GetLazyStatus().GetProperty("enabled").GetBoolean() != lazy)
                throw new IOException("恢复任务的按需读取方式已固定，不能在继续任务时更换。");
            if (!Text(status, "root_sha256").Equals(Convert.ToHexString(SHA256.HashData(rootObject.Span)), StringComparison.OrdinalIgnoreCase))
                throw new IOException("已有恢复任务对应另一云端版本，不能更换其固定根对象。");
            if (mode == "original") ValidateOriginalPublication(core, status, args.GetProperty("publication"));
            if (!restores.TryAdd(core.Id.ToString(), new(core, path, name, false, mode, sourceId))) throw new IOException("恢复任务已打开。");
            return new { id = core.Id.ToString(), status };
        }
        catch { core.Dispose(); throw; }
        }
        finally { identityGate?.Release(); }
    }
    internal static Guid ValidateCloudRestoreIdentity(Guid targetId, JsonElement status, string mode, Guid? expectedSource)
    {
        if (mode is not ("copy" or "original") || Text(status, "kind") != "cloud") throw new IOException("此文件不属于指定的云端恢复模式。");
        if (Text(status, "mode", "copy") != mode) throw new IOException("恢复模式与已保存任务不同，不能将原磁盘和独立副本互相切换。");
        if (!Guid.TryParse(Text(status, "source_volume_id"), out var source) || source == Guid.Empty || expectedSource is { } expected && source != expected)
            throw new IOException("恢复文件来源与所选云端磁盘不同。");
        if (mode == "original" ? targetId != source : targetId == source)
            throw new IOException("恢复后的磁盘身份不符合所选模式，已停止操作。");
        return source;
    }
    private static void ValidateOriginalPublication(CoreDisk core, JsonElement status, JsonElement publication)
    {
        var expected = publication.GetProperty("binding");
        var actual = core.Control(new { cmd = "cloud.status" }).GetProperty("binding");
        foreach (string key in new[] { "backend_id", "account_id", "remote_root", "device_id" })
            if (Text(actual, key) != Text(expected, key) || Text(expected, key).Length == 0)
                throw new IOException("原磁盘恢复任务的云端来源或写入身份与已保存记录不一致。");
        var commit = publication.GetProperty("commit");
        static bool U64(JsonElement value, string key, ulong expectedValue) => value.TryGetProperty(key, out var p) && p.TryGetUInt64(out var actualValue) && actualValue == expectedValue;
        if (!Guid.TryParse(Text(commit, "volumeId"), out var source) || source != core.Id
            || !U64(commit, "formatVersion", 4) || !U64(commit, "generation", status.GetProperty("generation").GetUInt64())
            || !U64(commit, "capacityBytes", core.Capacity) || Number(commit, "objectSizeBytes", CloudObjectGeometry.DefaultSize) != core.ObjectSizeBytes || Text(commit, "writerId") != Text(actual, "device_id")
            || Text(commit, "rootObjectId") != Text(status, "root_object_id")
            || !Text(commit, "rootSha256").Equals(Text(status, "root_sha256"), StringComparison.OrdinalIgnoreCase)
            || !commit.TryGetProperty("encrypted", out var encrypted) || encrypted.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || encrypted.GetBoolean() != core.Encrypted || commit.TryGetProperty("rootSlot", out _) && !U64(commit, "rootSlot", 0))
            throw new IOException("原磁盘恢复任务的发布版本与已保存记录不一致，不能在继续任务时更换。");
    }
    private async Task EnsureOriginalIdentityAvailableAsync(Guid sourceId, RestoreSession? own, CancellationToken ct, string? sameClosedTarget = null)
    {
        controller.EnsureIdentityNotCataloged(sourceId, sameClosedTarget ?? own?.Path);
        if (restores.Values.Any(session => !ReferenceEquals(session, own) && session.Core.Id == sourceId)
            || localRestores.Values.Any(record => !record.Complete && (record.SourceId.Equals(sourceId.ToString(), StringComparison.OrdinalIgnoreCase)
                || record.TargetId.Equals(sourceId.ToString(), StringComparison.OrdinalIgnoreCase))))
            throw new IOException("已有恢复任务引用这块原磁盘，请先完成或取消该任务，不能同时接回同一身份。");
        await ActiveDisks.Gate.WaitAsync(ct);
        try { if (ActiveDisks.Items.ContainsKey(sourceId)) throw new IOException("这块原磁盘仍由当前进程挂载，不能同时接回。"); }
        finally { ActiveDisks.Gate.Release(); }
        if (await mountedIdentityProbe(sourceId, ct)) throw new IOException("Windows 中仍挂载着同一身份的原磁盘，请先在原程序安全关闭它，再接回。");
    }
    private async Task<object?> RestoreActionAsync(string method, JsonElement args)
    {
        string id = Text(args, "id");
        if (!restores.TryGetValue(id, out var session)) throw new IOException("恢复任务尚未打开，请重新解锁恢复文件。");
        switch (method)
        {
            case "restore.status": return session.Core.Control(new { cmd = "restore.status", cursor = Number(args, "cursor", 0), limit = Math.Clamp(Number(args, "limit", 128), 1, 128) });
            case "restore.step": return session.Core.Control(new { cmd = "restore.step", max_pages = Math.Clamp(Number(args, "max_pages", 4096), 1, 4096),
                max_nodes = Math.Clamp(Number(args, "max_nodes", 256), 1, 256), max_objects = Math.Clamp(Number(args, "max_objects", 4), 1, 4) });
            case "restore.finish":
            {
                var identity = session.Mode == "original" ? originalRestoreGates.GetOrAdd(session.SourceId, _ => new(1, 1)) : null;
                if (identity is not null) await identity.WaitAsync();
                try
                {
                if (session.Mode == "original") await EnsureOriginalIdentityAvailableAsync(session.SourceId, session, CancellationToken.None);
                session.Core.Control(new { cmd = "restore.finish" });
                await FinishRestoreAsync(id, session); return new { ok = true };
                }
                finally { identity?.Release(); }
            }
            case "restore.cancel":
                session.Core.Flush(); session.Core.Dispose(); restores.TryRemove(id, out _); return new { ok = true };
            default: throw new IOException("不支持的恢复操作。");
        }
    }
    private static string ValidateRestoreName(string name)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 32 || name.Any(char.IsControl) || name.IndexOfAny("\\/:*?\"<>|".ToCharArray()) >= 0)
            throw new IOException("磁盘名称应为 1–32 个字符，且不能包含文件名特殊字符。");
        return name;
    }
    private object StartLocalRestore(DiskEntry entry, CoreDisk source, JsonElement args)
    {
        string path = Path.GetFullPath(Text(args, "path")); CheckTarget(path);
        string name = ValidateRestoreName(Text(args, "name", "快照副本"));
        if (File.Exists(path)) throw new IOException("恢复目标已存在。");
        string? password = Secret(args);
        var record = new LocalRestoreRecord { Id = Guid.NewGuid().ToString(), SourceId = entry.Id, SourcePath = entry.ContainerPath,
            SourceSnapshotId = Text(args, "snapshotId"), TargetPath = path, Name = name,
            TargetEncrypted = !string.IsNullOrEmpty(password), SourceEncrypted = source.Encrypted };
        // Persist the intent before native Begin can pin source pages. If target creation or
        // the second catalog save is interrupted, the UI can still discover the target path.
        if (!localRestores.TryAdd(record.Id, record)) throw new IOException("重复恢复任务身份。");
        try { SaveLocalRestores(); }
        catch { localRestores.TryRemove(record.Id, out _); throw; }
        var pending = new Maintenance { Id = record.Id, DiskId = entry.Id, Kind = "snapshotRestore", State = "paused" };
        maintenance[record.Id] = pending;
        CoreDisk? target = null;
        try
        {
            target = CoreDisk.BeginSnapshotRestore(source, record.SourceSnapshotId, path, password);
            record.TargetId = target.Id.ToString(); record.TargetEncrypted = target.Encrypted;
            var session = new RestoreSession(target, path, name, true); if (!restores.TryAdd(record.Id, session)) throw new IOException("恢复任务已打开。");
            target = null; // Ownership is retained even if the next metadata save fails.
            SaveLocalRestores();
            StartLocalRestoreLoop(record, session);
            return new { taskId = record.Id, id = record.TargetId };
        }
        catch (Exception error) { target?.Dispose(); pending.Error = error.Message; throw; }
    }
    private void StartLocalRestoreLoop(LocalRestoreRecord record, RestoreSession session)
    {
        var work = new Maintenance { Id = record.Id, DiskId = record.SourceId, Kind = "snapshotRestore" };
        maintenance[record.Id] = work;
        work.Task = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    work.Cancellation.Token.ThrowIfCancellationRequested();
                    var status = session.Core.Control(new { cmd = "restore.status", cursor = 0, limit = 1 });
                    if (status.TryGetProperty("source_pin_cleanup_pending", out var pendingCleanup) && pendingCleanup.ValueKind == JsonValueKind.True)
                    {
                        var commands = CommandFor(record.SourceId);
                        await commands.WaitAsync(work.Cancellation.Token);
                        try
                        {
                            var source = controller.TryGetCore(record.SourceId) ?? throw new IOException("请先解锁原磁盘，以完成恢复引用的清理。");
                            CoreDisk.ResumeSnapshotRestore(session.Core, source);
                            status = session.Core.Control(new { cmd = "restore.status", cursor = 0, limit = 1 });
                        }
                        finally { commands.Release(); }
                    }
                    work.Progress = status;
                    if (Text(status, "phase") != "complete") work.Progress = session.Core.Control(new { cmd = "snapshot.restore_step", max_pages = 256 });
                    if (Text(work.Progress, "phase") is "ready" or "complete")
                    {
                        // Pause never waits for this task while holding commands, and this wait
                        // itself is cancellable: finish/pause cannot deadlock on the command gate.
                        var commands = CommandFor(record.SourceId);
                        await commands.WaitAsync(work.Cancellation.Token);
                        try
                        {
                            if (Text(work.Progress, "phase") == "ready") session.Core.Control(new { cmd = "restore.finish" });
                            await FinishRestoreAsync(record.Id, session);
                            record.Complete = true;
                            try { SaveLocalRestores(); }
                            catch { record.Complete = false; throw; }
                            work.State = "done"; return;
                        }
                        finally { commands.Release(); }
                    }
                    await Task.Delay(25, work.Cancellation.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { work.Error = error.Message; }
            work.State = "paused";
        });
    }
    private async Task<object> PauseLocalRestoreAsync(JsonElement args, CancellationToken ct)
    {
        string id = Text(args, "taskId"); Task? pending;
        var commands = CommandFor(CommandId(args));
        await commands.WaitAsync(ct);
        try
        {
            if (!localRestores.ContainsKey(id)) throw new IOException("找不到本地快照恢复任务。");
            pending = maintenance.TryGetValue(id, out var work) ? work.Task : null;
            work?.Cancellation.Cancel();
        }
        finally { commands.Release(); }
        if (pending is not null) await pending;
        await commands.WaitAsync(ct);
        try
        {
            if (restores.TryGetValue(id, out var session)) session.Core.Flush();
            return new { ok = true };
        }
        finally { commands.Release(); }
    }
    private async Task<object> ResumeLocalRestoreAsync(JsonElement args)
    {
        string id = Text(args, "taskId");
        if (!localRestores.TryGetValue(id, out var record)) throw new IOException("找不到本地快照恢复任务。");
        if (record.Complete) return new { taskId = id, complete = true };
        if (maintenance.TryGetValue(id, out var active) && active.Task is { IsCompleted: false }) return new { taskId = id, complete = false };
        if (!restores.TryGetValue(id, out var session))
        {
            var target = new CoreDisk(record.TargetPath, NullableText(args, "targetPassword"));
            try
            {
                if (record.TargetId.Length != 0 && target.Id.ToString() != record.TargetId) throw new IOException("恢复文件身份与任务记录不一致。");
                var status = target.Control(new { cmd = "restore.status", cursor = 0, limit = 1 });
                bool reseeded = Text(status, "kind") == "uninitialized";
                if (reseeded)
                {
                    var source = await OpenRestoreSourceAsync(record, args);
                    target.Dispose();
                    target = CoreDisk.BeginSnapshotRestore(source, record.SourceSnapshotId, record.TargetPath, NullableText(args, "targetPassword"));
                    status = target.Control(new { cmd = "restore.status", cursor = 0, limit = 1 });
                    if (record.TargetId.Length != 0 && target.Id.ToString() != record.TargetId) throw new IOException("重新初始化后恢复目标身份变化，已停止。");
                }
                if (Text(status, "kind") != "local" || Text(status, "source_volume_id") != record.SourceId) throw new IOException("恢复文件来源与任务记录不一致。");
                record.TargetId = target.Id.ToString(); record.TargetEncrypted = target.Encrypted; SaveLocalRestores();
                if (!reseeded && (Text(status, "phase") != "complete" || status.TryGetProperty("source_pin_cleanup_pending", out var cleanup) && cleanup.ValueKind == JsonValueKind.True))
                    CoreDisk.ResumeSnapshotRestore(target, await OpenRestoreSourceAsync(record, args));
                session = new(target, record.TargetPath, record.Name, true);
                if (!restores.TryAdd(id, session)) throw new IOException("恢复任务已打开。");
            }
            catch { target.Dispose(); throw; }
        }
        StartLocalRestoreLoop(record, session);
        return new { taskId = id, complete = false };
    }
    private async Task<CoreDisk> OpenRestoreSourceAsync(LocalRestoreRecord record, JsonElement args)
    {
        var source = controller.Disks.SingleOrDefault(d => d.Id == record.SourceId) ?? throw new IOException("请先导入该恢复任务的原磁盘。");
        if (!SameRestorePath(source.ContainerPath, record.SourcePath)) throw new IOException("原磁盘路径已变化，请检查恢复任务。");
        if (controller.TryGetCore(source.Id) is null) await controller.UnlockAsync(source, NullableText(args, "sourcePassword"));
        return controller.TryGetCore(source.Id)!;
    }
    private static bool SameRestorePath(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    private static string? NullableText(JsonElement args, string name) => Text(args, name) is { Length: > 0 } value ? value : null;
    private async Task FinishRestoreAsync(string id, RestoreSession session)
    {
        // A copied managed MBR gets a new disk signature so Windows can mount it
        // alongside its source. Only the newly restored private target is changed.
        if (session.Mode == "copy")
        {
            if (!session.Local && session.Core.GetLazyStatus().GetProperty("enabled").GetBoolean())
            {
                EnsureReplicaIdentity(session.Core);
            }
            else DiskIdentityRewriter.AssignCopyIdentity(session.Core);
        }
        session.Core.Flush(); session.Core.Dispose(); restores.TryRemove(id, out _);
        string volumeId = session.Core.Id.ToString();
        var entry = controller.Disks.SingleOrDefault(d => d.Id == volumeId);
        if (entry is null)
        {
            await controller.ImportAsync(session.Path);
            entry = controller.Disks.Single(d => d.Id == volumeId);
        }
        if (!string.Equals(Path.GetFullPath(entry.ContainerPath), Path.GetFullPath(session.Path), StringComparison.OrdinalIgnoreCase))
            throw new IOException("恢复文件与列表中同名身份的容器路径不一致。");
        if (entry.Name != session.Name) await controller.UpdateSettingsAsync(entry, session.Name, entry.DriveLetter);
    }
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(cacheLoops.Keys.Select(StopCacheLoopAsync));
        foreach (var work in maintenance.Values) work.Cancellation.Cancel();
        await Task.WhenAll(maintenance.Values.Where(m => m.Task != null).Select(m => m.Task!));
        await controller.UnmountAllAsync();
        foreach (var restore in restores.Values) { restore.Core.Flush(); restore.Core.Dispose(); }
        restores.Clear(); controller.Dispose();
    }
}
