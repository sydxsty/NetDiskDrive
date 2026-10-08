using System.Collections.Concurrent;
using System.Text.Json;
using OverlayDisk.Cloud.Baidu;
using OverlayDisk.Cloud.Contracts;
using OverlayDisk.Cloud.Sync;
using OverlayDisk.WebHost;
using OverlayDisk.Worker;

namespace OverlayDisk.Services;

public sealed partial class ApplicationService : IApplicationService
{
    private readonly PrivilegedWorkerClient worker = new();
    private readonly AppSettings settings = SettingsStorage.Load();
    private readonly FileCloudSyncCache syncCache = new(Path.Combine(SettingsStorage.DirectoryPath, "cloud-cache"));
    private readonly ActivityJournal? journal;
    private string? journalWarning;
    private readonly ConcurrentDictionary<string, string> observedTasks = new();
    private readonly object gate = new(), jobGate = new();
    private readonly HashSet<string> deletingDisks = new();
    private bool networkAdmissionsOpen = true;
    private readonly SemaphoreSlim stateGate = new(1, 1), accountGate = new(1, 1);
    private readonly SemaphoreSlim settingsGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, JobRun> runs = new();
    private readonly ConcurrentDictionary<string, bool> pendingMaintenance = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> maintenanceAfter = new();
    private readonly ConcurrentDictionary<long, Task<byte[]>> cloudReads = new();
    private CancellationTokenSource cloudReadLifetime = new();
    private long cloudReadSequence;
    private bool cloudReadsOpen = true;
    private readonly string sessionPath = SettingsStorage.SessionPath;
    private readonly Task initialization, automatic;
    private BaiduClient? client;
    private CloudAccountInfo? account;
    private JsonElement workerState;
    private string? notice;
    private bool allowUnsyncedExit, exiting, stopped;
    private DateTimeOffset lastAutomatic = DateTimeOffset.MinValue;
    private sealed class JobRun
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string DiskId = "";
        public string Kind = "sync";
        public string Title = "云同步";
        public TransferProgress Progress = new("queued", "等待处理", 0, 0, 0);
        public CancellationTokenSource Cancellation = new();
        public Task? Task;
        public bool RequiresPassword;
        public long FileSystemFlushMs;
        public DateTimeOffset PhaseStartedUtc = DateTimeOffset.UtcNow;
        public long? CompletedPages, TotalPages, CompletedIndexNodes;
        public long DownloadedBytes;
        public bool ReadyToMount;
        public string? VerifiedScope;
    }
    public event EventHandler? StateChanged;
    public ApplicationService()
    {
        BaiduRequestScheduler.Shared.Configure(new BaiduRequestLimits(settings.BaiduRequestsPerSecond, settings.BaiduMaximumConcurrentRequests));
        worker.Prefetch = settings.Prefetch ?? new();
        worker.ObjectProvider = ReadLazyObjectAsync;
        try { journal = new ActivityJournal(Path.Combine(SettingsStorage.DirectoryPath, "activity")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { journalWarning = "日志目录无法打开，磁盘操作仍可继续：" + error.Message; }
        worker.ConnectionLost += (_, _) => { notice = "磁盘服务连接中断，未完成任务会在重连后检查。"; Changed(); };
        foreach (var r in settings.Restores.Values.Where(x => !x.Complete && !x.ContainerDeleted))
            runs[r.Id] = new JobRun { Id = r.Id, Kind = "restore", Title = (r.Mode == "original" ? "恢复原硬盘 · " : "新建云端副本 · ") + r.Name, RequiresPassword = r.Commit.Encrypted, Progress = new("paused", "恢复尚未完成，点击继续", 0, 0, 0) };
        initialization = Task.Run(InitializeAsync);
        automatic = Task.Run(AutomaticLoopAsync);
    }
    private void Changed() => StateChanged?.Invoke(this, EventArgs.Empty);
    private void Save() { lock (gate) SettingsStorage.Save(settings); }
    private void Log(string diskId, string runId, string kind, string action, string message, string level = "info",
        string? objectId = null, string? objectKind = null, long? bytes = null, ulong? generation = null, DateTimeOffset? timestampUtc = null, long? wireBytes = null)
    {
        try { journal?.Append(diskId, runId, kind, action, message, level, objectId, objectKind, bytes, generation, timestampUtc, wireBytes); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException) { journalWarning = "日志写入失败，上传回执仍单独保存在容器中：" + error.Message; }
    }
    private BaiduClient NewClient(BaiduCookieSession session) => new(session, options: new BaiduClientOptions
    {
        AssumeExclusiveWriter = true,
        MetadataCacheDirectory = Path.Combine(SettingsStorage.DirectoryPath, "baidu-metadata"),
        Diagnostic = d => Log("", "", "network", "api." + d.Operation, "网盘接口：" + d.Operation + "，状态：" + d.Code, d.Operation == "retry" ? "warning" : "error")
    });
    private static JsonElement Element(object? value) => value is JsonElement e ? e : JsonSerializer.SerializeToElement(value, SettingsStorage.Json);
    private static string Text(JsonElement args, string name, string fallback = "") => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : fallback;
    private static string? Password(JsonElement args) => string.IsNullOrEmpty(Text(args, "password")) ? null : Text(args, "password");
    private static bool Flag(JsonElement args, string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.True;
    private static ulong UInt(JsonElement args, string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var e) && e.TryGetUInt64(out var value) ? value : 0;
    private async Task InitializeAsync()
    {
        try
        {
            var session = await WindowsSessionVault.LoadAsync(sessionPath, lifetime.Token);
            if (session is null) return;
            var candidate = NewClient(session);
            CloudAccountInfo verified;
            try { verified = await candidate.ValidateAsync(lifetime.Token); }
            catch { await candidate.DisposeAsync(); throw; }
            client = candidate; account = verified;
            await WindowsSessionVault.SaveAsync(sessionPath, candidate.ExportSession(), lifetime.Token);
            lock (gate) settings.AccountHint = verified;
            Save(); notice = null;
            var repository = new CloudRepository(candidate, syncCache, verified.AccountId);
            await LoadPendingMaintenanceAsync(repository, verified.AccountId, lifetime.Token);
            await CleanupFinishedReaderPinsAsync(repository, verified.AccountId, lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { notice = Friendly(error); }
        finally { Changed(); }
    }
    private async Task AutomaticLoopAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, lifetime.Token);
                if (exiting || !worker.IsConnected || account == null || client == null) continue;
                int interval; lock (gate) interval = settings.SyncIntervalSeconds;
                if (DateTimeOffset.UtcNow - lastAutomatic < TimeSpan.FromSeconds(interval)) continue;
                lastAutomatic = DateTimeOffset.UtcNow;
                await RefreshWorkerAsync(lifetime.Token);
                foreach (var d in Disks())
                {
                    string id = Text(d, "id"); bool eligible;
                    if (replicaRuns.TryGetValue(id, out var replicaRun) && replicaRun.Active
                        || d.TryGetProperty("replica", out var replicaState) && replicaState.ValueKind == JsonValueKind.Object
                        && replicaState.TryGetProperty("candidate", out var replicaCandidate) && replicaCandidate.ValueKind == JsonValueKind.Object) continue;
                    lock (gate) eligible = settings.Bindings.ContainsKey(id) && !settings.PausedDisks.Contains(id);
                    bool cleanupDue = pendingMaintenance.GetValueOrDefault(id) && (!maintenanceAfter.TryGetValue(id, out var due) || DateTimeOffset.UtcNow >= due);
                    bool needsCacheSetup;
                    lock (gate) needsCacheSetup = settings.LocalCaches.GetValueOrDefault(id)?.LimitBytes > 0 && Flag(d, "unlocked") &&
                        (!d.TryGetProperty("cache", out var cached) || !Flag(cached, "source_ready") || !Flag(cached, "online") || cacheErrors.ContainsKey(id));
                    if (needsCacheSetup)
                    {
                        await accountGate.WaitAsync(lifetime.Token);
                        try { if (!exiting) await ApplyCacheSettingsSafeAsync(id, lifetime.Token); }
                        finally { accountGate.Release(); }
                    }
                    if (eligible && Flag(d, "unlocked") && (NeedsSync(d) || cleanupDue))
                    {
                        try { StartSync(id); }
                        catch (Exception error) { notice = Friendly(error); Changed(); }
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch (Exception error) { notice = Friendly(error); Changed(); }
        }
    }
    private bool NeedsSync(JsonElement disk)
    {
        if (!disk.TryGetProperty("cloud", out var c) || c.ValueKind != JsonValueKind.Object)
        { lock (gate) return settings.PendingDisks.Contains(Text(disk, "id")) || !settings.LastSuccess.ContainsKey(Text(disk, "id")); }
        if (Flag(c, "local_dirty") || UInt(c, "data_generation") > UInt(c, "published_generation")) return true;
        if (c.TryGetProperty("job", out var j) && j.ValueKind == JsonValueKind.Object && Text(j, "phase") != "published") return true;
        lock (gate) return !settings.LastSuccess.ContainsKey(Text(disk, "id"));
    }
    private JsonElement CachedWorkerState() { lock (gate) return workerState; }
    private void SetWorkerState(JsonElement value)
    {
        lock (gate) workerState = value;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("tasks", out var tasks)) return;
        foreach (var task in tasks.EnumerateArray())
        {
            string id = Text(task, "id"), state = Text(task, "state"), error = Text(task, "error");
            string key = state + ":" + error;
            if (observedTasks.TryGetValue(id, out var previous) && previous == key) continue;
            observedTasks[id] = key;
            Log(Text(task, "diskId"), id, Text(task, "kind"), "task." + state,
                Text(task, "title") + "：" + Text(task, "message"), error.Length == 0 ? "info" : "error");
        }
    }
    private IReadOnlyList<JsonElement> Disks()
    {
        var snapshot = CachedWorkerState();
        if (snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty("disks", out var array)) return array.EnumerateArray().Select(e => e.Clone()).ToArray();
        string catalog = Path.Combine(SettingsStorage.DirectoryPath, "disks.json");
        if (!File.Exists(catalog)) return Array.Empty<JsonElement>();
        try
        {
            var entries = JsonSerializer.Deserialize<List<DiskEntry>>(File.ReadAllText(catalog), SettingsStorage.Json) ?? new();
            foreach (var entry in entries) entry.Status = File.Exists(entry.ContainerPath) ? "未挂载" : "容器文件不可用";
            return entries.Select(d => Element(d)).ToArray();
        }
        catch (IOException) { return Array.Empty<JsonElement>(); }
    }
    private static int DiskObjectSize(JsonElement disk)
    {
        if (disk.ValueKind != JsonValueKind.Object) throw new IOException("缺少磁盘块大小信息。");
        if (disk.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object && info.TryGetProperty("object_size", out var actual))
            return CloudObjectGeometry.Validate(actual.GetInt32());
        if (disk.TryGetProperty("object_size", out var native)) return CloudObjectGeometry.Validate(native.GetInt32());
        if (disk.TryGetProperty("objectSizeBytes", out var saved)) return CloudObjectGeometry.Validate(saved.GetInt32());
        throw new IOException("磁盘缺少块大小字段，请使用创建该磁盘的对应版本程序。");
    }
    private JsonElement Disk(string id) => Disks().FirstOrDefault(d => Text(d, "id") == id) is var d && d.ValueKind == JsonValueKind.Object ? d : throw new IOException("找不到磁盘。");
    private async Task RefreshWorkerAsync(CancellationToken ct)
    {
        if (!worker.IsConnected) return;
        await stateGate.WaitAsync(ct);
        try { SetWorkerState(Element(await worker.InvokeAsync("state", Element(new { }), ct))); }
        finally { stateGate.Release(); }
    }
    public async Task<object?> GetStateAsync(CancellationToken cancellationToken)
    {
        if (worker.IsConnected)
        {
            try { await RefreshWorkerAsync(cancellationToken); }
            catch (Exception e) when (e is not OperationCanceledException) { notice = Friendly(e); }
        }
        var views = new List<object>();
        foreach (var disk in Disks())
        {
            if (disk.TryGetProperty("cloud", out var observed) && observed.ValueKind == JsonValueKind.Object)
            {
                bool changed = false; string observedId = Text(disk, "id");
                bool pending = NeedsSync(disk);
                lock (gate)
                {
                    if (settings.Bindings.ContainsKey(observedId)) changed = pending ? settings.PendingDisks.Add(observedId) : settings.PendingDisks.Remove(observedId);
                }
                if (changed) Save();
            }
            var values = disk.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
            lock (gate) values["lazySource"] = settings.Restores.Values.Any(r => r.Lazy && !r.ContainerDeleted && r.LocalDiskId == Text(disk, "id"));
            lock (gate) values["localCache"] = settings.LocalCaches.GetValueOrDefault(Text(disk, "id")) ?? new LocalCacheSettings();
            values["cacheError"] = cacheErrors.GetValueOrDefault(Text(disk, "id")) ?? Text(disk, "cacheError");
            values["sync"] = SyncView(disk); views.Add(values);
            values["replica"] = ReplicaView(disk);
        }
        var tasks = runs.Values.Select(TaskView).ToList();
        var snapshot = CachedWorkerState();
        if (snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty("tasks", out var nativeTasks)) tasks.AddRange(nativeTasks.EnumerateArray().Select(t => (object)t.Clone()));
        object preferences;
        lock (gate) preferences = new { settings.SyncIntervalSeconds, settings.MaxParallelTransfers, settings.SyncPreparationCacheMiB, settings.SyncOnExit, settings.BaiduRequestsPerSecond, settings.BaiduMaximumConcurrentRequests, settings.DefaultCapacityGiB, settings.DefaultDirectory, settings.DefaultObjectSizeBytes, settings.Prefetch };
        bool? driver = snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty("driverAvailable", out var hasDriver) ? hasDriver.GetBoolean() : null;
        return new { connected = worker.IsConnected, driverAvailable = driver, disks = views, tasks, settings = preferences, network = BaiduRequestScheduler.Shared.Snapshot(), account, authChecking = !initialization.IsCompleted, error = notice, logWarning = journalWarning };
    }
    private object SyncView(JsonElement disk)
    {
        string id = Text(disk, "id"); bool enabled, paused; DateTimeOffset? last;
        lock (gate) { enabled = settings.Bindings.ContainsKey(id); paused = settings.PausedDisks.Contains(id); last = settings.LastSuccess.TryGetValue(id, out var t) ? t : null; }
        runs.TryGetValue(id, out var run); var p = run?.Kind == "sync" ? run.Progress : null;
        long estimate = disk.TryGetProperty("cloud", out var core) && core.ValueKind == JsonValueKind.Object ? SyncCoordinator.GetLong(core, "estimated_bytes") : 0;
        if (core.ValueKind == JsonValueKind.Object && core.TryGetProperty("job", out var job) && job.ValueKind == JsonValueKind.Object)
            estimate = Math.Max(0, SyncCoordinator.GetLong(job, "total_objects") - SyncCoordinator.GetLong(job, "uploaded_objects")) * DiskObjectSize(disk);
        string state = p?.Phase ?? (paused ? "paused" : enabled ? NeedsSync(disk) ? "pending" : "synced" : "disabled");
        string message = p?.Message ?? (paused ? "同步已暂停" : enabled ? state == "synced" ? "云端已同步" : Flag(disk, "unlocked") ? "等待同步" : "解锁后继续同步" : "本地已保存");
        if (enabled && !Flag(disk, "unlocked") && p == null && last != null && !NeedsSync(disk)) { state = "unknown"; message = "上次已同步，解锁后检查当前状态"; }
        if (Text(disk, "status").Contains("不可用", StringComparison.Ordinal)) message = Text(disk, "status");
        if (run?.Task?.IsCompleted != false && enabled && !paused && NeedsSync(disk) && state == "synced") { state = "pending"; message = "有新变更，等待同步"; }
        return new { enabled, state, message, uploadedBytes = p?.UploadedBytes ?? 0, logicalUploadedBytes = p?.LogicalUploadedBytes ?? 0, reusedBytes = p?.ReusedBytes ?? 0, completedBytes = p?.CompletedBytes ?? 0, totalBytes = p?.TotalBytes ?? 0, pendingBytes = p?.Phase is "uploading" or "preparing" or "publishing" ? p.PendingBytes : estimate, estimated = p?.Estimated ?? true, lastSuccessUtc = last };
    }
    private static object TaskView(JobRun r) => new { id = r.Id, diskId = r.DiskId, kind = r.Kind, title = r.Title, state = r.Progress.Phase, message = r.Progress.Message, uploadedBytes = r.Progress.UploadedBytes, logicalUploadedBytes = r.Progress.LogicalUploadedBytes, reusedBytes = r.Progress.ReusedBytes, downloadedBytes = r.Kind is "restore" or "replica" ? (long?)Interlocked.Read(ref r.DownloadedBytes) : null, readyToMount = r.ReadyToMount, verifiedScope = r.VerifiedScope, completedBytes = r.Progress.CompletedBytes, totalBytes = r.Progress.TotalBytes, completedPages = r.CompletedPages, totalPages = r.TotalPages, completedIndexNodes = r.CompletedIndexNodes, pendingBytes = r.Kind == "sync" ? (long?)r.Progress.PendingBytes : null, error = r.Progress.Error, canPause = r.Kind != "replica" && r.Task is { IsCompleted: false }, canResume = r.Kind != "replica" && r.Task is not { IsCompleted: false } && r.Progress.Phase is "paused" or "error", requiresPassword = r.RequiresPassword };

    public async Task<object?> InvokeAsync(string method, JsonElement args, CancellationToken ct)
    {
        string id = Text(args, "id");
        if (method is "sync.enable" or "sync.cleanup" or "sync.now" or "sync.resume" or "cache.settings"
            && replicaRuns.TryGetValue(id, out var replicaRun) && replicaRun.Active)
            throw new IOException("正在准备或确认云端快照，请先完成加载或取消。");
        switch (method)
        {
            case "replica.prepare": return await PrepareReplicaAsync(id, ct);
            case "replica.apply": return await ApplyReplicaAsync(id, args, ct);
            case "replica.cancel": return await CancelReplicaAsync(id, args, ct);
            case "replica.status": return await ReplicaStatusAsync(id, ct);
            case "app.state": return await GetStateAsync(ct);
            case "tasks.logs":
            {
                long? before = args.TryGetProperty("before", out var b) && b.TryGetInt64(out var boundary) ? boundary : null;
                int limit = args.TryGetProperty("limit", out var n) && n.TryGetInt32(out var count) ? count : 200;
                var page = journal?.Read(before, limit, Text(args, "diskId"), Text(args, "level"))
                    ?? new ActivityPage(Array.Empty<ActivityEntry>(), null, false, null, 65536, journalWarning);
                return journalWarning is null ? page : page with { Warning = journalWarning };
            }
            case "app.allowUnsyncedExit": allowUnsyncedExit = true; return new { ok = true };
            case "auth.acceptCookies": return await AcceptCookiesAsync(args, ct);
            case "auth.logout": await LogoutAsync(ct); return new { ok = true };
            case "settings.save":
            {
                await settingsGate.WaitAsync(ct);
                try
                {
                RequireRunning();
                int interval = args.GetProperty("syncIntervalSeconds").GetInt32(), parallel = args.GetProperty("maxParallelTransfers").GetInt32();
                if (interval is < 15 or > 3600 || parallel is < 1 or > 4) throw new IOException("同步间隔应为 15–3600 秒，并发数为 1–4。");
                int prepareCacheMiB = SettingsStorage.PreparationCacheMiB(args, settings.SyncPreparationCacheMiB);
                double rate = args.TryGetProperty("baiduRequestsPerSecond", out var rateValue) ? rateValue.GetDouble() : settings.BaiduRequestsPerSecond;
                int networkParallel = args.TryGetProperty("baiduMaximumConcurrentRequests", out var networkValue) ? networkValue.GetInt32() : settings.BaiduMaximumConcurrentRequests;
                if (!double.IsFinite(rate) || rate is < 0.1 or > 20 || networkParallel is < 1 or > 8)
                    throw new IOException("网盘请求频率应为 0.1–20 次/秒，同时请求数为 1–8。");
                PrefetchSettings? prefetch = args.TryGetProperty("prefetch", out var prefetchValue) ? prefetchValue.Deserialize<PrefetchSettings>(SettingsStorage.Json) ?? throw new IOException("缺少预取设置。") : null;
                prefetch?.Validate();
                bool? syncOnExit = args.TryGetProperty("syncOnExit", out var exitValue) ? exitValue.GetBoolean() : null;
                lock (gate)
                {
                    settings.SyncIntervalSeconds = interval; settings.MaxParallelTransfers = parallel;
                    settings.SyncPreparationCacheMiB = prepareCacheMiB;
                    if (syncOnExit.HasValue) settings.SyncOnExit = syncOnExit.Value;
                    if (prefetch is not null) settings.Prefetch = prefetch;
                    settings.BaiduRequestsPerSecond = rate; settings.BaiduMaximumConcurrentRequests = networkParallel;
                }
                Save(); BaiduRequestScheduler.Shared.Configure(new BaiduRequestLimits(rate, networkParallel));
                if (prefetch is not null) await worker.ConfigurePrefetchAsync(prefetch, ct);
                Changed(); return new { ok = true, syncPreparationCacheMiB = prepareCacheMiB };
                }
                finally { settingsGate.Release(); }
            }
            case "cloud.list": return await Repository().ListDisksForReplicaAsync(ct);
            case "cache.settings":
                await accountGate.WaitAsync(ct);
                try { RequireRunning(); return await SaveCacheSettingsAsync(id, args, ct); }
                finally { accountGate.Release(); }
            case "cloud.import":
                await accountGate.WaitAsync(ct);
                try { RequireRunning(); return await StartRestoreAsync(args, ct); }
                finally { accountGate.Release(); }
            case "restore.pause": return await RestoreTaskActionAsync(method, args, ct);
            case "restore.resume":
                await accountGate.WaitAsync(ct);
                try { RequireRunning(); return await RestoreTaskActionAsync(method, args, ct); }
                finally { accountGate.Release(); }
            case "sync.enable":
            {
                await accountGate.WaitAsync(ct);
                try
                {
                RequireRunning();
                var requestedCache = args.TryGetProperty("localCache", out var cacheChoice) ? ParseCacheSettings(cacheChoice) : null;
                await RefreshWorkerAsync(ct); var d = Disk(id);
                if (!Flag(d, "unlocked")) throw new IOException("请先解锁磁盘。");
                var repository = Repository(); CloudBinding binding;
                string remoteRoot = CloudRepository.RootPath(id), accountId = account!.AccountId;
                var volume = new WorkerVolume(worker, id, DiskObjectSize(Disk(id)));
                var cloud = await volume.ControlAsync(new { cmd = "cloud.status" }, ct);
                lock (gate)
                {
                    var saved = settings.Bindings.GetValueOrDefault(id);
                    binding = saved is not null && saved.ProviderId == repository.Store.ProviderId && saved.AccountId == accountId && saved.RemoteRoot == remoteRoot
                        ? saved : new(repository.Store.ProviderId, accountId, remoteRoot, settings.DeviceId);
                }
                // An original import retains its per-volume writer, even after settings
                // recovery or when this installation has a different global device ID.
                if (cloud.TryGetProperty("binding", out var nativeBinding) && nativeBinding.ValueKind == JsonValueKind.Object &&
                    Text(nativeBinding, "backend_id") == binding.ProviderId && Text(nativeBinding, "account_id") == accountId &&
                    Text(nativeBinding, "remote_root") == remoteRoot && Guid.TryParse(Text(nativeBinding, "device_id"), out _))
                    binding = binding with { DeviceId = Text(nativeBinding, "device_id") };
                await repository.EnsureWriterAsync(binding, id, ct);
                await volume.ControlAsync(new { cmd = "cloud.bind", backend_id = binding.ProviderId, account_id = binding.AccountId, remote_root = binding.RemoteRoot, device_id = binding.DeviceId, enabled = true }, ct);
                lock (gate) { settings.Bindings[id] = binding; settings.PausedDisks.Remove(id); settings.PendingDisks.Add(id); if (requestedCache is not null) settings.LocalCaches[id] = requestedCache; }
                Save(); await ApplyCacheSettingsSafeAsync(id, ct); StartSync(id); Changed(); return new { ok = true };
                }
                finally { accountGate.Release(); }
            }
            case "sync.cleanupStatus":
            {
                CloudBinding binding;lock(gate) binding=settings.Bindings.GetValueOrDefault(id)??throw new IOException("该磁盘尚未启用云同步。");
                return await Repository().CleanupEstimateAsync(binding,id,ct);
            }
            case "sync.cleanup": StartCleanup(id);return new {ok=true};
            case "sync.cleanupPause":
                if(runs.TryGetValue(id,out var cleaning)&&cleaning.Kind=="cleanup"&&cleaning.Task is {IsCompleted:false}){cleaning.Cancellation.Cancel();await cleaning.Task;}
                return new {ok=true};
            case "sync.diagnostics":
            {
                var native = Element(await worker.InvokeAsync("sync.diagnostics", args, ct));
                var values = native.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
                if (runs.TryGetValue(id, out var active))
                {
                    values["phase"] = active.Progress.Phase; values["message"] = active.Progress.Message;
                    values["filesystem_flush_ms"] = active.FileSystemFlushMs;
                    values["phase_elapsed_ms"] = (long)(DateTimeOffset.UtcNow - active.PhaseStartedUtc).TotalMilliseconds;
                }
                values["api_requests"] = client?.GetRequestCounts().Values.Sum() ?? 0;
                values["api_scope"] = "当前账户会话累计";
                var requests = BaiduRequestScheduler.Shared.Snapshot();
                values["api_queued"] = requests.QueuedRequests; values["api_active"] = requests.ActiveRequests;
                values["api_requests_per_second"] = requests.Limits.RequestsPerSecond;
                return values;
            }
            case "sync.now": case "sync.resume":
                lock (gate) settings.PausedDisks.Remove(id); Save(); StartSync(id); return new { ok = true };
            case "sync.pause": await PauseSyncAsync(id, true); return new { ok = true };
            case "disks.unmount": await UnmountAndSyncAsync(id, ct); return new { ok = true };
            case "disks.delete": return await DeleteDiskAsync(id, args, ct);
        }
        if (method is "disks.create" or "disks.import" or "disks.mount" or "disks.unlock")
        {
            await accountGate.WaitAsync(ct);
            try { RequireRunning(); return await InvokeDiskAsync(method, args, ct); }
            finally { accountGate.Release(); }
        }
        return await InvokeDiskAsync(method, args, ct);
    }
    private async Task<object?> InvokeDiskAsync(string method, JsonElement args, CancellationToken ct)
    {
        string id = Text(args, "id");
        var diskMethods = new HashSet<string>(StringComparer.Ordinal) { "disks.create", "disks.import", "disks.mount", "disks.unlock", "disks.settings", "disks.open", "driver.install", "snapshots.list", "snapshots.create", "snapshots.rename", "snapshots.delete", "snapshots.restore", "snapshots.restorePause", "snapshots.restoreResume", "blocks.list", "blocks.summary", "blocks.query", "blocks.changes", "reclaim.start", "reclaim.pause", "reclaim.resume", "compact.start", "compact.pause", "compact.resume", "compact.cancel", "compact.status" };
        if (!diskMethods.Contains(method)) throw new IOException("不支持的界面操作。");
        if (method == "disks.mount")
        {
            // Authenticate a moved/imported container and recover its fixed source before NTFS can read it.
            await worker.InvokeAsync("disks.unlock", args, ct);
            await RefreshWorkerAsync(ct); RecoverLazySource(Disk(id)); RecoverCacheSource(Disk(id));
            await ApplyCacheSettingsSafeAsync(id, ct);
        }
        bool recordAction = method is "reclaim.start" or "reclaim.pause" or "reclaim.resume" or "compact.start" or "compact.pause" or "compact.resume" or "compact.cancel" or "snapshots.create" or "snapshots.delete" or "snapshots.restore" or "snapshots.restorePause" or "snapshots.restoreResume";
        object? result;
        try { result = await worker.InvokeAsync(method, args, ct); }
        catch (Exception e) { if (recordAction) Log(id, Text(args, "taskId"), "local", method + ".failed", Friendly(e), "error"); throw; }
        if (recordAction) Log(id, Text(args, "taskId"), "local", method, method switch
        {
            "reclaim.start" => "已启动手动 NTFS 空闲空间回收", "reclaim.pause" => "手动回收已暂停", "reclaim.resume" => "继续手动回收",
            "compact.start" => "已启动后台整理", "compact.pause" => "后台整理已暂停", "compact.resume" => "后台整理继续", "compact.cancel" => "已停止本次整理，保留已完成部分",
            "snapshots.create" => "已创建快照：" + Text(args, "name"), "snapshots.delete" => "已删除本地快照：" + Text(args, "snapshotId"),
            "snapshots.restore" => "已启动本地快照恢复", "snapshots.restorePause" => "本地快照恢复已暂停", _ => "本地快照恢复继续"
        });
        if (method is "blocks.list" or "blocks.summary" or "blocks.query" or "blocks.changes" or "snapshots.list" or "compact.status") return result;
        await RefreshWorkerAsync(ct);
        if (method == "disks.unlock") { RecoverLazySource(Disk(id)); RecoverCacheSource(Disk(id)); await ApplyCacheSettingsSafeAsync(id, ct); }
        if (method == "disks.create")
        {
            lock (gate) { settings.DefaultCapacityGiB = (int)(UInt(args, "capacityBytes") / 1073741824); settings.DefaultDirectory = Path.GetDirectoryName(Text(args, "containerPath")); settings.DefaultObjectSizeBytes = (uint)(args.TryGetProperty("objectSizeBytes", out var size) ? CloudObjectGeometry.Validate(size.GetInt32()) : CloudObjectGeometry.DefaultSize); } Save();
        }
        if (method is "disks.mount" or "disks.unlock")
        {
            bool sync; lock (gate) sync = settings.Bindings.ContainsKey(id) && !settings.PausedDisks.Contains(id);
            if (sync && client != null && account != null) StartSync(id);
        }
        Changed(); return result;
    }
    private CloudRepository Repository()
    {
        if (client == null || account == null) throw new IOException("请先登录百度网盘。");
        return new CloudRepository(client, syncCache, account.AccountId);
    }
    private void RequireRunning()
    { if (exiting || stopped) throw new IOException("程序正在退出，请等待安全关闭后重新打开。"); }
    private void RecoverLazySource(JsonElement disk)
    {
        if (!Flag(disk, "unlocked") || !disk.TryGetProperty("lazy", out var lazy) || !Flag(lazy, "enabled")) return;
        if (!lazy.TryGetProperty("backing", out var backing) || backing.ValueKind != JsonValueKind.Object)
        {
            if (disk.TryGetProperty("cache", out var cache) && Flag(cache, "source_ready")) return;
            throw new IOException("按需磁盘缺少已认证的云端来源。");
        }
        string sourceId = Text(backing, "source_volume_id"), root = Text(backing, "remote_root"), pin = Text(backing, "reader_pin"),
            rootId = Text(backing, "root_object_id"), hash = Text(backing, "root_sha256"), accountId = Text(backing, "account_id");
        string pinPrefix = root + "/readers/";
        if (Text(backing, "provider_id") != "baidu-private-web" || !CloudRepository.IsRootForVolume(root, sourceId) ||
            !pin.StartsWith(pinPrefix, StringComparison.Ordinal) || !pin.EndsWith(".json", StringComparison.Ordinal) ||
            !Guid.TryParse(rootId, out _) || hash.Length != 64 || !hash.All(Uri.IsHexDigit) || string.IsNullOrEmpty(accountId))
            throw new IOException("按需磁盘的来源身份无效。");
        string recordId = pin[pinPrefix.Length..^5];
        if (!Guid.TryParse(recordId, out _)) throw new IOException("按需磁盘的来源引用无效。");
        string id = Text(disk, "id"), path = Text(disk, "containerPath");
        var nativeReplica = disk.TryGetProperty("replica", out var replica) && replica.ValueKind == JsonValueKind.Object ? replica : Element(new { });
        RemoteCommit? authenticatedCommit = null;
        CloudBinding? authenticatedBinding = null;
        if (Text(nativeReplica, "mode") == "original" && disk.TryGetProperty("cloud", out var nativeCloud) && nativeCloud.ValueKind == JsonValueKind.Object
            && nativeCloud.TryGetProperty("binding", out var nativeBinding) && nativeBinding.ValueKind == JsonValueKind.Object
            && Text(nativeBinding, "backend_id") == "baidu-private-web" && Text(nativeBinding, "account_id") == accountId
            && Text(nativeBinding, "remote_root") == root && Guid.TryParse(Text(nativeBinding, "device_id"), out _))
            authenticatedBinding = new("baidu-private-web", accountId, root, Text(nativeBinding, "device_id"));
        if (nativeReplica.TryGetProperty("commit", out var commitValue) && commitValue.ValueKind == JsonValueKind.Object)
            authenticatedCommit = commitValue.Deserialize<RemoteCommit>(SettingsStorage.Json);
        if (authenticatedCommit is { } described && (described.VolumeId != sourceId || described.RootObjectId != rootId
            || !described.RootSha256.Equals(hash, StringComparison.OrdinalIgnoreCase) || described.ObjectSizeBytes != DiskObjectSize(disk)))
            throw new IOException("磁盘的云端提交记录与已认证来源不一致。");
        bool changed = false;
        lock (gate)
        {
            if (settings.Restores.TryGetValue(recordId, out var existing))
            {
                if (!existing.Lazy || existing.AccountId != accountId || existing.RemoteRoot != root || existing.Commit.RootObjectId != rootId ||
                    !existing.Commit.RootSha256.Equals(hash, StringComparison.OrdinalIgnoreCase) || existing.Commit.ObjectSizeBytes != DiskObjectSize(disk) || existing.LocalDiskId != id || existing.ContainerDeleted)
                    throw new IOException("云端磁盘来源与本地记录冲突。");
                if (existing.TargetPath != path) { existing.TargetPath = path; changed = true; }
                if (existing.PreparedReplicaOnly) { existing.PreparedReplicaOnly = false; changed = true; }
                if (authenticatedBinding != null && existing.Mode == "original" && existing.OriginalBinding is null)
                { existing.OriginalBinding = authenticatedBinding; changed = true; }
                if (authenticatedCommit != null && !existing.VerifiedCommit)
                { existing.Commit = authenticatedCommit; existing.VerifiedCommit = true; changed = true; }
            }
            else
            {
                if (authenticatedCommit is null && settings.ReplicaCandidates.TryGetValue(id, out var completedCandidate)
                    && completedCandidate.Commit.RootObjectId == rootId && completedCandidate.Commit.RootSha256.Equals(hash, StringComparison.OrdinalIgnoreCase))
                    authenticatedCommit = completedCandidate.Commit;
                string mode = Text(nativeReplica, "mode", "copy");
                var predecessor = settings.Restores.Values.FirstOrDefault(r => r.LocalDiskId == id && r.AccountId == accountId && r.RemoteRoot == root);
                settings.Restores[recordId] = new RestoreRecord { Id = recordId, LocalDiskId = id, AccountId = accountId, RemoteRoot = root,
                    ReaderPin = pin, TargetPath = path, Name = Text(disk, "name"), Lazy = true, Begun = true, Complete = true,
                    Mode = mode, OriginalConfirmed = mode == "original", OriginalBinding = mode == "original" ? authenticatedBinding ?? predecessor?.OriginalBinding ?? settings.Bindings.GetValueOrDefault(id) : null,
                    VerifiedCommit = authenticatedCommit != null,
                    Commit = authenticatedCommit ?? new RemoteCommit(4, sourceId, settings.DeviceId, UInt(nativeReplica, "generation"), Text(disk, "name"), UInt(disk, "capacityBytes"), Flag(disk, "encrypted"), rootId, hash, DateTimeOffset.MinValue) { ObjectSizeBytes = DiskObjectSize(disk) } };
                settings.ReplicaCandidates.Remove(id);
                changed = true;
            }
        }
        if (changed) Save();
    }
    private Task<byte[]> ReadLazyObjectAsync(LazyObjectRequest request, CancellationToken ct)
    {
        lock (jobGate)
        {
            if (!cloudReadsOpen) throw new IOException("网盘连接正在切换，请稍后重试。");
            var linked = replicaRuns.TryGetValue(request.DiskId, out var preparingReplica) && preparingReplica.Active && preparingReplica.Job.Progress.Phase != "switching"
                ? CancellationTokenSource.CreateLinkedTokenSource(ct, cloudReadLifetime.Token, preparingReplica.Job.Cancellation.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(ct, cloudReadLifetime.Token);
            long sequence = Interlocked.Increment(ref cloudReadSequence);
            // Task.Run ensures registration precedes completion even for an immediate validation failure.
            var task = Task.Run(async () =>
            {
                try
                {
                    await initialization.WaitAsync(linked.Token);
                    var source = Element(await worker.InvokeAsync("cache.source", Element(new { id = request.DiskId, object_id = request.ObjectId }), linked.Token));
                    string remoteRoot = CacheSourcePolicy.ValidateRequest(source, request, client is null ? null : account?.AccountId);
                    var repository = Repository();
                    string purpose = request.Prefetch ? "顺序预取" : request.Reason switch
                    {
                        "sync" => "同步修改路径", "copy_publish" => "首次发布独立副本", "write" => "前台写入",
                        "reclaim" => "手动回收", "replica" => "加载云端快照", _ => "前台读取"
                    };
                    string type = request.ObjectKind switch { "metadata" => "容器索引", "data" => "数据对象", _ => "云端对象" };
                    string downloadRunId = request.Reason is "sync" or "copy_publish"
                        ? runs.Values.FirstOrDefault(r => r.DiskId == request.DiskId && r.Kind == "sync" && r.Task is { IsCompleted: false })?.Id ?? "" : "";
                    Log(request.DiskId, downloadRunId, "download", request.Prefetch ? "prefetch.started" : "demand.started",
                        $"{purpose}：等待下载{type}", objectId: request.ObjectId, objectKind: request.ObjectKind, bytes: request.Length);
                    try
                    {
                        var downloaded = await repository.ReadObjectForReplicaAsync(remoteRoot, request.ObjectId, request.Sha256, request.Length, linked.Token);
                        var bytes = downloaded.Canonical;
                        if (replicaRuns.TryGetValue(request.DiskId, out var pulling) && pulling.Active)
                            Interlocked.Add(ref pulling.Job.DownloadedBytes, downloaded.WireBytes);
                        else
                        {
                            var restoring = runs.Values.FirstOrDefault(r => r.Kind == "restore" && r.DiskId == request.DiskId && r.Task is { IsCompleted: false });
                            if (restoring != null) Interlocked.Add(ref restoring.DownloadedBytes, downloaded.WireBytes);
                        }
                        Log(request.DiskId, downloadRunId, "download", request.Prefetch ? "prefetch.verified" : "demand.verified",
                            $"{purpose}：{type}已通过长度和摘要校验，正在交给磁盘缓存", objectId: request.ObjectId, objectKind: request.ObjectKind, bytes: bytes.Length, wireBytes: downloaded.WireBytes);
                        return bytes;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        Log(request.DiskId, downloadRunId, "download", "download.failed", $"{purpose}：{type}下载失败；{Friendly(error)}",
                            "error", request.ObjectId, objectKind: request.ObjectKind);
                        throw;
                    }
                }
                finally { linked.Dispose(); }
            }, CancellationToken.None);
            cloudReads[sequence] = task;
            _ = task.ContinueWith(_ => { cloudReads.TryRemove(sequence, out var ignored); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }
    private void RequireLazyDisksClosed(string? replacementAccountId = null)
    {
        var openIds = Disks().Where(d => Flag(d, "unlocked") || Flag(d, "mounted")).Select(d => Text(d, "id")).ToHashSet(StringComparer.Ordinal);
        lock (gate)
            if (settings.Restores.Values.Any(r => r.Lazy && !r.ContainerDeleted && r.AccountId != replacementAccountId && (openIds.Contains(r.LocalDiskId ?? "") || openIds.Contains(r.WorkerId ?? ""))) ||
                settings.CacheSources.Any(p => !p.Value.ContainerDeleted && p.Value.Binding.AccountId != replacementAccountId && openIds.Contains(p.Key)))
                throw new IOException("请先安全卸载并关闭按需加载的云端磁盘，再退出或切换网盘账户。");
    }
    private async Task StopCloudReadsAsync()
    {
        Task<byte[]>[] tasks;
        lock (jobGate) { cloudReadsOpen = false; cloudReadLifetime.Cancel(); tasks = cloudReads.Values.ToArray(); }
        try { await Task.WhenAll(tasks); } catch (Exception) { /* Individual read errors are already reported to their disk requests. */ }
        if (worker.IsConnected)
            foreach (var disk in Disks().Where(d => Flag(d, "unlocked")))
                try { await worker.InvokeAsync("cache.online", Element(new { id = Text(disk, "id"), available = false }), CancellationToken.None); }
                catch (IOException) { /* Closing/disconnected workers stop their quota loops independently. */ }
        lock (jobGate) { cloudReadLifetime.Dispose(); cloudReadLifetime = new(); }
    }
    private async Task<object?> DeleteDiskAsync(string id, JsonElement args, CancellationToken ct)
    {
        var disk = Disk(id); string path = Text(disk, "containerPath");
        lock (jobGate)
        {
            if (deletingDisks.Contains(id) || (runs.TryGetValue(id, out var run) && run.Task is { IsCompleted: false }))
                throw new IOException("磁盘正在同步或删除，请等待任务完成，或先暂停同步。");
            lock (gate) if (settings.Restores.Values.Any(r => !r.Complete && !r.ContainerDeleted && string.Equals(Path.GetFullPath(r.TargetPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
                throw new IOException("该容器仍有未完成的云端恢复任务，暂时不能删除。");
            deletingDisks.Add(id);
        }
        try
        {
            var result = await worker.InvokeAsync("disks.delete", args, ct);
            if (Flag(args, "deleteContainer")) lock (gate)
            {
                settings.Bindings.Remove(id); settings.LastSuccess.Remove(id);
                settings.PendingDisks.Remove(id); settings.PausedDisks.Remove(id);
                settings.LocalCaches.Remove(id);
                if (settings.CacheSources.TryGetValue(id, out var cachedSource)) cachedSource.ContainerDeleted = true;
                foreach (var record in settings.Restores.Values.Where(r => r.Lazy && r.LocalDiskId == id)) record.ContainerDeleted = true;
            }
            runs.TryRemove(id, out _);
            pendingMaintenance.TryRemove(id, out _);
            maintenanceAfter.TryRemove(id, out _);
            try { Save(); } catch (IOException) { notice = "磁盘已移除，但部分设置尚未写入；请检查配置目录可写。"; }
            Log(id, "", "disk", "disk.deleted", (Flag(args, "deleteContainer") ? "已删除本地容器和列表记录；云端副本保留：" : "已从列表移除；本地容器和云端副本保留：") + Text(disk, "name") + " · " + path);
            await RefreshWorkerAsync(ct); Changed(); return result;
        }
        catch (Exception e) { Log(id, "", "disk", "disk.delete_failed", Friendly(e), "error"); throw; }
        finally { lock (jobGate) deletingDisks.Remove(id); }
    }
    private async Task<object> AcceptCookiesAsync(JsonElement args, CancellationToken ct)
    {
        await initialization;
        await accountGate.WaitAsync(ct);
        try
        {
            RequireRunning();
            BaiduCookieSession session = args.ValueKind == JsonValueKind.Array
                ? new(JsonSerializer.Deserialize<BaiduCookieRecord[]>(args.GetRawText(), SettingsStorage.Json)!)
                : JsonSerializer.Deserialize<BaiduCookieSession>(args.GetRawText(), SettingsStorage.Json) ?? throw new IOException("登录会话无效。");
            var candidate = NewClient(session);
            CloudAccountInfo verified;
            try
            {
                verified = await candidate.ValidateAsync(ct); await candidate.InitializeWebSessionAsync(ct);
                await RefreshWorkerAsync(ct); RequireLazyDisksClosed(verified.AccountId);
            }
            catch { await candidate.DisposeAsync(); throw; }
            await StopNetworkTasksAsync();
            await StopCloudReadsAsync();
            var old = client;
            lock (jobGate) { client = candidate; account = verified; networkAdmissionsOpen = !exiting; cloudReadsOpen = true; }
            if (old != null) await old.DisposeAsync();
            await WindowsSessionVault.SaveAsync(sessionPath, candidate.ExportSession(), ct);
            lock (gate) settings.AccountHint = verified;
            Save(); notice = null;
            var repository = new CloudRepository(candidate, syncCache, verified.AccountId);
            await LoadPendingMaintenanceAsync(repository, verified.AccountId, ct);
            await CleanupFinishedReaderPinsAsync(repository, verified.AccountId, ct);
            foreach (var d in Disks().Where(d => Flag(d, "unlocked"))) await ApplyCacheSettingsSafeAsync(Text(d, "id"), ct);
            Changed(); return verified;
        }
        finally { accountGate.Release(); }
    }
    private async Task CleanupFinishedReaderPinsAsync(CloudRepository repository, string accountId, CancellationToken ct)
    {
        RestoreRecord[] cleanup;
        lock (gate) cleanup = settings.Restores.Values.Where(r => r.Complete && !r.Lazy && r.ReaderPin != null && r.AccountId == accountId).ToArray();
        foreach (var record in cleanup)
        {
            string expected = CloudRepository.RootPath(record.Commit.VolumeId) + "/readers/" + CloudRepository.Component(record.Id) + ".json";
            if (record.ReaderPin != expected) continue;
            try { await repository.Store.DeleteAsync(expected, ct); lock (gate) record.ReaderPin = null; Save(); }
            catch (IOException) { notice = "恢复已完成，远端临时引用将在连接恢复后继续清理。"; }
        }
    }
    private async Task LoadPendingMaintenanceAsync(CloudRepository repository, string accountId, CancellationToken ct)
    {
        KeyValuePair<string, CloudBinding>[] bindings;
        lock (gate) bindings = settings.Bindings.Where(p => p.Value.AccountId == accountId).ToArray();
        foreach (var pair in bindings)
            pendingMaintenance[pair.Key] = await repository.HasPendingWorkAsync(pair.Value, pair.Key, ct);
    }
    private async Task LogoutAsync(CancellationToken ct)
    {
        await initialization; await accountGate.WaitAsync(ct);
        try
        {
            RequireRunning();
            await RefreshWorkerAsync(ct); RequireLazyDisksClosed();
            await StopNetworkTasksAsync();
            await StopCloudReadsAsync();
            var previous = client;
            lock (jobGate) { client = null; account = null; networkAdmissionsOpen = !exiting; cloudReadsOpen = true; }
            if (previous != null) await previous.DisposeAsync();
            WindowsSessionVault.Delete(sessionPath); lock (gate) settings.AccountHint = null;
            Save(); notice = null; Changed();
        }
        finally { accountGate.Release(); }
    }
    private void StartSync(string id, bool allowDuringExit = false)
    {
        lock (jobGate)
        {
        if ((!networkAdmissionsOpen || exiting) && !allowDuringExit) throw new IOException("正在结束当前网络任务，请稍候。");
        if (deletingDisks.Contains(id)) throw new IOException("磁盘正在删除。");
        if (replicaRuns.TryGetValue(id, out var pulling) && pulling.Active) throw new IOException("正在加载云端快照，请先完成或取消加载。");
        lock (gate) if (settings.PausedDisks.Contains(id) && !allowDuringExit) throw new IOException("同步已暂停，请点击继续。");

        var repository = Repository(); CloudBinding binding; int concurrency, prepareCacheMiB;
        lock (gate)
        {
            if (!settings.Bindings.TryGetValue(id, out binding!)) throw new IOException("请先启用此磁盘的云同步。");
            if (binding.AccountId != account!.AccountId) throw new IOException("这块磁盘绑定了另一个网盘账户。");
            concurrency = settings.MaxParallelTransfers;
            prepareCacheMiB = SyncPreparationLimits.ValidateCacheMiB(settings.SyncPreparationCacheMiB);
        }
        if (runs.TryGetValue(id, out var existing) && existing.Task is { IsCompleted: false }) return;
        var disk = Disk(id); if (!Flag(disk, "unlocked")) throw new IOException("请先解锁磁盘以继续同步。");
        var run = new JobRun { DiskId = id, Title = "同步 · " + Text(disk, "name") };
        runs[id] = run;
        Log(id, run.Id, "sync", "sync.queued", run.Title + "，已加入队列");
        run.Task = Task.Run(async () =>
        {
            try
            {
                var local = new WorkerVolume(worker, id, DiskObjectSize(Disk(id)));
                var initial = await local.ControlAsync(new { cmd = "cloud.status" }, run.Cancellation.Token);
                if (initial.TryGetProperty("status", out var nested)) initial = nested;
                bool hasJob = initial.TryGetProperty("job", out var existing) && existing.ValueKind == JsonValueKind.Object;
                if (!hasJob)
                {
                    run.Progress = new("flushing", "正在刷新文件系统缓存", 0, 0, 0, true); run.PhaseStartedUtc = DateTimeOffset.UtcNow; Changed();
                    Log(id, run.Id, "sync", "flush.started", "开始刷新本次版本的文件系统缓存");
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    await worker.InvokeAsync("disks.flush", Element(new { id }), run.Cancellation.Token);
                    run.FileSystemFlushMs = watch.ElapsedMilliseconds;
                    Log(id, run.Id, "sync", "sync.flushed", $"文件系统缓存已保存（{run.FileSystemFlushMs} ms），开始固定本次版本");
                }
                else Log(id, run.Id, "sync", "sync.resuming", "继续已固定的上传版本，无需再次刷新当前文件系统");
                var observer = new InlineProgress<TransferProgress>(p => {
                    if (run.Progress.Phase != p.Phase) { run.PhaseStartedUtc = DateTimeOffset.UtcNow; Log(id, run.Id, "sync", "phase." + p.Phase, p.Message); }
                    run.Progress = p; Changed();
                });
                var events = new InlineProgress<SyncLogEntry>(e => Log(id, run.Id, "sync", e.Action, e.Message, e.Level, e.ObjectId, e.ObjectKind, e.Bytes, e.Generation, e.TimestampUtc, e.WireBytes));
                var result = await new SyncCoordinator(repository).RunAsync(new WorkerVolume(worker, id, DiskObjectSize(Disk(id))), binding, Text(disk, "name"), UInt(disk, "capacityBytes"), Flag(disk, "encrypted"), concurrency, observer, run.Cancellation.Token, events, prepareCacheMiB);
                lock (gate)
                {
                    settings.LastSuccess[id] = result.Commit.UpdatedUtc;
                    if (result.HasPendingChanges) settings.PendingDisks.Add(id); else settings.PendingDisks.Remove(id);
                }
                Save();
                await ApplyCacheSettingsSafeAsync(id, run.Cancellation.Token);
                run.Progress = run.Progress with { Phase = result.HasPendingChanges ? "pending" : "synced", Message = result.HasPendingChanges ? "此快照已同步，仍有新变更待上传" : result.CleanupPending ? "云端已同步，旧对象等待手动清理" : "云端已同步", CompletedBytes = run.Progress.TotalBytes, PendingBytes = 0, Estimated = result.HasPendingChanges };
                Log(id, run.Id, "sync", "sync.completed", run.Progress.Message, generation: result.Commit.Generation);
                if (client is { } current) await WindowsSessionVault.SaveAsync(sessionPath, current.ExportSession());
            }
            catch (OperationCanceledException) { if (run.Progress.Phase != "synced") run.Progress = run.Progress with { Phase = "paused", Message = "同步已暂停，进度已保留" }; Log(id, run.Id, "sync", "sync.paused", run.Progress.Message); }
            catch (Exception error)
            {
                bool rejected = error is CloudProviderException provider && (provider.AuthenticationRequired || provider.Code is "Http:403" or "Http:429");
                string message = Friendly(error) + (rejected ? " 自动同步已暂停；检查登录状态和限流设置后，可手动继续。" : "");
                run.Progress = run.Progress with { Phase = rejected ? "paused" : "error", Message = message, Error = message };
                Log(id, run.Id, "sync", "sync.failed", message, "error");
                lock (gate) { settings.PendingDisks.Add(id); if (rejected) settings.PausedDisks.Add(id); }
                Save();
            }
            finally
            {
                try { pendingMaintenance[id] = await repository.HasPendingWorkAsync(binding, id, CancellationToken.None); }
                catch (IOException e) { pendingMaintenance[id] = true; Log(id, run.Id, "sync", "cache.read_failed", e.Message, "warning"); }
                if (pendingMaintenance.GetValueOrDefault(id)) maintenanceAfter[id] = DateTimeOffset.UtcNow.AddMinutes(5);
                else maintenanceAfter.TryRemove(id, out _);
                Changed();
            }
        });
        Changed();
            }
}
    private async Task ReleaseDeletedSourcePinsAsync(CloudBinding binding, CancellationToken ct)
    {
        RestoreRecord[] records;
        lock (gate) records = settings.Restores.Values.Where(r => (r.ContainerDeleted || r.SourcePinReplaced) && r.ReaderPin != null && r.AccountId == binding.AccountId && r.RemoteRoot == binding.RemoteRoot).ToArray();
        var repository = Repository();
        foreach (var record in records)
        {
            string expected = record.RemoteRoot + "/readers/" + CloudRepository.Component(record.Id) + ".json";
            if (!CloudRepository.IsRootForVolume(record.RemoteRoot, record.Commit.VolumeId) || record.ReaderPin != expected)
                throw new IOException("待清理的云端来源引用不符合记录，已停止清理。");
            await repository.Store.DeleteAsync(expected, ct);
            lock (gate) record.ReaderPin = null;
            Save(); Log(record.LocalDiskId ?? "", "", "cleanup", "cleanup.deleted", "手动清理已删除本地副本对应的云端读取引用");
        }
    }
    private void StartCleanup(string id)
    {
        lock(jobGate)
        {
            if(!networkAdmissionsOpen||exiting||deletingDisks.Contains(id))throw new IOException("当前无法启动云端清理。");
            if(runs.TryGetValue(id,out var current)&&current.Task is {IsCompleted:false})throw new IOException("请先完成或暂停当前网络任务。");
            var disk=Disk(id);if(!Flag(disk,"unlocked"))throw new IOException("请先解锁磁盘，以核对当前已发布版本。");
            CloudBinding binding;lock(gate)binding=settings.Bindings.GetValueOrDefault(id)??throw new IOException("磁盘尚未启用同步。");
            var repository=Repository();var run=new JobRun{DiskId=id,Kind="cleanup",Title="手动清理云端旧块",Progress=new("cleaning","正在核对待清理清单",0,0,0)};
            runs[id]=run;Log(id,run.Id,"cleanup","cleanup.queued","已收到手动清理请求");
            run.Task=Task.Run(async()=>
            {
                try
                {
                    await ApplyCacheSettingsAsync(id, run.Cancellation.Token, handOffOriginalPin: true);
                    await ReleaseDeletedSourcePinsAsync(binding, run.Cancellation.Token);
                    await ReleaseDeletedCachePinsAsync(binding, run.Cancellation.Token);
                    var estimate=await repository.CleanupEstimateAsync(binding,id,run.Cancellation.Token);long completed=0;
                    run.Progress=new("cleaning","正在删除已确认过时的云端对象",0,estimate.Bytes,0);Changed();
                    var events=new InlineProgress<SyncLogEntry>(e=>
                    {
                        Log(id,run.Id,"cleanup",e.Action,e.Message,e.Level,e.ObjectId,e.ObjectKind,e.Bytes,e.Generation,e.TimestampUtc);
                        if(e.Action=="cleanup.deleted"&&e.Bytes is long bytes){completed+=bytes;run.Progress=run.Progress with {CompletedBytes=completed};Changed();}
                    });
                    var result=await new SyncCoordinator(repository).CleanupAsync(new WorkerVolume(worker,id,DiskObjectSize(Disk(id))),binding,events,run.Cancellation.Token);
                    run.Progress=run.Progress with {Phase=result.Pending?"paused":"complete",Message=result.Pending?$"剩余 {result.RemainingObjects} 个对象，等待稍后手动继续":"手动清理已完成"};
                }
                catch(OperationCanceledException){run.Progress=run.Progress with {Phase="paused",Message="手动清理已暂停，清单已保留"};}
                catch(Exception error){run.Progress=run.Progress with {Phase="error",Message=Friendly(error),Error=Friendly(error)};Log(id,run.Id,"cleanup","cleanup.failed",Friendly(error),"error");}
                finally{Changed();}
            });Changed();
        }
    }

    private async Task PauseSyncAsync(string id, bool savePreference)
    {
        if (savePreference) { lock (gate) settings.PausedDisks.Add(id); Save(); }
        if (runs.TryGetValue(id, out var task) && task.Task is { IsCompleted: false }) { task.Cancellation.Cancel(); await task.Task; }
        if (savePreference && runs.TryGetValue(id, out var pausedRun)) pausedRun.Progress = pausedRun.Progress with { Phase = "paused", Message = "自动同步已暂停，进度已保留" };
        if (worker.IsConnected && Flag(Disk(id), "unlocked"))
        {
            try { await new WorkerVolume(worker, id, DiskObjectSize(Disk(id))).ControlAsync(new { cmd = "cloud.pause", paused = true }, CancellationToken.None); }
            catch (IOException) { }
        }
        Changed();
    }
    private async Task StopNetworkTasksAsync()
    {
        JobRun[] tasks;
        lock (jobGate)
        {
            networkAdmissionsOpen = false;
            tasks = runs.Values.Where(r => r.Task is { IsCompleted: false }).ToArray();
            foreach (var task in tasks) task.Cancellation.Cancel();
        }
        try { await Task.WhenAll(tasks.Select(t => t.Task!)); }
        catch when (tasks.All(t => t.Task!.IsCompleted)) { /* Each task owns its logged failure/cancellation. */ }
        foreach (var pair in replicaRuns.Where(p => p.Value.Active).ToArray())
            await CancelReplicaAsync(pair.Key, Element(new { id = pair.Key }), CancellationToken.None);
    }
    private async Task UnmountAndSyncAsync(string id, CancellationToken ct)
    {
        await PauseSyncAsync(id, false);
        await worker.InvokeAsync("disks.quiesce", Element(new { id }), ct);
        await RefreshWorkerAsync(ct);
        bool bound; lock (gate) bound = settings.Bindings.ContainsKey(id);
        if (bound && NeedsSync(Disk(id)))
        {
            try
            {
                StartSync(id); await runs[id].Task!;
                if (runs[id].Progress.Phase != "synced") { notice = "本地已保存，云端尚未完成；下次启动并解锁磁盘时会重试。"; lock (gate) settings.PendingDisks.Add(id); Save(); }
            }
            catch (Exception error) { notice = "本地已保存，云端尚未完成；下次解锁后重试。" + Friendly(error); lock (gate) settings.PendingDisks.Add(id); Save(); }
        }
        else if (bound)
        {
            lock (gate) settings.PendingDisks.Remove(id);
            Save();
        }
        await worker.InvokeAsync("disks.close", Element(new { id }), ct);
        await RefreshWorkerAsync(ct); Changed();
    }
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await accountGate.WaitAsync(cancellationToken);
        try { await ShutdownCoreAsync(cancellationToken); }
        finally { accountGate.Release(); }
    }
    private async Task ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        if (stopped) return;
        bool syncBeforeExit;
        lock (gate) syncBeforeExit = settings.SyncOnExit && !allowUnsyncedExit;
        lock (jobGate) { exiting = true; networkAdmissionsOpen = false; }
        try
        {
            await initialization;
            await StopNetworkTasksAsync();
            if (worker.HasRunningWorker)
            {
                SetWorkerState(Element(await worker.InvokeAsync("state", Element(new { }), cancellationToken)));
                foreach (var disk in Disks().Where(d => Flag(d, "unlocked")))
                    await worker.InvokeAsync("disks.quiesce", Element(new { id = Text(disk, "id") }), cancellationToken);
                await RefreshWorkerAsync(cancellationToken);
                // Capture final dirty state after the safe filesystem cut, even if
                // the UI never polled between the last write and exit.
                foreach (var disk in Disks().Where(d => Flag(d, "unlocked")))
                {
                    string id = Text(disk, "id"); bool pending = NeedsSync(disk);
                    lock (gate) if (settings.Bindings.ContainsKey(id))
                    {
                        if (pending) settings.PendingDisks.Add(id);
                        else settings.PendingDisks.Remove(id);
                    }
                }
                if (syncBeforeExit)
                {
                    var failures = new List<string>();
                    foreach (var disk in Disks().Where(d => Flag(d, "unlocked")))
                    {
                        string id = Text(disk, "id"); bool bound;
                        lock (gate) bound = settings.Bindings.ContainsKey(id);
                        if (!bound || !NeedsSync(disk)) continue;
                        try
                        {
                            StartSync(id, true); await runs[id].Task!;
                            if (runs[id].Progress.Phase != "synced") failures.Add(Text(disk, "name"));
                        }
                        catch { failures.Add(Text(disk, "name")); }
                    }
                    if (failures.Count != 0) throw new IOException("以下磁盘尚未完成云同步：" + string.Join("、", failures) + "。本地数据已保存，下次启动并解锁后继续尝试。");
                }
            }
            if (syncBeforeExit)
            {
                var lockedPending = Disks().Where(d => (!worker.HasRunningWorker || !Flag(d, "unlocked")) && NeedsSync(d) && settings.Bindings.ContainsKey(Text(d, "id"))).Select(d => Text(d, "name")).ToArray();
                if (lockedPending.Length != 0) throw new IOException("以下磁盘的云端状态尚未确认：" + string.Join("、", lockedPending) + "。本地数据已保存，下次解锁后重试。");
            }
            await worker.ShutdownAsync(cancellationToken);
            await StopCloudReadsAsync();
            // Once disks are safely closed, optional preference/session persistence must not
            // revive a half-stopped UI with its demand-read channel permanently closed.
            try { Save(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Log("", "", "system", "settings.save_failed", "磁盘已安全关闭；退出时设置保存失败：" + error.Message, "warning"); }
            lifetime.Cancel();
            if (client != null)
            {
                try { await WindowsSessionVault.SaveAsync(sessionPath, client.ExportSession()); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                { Log("", "", "system", "session.save_failed", "磁盘已安全关闭；退出时登录状态保存失败。", "warning"); }
                await client.DisposeAsync(); client = null;
            }
            stopped = true;
            try { journal?.Dispose(); } catch (IOException) { /* Optional activity history must not reopen a safely stopped service. */ }
        }
        catch { lock (jobGate) { exiting = false; networkAdmissionsOpen = true; } throw; }
    }
    private static string Friendly(Exception error) => error is CloudProviderException { AuthenticationRequired: true }
        ? "登录状态已失效，请在官方网页中重新验证。" : error is CloudProviderException { IsTransient: true }
        ? "网络暂时不可用，已保留进度，稍后重试。" : error.Message;
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private async Task<object> StartRestoreAsync(JsonElement args, CancellationToken ct)
    {
        var repository = Repository(); string volumeId = Text(args, "id"), root = CloudRepository.RootPath(volumeId);
        string mode = Text(args, "mode", "copy");
        if (mode is not ("copy" or "original")) throw new IOException("请选择新建副本或恢复原硬盘。");
        bool original = mode == "original";
        if (original && !Flag(args, "originalConfirmed")) throw new IOException("恢复原硬盘前，请确认没有本地副本和其他并发写者。");
        string path = Path.GetFullPath(Text(args, "path"));
        if (!Path.GetExtension(path).Equals(".odv4", StringComparison.OrdinalIgnoreCase) || File.Exists(path) || Directory.Exists(path))
            throw new IOException("请选择尚不存在的 .odv4 文件。");
        await RefreshWorkerAsync(ct);
        if (original)
        {
            if (Disks().Any(d => Text(d, "id") == volumeId))
                throw new IOException("本机仍保留这块硬盘的记录。请先关闭磁盘并在设置中移除旧记录，再恢复原硬盘。");
            lock (gate)
            {
                if (settings.Restores.Values.Any(r => !r.ContainerDeleted && r.LocalDiskId == volumeId && File.Exists(r.TargetPath)))
                    throw new IOException("本机仍存在同身份的恢复容器，请先处理旧容器。");
                if (settings.Restores.Values.Any(r => r.Mode == "original" && r.Commit.VolumeId == volumeId &&
                    runs.TryGetValue(r.Id, out var prior) && prior.Task is { IsCompleted: false }))
                    throw new IOException("这块原硬盘已有正在进行的恢复任务。");
            }
        }
        await worker.InvokeAsync("restore.preflight", Element(new { mode, sourceVolumeId = volumeId, path }), ct);
        var current = await repository.LatestForReplicaAsync(root, ct: ct) ?? throw new IOException("云端没有完整版本。");
        var originalBinding = original ? await repository.PrepareOriginalRestoreForReplicaAsync(volumeId, current.Commit, ct) : null;
        var record = new RestoreRecord { AccountId = account!.AccountId, RemoteRoot = root, TargetPath = path,
            Name = Text(args, "name", current.Commit.Name + (original ? "" : " 副本")), Commit = current.Commit,
            Mode = mode, OriginalConfirmed = original, OriginalBinding = originalBinding, VerifiedCommit = true,
            Lazy = true };
        record.ReaderPin = root + "/readers/" + record.Id + ".json";
        lock (gate)
        {
            // Explicit original recovery supersedes records of already absent local containers.
            // Their remote reader pins remain for the user's manual cloud cleanup.
            if (original)
                foreach (var old in settings.Restores.Values.Where(r => (r.LocalDiskId == volumeId || r.Mode == "original" && r.Commit.VolumeId == volumeId) && !r.ContainerDeleted && !File.Exists(r.TargetPath)))
                { old.ContainerDeleted = true; runs.TryRemove(old.Id, out _); }
            settings.Restores[record.Id] = record;
        }
        Save(); StartRestoreRun(record, Password(args), repository); return new { taskId = record.Id };
    }
    private void StartRestoreRun(RestoreRecord record, string? password, CloudRepository repository)
    {
        lock (jobGate)
        {
        if (!networkAdmissionsOpen || exiting) throw new IOException("正在结束当前网络任务，请稍候。");

        if (record.ContainerDeleted) throw new IOException("这个恢复任务对应的本地容器已移除，请创建新的导入任务。");
        if (record.Mode is not ("copy" or "original") || record.Mode == "original" && !record.OriginalConfirmed)
            throw new IOException("原硬盘恢复任务缺少已确认的导入方式。");
        if (record.AccountId != account?.AccountId) throw new IOException("请登录创建该恢复任务时使用的网盘账户。");
        if (runs.TryGetValue(record.Id, out var old) && old.Task is { IsCompleted: false }) return;
        var run = new JobRun { Id = record.Id, DiskId = record.Commit.VolumeId, Kind = "restore", Title = (record.Mode == "original" ? "恢复原硬盘 · " : "新建云端副本 · ") + record.Name, RequiresPassword = record.Commit.Encrypted };
        runs[record.Id] = run;
        Log(run.DiskId, run.Id, "restore", "restore.started", run.Title, generation: record.Commit.Generation);
        run.Task = Task.Run(async () =>
        {
            string? workerId = null;
            var ct = run.Cancellation.Token;
            try
            {
                run.Progress = new("downloading", "正在验证云端版本", 0, record.Commit.ObjectSizeBytes, 0); Changed();
                if (record.Mode == "original" && (record.Begun || record.OriginalBinding is null))
                {
                    var binding = await repository.PrepareOriginalRestoreForReplicaAsync(record.Commit.VolumeId, record.Commit, ct);
                    lock (gate) record.OriginalBinding = binding;
                    Save();
                }
                await repository.EnsureReplicaReaderAsync(record.RemoteRoot, record.Commit, record.Id, record.Begun, ct);
                var downloadedRoot = await repository.ReadObjectForReplicaAsync(record.RemoteRoot, record.Commit.RootObjectId, record.Commit.RootSha256, record.Commit.ObjectSizeBytes, ct);
                byte[] root = downloadedRoot.Canonical; Interlocked.Add(ref run.DownloadedBytes, downloadedRoot.WireBytes);
                var begin = Element(await worker.BeginRestoreAsync(Element(new { path = record.TargetPath, name = record.Name, password, resume = record.Begun || File.Exists(record.TargetPath), lazy = record.Lazy,
                    mode = record.Mode, sourceVolumeId = record.Commit.VolumeId, objectSizeBytes = record.Commit.ObjectSizeBytes, commit = record.Commit,
                    publication = record.OriginalBinding is { } originalBinding ? new { commit = record.Commit,
                        binding = new { backend_id = originalBinding.ProviderId, account_id = originalBinding.AccountId,
                            remote_root = originalBinding.RemoteRoot, device_id = originalBinding.DeviceId, enabled = true } } : null,
                    backing = new { provider_id = repository.Store.ProviderId, account_id = record.AccountId, source_volume_id = record.Commit.VolumeId,
                        remote_root = record.RemoteRoot, root_object_id = record.Commit.RootObjectId, root_sha256 = record.Commit.RootSha256, reader_pin = record.ReaderPin }
                }), root, ct));
                workerId = begin.GetProperty("id").GetString()!;
                run.DiskId = workerId; run.VerifiedScope = ReplicaVerification;
                lock (gate) { record.WorkerId = workerId; record.LocalDiskId = workerId; record.Begun = true; } Save();
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var status = Element(await worker.InvokeAsync("restore.status", Element(new { id = workerId, cursor = 0, limit = 128 }), ct));
                    if (status.TryGetProperty("root_object_id", out var rootId) && rootId.ValueKind == JsonValueKind.String && rootId.GetString() != record.Commit.RootObjectId) throw new IOException("恢复文件属于另一个云端版本。");
                    if (status.TryGetProperty("root_sha256", out var sha) && sha.ValueKind == JsonValueKind.String && sha.GetString() != record.Commit.RootSha256) throw new IOException("恢复文件的根校验值不匹配。");
                    if (Text(status, "mode", "copy") != record.Mode) throw new IOException("恢复容器的导入方式与任务记录不一致。");
                    if (record.Mode == "original" && workerId != record.Commit.VolumeId) throw new IOException("原硬盘恢复不能更改磁盘身份。");
                    if (Text(status, "source_volume_id") != record.Commit.VolumeId || UInt(status, "generation") != record.Commit.Generation || UInt(status, "capacity_bytes") != record.Commit.CapacityBytes || Flag(status, "encrypted") != record.Commit.Encrypted || DiskObjectSize(status) != record.Commit.ObjectSizeBytes)
                        throw new IOException("云端提交描述与已认证的磁盘根不一致。");
                    string phase = Text(status, "phase");
                    long done = SyncCoordinator.GetLong(status, "completed_objects") * record.Commit.ObjectSizeBytes;
                    long total = SyncCoordinator.GetLong(status, "total_objects") * record.Commit.ObjectSizeBytes;
                    run.CompletedPages = SyncCoordinator.GetLong(status, "completed_pages");
                    run.TotalPages = SyncCoordinator.GetLong(status, "expected_pages");
                    run.CompletedIndexNodes = SyncCoordinator.GetLong(status, "completed_nodes");
                    string activePhase = phase == "building" ? Text(status, "work_phase") == "data" ? "verifying" : "indexing" : "downloading";
                    string indexProgress = run.TotalPages > 0
                        ? $"已验证 {run.CompletedPages:N0} 个页映射；未访问区域按需验证"
                        : "正在验证挂载所需的索引根";
                    if (run.Progress.Phase != activePhase)
                    {
                        run.PhaseStartedUtc = DateTimeOffset.UtcNow;
                        Log(run.DiskId, run.Id, "restore", "phase." + activePhase,
                            activePhase == "indexing" ? "开始验证并接入云端不可变索引" : activePhase == "verifying" ? "开始完成已下载内容的本地校验" : "等待所需云端对象下载");
                    }
                    run.Progress = new(activePhase, phase == "building"
                        ? activePhase == "verifying" ? "下载已完成，正在完成本地内容校验" : "正在接入云端索引 · " + indexProgress
                        : record.Lazy ? "正在下载索引 · " + indexProgress : phase == "metadata" ? "正在下载索引 · " + indexProgress : "正在下载磁盘内容 · " + indexProgress,
                        done, total, 0, record.Lazy); Changed();
                    if (phase is "ready" or "complete")
                    {
                        Log(run.DiskId, run.Id, "restore", "index.completed",
                            $"索引已就绪：{run.CompletedPages:N0} 个页映射、{run.CompletedIndexNodes:N0} 个索引节点；本地读取元数据对象 {SyncCoordinator.GetLong(status, "metadata_object_reads"):N0} 次，逐页任务 {SyncCoordinator.GetLong(status, "queued_page_records"):N0} 个");
                        break;
                    }
                    var missing = status.TryGetProperty("needed", out var n) ? n : status.GetProperty("items");
                    var objects = missing.EnumerateArray().Select(SyncCoordinator.ParseObject).ToArray();
                    if (objects.Any(obj => obj.Length != record.Commit.ObjectSizeBytes)) throw new IOException("恢复对象大小与已确认云端版本不一致。");
                    if (objects.Length == 0 && phase == "building")
                    {
                        await worker.InvokeAsync("restore.step", Element(new { id = workerId, max_nodes = 256, max_pages = 4096 }), ct);
                        continue;
                    }
                    if (objects.Length == 0) throw new IOException("恢复状态缺少下一批对象，已保留进度。");
                    int concurrency; lock (gate) concurrency = settings.MaxParallelTransfers;
                    await Parallel.ForEachAsync(objects, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = concurrency }, async (obj, token) =>
                    {
                        Log(run.DiskId, run.Id, "restore", "download.started", "开始下载恢复对象", objectId: obj.Id, objectKind: obj.Kind, bytes: obj.Length);
                        var downloaded = await repository.ReadObjectForReplicaAsync(record.RemoteRoot, obj.Id, obj.Sha256, record.Commit.ObjectSizeBytes, token);
                        await worker.AcceptRestoreObjectAsync(workerId!, obj.Id, downloaded.Canonical, token);
                        Interlocked.Add(ref run.DownloadedBytes, downloaded.WireBytes);
                        Log(run.DiskId, run.Id, "restore", "download.confirmed", "恢复对象已校验并写入本地", objectId: obj.Id, objectKind: obj.Kind, bytes: obj.Length, wireBytes: downloaded.WireBytes);
                    });
                }
                if (record.Mode == "original")
                {
                    var checkedBinding = await repository.PrepareOriginalRestoreForReplicaAsync(record.Commit.VolumeId, record.Commit, ct);
                    if (checkedBinding != record.OriginalBinding) throw new IOException("恢复期间原硬盘的写入者记录发生变化。");
                }
                await worker.InvokeAsync("restore.finish", Element(new { id = workerId }), ct); workerId = null;
                lock (gate)
                {
                    record.Complete = true; record.WorkerId = null;
                    if (record.Mode == "original")
                    {
                        settings.Bindings[record.Commit.VolumeId] = record.OriginalBinding!;
                        settings.LastSuccess[record.Commit.VolumeId] = record.Commit.UpdatedUtc;
                        settings.PendingDisks.Remove(record.Commit.VolumeId); settings.PausedDisks.Remove(record.Commit.VolumeId);
                    }
                }
                Save();
                if (!record.Lazy)
                {
                    try { await repository.Store.DeleteAsync(record.ReaderPin!, ct); lock (gate) record.ReaderPin = null; Save(); maintenanceAfter.TryRemove(record.Commit.VolumeId, out _); }
                    catch (IOException) { notice = "磁盘已恢复，远端恢复引用将在下次连接时清理。"; }
                }
                run.Progress = new("complete", record.Mode == "original" ? "原硬盘已就绪，可挂载；未访问的索引和文件内容按需下载与验证" : record.Lazy ? "副本已就绪，可挂载；索引和文件内容按需下载与验证，云同步保持关闭" : "副本已恢复并校验，可在磁盘页挂载", run.Progress.TotalBytes, run.Progress.TotalBytes, 0); run.RequiresPassword = false;
                run.ReadyToMount = true;
                Log(run.DiskId, run.Id, "restore", "restore.completed", run.Progress.Message, generation: record.Commit.Generation);
                await RefreshWorkerAsync(CancellationToken.None);
            }
            catch (OperationCanceledException) { run.Progress = run.Progress with { Phase = "paused", Message = "导入已暂停，已保存的进度会保留" }; Log(run.DiskId, run.Id, "restore", "restore.paused", run.Progress.Message); }
            catch (Exception error) { run.Progress = run.Progress with { Phase = "error", Message = Friendly(error), Error = Friendly(error) }; Log(run.DiskId, run.Id, "restore", "restore.failed", Friendly(error), "error"); }
            finally
            {
                if (workerId != null && worker.IsConnected)
                {
                    try { await worker.InvokeAsync("restore.cancel", Element(new { id = workerId }), CancellationToken.None); } catch { }
                    lock (gate) record.WorkerId = null; Save();
                }
                Changed();
            }
        });
        Changed();
            }
}
    private async Task<object> RestoreTaskActionAsync(string method, JsonElement args, CancellationToken ct)
    {
        string taskId = Text(args, "taskId");
        RestoreRecord record; lock (gate) record = settings.Restores.GetValueOrDefault(taskId) ?? throw new IOException("找不到云端恢复任务。");
        if (method == "restore.pause")
        {
            if (runs.TryGetValue(taskId, out var run) && run.Task is { IsCompleted: false }) { run.Cancellation.Cancel(); await run.Task; }
        }
        else StartRestoreRun(record, Password(args), Repository());
        Changed(); return new { ok = true };
    }
}
